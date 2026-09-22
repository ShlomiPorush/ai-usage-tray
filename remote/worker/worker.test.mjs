import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";
import { base64UrlEncode } from "../shared/web-push.mjs";
import worker from "./worker.js";

const WRITE_ID = "0123456789abcdef0123456789abcdef";
const READ_ID = "3eb1bd439947eb762998e566ccc2e099";

const MAX_PUSH_SUBSCRIPTIONS = 8;
const REFRESH_INTERVAL_SECONDS = 86400;

class MemoryKv {
  values = new Map();
  puts = [];
  lists = 0;
  // Optional hook awaited at the start of every list(), used to hold concurrent
  // registrations on the same pre-write view that real KV can serve them.
  beforeList = null;

  async get(key, options) {
    const value = this.values.get(key);
    if (value === undefined) return null;
    return options?.type === "json" ? JSON.parse(value) : value;
  }

  async put(key, value, options) {
    this.values.set(key, String(value));
    this.puts.push({ key, options });
  }

  async delete(key) {
    this.values.delete(key);
  }

  async list({ prefix }) {
    this.lists += 1;
    if (this.beforeList) await this.beforeList(this.lists);
    return {
      keys: [...this.values.keys()]
        .filter((key) => key.startsWith(prefix))
        .sort()
        .map((name) => ({ name })),
    };
  }
}

function deferred() {
  let release;
  const promise = new Promise((resolve) => {
    release = resolve;
  });
  return { promise, release };
}

let env;
let pushCalls;

async function browserSubscription(endpoint) {
  const subscriberKeys = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" },
    true,
    ["deriveBits"],
  );
  return {
    endpoint,
    keys: {
      p256dh: base64UrlEncode(await crypto.subtle.exportKey("raw", subscriberKeys.publicKey)),
      auth: base64UrlEncode(crypto.getRandomValues(new Uint8Array(16))),
    },
  };
}

beforeEach(async () => {
  const vapidKeys = await crypto.subtle.generateKey(
    { name: "ECDSA", namedCurve: "P-256" },
    true,
    ["sign", "verify"],
  );
  const privateJwk = await crypto.subtle.exportKey("jwk", vapidKeys.privateKey);
  pushCalls = [];
  env = {
    USAGE: new MemoryKv(),
    REMOTE_VIEW_VERSION: "1.1.0-worker-test",
    VAPID_PUBLIC_KEY: base64UrlEncode(await crypto.subtle.exportKey("raw", vapidKeys.publicKey)),
    VAPID_PRIVATE_KEY: privateJwk.d,
    VAPID_SUBJECT: "mailto:test@example.com",
    PUSH_SENDER: async (subscription, message) => {
      pushCalls.push({ subscription, message });
      return new Response(null, { status: 201 });
    },
  };
});

test("reports the deployed remote-view version", async () => {
  const response = await run(new Request("https://viewer.example/version"));
  assert.equal(response.status, 200);
  assert.equal(response.headers.get("cache-control"), "no-store");
  assert.deepEqual(await response.json(), { version: "1.1.0-worker-test" });
});

function upload(session, writeId = WRITE_ID, weekly = 42) {
  return new Request(`https://viewer.example/u/${writeId}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      version: 2,
      generatedAt: "2026-08-28T12:00:00Z",
      displayMode: "used",
      accounts: [{
        id: "claude:work",
        name: "Claude Work",
        alert: { enabled: true, thresholdPercent: 80, resetEnabled: true },
        windows: [
          { label: "Session", usedPercent: session },
          { label: "Weekly", usedPercent: weekly },
        ],
      }],
    }),
  });
}

async function run(request) {
  const pending = [];
  const response = await worker.fetch(request, env, {
    waitUntil(promise) {
      pending.push(promise);
    },
  });
  await Promise.all(pending);
  return response;
}

async function readIdOf(writeId) {
  const digest = new Uint8Array(await crypto.subtle.digest(
    "SHA-256",
    new TextEncoder().encode(writeId),
  ));
  return [...digest.slice(0, 16)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

async function endpointHash(endpoint) {
  return base64UrlEncode(await crypto.subtle.digest(
    "SHA-256",
    new TextEncoder().encode(endpoint),
  ));
}

function subscribe(readId, subscription) {
  return run(new Request(
    `https://viewer.example/u/${readId}/push-subscription`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(subscription),
    },
  ));
}

function writesUnder(prefix) {
  return env.USAGE.puts.filter((entry) => entry.key.startsWith(prefix)).length;
}

