import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { afterEach, beforeEach, test } from "node:test";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import { createRemoteViewServer, deriveReadId, SnapshotStore } from "./server.mjs";
import { base64UrlEncode } from "../shared/web-push.mjs";

const require = createRequire(import.meta.url);
const {
  describeNotificationError,
  hasEnabledAlertAccounts,
  resolveNotificationControl,
  resolvePercentMode,
} = require("../../web/app.js");

const WRITE_ID = "0123456789abcdef0123456789abcdef";
const READ_ID = "3eb1bd439947eb762998e566ccc2e099";
const PAYLOAD = JSON.stringify({
  version: 2,
  generatedAt: "2026-08-27T12:00:00Z",
  displayMode: "remaining",
  accounts: [{ id: "codex:test", provider: "codex", windows: [] }],
});

let fixture;

beforeEach(async () => {
  const directory = await mkdtemp(join(tmpdir(), "ai-usage-remote-view-"));
  let currentTime = Date.parse("2026-08-27T12:00:00Z");
  const webRoot = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "web");
  const vapidKeys = await crypto.subtle.generateKey(
    { name: "ECDSA", namedCurve: "P-256" },
    true,
    ["sign", "verify"],
  );
  const vapidPrivate = await crypto.subtle.exportKey("jwk", vapidKeys.privateKey);
  const pushCalls = [];
  const app = createRemoteViewServer({
    databasePath: join(directory, "usage.db"),
    webRoot,
    now: () => currentTime,
    ttlMs: 1000,
    cleanupIntervalMs: 0,
    runtimeVersion: "1.1.0-test",
    vapidConfiguration: {
      publicKey: base64UrlEncode(await crypto.subtle.exportKey("raw", vapidKeys.publicKey)),
      privateKey: vapidPrivate.d,
      subject: "mailto:test@example.com",
    },
    pushSender: async (subscription, message) => {
      pushCalls.push({ subscription, message });
      return new Response(null, { status: 201 });
    },
  });
  await new Promise((resolveListen) => app.server.listen(0, "127.0.0.1", resolveListen));
  const address = app.server.address();
  fixture = {
    app,
    directory,
    baseUrl: `http://127.0.0.1:${address.port}`,
    pushCalls,
    advance(milliseconds) {
      currentTime += milliseconds;
    },
  };
});

afterEach(async () => {
  await fixture.app.close();
  await rm(fixture.directory, { recursive: true, force: true });
});

test("derives the protocol v2 read id", () => {
  assert.equal(deriveReadId(WRITE_ID), READ_ID);
});

test("viewer defaults to the desktop percentage preference without a browser override", () => {
  assert.equal(resolvePercentMode(null, "remaining"), "left");
  assert.equal(resolvePercentMode(null, "used"), "used");
  assert.equal(resolvePercentMode("invalid", "remaining"), "left");
});

test("viewer keeps an explicit browser percentage override", () => {
  assert.equal(resolvePercentMode("used", "remaining"), "used");
  assert.equal(resolvePercentMode("left", "used"), "left");
});

test("viewer presents notification actions from the real subscription state", () => {
  assert.deepEqual(resolveNotificationControl({
    alertsConfigured: true,
    permission: "granted",
    pushReady: true,
    subscribed: false,
    supported: true,
  }), {
    disabled: false,
    label: "Enable alerts",
    state: "off",
    testEnabled: false,
    testVisible: false,
    title: "Enable browser alerts on this device.",
    visible: true,
  });

  assert.deepEqual(resolveNotificationControl({
    alertsConfigured: true,
    permission: "granted",
    pushReady: true,
    subscribed: true,
    supported: true,
  }), {
    disabled: false,
    label: "Disable alerts",
    state: "on",
    testEnabled: true,
    testVisible: true,
    title: "Browser alerts are on. Click to turn them off.",
    visible: true,
  });
});

