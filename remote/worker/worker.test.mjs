import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";
import { base64UrlEncode } from "../shared/web-push.mjs";
import worker from "./worker.js";

const WRITE_ID = "0123456789abcdef0123456789abcdef";
const READ_ID = "3eb1bd439947eb762998e566ccc2e099";

class MemoryKv {
  values = new Map();
  puts = [];

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
    return {
      keys: [...this.values.keys()]
        .filter((key) => key.startsWith(prefix))
        .sort()
        .map((name) => ({ name })),
    };
  }
}

let env;
let pushCalls;

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
    VAPID_PUBLIC_KEY: base64UrlEncode(await crypto.subtle.exportKey("raw", vapidKeys.publicKey)),
    VAPID_PRIVATE_KEY: privateJwk.d,
    VAPID_SUBJECT: "mailto:test@example.com",
    PUSH_SENDER: async (subscription, message) => {
      pushCalls.push({ subscription, message });
      return new Response(null, { status: 201 });
    },
  };
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

test("matches the server subscription route and delivers a crossing", async () => {
  assert.equal((await run(upload(79))).status, 204);

  const subscriberKeys = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" },
    true,
    ["deriveBits"],
  );
  const subscription = {
    endpoint: "https://push.example.test/worker-subscription",
    keys: {
      p256dh: base64UrlEncode(await crypto.subtle.exportKey("raw", subscriberKeys.publicKey)),
      auth: base64UrlEncode(crypto.getRandomValues(new Uint8Array(16))),
    },
  };
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
  const subscriberKeys = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" },
    true,
    ["deriveBits"],
  );
  const subscription = {
    endpoint: "https://push.example.test/moved-subscription",
    keys: {
      p256dh: base64UrlEncode(await crypto.subtle.exportKey("raw", subscriberKeys.publicKey)),
      auth: base64UrlEncode(crypto.getRandomValues(new Uint8Array(16))),
    },
  };
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
  await env.USAGE.put(`push:${READ_ID}:test`, JSON.stringify({ endpoint: "https://push.example.test/a" }));

  const response = await run(new Request(`https://viewer.example/u/${WRITE_ID}`, { method: "DELETE" }));

  assert.equal(response.status, 204);
  assert.equal((await env.USAGE.list({ prefix: `push:${READ_ID}:` })).keys.length, 0);
});
