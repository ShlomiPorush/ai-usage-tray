import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, stat } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { ensureVapidConfiguration } from "./vapid-configuration.mjs";
import { validateVapidConfiguration } from "../shared/web-push.mjs";

test("generates and reuses a persistent VAPID configuration", async () => {
  const directory = await mkdtemp(join(tmpdir(), "ai-usage-vapid-"));
  const path = join(directory, "vapid.json");
  try {
    const first = await ensureVapidConfiguration({ env: {}, path });
    const second = await ensureVapidConfiguration({ env: {}, path });

    assert.equal(validateVapidConfiguration(first), true);
    assert.deepEqual(second, first);
    assert.deepEqual(JSON.parse(await readFile(path, "utf8")), first);
    if (process.platform !== "win32") {
      assert.equal((await stat(path)).mode & 0o777, 0o600);
    }
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("prefers a complete environment VAPID configuration", async () => {
  const directory = await mkdtemp(join(tmpdir(), "ai-usage-vapid-env-"));
  const path = join(directory, "vapid.json");
  try {
    const generated = await ensureVapidConfiguration({ env: {}, path });
    const configured = await ensureVapidConfiguration({
      env: {
        VAPID_PUBLIC_KEY: generated.publicKey,
        VAPID_PRIVATE_KEY: generated.privateKey,
        VAPID_SUBJECT: "mailto:operator@example.com",
      },
      path,
    });

    assert.deepEqual(configured, {
      publicKey: generated.publicKey,
      privateKey: generated.privateKey,
      subject: "mailto:operator@example.com",
    });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("rejects partial environment VAPID configuration", async () => {
  await assert.rejects(
    ensureVapidConfiguration({ env: { VAPID_PUBLIC_KEY: "incomplete" }, path: null }),
    /must form a valid configuration/,
  );
});