test("viewer explains unavailable push configuration instead of offering a broken action", () => {
  assert.deepEqual(resolveNotificationControl({
    alertsConfigured: true,
    permission: "granted",
    pushReady: false,
    subscribed: false,
    supported: true,
  }), {
    disabled: true,
    label: "Alerts unavailable",
    state: "unavailable",
    testEnabled: false,
    testVisible: false,
    title: "Browser alerts are not configured on this server.",
    visible: true,
  });
  assert.equal(
    describeNotificationError(new Error("push_not_configured")),
    "Browser alerts are not configured on this server.",
  );
  assert.equal(
    describeNotificationError({
      message: "Registration failed - push service not available",
      name: "AbortError",
    }),
    "Browser push is unavailable. Check this browser's notification settings and try again.",
  );
});

test("stores, reads, and deletes an unchanged JSON payload", async () => {
  const put = await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: PAYLOAD,
  });
  assert.equal(put.status, 204);
  assert.equal(put.headers.get("x-read-id"), READ_ID);

  const get = await fetch(`${fixture.baseUrl}/u/${READ_ID}`);
  assert.equal(get.status, 200);
  assert.equal(await get.text(), PAYLOAD);
  assert.equal(get.headers.get("cache-control"), "no-store");

  const secretGet = await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`);
  assert.equal(secretGet.status, 404);

  const remove = await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, { method: "DELETE" });
  assert.equal(remove.status, 204);
  assert.equal(remove.headers.get("x-read-id"), READ_ID);
  assert.equal((await fetch(`${fixture.baseUrl}/u/${READ_ID}`)).status, 404);
});

test("reports the deployed remote-view version", async () => {
  const response = await fetch(`${fixture.baseUrl}/version`);
  assert.equal(response.status, 200);
  assert.equal(response.headers.get("cache-control"), "no-store");
  assert.deepEqual(await response.json(), { version: "1.1.0-test" });
});

test("expires snapshots after their configured TTL", async () => {
  await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: PAYLOAD,
  });
  fixture.advance(1001);

  assert.equal((await fetch(`${fixture.baseUrl}/u/${READ_ID}`)).status, 404);
  assert.equal(fixture.app.store.deleteExpired(Date.parse("2026-08-27T12:00:01.001Z")), 1);
});

test("persists snapshots when SQLite is reopened", () => {
  const databasePath = join(fixture.directory, "persistent.db");
  const expiresAt = Date.parse("2026-08-28T12:00:00Z");
  let store = new SnapshotStore(databasePath);
  store.put(READ_ID, PAYLOAD, expiresAt);
  store.close();

  store = new SnapshotStore(databasePath);
  assert.equal(store.get(READ_ID, expiresAt - 1), PAYLOAD);
  store.close();
});

test("matches worker validation and error responses", async () => {
  const unsupported = await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    body: PAYLOAD,
  });
  assert.equal(unsupported.status, 415);
  assert.deepEqual(await unsupported.json(), { error: "unsupported_media_type" });

  const invalidJson = await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: "{",
  });
  assert.equal(invalidJson.status, 400);
  assert.deepEqual(await invalidJson.json(), { error: "invalid_json" });

  const invalidPayload = await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ version: 2, accounts: "wrong" }),
  });
  assert.equal(invalidPayload.status, 422);
  assert.deepEqual(await invalidPayload.json(), {
    error: "invalid_payload",
    reason: "bad_accounts",
  });

  assert.equal((await fetch(`${fixture.baseUrl}/u/not-an-id`)).status, 400);
  assert.equal((await fetch(`${fixture.baseUrl}/missing`)).status, 404);

  const tooLarge = await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ version: 2, accounts: [], padding: "x".repeat(16 * 1024) }),
  });
  assert.equal(tooLarge.status, 413);
  assert.deepEqual(await tooLarge.json(), { error: "too_large" });
});

test("serves the viewer, same-origin config, demo, and health endpoint", async () => {
  const page = await fetch(`${fixture.baseUrl}/`);
  assert.equal(page.status, 200);
  assert.match(page.headers.get("content-security-policy"), /script-src 'self' 'sha256-/);
  assert.equal(page.headers.get("x-frame-options"), "DENY");
  const pageHtml = await page.text();
  assert.match(pageHtml, /AI Usage/);
  assert.match(
    pageHtml,
    /<h1>AI usage<\/h1>\s*<span class="version-badge" id="remote-version" hidden><\/span>/,
  );
  assert.doesNotMatch(pageHtml, /class="site-footer"/);

  const config = await fetch(`${fixture.baseUrl}/config.js`);
  assert.equal(await config.text(), 'window.REMOTE_VIEW_CONFIG = { apiBase: "" };\n');

  const serviceWorker = await fetch(`${fixture.baseUrl}/sw.js`);
  assert.equal(serviceWorker.headers.get("cache-control"), "no-store");

  const demo = await fetch(`${fixture.baseUrl}/u/demo`);
  assert.equal(demo.status, 200);
  const demoPayload = await demo.json();
  assert.equal(demoPayload.version, 2);
  assert.equal(demoPayload.displayMode, "used");
  assert.equal((await fetch(`${fixture.baseUrl}/u/demo`, { method: "DELETE" })).status, 405);

  const health = await fetch(`${fixture.baseUrl}/health`);
  assert.deepEqual(await health.json(), { status: "ok" });
});

test("viewer exposes browser alerts only when an account opted in", () => {
  assert.equal(hasEnabledAlertAccounts({ accounts: [] }), false);
  assert.equal(hasEnabledAlertAccounts({
    accounts: [{ alert: { enabled: false } }, { alert: { enabled: true, thresholdPercent: 80 } }],
  }), true);
});

test("registers browser subscriptions and pushes only new per-window crossings", async () => {
  const subscriberKeys = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" },
    true,
    ["deriveBits"],
  );
  const subscription = {
    endpoint: "https://push.example.test/subscription-1",
    keys: {
      p256dh: base64UrlEncode(await crypto.subtle.exportKey("raw", subscriberKeys.publicKey)),
      auth: base64UrlEncode(crypto.getRandomValues(new Uint8Array(16))),
    },
  };
  const usage = (session, weekly) => JSON.stringify({
    version: 2,
    generatedAt: "2026-08-27T12:00:00Z",
    displayMode: "remaining",
    accounts: [{
      id: "claude:work",
      provider: "claude",
      name: "Claude Work",
      alert: { enabled: true, thresholdPercent: 80, resetEnabled: true },
      windows: [
        { label: "Session", usedPercent: session },
        { label: "Weekly", usedPercent: weekly },
      ],
    }],
  });

  await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: usage(79, 82),
  });
  const vapid = await fetch(`${fixture.baseUrl}/push/vapid-public-key`);
  assert.equal(vapid.status, 200);
  assert.equal(typeof (await vapid.json()).publicKey, "string");

  const register = await fetch(`${fixture.baseUrl}/u/${READ_ID}/push-subscription`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(subscription),
  });
  assert.equal(register.status, 204);

  await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: usage(81, 83),
  });
  assert.equal(fixture.pushCalls.length, 1);

  await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: usage(0, 0),
  });
  assert.equal(fixture.pushCalls.length, 2);
  assert.equal(fixture.pushCalls[1].message.resets.length, 1);
  assert.equal(fixture.pushCalls[1].message.resets[0].windowKey, "weekly");
  assert.equal(fixture.pushCalls[0].message.displayMode, "remaining");
  assert.equal(fixture.pushCalls[0].message.alerts.length, 1);
  assert.equal(fixture.pushCalls[0].message.alerts[0].windowKey, "session");

  await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: usage(0, 0),
  });
  assert.equal(fixture.pushCalls.length, 2);

  const unregister = await fetch(`${fixture.baseUrl}/u/${READ_ID}/push-subscription`, {
    method: "DELETE",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ endpoint: subscription.endpoint }),
  });
  assert.equal(unregister.status, 204);
  assert.deepEqual(fixture.app.store.listSubscriptions(READ_ID), []);
});

test("deleting a remote view also removes its browser subscriptions", async () => {
  await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: PAYLOAD,
  });
  const subscriberKeys = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" },
    true,
    ["deriveBits"],
  );
  const subscription = {
    endpoint: "https://push.example.test/subscription-delete",
    keys: {
      p256dh: base64UrlEncode(await crypto.subtle.exportKey("raw", subscriberKeys.publicKey)),
      auth: base64UrlEncode(crypto.getRandomValues(new Uint8Array(16))),
    },
  };
  await fetch(`${fixture.baseUrl}/u/${READ_ID}/push-subscription`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(subscription),
  });

  await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, { method: "DELETE" });

  assert.deepEqual(fixture.app.store.listSubscriptions(READ_ID), []);
});
