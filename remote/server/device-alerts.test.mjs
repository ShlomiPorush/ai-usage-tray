import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { afterEach, beforeEach, test } from "node:test";
import { fileURLToPath } from "node:url";
import { createRemoteViewServer, SnapshotStore } from "./server.mjs";
import { base64UrlEncode } from "../shared/web-push.mjs";

// Browser alerts that follow the device's own preferences instead of the
// desktop's account.alert (relay 1.10.0).

const WEB_ROOT = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "web");
const WRITE_ID = "0123456789abcdef0123456789abcdef";
const READ_ID = "3eb1bd439947eb762998e566ccc2e099";
const OTHER_WRITE_ID = "fedcba9876543210fedcba9876543210";

let fixture;

async function browserSubscription(endpoint) {
  const keys = await crypto.subtle.generateKey({ name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"]);
  return {
    endpoint,
    keys: {
      p256dh: base64UrlEncode(await crypto.subtle.exportKey("raw", keys.publicKey)),
      auth: base64UrlEncode(crypto.getRandomValues(new Uint8Array(16))),
    },
  };
}

// Two accounts: the desktop has alerts on only for "claude:work" at 80%.
function snapshot(claude, codex) {
  return JSON.stringify({
    version: 2,
    generatedAt: "2026-08-27T12:00:00Z",
    accounts: [
      {
        id: "claude:work",
        provider: "claude",
        name: "Claude Work",
        alert: { enabled: true, thresholdPercent: 80, resetEnabled: false },
        windows: [{ label: "Session", usedPercent: claude }],
      },
      {
        id: "codex:home",
        provider: "codex",
        name: "Codex Home",
        alert: { enabled: false, thresholdPercent: 90, resetEnabled: false },
        windows: [{ label: "Session", usedPercent: codex }],
      },
    ],
  });
}

function preferences(accounts, resetAlerts = true) {
  return { accounts, resetAlerts };
}

beforeEach(async () => {
  const directory = await mkdtemp(join(tmpdir(), "ai-usage-device-alerts-"));
  const vapidKeys = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
  const vapidConfiguration = {
    publicKey: base64UrlEncode(await crypto.subtle.exportKey("raw", vapidKeys.publicKey)),
    privateKey: (await crypto.subtle.exportKey("jwk", vapidKeys.privateKey)).d,
    subject: "mailto:test@example.com",
  };
  const pushCalls = [];
  const app = createRemoteViewServer({
    databasePath: join(directory, "usage.db"),
    webRoot: WEB_ROOT,
    now: () => Date.parse("2026-08-27T12:00:00Z"),
    cleanupIntervalMs: 0,
    vapidConfiguration,
    pushSender: async (subscription, message) => {
      pushCalls.push({ subscription, message });
      return new Response(null, { status: 201 });
    },
  });
  await new Promise((listening) => app.server.listen(0, "127.0.0.1", listening));
  fixture = { app, directory, pushCalls, baseUrl: `http://127.0.0.1:${app.server.address().port}` };
});

afterEach(async () => {
  await fixture.app.close();
  await rm(fixture.directory, { recursive: true, force: true });
});

function upload(body, writeId = WRITE_ID) {
  return fetch(`${fixture.baseUrl}/u/${writeId}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body,
  });
}

function register(body, readId = READ_ID) {
  return fetch(`${fixture.baseUrl}/u/${readId}/push-subscription`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}

function storedPreferences(endpoint, readId = READ_ID) {
  const entry = fixture.app.store.listSubscriptionEntries(readId)
    .find((candidate) => candidate.subscription.endpoint === endpoint);
  if (entry === undefined) return undefined;
  // The store hands out null-prototype account maps; compare them as JSON.
  return entry.preferences === null ? null : JSON.parse(JSON.stringify(entry.preferences));
}

test("the relay advertises device preferences next to its push key", async () => {
  const response = await fetch(`${fixture.baseUrl}/push/vapid-public-key`);
  assert.equal(response.status, 200);
  assert.equal((await response.json()).devicePreferences, true);
});

test("a bare subscription from an older page follows the desktop and keeps its 204", async () => {
  await upload(snapshot(10, 10));
  const subscription = await browserSubscription("https://fcm.googleapis.com/fcm/send/bare");
  const response = await register(subscription);
  assert.equal(response.status, 204);
  assert.equal(storedPreferences(subscription.endpoint), null);
});

test("the page's association creates a device row with the defaults and never downgrades one", async () => {
  await upload(snapshot(10, 10));
  const subscription = await browserSubscription("https://fcm.googleapis.com/fcm/send/associate");

  const created = await register({ subscription });
  assert.equal(created.status, 200);
  const body = await created.json();
  assert.equal(body.mode, "device");
  assert.deepEqual(body.preferences, { accounts: {}, resetAlerts: true });

  // A later bare refresh of the same endpoint keeps device mode.
  assert.equal((await register(subscription)).status, 204);
  assert.notEqual(storedPreferences(subscription.endpoint), null);

  // The association of a desktop row leaves it on the desktop's rules.
  const legacy = await browserSubscription("https://fcm.googleapis.com/fcm/send/legacy");
  await register(legacy);
  const associated = await register({ subscription: legacy });
  assert.equal(associated.status, 200);
  assert.deepEqual(await associated.json(), { mode: "desktop", preferences: null, revision: 0 });
});

test("a save stores the preferences, survives refreshes, and an older revision never overwrites it", async () => {
  await upload(snapshot(10, 10));
  const subscription = await browserSubscription("https://fcm.googleapis.com/fcm/send/save");
  await register(subscription);

  const chosen = preferences({ "claude:work": { enabled: true, thresholdPercent: 75 } }, false);
  const saved = await register({ subscription, preferences: chosen, revision: 2000 });
  assert.equal(saved.status, 200);
  assert.deepEqual(await saved.json(), { mode: "device", preferences: chosen, revision: 2000 });

  await register(subscription);
  await register({ subscription });
  assert.deepEqual(storedPreferences(subscription.endpoint), chosen);

  // A retry that left the page before the save above arrives late.
  const late = await register({
    subscription,
    preferences: preferences({ "claude:work": { enabled: true, thresholdPercent: 90 } }),
    revision: 1000,
  });
  assert.equal(late.status, 200);
  assert.deepEqual((await late.json()).preferences, chosen);
  assert.deepEqual(storedPreferences(subscription.endpoint), chosen);
});

test("moving an endpoint to another view drops the old view's preferences", async () => {
  await upload(snapshot(10, 10));
  const otherReadId = (await upload(snapshot(10, 10), OTHER_WRITE_ID)).headers.get("X-Read-Id");
  const subscription = await browserSubscription("https://fcm.googleapis.com/fcm/send/move");
  await register({
    subscription,
    preferences: preferences({ "claude:work": { enabled: false, thresholdPercent: 50 } }),
    revision: 5,
  });

  const moved = await register({ subscription }, otherReadId);
  assert.deepEqual(await moved.json(), { mode: "device", preferences: { accounts: {}, resetAlerts: true }, revision: 0 });
  assert.equal(storedPreferences(subscription.endpoint), undefined);

  // Back with an old page: the desktop's rules, not the first view's choice.
  await register(subscription, READ_ID);
  assert.equal(storedPreferences(subscription.endpoint), null);
});

test("malformed preferences and unknown fields are refused", async () => {
  await upload(snapshot(10, 10));
  const subscription = await browserSubscription("https://fcm.googleapis.com/fcm/send/invalid");
  const valid = preferences({ "claude:work": { enabled: true, thresholdPercent: 80 } });
  const cases = [
    [{ subscription, preferences: valid }, "invalid_preferences"],
    [{ subscription, revision: 3 }, "invalid_preferences"],
    [{ subscription, preferences: valid, revision: 0 }, "invalid_preferences"],
    [{ subscription, preferences: valid, revision: 1.5 }, "invalid_preferences"],
    [{ subscription, preferences: { ...valid, extra: true }, revision: 3 }, "invalid_preferences"],
    [{ subscription, preferences: preferences({ a: { enabled: true, thresholdPercent: 0 } }), revision: 3 }, "invalid_preferences"],
    [{ subscription, preferences: preferences({ a: { enabled: "yes", thresholdPercent: 50 } }), revision: 3 }, "invalid_preferences"],
    [{ subscription, preferences: preferences({ "": { enabled: true, thresholdPercent: 50 } }), revision: 3 }, "invalid_preferences"],
    [{ subscription, preferences: preferences({ A: { enabled: true, thresholdPercent: 50 }, a: { enabled: true, thresholdPercent: 60 } }), revision: 3 }, "invalid_preferences"],
    [{ subscription, preferences: preferences(Object.fromEntries(
      Array.from({ length: 33 }, (_, index) => [`account-${index}`, { enabled: true, thresholdPercent: 50 }]),
    )), revision: 3 }, "invalid_preferences"],
    [{ subscription, mode: "device" }, "invalid_subscription"],
    [{ subscription: { endpoint: "https://evil.example/x", keys: subscription.keys } }, "invalid_subscription"],
  ];
  for (const [body, error] of cases) {
    const response = await register(body);
    assert.equal(response.status, 422, JSON.stringify(body).slice(0, 120));
    assert.deepEqual(await response.json(), { error });
  }
  assert.deepEqual(fixture.app.store.listSubscriptions(READ_ID), []);
});

test("an account id like __proto__ is an ordinary entry", async () => {
  await upload(snapshot(10, 10));
  const subscription = await browserSubscription("https://fcm.googleapis.com/fcm/send/proto");
  const response = await register({
    subscription,
    preferences: JSON.parse('{"accounts":{"__proto__":{"enabled":false,"thresholdPercent":40}},"resetAlerts":true}'),
    revision: 7,
  });
  assert.equal(response.status, 200);
  const stored = storedPreferences(subscription.endpoint);
  assert.deepEqual(Object.keys(stored.accounts), ["__proto__"]);
  assert.equal(stored.accounts.__proto__.thresholdPercent, 40);
});

test("each subscription gets the alerts of its own rules", async () => {
  await upload(snapshot(10, 10));
  const desktop = await browserSubscription("https://fcm.googleapis.com/fcm/send/desktop");
  const defaults = await browserSubscription("https://fcm.googleapis.com/fcm/send/defaults");
  const custom = await browserSubscription("https://fcm.googleapis.com/fcm/send/custom");
  const muted = await browserSubscription("https://fcm.googleapis.com/fcm/send/muted");
  await register(desktop);
  await register({ subscription: defaults });
  await register({
    subscription: custom,
    preferences: preferences({
      "claude:work": { enabled: true, thresholdPercent: 50 },
      "codex:home": { enabled: true, thresholdPercent: 60 },
    }),
    revision: 1,
  });
  await register({
    subscription: muted,
    preferences: preferences({
      "claude:work": { enabled: false, thresholdPercent: 50 },
      "codex:home": { enabled: false, thresholdPercent: 50 },
    }),
    revision: 1,
  });

  // Claude 10 → 85, Codex 10 → 95.
  await upload(snapshot(85, 95));

  const alertsFor = (subscription) => fixture.pushCalls
    .filter((call) => call.subscription.endpoint === subscription.endpoint)
    .flatMap((call) => call.message.alerts.map((alert) => `${alert.accountId}@${alert.thresholdPercent}`))
    .sort();
  // Desktop: only Claude, at the desktop's 80%.
  assert.deepEqual(alertsFor(desktop), ["claude:work@80"]);
  // Defaults: every account at 90%; Claude at 85% has not reached it.
  assert.deepEqual(alertsFor(defaults), ["codex:home@90"]);
  assert.deepEqual(alertsFor(custom), ["claude:work@50", "codex:home@60"]);
  assert.deepEqual(alertsFor(muted), []);
});

test("an account added after the device saved alerts at 90%", async () => {
  await upload(snapshot(10, 10));
  const subscription = await browserSubscription("https://fcm.googleapis.com/fcm/send/new-account");
  await register({
    subscription,
    preferences: preferences({ "claude:work": { enabled: false, thresholdPercent: 50 } }),
    revision: 1,
  });
  await upload(snapshot(95, 95));
  assert.deepEqual(
    fixture.pushCalls.flatMap((call) => call.message.alerts.map((alert) => alert.accountId)),
    ["codex:home"],
  );
});

test("a database from before device preferences opens with its rows on the desktop's rules", () => {
  const databasePath = join(fixture.directory, "old.db");
  const old = new DatabaseSync(databasePath);
  old.exec(`
    CREATE TABLE push_subscriptions (
      endpoint TEXT PRIMARY KEY,
      read_id TEXT NOT NULL,
      subscription TEXT NOT NULL
    ) STRICT;
  `);
  const subscription = { endpoint: "https://fcm.googleapis.com/fcm/send/old", keys: { p256dh: "x", auth: "y" } };
  old.prepare("INSERT INTO push_subscriptions VALUES (?, ?, ?)")
    .run(subscription.endpoint, READ_ID, JSON.stringify(subscription));
  old.close();

  let store = new SnapshotStore(databasePath);
  assert.deepEqual(store.listSubscriptionEntries(READ_ID), [{ subscription, preferences: null }]);
  store.close();
  // Opening again is a no-op migration.
  store = new SnapshotStore(databasePath);
  assert.deepEqual(store.listSubscriptionEntries(READ_ID), [{ subscription, preferences: null }]);
  store.close();
});

test("an account id that grows when lowercased is refused, not stored and then dropped", async () => {
  await upload(snapshot(10, 10));
  const subscription = await browserSubscription("https://fcm.googleapis.com/fcm/send/unicode");
  const response = await register({
    subscription,
    preferences: preferences({ ["İ".repeat(128)]: { enabled: false, thresholdPercent: 50 } }),
    revision: 1,
  });
  assert.equal(response.status, 422);
  assert.deepEqual(await response.json(), { error: "invalid_preferences" });
});

test("a revision far ahead of the relay's clock is refused, so it can never outrank later saves", async () => {
  await upload(snapshot(10, 10));
  const subscription = await browserSubscription("https://fcm.googleapis.com/fcm/send/revision");
  const rule = (thresholdPercent) => preferences({ "claude:work": { enabled: true, thresholdPercent } });
  const now = Date.parse("2026-08-27T12:00:00Z");
  const DAY = 24 * 60 * 60 * 1000;

  const ahead = await register({ subscription, preferences: rule(60), revision: now + DAY + 1 });
  assert.equal(ahead.status, 422);
  assert.deepEqual(await ahead.json(), { error: "clock_skew" });
  assert.equal(storedPreferences(subscription.endpoint), undefined);

  // Up to a day ahead is accepted and ordered normally.
  const slightlyAhead = await register({ subscription, preferences: rule(60), revision: now + 20 * 60 * 60 * 1000 });
  assert.equal(slightlyAhead.status, 200);
  // A save from a corrected clock is ignored, and the answer carries the
  // stored revision, which the viewer builds its next save on.
  const corrected = await register({ subscription, preferences: rule(70), revision: now });
  const body = await corrected.json();
  assert.equal(body.preferences.accounts["claude:work"].thresholdPercent, 60);
  assert.equal(body.revision, now + 20 * 60 * 60 * 1000);
  const next = await register({ subscription, preferences: rule(70), revision: body.revision + 1 });
  assert.equal((await next.json()).preferences.accounts["claude:work"].thresholdPercent, 70);

  // A late retry of the earlier choice stays out.
  const late = await register({ subscription, preferences: rule(95), revision: now + 10 * 60 * 60 * 1000 });
  assert.equal((await late.json()).preferences.accounts["claude:work"].thresholdPercent, 70);
});
