import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { afterEach, beforeEach, test } from "node:test";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import {
  createRemoteViewServer,
  deriveReadId,
  HEALTH_WRITE_PROBE_INTERVAL_MS,
  readAllowedEndpointHosts,
  SnapshotStore,
} from "./server.mjs";
import {
  base64UrlEncode,
  sendWebPush,
  validatePushSubscription,
} from "../shared/web-push.mjs";

const require = createRequire(import.meta.url);
const {
  resetExpiryInfo,
  resetExpiryNotice,
  describeNotificationError,
  hasEnabledAlertAccounts,
  resolveNotificationControl,
  resolvePercentMode,
} = require("../../web/app.js");

test("reset expiry uses calendar days and only warns about unexpired resets through day seven", () => {
  const now = new Date(2026, 8, 17, 12).getTime();
  const date = (days) => new Date(2026, 8, 17 + days, 12).toISOString();
  assert.equal(resetExpiryInfo(date(7), now).text, "7 days left");
  assert.equal(resetExpiryInfo(date(0), now).text, "Expired");
  assert.equal(resetExpiryInfo(new Date(now + 60000).toISOString(), now).text, "0 days left, expires today");
  assert.equal(resetExpiryInfo("invalid", now), null);
  assert.equal(resetExpiryInfo(null, now), null);
  assert.equal(resetExpiryNotice({ available: 5, credits: [-1, 0, 1, 7, 8].map(days => ({ expiresAt: date(days) })) }, now), "2 resets expire within 7 days.");
  assert.equal(resetExpiryNotice({ available: 2, expiresAt: date(7) }, now), "1 reset expires within 7 days.");
  assert.equal(resetExpiryNotice({ available: 2, credits: [] }, now), "");
  assert.equal(resetExpiryNotice({ available: 0, expiresAt: date(1) }, now), "");
});

const WRITE_ID = "0123456789abcdef0123456789abcdef";
const READ_ID = "3eb1bd439947eb762998e566ccc2e099";
const PAYLOAD = JSON.stringify({
  version: 2,
  generatedAt: "2026-08-27T12:00:00Z",
  displayMode: "remaining",
  accounts: [{ id: "codex:test", provider: "codex", windows: [] }],
});

const WEB_ROOT = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "web");

let fixture;

