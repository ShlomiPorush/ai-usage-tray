import assert from "node:assert/strict";
import { test } from "node:test";
import {
  canonicalRequestString,
  DEFAULT_SIGNING_KEY,
  resolveSigningKey,
  sha256Hex,
  signRequest,
  verifyRequestSignature,
} from "../shared/request-signing.mjs";
import {
  DEFAULT_SIGNED_WRITES_PER_MINUTE,
  DEFAULT_UNSIGNED_WRITES_PER_MINUTE,
  RATE_LIMIT_WINDOW_MS,
  readTrustProxy,
  resolveClientAddress,
  WriteRateLimiter,
} from "./server.mjs";

// The authoritative cross-implementation vector. The same numbers are asserted
// by RemoteViewSignatureTests in tests/costats.Core.Tests, so the desktop app
// and the relay cannot drift apart silently. Changing anything here is a
// protocol change.
const VECTOR = {
  key: DEFAULT_SIGNING_KEY,
  timestamp: 1767225600,
  method: "PUT",
  path: "/u/0123456789abcdef0123456789abcdef",
  body: '{"version":2,"generatedAt":"2026-08-27T12:00:00Z","accounts":[]}',
  bodySha256: "f7d294d301e5c845b8ff9f6d4da1888a94e90bde065fbd3d4ab33b6c74eead9d",
  canonical:
    "v1\n1767225600\nPUT\n/u/0123456789abcdef0123456789abcdef\n" +
    "f7d294d301e5c845b8ff9f6d4da1888a94e90bde065fbd3d4ab33b6c74eead9d",
  signature: "550e42d03d30c657c7a483a2a7b7c91e63e2f0aeb49e7a8bf1feb9366b915cb0",
};

test("the default signing key is the documented public constant", () => {
  assert.equal(DEFAULT_SIGNING_KEY, "ai-usage-tray-public-default-key-v1");
  assert.equal(resolveSigningKey({}), DEFAULT_SIGNING_KEY);
  assert.equal(resolveSigningKey({ SNAPSHOT_SIGNING_KEY: "   " }), DEFAULT_SIGNING_KEY);
  assert.equal(resolveSigningKey({ SNAPSHOT_SIGNING_KEY: " operator-key " }), "operator-key");
});

test("known-answer vector shared with the desktop client", async () => {
  assert.equal(await sha256Hex(VECTOR.body), VECTOR.bodySha256);
  assert.equal(
    await canonicalRequestString(VECTOR.timestamp, VECTOR.method, VECTOR.path, VECTOR.body),
    VECTOR.canonical,
  );
  assert.equal(await signRequest(VECTOR), VECTOR.signature);

  // A lowercase method is canonicalised, so both spellings sign identically.
  assert.equal(await signRequest({ ...VECTOR, method: "put" }), VECTOR.signature);
});

test("a valid signature verifies inside the clock-skew window", async () => {
  const verified = await verifyRequestSignature({
    ...VECTOR,
    signature: VECTOR.signature,
    nowSeconds: VECTOR.timestamp + 299,
  });
  assert.deepEqual(verified, { signed: true, reason: null });

  assert.deepEqual(
    await verifyRequestSignature({
      ...VECTOR,
      signature: VECTOR.signature.toUpperCase(),
      nowSeconds: VECTOR.timestamp,
    }),
    { signed: true, reason: null },
  );
});

test("anything other than a valid, fresh signature stays unsigned", async () => {
  const cases = [
    [{}, "missing_signature"],
    [{ signature: VECTOR.signature, timestamp: "" }, "missing_signature"],
    [{ signature: VECTOR.signature, timestamp: "not-a-number" }, "invalid_timestamp"],
    [{ signature: "short", timestamp: VECTOR.timestamp }, "invalid_signature"],
    [{ signature: "z".repeat(64), timestamp: VECTOR.timestamp }, "invalid_signature"],
    [{ signature: "0".repeat(64), timestamp: VECTOR.timestamp }, "invalid_signature"],
    [{ signature: VECTOR.signature, timestamp: VECTOR.timestamp - 301 }, "stale_timestamp"],
    [{ signature: VECTOR.signature, timestamp: VECTOR.timestamp + 301 }, "stale_timestamp"],
  ];

  for (const [overrides, reason] of cases) {
    const result = await verifyRequestSignature({
      ...VECTOR,
      signature: undefined,
      ...overrides,
      nowSeconds: VECTOR.timestamp,
    });
    assert.deepEqual(result, { signed: false, reason }, JSON.stringify(overrides));
  }

  // The signature covers the body and the path, so neither can be swapped out.
  for (const changed of [{ body: `${VECTOR.body} ` }, { path: "/u/ffffffffffffffffffffffffffffffff" }]) {
    const result = await verifyRequestSignature({
      ...VECTOR,
      ...changed,
      signature: VECTOR.signature,
      nowSeconds: VECTOR.timestamp,
    });
    assert.deepEqual(result, { signed: false, reason: "invalid_signature" });
  }

  // A different key is a different signature, which is the only separation an
  // operator with a private key gains.
  assert.deepEqual(
    await verifyRequestSignature({
      ...VECTOR,
      key: "operator-key",
      signature: VECTOR.signature,
      nowSeconds: VECTOR.timestamp,
    }),
    { signed: false, reason: "invalid_signature" },
  );
});

