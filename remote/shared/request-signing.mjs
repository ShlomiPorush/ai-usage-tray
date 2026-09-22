// Request signing for remote-view writes.
//
// Honest threat model, so nobody builds on a wrong assumption:
// this repository is public, so DEFAULT_SIGNING_KEY below is public knowledge
// by construction. A signature made with it proves nothing about who sent the
// request. It only proves that the sender implemented this format, which is
// enough to tell a real client apart from a naive scripted flood and therefore
// enough to hand the two different rate-limit budgets. The per-IP rate limit is
// the actual enforcement. An operator who runs a private relay for their own
// desktops can set a real key on both sides and gain real separation; on the
// shared public relay the signature is a tiering hint, not authentication.
//
// Written against Web Crypto only (no node: imports), like the other modules in
// this directory, so the Cloudflare Worker can adopt it unchanged later.

const encoder = new TextEncoder();

/** Canonical-string version prefix. Bump only with a protocol change. */
export const SIGNATURE_VERSION = "v1";

/** Public, non-secret fallback key. See the note at the top of this file. */
export const DEFAULT_SIGNING_KEY = "ai-usage-tray-public-default-key-v1";

/** How far a request timestamp may sit from the relay clock, in seconds. */
export const MAX_CLOCK_SKEW_SECONDS = 300;

export const TIMESTAMP_HEADER = "X-Costats-Timestamp";
export const SIGNATURE_HEADER = "X-Costats-Signature";

const HEX_SIGNATURE = /^[0-9a-fA-F]{64}$/;
const DECIMAL_TIMESTAMP = /^[0-9]{1,15}$/;

function toHex(buffer) {
  return [...new Uint8Array(buffer)]
    .map((byte) => byte.toString(16).padStart(2, "0"))
    .join("");
}

function fromHex(value) {
  const bytes = new Uint8Array(value.length / 2);
  for (let index = 0; index < bytes.length; index += 1) {
    bytes[index] = Number.parseInt(value.slice(index * 2, index * 2 + 2), 16);
  }
  return bytes;
}

export async function sha256Hex(body) {
  const digest = await crypto.subtle.digest("SHA-256", encoder.encode(body ?? ""));
  return toHex(digest);
}

/**
 * The exact bytes both sides sign:
 *
 *   "v1\n" + timestamp + "\n" + METHOD + "\n" + path + "\n" + sha256hex(body)
 *
 * `path` is the request path without the query string; `body` is the raw
 * request body as sent, so a re-serialised copy will not match.
 */
export async function canonicalRequestString(timestamp, method, path, body = "") {
  const digest = await sha256Hex(body);
  return `${SIGNATURE_VERSION}\n${timestamp}\n${String(method).toUpperCase()}\n${path}\n${digest}`;
}

async function importKey(key) {
  return crypto.subtle.importKey(
    "raw",
    encoder.encode(key),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign", "verify"],
  );
}

/** Lowercase hex HMAC-SHA256 over the canonical string. */
export async function signRequest({
  key = DEFAULT_SIGNING_KEY,
  timestamp,
  method,
  path,
  body = "",
} = {}) {
  const canonical = await canonicalRequestString(timestamp, method, path, body);
  const signature = await crypto.subtle.sign(
    "HMAC",
    await importKey(key),
    encoder.encode(canonical),
  );
  return toHex(signature);
}

/**
 * Classifies a write request as signed or unsigned. Never throws and never
 * rejects the request by itself: the caller uses the result to pick a
 * rate-limit tier, so an unsigned or badly signed request stays accepted as
 * long as it fits the strict budget. `reason` is for logging and tests.
 */
export async function verifyRequestSignature({
  key = DEFAULT_SIGNING_KEY,
  timestamp,
  signature,
  method,
  path,
  body = "",
  nowSeconds,
  maxSkewSeconds = MAX_CLOCK_SKEW_SECONDS,
} = {}) {
  // A header is always a string; a number is accepted so callers and tests can
  // pass the value they just computed.
  const rawTimestamp = typeof timestamp === "number"
    ? String(timestamp)
    : typeof timestamp === "string" ? timestamp.trim() : "";
  const rawSignature = typeof signature === "string" ? signature.trim() : "";
  if (rawTimestamp === "" || rawSignature === "") {
    return { signed: false, reason: "missing_signature" };
  }
  if (!DECIMAL_TIMESTAMP.test(rawTimestamp)) {
    return { signed: false, reason: "invalid_timestamp" };
  }
  if (!HEX_SIGNATURE.test(rawSignature)) {
    return { signed: false, reason: "invalid_signature" };
  }

  const seconds = Number(rawTimestamp);
  const reference = Number.isFinite(nowSeconds) ? nowSeconds : Date.now() / 1000;
  if (Math.abs(reference - seconds) > maxSkewSeconds) {
    return { signed: false, reason: "stale_timestamp" };
  }

  const canonical = await canonicalRequestString(rawTimestamp, method, path, body);
  let valid = false;
  try {
    // crypto.subtle.verify compares in constant time.
    valid = await crypto.subtle.verify(
      "HMAC",
      await importKey(key),
      fromHex(rawSignature.toLowerCase()),
      encoder.encode(canonical),
    );
  } catch {
    valid = false;
  }

  return valid ? { signed: true, reason: null } : { signed: false, reason: "invalid_signature" };
}

/** Env override, falling back to the public default when unset or blank. */
export function resolveSigningKey(environment = {}) {
  const configured = environment.SNAPSHOT_SIGNING_KEY;
  const trimmed = typeof configured === "string" ? configured.trim() : "";
  return trimmed === "" ? DEFAULT_SIGNING_KEY : trimmed;
}