test("matches the server subscription route and delivers a crossing", async () => {
  assert.equal((await run(upload(79))).status, 204);

  const subscription = await browserSubscription(
    "https://fcm.googleapis.com/fcm/send/worker-subscription",
  );
  const registered = await run(new Request(
    `https://viewer.example/u/${READ_ID}/push-subscription`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(subscription),
    },
  ));
  assert.equal(registered.status, 204);

  assert.equal((await run(upload(81))).status, 204);
  assert.equal(pushCalls.length, 1);
  assert.equal(pushCalls[0].message.alerts[0].windowKey, "session");
  assert.ok(env.USAGE.puts.some((entry) =>
    entry.key.startsWith(`push:${READ_ID}:`) && entry.options?.expirationTtl === 604800));

  assert.equal((await run(upload(0, WRITE_ID, 0))).status, 204);
  assert.equal(pushCalls.length, 2);
  assert.equal(pushCalls[1].message.resets.length, 1);
  assert.equal(pushCalls[1].message.resets[0].windowKey, "weekly");

  const removed = await run(new Request(
    `https://viewer.example/u/${READ_ID}/push-subscription`,
    {
      method: "DELETE",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ endpoint: subscription.endpoint }),
    },
  ));
  assert.equal(removed.status, 204);
  assert.equal((await env.USAGE.list({ prefix: `push:${READ_ID}:` })).keys.length, 0);
});

test("an old view cannot remove an endpoint mapping after the subscription moves", async () => {
  const secondWriteId = "ffffffffffffffffffffffffffffffff";
  const secondReadId = await readIdOf(secondWriteId);
  await run(upload(10));
  await run(upload(10, secondWriteId));
  const subscription = await browserSubscription(
    "https://updates.push.services.mozilla.com/wpush/v2/moved-subscription",
  );
  const subscribe = (readId) => run(new Request(
    `https://viewer.example/u/${readId}/push-subscription`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(subscription),
    },
  ));
  assert.equal((await subscribe(READ_ID)).status, 204);
  assert.equal((await subscribe(secondReadId)).status, 204);

  assert.equal((await run(new Request(
    `https://viewer.example/u/${READ_ID}/push-subscription`,
    {
      method: "DELETE",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ endpoint: subscription.endpoint }),
    },
  ))).status, 204);

  const hash = await endpointHash(subscription.endpoint);
  assert.equal(await env.USAGE.get(`push-endpoint:${hash}`), secondReadId);
  assert.notEqual(await env.USAGE.get(`push:${secondReadId}:${hash}`), null);
});

test("deleting a view removes all subscription records", async () => {
  await run(upload(10));
  await env.USAGE.put(
    `push:${READ_ID}:test`,
    JSON.stringify({ endpoint: "https://web.push.apple.com/a" }),
  );

  const response = await run(new Request(`https://viewer.example/u/${WRITE_ID}`, { method: "DELETE" }));

  assert.equal(response.status, 204);
  assert.equal((await env.USAGE.list({ prefix: `push:${READ_ID}:` })).keys.length, 0);
});

test("registers only push-service endpoints, or the hosts the operator named", async () => {
  await run(upload(10));
  const register = (subscription) => run(new Request(
    `https://viewer.example/u/${READ_ID}/push-subscription`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(subscription),
    },
  ));

  assert.equal((await register(await browserSubscription("https://169.254.169.254/x"))).status, 422);
  assert.equal((await register(await browserSubscription("https://evil.example/x"))).status, 422);
  assert.equal(
    (await register(await browserSubscription("https://fcm.googleapis.com:8443/x"))).status,
    422,
  );
  assert.equal((await env.USAGE.list({ prefix: `push:${READ_ID}:` })).keys.length, 0);

  const operatorEndpoint = await browserSubscription("https://push.example.test/operator-endpoint");
  assert.equal((await register(operatorEndpoint)).status, 422);

  env.PUSH_ENDPOINT_ALLOWED_HOSTS = "push.example.test";
  assert.equal((await register(operatorEndpoint)).status, 204);
  assert.equal((await env.USAGE.list({ prefix: `push:${READ_ID}:` })).keys.length, 1);
});

