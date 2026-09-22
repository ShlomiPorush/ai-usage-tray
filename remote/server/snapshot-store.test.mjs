import assert from "node:assert/strict";
import { mkdtemp, rm, stat } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import {
  JOURNAL_SIZE_LIMIT_BYTES,
  resolveVapidKeyPath,
  shouldVacuum,
  SnapshotStore,
  VACUUM_MIN_DATABASE_BYTES,
} from "./server.mjs";

// The probes log the SQLite error, which is noise when the test causes it.
async function withoutErrorLog(body) {
  const original = console.error;
  console.error = () => {};
  try {
    return await body();
  } finally {
    console.error = original;
  }
}

async function withStore(body) {
  const directory = await mkdtemp(join(tmpdir(), "ai-usage-store-"));
  const databasePath = join(directory, "usage.db");
  const store = new SnapshotStore(databasePath);
  try {
    return await body({ store, databasePath, directory });
  } finally {
    try {
      store.close();
    } catch {
      // Already closed by the test.
    }
    await rm(directory, { recursive: true, force: true });
  }
}

function snapshotPayload(index) {
  return JSON.stringify({
    version: 2,
    generatedAt: "2026-08-27T12:00:00Z",
    accounts: [{ id: `codex:${index}`, provider: "codex", windows: [] }],
  });
}

test("bounds the write-ahead log with journal_size_limit", async () => {
  await withStore(({ store }) => {
    assert.equal(store.readPragma("journal_size_limit"), JOURNAL_SIZE_LIMIT_BYTES);
    assert.equal(store.database.prepare("PRAGMA journal_mode").get().journal_mode, "wal");
  });
});

test("compact truncates the write-ahead log after an expiry sweep", async () => {
  await withStore(async ({ store, databasePath }) => {
    for (let index = 0; index < 500; index += 1) {
      store.put(`${index}`.padStart(32, "0"), snapshotPayload(index), 1000);
    }
    const walPath = `${databasePath}-wal`;
    assert.ok((await stat(walPath)).size > 0, "expected writes to grow the log");

    assert.equal(store.deleteExpired(2000), 500);
    const result = store.compact();

    assert.equal(result.checkpointed, true);
    assert.equal(result.vacuumed, false, "a small database must not be rewritten");
    assert.equal((await stat(walPath)).size, 0);
  });
});

test("vacuum runs only for a large, mostly free database", () => {
  const pageSize = 4096;
  const largePages = Math.ceil((VACUUM_MIN_DATABASE_BYTES * 2) / pageSize);

  assert.equal(
    shouldVacuum({
      freelistCount: Math.floor(largePages * 0.6),
      pageCount: largePages,
      databaseBytes: largePages * pageSize,
    }),
    true,
  );
  // Mostly free, but too small for the rewrite to be worth it.
  assert.equal(
    shouldVacuum({ freelistCount: 900, pageCount: 1000, databaseBytes: 4_096_000 }),
    false,
  );
  // Large, but barely any space to reclaim.
  assert.equal(
    shouldVacuum({
      freelistCount: Math.floor(largePages * 0.4),
      pageCount: largePages,
      databaseBytes: largePages * pageSize,
    }),
    false,
  );
  assert.equal(shouldVacuum({ freelistCount: 0, pageCount: 0, databaseBytes: 0 }), false);
  assert.equal(shouldVacuum(), false);
});

test("space statistics describe the database file", async () => {
  await withStore(({ store }) => {
    const stats = store.spaceStats();
    assert.ok(stats.pageCount > 0);
    assert.ok(stats.databaseBytes >= stats.pageCount);
    assert.equal(shouldVacuum(stats), false);
  });
});

test("health probes report a database that can no longer be used", async () => {
  await withStore(async ({ store }) => {
    assert.equal(store.ping(), true);
    assert.equal(store.canWrite(1000), true);

    await withoutErrorLog(() => {
      store.writeProbeStatement = {
        run() {
          throw new Error("database or disk is full");
        },
      };
      assert.equal(store.canWrite(1000), false, "a failing write must not report healthy");
      assert.equal(store.ping(), true, "reads keep working when the disk is full");

      store.close();
      assert.equal(store.ping(), false);
    });
  });
});

test("resolves the VAPID key path from the environment", () => {
  assert.equal(
    resolveVapidKeyPath("/data/usage.db", {}),
    join("/data", "vapid.json"),
  );
  assert.equal(resolveVapidKeyPath(":memory:", {}), null);
  assert.equal(
    resolveVapidKeyPath("/data/usage.db", { VAPID_KEY_PATH: "/secrets/vapid.json" }),
    "/secrets/vapid.json",
  );
  assert.equal(
    resolveVapidKeyPath(":memory:", { VAPID_KEY_PATH: "/secrets/vapid.json" }),
    "/secrets/vapid.json",
  );
  assert.equal(
    resolveVapidKeyPath("/data/usage.db", { VAPID_KEY_PATH: "   " }),
    join("/data", "vapid.json"),
  );
  assert.throws(
    () => resolveVapidKeyPath("/data/usage.db", { VAPID_KEY_PATH: "secrets/vapid.json" }),
    /must be an absolute path/,
  );
});