// A subscription that looks like the one a browser hands to the viewer.
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
  const directory = await mkdtemp(join(tmpdir(), "ai-usage-remote-view-"));
  let currentTime = Date.parse("2026-08-27T12:00:00Z");
  const webRoot = WEB_ROOT;
  const vapidKeys = await crypto.subtle.generateKey(
    { name: "ECDSA", namedCurve: "P-256" },
    true,
    ["sign", "verify"],
  );
  const vapidPrivate = await crypto.subtle.exportKey("jwk", vapidKeys.privateKey);
  const vapidConfiguration = {
    publicKey: base64UrlEncode(await crypto.subtle.exportKey("raw", vapidKeys.publicKey)),
    privateKey: vapidPrivate.d,
    subject: "mailto:test@example.com",
  };
  const pushCalls = [];
  const app = createRemoteViewServer({
    databasePath: join(directory, "usage.db"),
    webRoot,
    now: () => currentTime,
    ttlMs: 1000,
    cleanupIntervalMs: 0,
    runtimeVersion: "1.1.0-test",
    vapidConfiguration,
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
    vapidConfiguration,
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
  assert.match(page.headers.get("cache-control"), /\bno-transform\b/);
  assert.match(page.headers.get("content-security-policy"), /script-src 'self' 'sha256-/);
  assert.equal(page.headers.get("x-frame-options"), "DENY");
  const pageHtml = await page.text();
  assert.match(pageHtml, /AI Usage/);
  assert.match(
    pageHtml,
    /<h1>AI usage<\/h1>\s*<span class="version-badge" id="remote-version" hidden><\/span>/,
  );
  assert.doesNotMatch(pageHtml, /class="site-footer"/);

  // Page assets must be served by this container, including any future fonts.
  for (const tag of pageHtml.matchAll(/<(?:script|link)\b[^>]*>/gi)) {
    const reference = tag[0].match(/(?:src|href)="([^"]+)"/i)?.[1];
    if (!reference) continue;
    const resource = new URL(reference, fixture.baseUrl);
    assert.equal(resource.origin, fixture.baseUrl);
    assert.equal((await fetch(resource)).status, 200);
  }
  const stylesheet = await (await fetch(`${fixture.baseUrl}/styles.css`)).text();
  assert.doesNotMatch(stylesheet, /@import\b/i);
  for (const match of stylesheet.matchAll(/url\(\s*["']?([^"')\s]+)/gi)) {
    assert.equal(new URL(match[1], fixture.baseUrl).origin, fixture.baseUrl);
  }

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
  assert.equal(health.status, 200);
  assert.deepEqual(await health.json(), { status: "ok" });
});

test("health reports degraded while the database cannot be written", async () => {
  const store = fixture.app.store;
  const canWrite = store.canWrite.bind(store);
  store.canWrite = () => false;
  try {
    const degraded = await fetch(`${fixture.baseUrl}/health`);
    assert.equal(degraded.status, 503);
    assert.deepEqual(await degraded.json(), { status: "degraded" });
  } finally {
    store.canWrite = canWrite;
  }

  // The probe result is reused for a few seconds, so recovery needs a new window.
  fixture.advance(HEALTH_WRITE_PROBE_INTERVAL_MS + 1);
  const recovered = await fetch(`${fixture.baseUrl}/health`);
  assert.equal(recovered.status, 200);
  assert.deepEqual(await recovered.json(), { status: "ok" });

  // A public endpoint must not turn a request flood into a transaction flood.
  let probes = 0;
  store.canWrite = (at) => {
    probes += 1;
    return canWrite(at);
  };
  try {
    assert.equal((await fetch(`${fixture.baseUrl}/health`)).status, 200);
    assert.equal(probes, 0, "expected the recent probe result to be reused");
    fixture.advance(HEALTH_WRITE_PROBE_INTERVAL_MS + 1);
    assert.equal((await fetch(`${fixture.baseUrl}/health`)).status, 200);
    assert.equal(probes, 1);
  } finally {
    store.canWrite = canWrite;
  }
});

test("periodic cleanup sweeps expired rows and compacts the database", async () => {
  const directory = await mkdtemp(join(tmpdir(), "ai-usage-cleanup-"));
  let currentTime = Date.parse("2026-08-27T12:00:00Z");
  const app = createRemoteViewServer({
    databasePath: join(directory, "usage.db"),
    webRoot: WEB_ROOT,
    now: () => currentTime,
    ttlMs: 1000,
    cleanupIntervalMs: 10,
  });
  const compactCalls = [];
  const compact = app.store.compact.bind(app.store);
  app.store.compact = () => {
    const result = compact();
    compactCalls.push(result);
    return result;
  };
  try {
    app.store.put(READ_ID, PAYLOAD, currentTime + 1000);
    currentTime += 5000;
    while (compactCalls.length === 0) {
      await new Promise((resolveWait) => setTimeout(resolveWait, 10));
    }
    assert.equal(app.store.get(READ_ID, currentTime), null);
    assert.equal(compactCalls[0].checkpointed, true);
    assert.equal(compactCalls[0].vacuumed, false);
  } finally {
    await app.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test("viewer exposes browser alerts only when an account opted in", () => {
  assert.equal(hasEnabledAlertAccounts({ accounts: [] }), false);
  assert.equal(hasEnabledAlertAccounts({
    accounts: [{ alert: { enabled: false } }, { alert: { enabled: true, thresholdPercent: 80 } }],
  }), true);
});

test("registers browser subscriptions and pushes only new per-window crossings", async () => {
  const subscription = await browserSubscription("https://fcm.googleapis.com/fcm/send/subscription-1");
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
  const subscription = await browserSubscription(
    "https://web.push.apple.com/subscription-delete",
  );
  await fetch(`${fixture.baseUrl}/u/${READ_ID}/push-subscription`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(subscription),
  });

  await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, { method: "DELETE" });

  assert.deepEqual(fixture.app.store.listSubscriptions(READ_ID), []);
});

test("accepts the real browser push services and no other endpoint", async () => {
  const allowed = [
    "https://fcm.googleapis.com/fcm/send/cxyDU4KHb1Y:APA91bF",
    "https://updates.push.services.mozilla.com/wpush/v2/gAAAAABm9",
    "https://web.push.apple.com/QN6ZQmS6ZiQpV0bHm5cVPA",
    "https://sin.notify.windows.com/w/?token=BQYAAAB",
  ];
  for (const endpoint of allowed) {
    assert.equal(validatePushSubscription(await browserSubscription(endpoint)), true, endpoint);
  }

  const refused = [
    "https://169.254.169.254/x",
    "https://127.0.0.1/x",
    "https://[::1]/x",
    "https://nas.lan:8080/x",
    "https://evil.example/x",
    // An allowlisted host on another port would still reach a different service.
    "https://fcm.googleapis.com:8443/x",
    // Only subdomains of the wildcard entries, never a look-alike domain.
    "https://push.apple.com.evil.example/x",
    "https://evil-push.apple.com/x",
    "http://fcm.googleapis.com/x",
  ];
  for (const endpoint of refused) {
    assert.equal(validatePushSubscription(await browserSubscription(endpoint)), false, endpoint);
  }

  // Host matching ignores case and a trailing root dot.
  assert.equal(
    validatePushSubscription(await browserSubscription("https://FCM.GoogleAPIs.com./fcm/send/x")),
    true,
  );
});

test("refuses to register a subscription on an unlisted endpoint host", async () => {
  await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: PAYLOAD,
  });

  const response = await fetch(`${fixture.baseUrl}/u/${READ_ID}/push-subscription`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(await browserSubscription("https://169.254.169.254/latest/meta-data")),
  });

  assert.equal(response.status, 422);
  assert.deepEqual(await response.json(), { error: "invalid_subscription" });
  assert.deepEqual(fixture.app.store.listSubscriptions(READ_ID), []);
});

test("drops a stored subscription whose endpoint host is no longer allowed", async () => {
  const usage = (session) => JSON.stringify({
    version: 2,
    generatedAt: "2026-08-27T12:00:00Z",
    accounts: [{
      id: "claude:work",
      provider: "claude",
      alert: { enabled: true, thresholdPercent: 80 },
      windows: [{ label: "Session", usedPercent: session }],
    }],
  });
  await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: usage(79),
  });
  // Rows an older build accepted are not trusted at delivery time.
  fixture.app.store.putSubscription(
    READ_ID,
    await browserSubscription("https://169.254.169.254/latest/meta-data"),
  );

  await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: usage(81),
  });

  assert.equal(fixture.pushCalls.length, 0);
  assert.deepEqual(fixture.app.store.listSubscriptions(READ_ID), []);
});