test("an upload rewrites a subscription key at most once a day", async () => {
  let clock = Date.UTC(2026, 0, 5, 9, 0, 0);
  env.NOW = () => clock;
  await run(upload(10));
  for (let index = 0; index < 3; index++) {
    const subscription = await browserSubscription(
      `https://fcm.googleapis.com/fcm/send/refresh-${index}`,
    );
    assert.equal((await subscribe(READ_ID, subscription)).status, 204);
  }

  // A byte-identical repeat upload inside the interval must cost one KV write:
  // the snapshot itself. Every subscription TTL still has days left.
  env.USAGE.puts.length = 0;
  assert.equal((await run(upload(10))).status, 204);
  assert.deepEqual(env.USAGE.puts.map((entry) => entry.key), [READ_ID]);

  // Nothing changes at the edge of the interval either.
  clock += REFRESH_INTERVAL_SECONDS * 1000;
  env.USAGE.puts.length = 0;
  assert.equal((await run(upload(10))).status, 204);
  assert.deepEqual(env.USAGE.puts.map((entry) => entry.key), [READ_ID]);

  // One second past it, each subscription is refreshed exactly once: its own key
  // plus its endpoint mapping.
  clock += 1000;
  env.USAGE.puts.length = 0;
  assert.equal((await run(upload(10))).status, 204);
  assert.equal(env.USAGE.puts.length, 7);
  assert.equal(writesUnder(`push:${READ_ID}:`), 3);
  assert.equal(writesUnder("push-endpoint:"), 3);
  assert.ok(env.USAGE.puts.every((entry) =>
    entry.key === READ_ID || entry.options?.expirationTtl === 604800));

  // And the refresh resets the interval instead of repeating on every upload.
  env.USAGE.puts.length = 0;
  assert.equal((await run(upload(10))).status, 204);
  assert.deepEqual(env.USAGE.puts.map((entry) => entry.key), [READ_ID]);
});

test("a subscription stored without a refresh timestamp is rewritten once", async () => {
  env.NOW = () => Date.UTC(2026, 0, 5, 9, 0, 0);
  await run(upload(10));
  const subscription = await browserSubscription(
    "https://fcm.googleapis.com/fcm/send/legacy-subscription",
  );
  const hash = await endpointHash(subscription.endpoint);
  const key = `push:${READ_ID}:${hash}`;
  // The shape written by the worker before refresh timestamps existed.
  await env.USAGE.put(key, JSON.stringify(subscription));

  env.USAGE.puts.length = 0;
  assert.equal((await run(upload(10))).status, 204);
  assert.equal(writesUnder(`push:${READ_ID}:`), 1);
  const stored = await env.USAGE.get(key, { type: "json" });
  assert.equal(stored.endpoint, subscription.endpoint);
  assert.equal(typeof stored.refreshedAt, "number");

  env.USAGE.puts.length = 0;
  assert.equal((await run(upload(10))).status, 204);
  assert.deepEqual(env.USAGE.puts.map((entry) => entry.key), [READ_ID]);
});

test("rejects a registration past the cap and self-heals a concurrent burst", async () => {
  env.NOW = () => Date.UTC(2026, 0, 5, 9, 0, 0);
  await run(upload(10));
  const subscriptions = [];
  for (let index = 0; index < 20; index++) {
    subscriptions.push(await browserSubscription(
      `https://fcm.googleapis.com/fcm/send/burst-${index}`,
    ));
  }

  // Sequential control: the pre-put list check still answers 429 once the cap
  // is reached, and the stored set stops growing.
  for (let index = 0; index < MAX_PUSH_SUBSCRIPTIONS; index++) {
    assert.equal((await subscribe(READ_ID, subscriptions[index])).status, 204);
  }
  assert.equal((await subscribe(READ_ID, subscriptions[MAX_PUSH_SUBSCRIPTIONS])).status, 429);
  assert.equal(
    (await env.USAGE.list({ prefix: `push:${READ_ID}:` })).keys.length,
    MAX_PUSH_SUBSCRIPTIONS,
  );

  // Concurrent burst: every request sees the same pre-write list, exactly what a
  // real eventually consistent KV list can serve.
  for (const key of [...env.USAGE.values.keys()]) {
    if (key.startsWith(`push:${READ_ID}:`) || key.startsWith("push-endpoint:")) {
      await env.USAGE.delete(key);
    }
  }
  const barrier = deferred();
  let arrived = 0;
  env.USAGE.lists = 0;
  env.USAGE.beforeList = async (call) => {
    if (call > subscriptions.length) return; // the self-heal list must not block
    arrived += 1;
    if (arrived === subscriptions.length) barrier.release();
    await barrier.promise;
  };

  const responses = await Promise.all(
    subscriptions.map((subscription) => subscribe(READ_ID, subscription)),
  );
  env.USAGE.beforeList = null;

  assert.ok(responses.every((response) => response.status === 204 || response.status === 429));
  const stored = (await env.USAGE.list({ prefix: `push:${READ_ID}:` })).keys.map((e) => e.name);
  assert.equal(stored.length, MAX_PUSH_SUBSCRIPTIONS);

  // Eviction order is deterministic: same createdAt, so the lowest key names win.
  const allKeys = await Promise.all(subscriptions.map(async (subscription) =>
    `push:${READ_ID}:${await endpointHash(subscription.endpoint)}`));
  assert.deepEqual(stored, [...allKeys].sort().slice(0, MAX_PUSH_SUBSCRIPTIONS));

  // The endpoint mappings of the evicted entries go with them.
  assert.equal(
    (await env.USAGE.list({ prefix: "push-endpoint:" })).keys.length,
    MAX_PUSH_SUBSCRIPTIONS,
  );
});
