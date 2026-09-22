// Cloudflare runs worker.bundle.js, not worker.js, so the generated artifact gets
// its own end-to-end test. The bundle entry wraps the API and must hand the
// execution context through: without it a threshold-crossing PUT stays open until
// every push delivery has settled instead of answering immediately and finishing
// the fan-out in ctx.waitUntil.
import assert from "node:assert/strict";
import { setTimeout as delay } from "node:timers/promises";
import { beforeEach, test } from "node:test";
import { base64UrlEncode } from "../shared/web-push.mjs";
import bundled from "./worker.bundle.js";

const WRITE_ID = "0123456789abcdef0123456789abcdef";
const READ_ID = "3eb1bd439947eb762998e566ccc2e099";

// A blocked push sender must not delay the PUT response by more than this.
const RESPONSE_BUDGET_MS = 500;
// Long enough to let a delivery that is already unblocked settle, short enough to
// keep the test quick; it only decides between "still running" and "already done".
const DELIVERY_PROBE_MS = 50;
const TIMED_OUT = Symbol("timed-out");
const STILL_RUNNING = Symbol("still-running");

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
let gate;

function deferred() {
  let release;
  const promise = new Promise((resolve) => {
    release = resolve;
  });
  return { promise, release };
}

// One execution context per request, mirroring the Cloudflare runtime: whatever
// the worker defers lands in `pending`.
function executionContext() {
  const pending = [];
  return {
    pending,
    waitUntil(promise) {
      pending.push(promise);
    },
  };
}

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
  gate = deferred();
  env = {
    USAGE: new MemoryKv(),
    REMOTE_VIEW_VERSION: "1.1.0-bundle-test",
    VAPID_PUBLIC_KEY: base64UrlEncode(await crypto.subtle.exportKey("raw", vapidKeys.publicKey)),
    VAPID_PRIVATE_KEY: privateJwk.d,
    VAPID_SUBJECT: "mailto:test@example.com",
    // Stays pending until the test releases the gate, so a response that arrives
    // first proves the delivery was deferred rather than awaited.
    PUSH_SENDER: async (subscription, message) => {
      pushCalls.push({ subscription, message });
      await gate.promise;
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
  const context = executionContext();
  const response = await bundled.fetch(request, env, context);
  await Promise.all(context.pending);
  return response;
}

test("the bundle defers push delivery instead of holding the PUT response", async () => {
  assert.equal((await run(upload(79))).status, 204);

  const subscription = await browserSubscription(
    "https://fcm.googleapis.com/fcm/send/bundle-subscription",
  );
  assert.equal((await run(new Request(
    `https://viewer.example/u/${READ_ID}/push-subscription`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(subscription),
    },
  ))).status, 204);

  const context = executionContext();
  const pending = bundled.fetch(upload(81), env, context);

  try {
    const settled = await Promise.race([
      pending,
      delay(RESPONSE_BUDGET_MS, TIMED_OUT, { ref: false }),
    ]);
    assert.notEqual(
      settled,
      TIMED_OUT,
      "the crossing PUT was held open by push delivery: the bundle entry must pass " +
        "the execution context to the API",
    );
    assert.equal(settled.status, 204);
    assert.equal(
      context.pending.length,
      1,
      "the delivery must be handed to ctx.waitUntil",
    );
    assert.equal(
      await Promise.race([
        context.pending[0].then(() => "finished"),
        delay(DELIVERY_PROBE_MS, STILL_RUNNING, { ref: false }),
      ]),
      STILL_RUNNING,
      "the response must arrive while the deferred delivery is still running",
    );
  } finally {
    gate.release();
    await Promise.allSettled([pending, ...context.pending]);
  }

  assert.equal(pushCalls.length, 1);
  assert.equal(pushCalls[0].message.alerts[0].windowKey, "session");
  assert.ok(env.USAGE.puts.some((entry) =>
    entry.key.startsWith(`push:${READ_ID}:`) && entry.options?.expirationTtl === 604800));
});

test("the bundle entry still serves the embedded viewer page", async () => {
  const page = await run(new Request("https://viewer.example/"));
  assert.equal(page.status, 200);
  assert.match(page.headers.get("content-type"), /^text\/html/);
  assert.match(page.headers.get("content-security-policy"), /default-src 'none'/);

  const version = await run(new Request("https://viewer.example/version"));
  assert.equal(version.status, 200);
  assert.deepEqual(await version.json(), { version: "1.1.0-bundle-test" });
});
