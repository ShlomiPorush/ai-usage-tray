import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { afterEach, beforeEach, test } from "node:test";
import { fileURLToPath } from "node:url";
import { createRemoteViewServer, deriveReadId, SnapshotStore } from "./server.mjs";

const WRITE_ID = "0123456789abcdef0123456789abcdef";
const READ_ID = "3eb1bd439947eb762998e566ccc2e099";
const PAYLOAD = JSON.stringify({
  version: 2,
  generatedAt: "2026-08-27T12:00:00Z",
  accounts: [{ id: "codex:test", provider: "codex", windows: [] }],
});

let fixture;

beforeEach(async () => {
  const directory = await mkdtemp(join(tmpdir(), "ai-usage-remote-view-"));
  let currentTime = Date.parse("2026-08-27T12:00:00Z");
  const webRoot = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "web");
  const app = createRemoteViewServer({
    databasePath: join(directory, "usage.db"),
    webRoot,
    now: () => currentTime,
    ttlMs: 1000,
    cleanupIntervalMs: 0,
  });
  await new Promise((resolveListen) => app.server.listen(0, "127.0.0.1", resolveListen));
  const address = app.server.address();
  fixture = {
    app,
    directory,
    baseUrl: `http://127.0.0.1:${address.port}`,
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
  assert.match(await page.text(), /AI Usage/);

  const config = await fetch(`${fixture.baseUrl}/config.js`);
  assert.equal(await config.text(), 'window.REMOTE_VIEW_CONFIG = { apiBase: "" };\n');

  const serviceWorker = await fetch(`${fixture.baseUrl}/sw.js`);
  assert.equal(serviceWorker.headers.get("cache-control"), "no-store");

  const demo = await fetch(`${fixture.baseUrl}/u/demo`);
  assert.equal(demo.status, 200);
  assert.equal((await demo.json()).version, 2);
  assert.equal((await fetch(`${fixture.baseUrl}/u/demo`, { method: "DELETE" })).status, 405);

  const health = await fetch(`${fixture.baseUrl}/health`);
  assert.deepEqual(await health.json(), { status: "ok" });
});