test("PUSH_ENDPOINT_ALLOWED_HOSTS lets an operator name their own push host", async () => {
  const subscription = await browserSubscription("https://push.example.test/operator-endpoint");
  const register = (baseUrl) => fetch(`${baseUrl}/u/${READ_ID}/push-subscription`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(subscription),
  });
  const upload = (baseUrl) => fetch(`${baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: PAYLOAD,
  });

  await upload(fixture.baseUrl);
  assert.equal((await register(fixture.baseUrl)).status, 422);

  const previous = process.env.PUSH_ENDPOINT_ALLOWED_HOSTS;
  process.env.PUSH_ENDPOINT_ALLOWED_HOSTS = "push.example.test, fcm.googleapis.com";
  const configured = createRemoteViewServer({
    databasePath: join(fixture.directory, "configured.db"),
    webRoot: WEB_ROOT,
    cleanupIntervalMs: 0,
    vapidConfiguration: fixture.vapidConfiguration,
    allowedEndpointHosts: readAllowedEndpointHosts(),
    pushSender: async () => new Response(null, { status: 201 }),
  });
  try {
    await new Promise((listening) => configured.server.listen(0, "127.0.0.1", listening));
    const baseUrl = `http://127.0.0.1:${configured.server.address().port}`;
    await upload(baseUrl);

    assert.equal((await register(baseUrl)).status, 204);
    assert.deepEqual(
      configured.store.listSubscriptions(READ_ID).map((entry) => entry.endpoint),
      [subscription.endpoint],
    );
    assert.equal(
      (await fetch(`${baseUrl}/u/${READ_ID}/push-subscription`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(await browserSubscription("https://evil.example/x")),
      })).status,
      422,
    );
  } finally {
    await configured.close();
    if (previous === undefined) delete process.env.PUSH_ENDPOINT_ALLOWED_HOSTS;
    else process.env.PUSH_ENDPOINT_ALLOWED_HOSTS = previous;
  }
});

test("web push delivery refuses redirects, times out, and stays on allowed hosts", async () => {
  const requests = [];
  const fakeFetch = async (url, options) => {
    requests.push({ url, options });
    return new Response(null, { status: 201 });
  };

  const response = await sendWebPush(
    await browserSubscription("https://fcm.googleapis.com/fcm/send/redirect-policy"),
    { type: "usage-alerts" },
    fixture.vapidConfiguration,
    fakeFetch,
  );

  assert.equal(response.status, 201);
  assert.equal(requests[0].options.redirect, "error");
  assert.ok(requests[0].options.signal instanceof AbortSignal);

  await assert.rejects(
    sendWebPush(
      await browserSubscription("https://evil.example/x"),
      { type: "usage-alerts" },
      fixture.vapidConfiguration,
      fakeFetch,
    ),
    /invalid_subscription/,
  );
  await assert.doesNotReject(sendWebPush(
    await browserSubscription("https://push.example.test/operator-endpoint"),
    { type: "usage-alerts" },
    fixture.vapidConfiguration,
    fakeFetch,
    Date.now(),
    { allowedEndpointHosts: ["push.example.test"] },
  ));
  assert.equal(requests.length, 2);
});