test("a DELETE signs over the empty-body digest", async () => {
  const signature = await signRequest({
    timestamp: VECTOR.timestamp,
    method: "DELETE",
    path: VECTOR.path,
    body: "",
  });
  assert.equal(signature, "0a58e18aa3ae706773c9d1745fca602d978371232cb787317a19c2a2acc6660c");
  assert.deepEqual(
    await verifyRequestSignature({
      timestamp: VECTOR.timestamp,
      method: "DELETE",
      path: VECTOR.path,
      body: "",
      signature,
      nowSeconds: VECTOR.timestamp,
    }),
    { signed: true, reason: null },
  );
});

test("the write limiter counts accepted writes inside a fixed window", () => {
  const limiter = new WriteRateLimiter();
  let now = 1_000_000;

  for (let attempt = 1; attempt <= DEFAULT_UNSIGNED_WRITES_PER_MINUTE; attempt += 1) {
    const verdict = limiter.check("203.0.113.7", DEFAULT_UNSIGNED_WRITES_PER_MINUTE, now);
    assert.equal(verdict.allowed, true, `attempt ${attempt}`);
    assert.equal(verdict.count, attempt);
  }

  const refused = limiter.check("203.0.113.7", DEFAULT_UNSIGNED_WRITES_PER_MINUTE, now);
  assert.equal(refused.allowed, false);
  assert.equal(refused.retryAfterSeconds, 60);

  // A refused write is not counted, so the generous tier still has its budget.
  const signed = limiter.check("203.0.113.7", DEFAULT_SIGNED_WRITES_PER_MINUTE, now);
  assert.equal(signed.allowed, true);
  assert.equal(signed.count, DEFAULT_UNSIGNED_WRITES_PER_MINUTE + 1);

  // One bucket per client: the total is the largest limit, not the sum.
  assert.equal(limiter.check("203.0.113.7", DEFAULT_UNSIGNED_WRITES_PER_MINUTE, now).allowed, false);
  assert.equal(limiter.check("198.51.100.4", DEFAULT_UNSIGNED_WRITES_PER_MINUTE, now).allowed, true);

  // Retry-After shrinks as the window runs out and never drops below a second.
  now += RATE_LIMIT_WINDOW_MS - 1500;
  assert.equal(
    limiter.check("203.0.113.7", DEFAULT_UNSIGNED_WRITES_PER_MINUTE, now).retryAfterSeconds,
    2,
  );
  now += 1400;
  assert.equal(
    limiter.check("203.0.113.7", DEFAULT_UNSIGNED_WRITES_PER_MINUTE, now).retryAfterSeconds,
    1,
  );

  now += RATE_LIMIT_WINDOW_MS;
  assert.equal(limiter.check("203.0.113.7", DEFAULT_UNSIGNED_WRITES_PER_MINUTE, now).allowed, true);
});

test("the write limiter prunes elapsed windows and stays bounded", () => {
  const limiter = new WriteRateLimiter({ maxBuckets: 4 });
  const now = 5_000_000;

  for (let index = 0; index < 50; index += 1) {
    limiter.check(`198.51.100.${index}`, 5, now);
  }
  assert.equal(limiter.size, 4, "expected the bucket map to stay capped");

  // Elapsed windows are forgotten rather than held for the cap to evict.
  const later = now + RATE_LIMIT_WINDOW_MS;
  limiter.prune(later);
  assert.equal(limiter.size, 0);

  const wide = new WriteRateLimiter();
  wide.check("203.0.113.1", 5, now);
  wide.check("203.0.113.2", 5, now + RATE_LIMIT_WINDOW_MS - 1);
  wide.prune(now + RATE_LIMIT_WINDOW_MS);
  assert.deepEqual([...wide.buckets.keys()], ["203.0.113.2"]);
});

test("the limit keys on the socket peer unless TRUST_PROXY is set", () => {
  const request = (remoteAddress, forwarded) => ({
    socket: { remoteAddress },
    headers: forwarded === undefined ? {} : { "x-forwarded-for": forwarded },
  });

  assert.equal(resolveClientAddress(request("203.0.113.7")), "203.0.113.7");
  assert.equal(resolveClientAddress(request("::ffff:203.0.113.7")), "203.0.113.7");
  assert.equal(resolveClientAddress(request("2001:DB8::1")), "2001:db8::1");
  assert.equal(resolveClientAddress(request(undefined)), "unknown");

  // A forwarded header is ignored by default, so it cannot be used to evade.
  assert.equal(
    resolveClientAddress(request("203.0.113.7", "198.51.100.9")),
    "203.0.113.7",
  );

  const trustProxy = { trustProxy: true };
  assert.equal(
    resolveClientAddress(request("10.0.0.1", " 198.51.100.9 , 10.0.0.1 "), trustProxy),
    "198.51.100.9",
  );
  assert.equal(
    resolveClientAddress(request("10.0.0.1", "[2001:db8::1]:4443"), trustProxy),
    "2001:db8::1",
  );
  assert.equal(
    resolveClientAddress(request("10.0.0.1", "198.51.100.9:51234"), trustProxy),
    "198.51.100.9",
  );
  // An empty or absent header falls back to the socket peer instead of pooling
  // every such request under one key.
  assert.equal(resolveClientAddress(request("10.0.0.1", "  "), trustProxy), "10.0.0.1");
  assert.equal(resolveClientAddress(request("10.0.0.1"), trustProxy), "10.0.0.1");

  assert.equal(readTrustProxy({}), false);
  assert.equal(readTrustProxy({ TRUST_PROXY: "0" }), false);
  assert.equal(readTrustProxy({ TRUST_PROXY: "true" }), false);
  assert.equal(readTrustProxy({ TRUST_PROXY: " 1 " }), true);
});
