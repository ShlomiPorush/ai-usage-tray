// AI Usage Tray - remote view worker, bundled.
//
// GENERATED FILE - do not edit by hand.
// Re-create it with:  cd remote/worker && node bundle.mjs
// Sources: worker.js and every file in web/ (page, styles, script, manifest,
//          service worker, icons).
//
// Serves the JSON API (PUT/DELETE /u/{writeId}, GET /u/{readId}) and the viewer
// page from a single URL.
// Requires one KV binding named USAGE.

function normalizedPercent(value) {
  const number = Number(value);
  return Number.isFinite(number) ? Math.max(0, Math.min(100, Math.round(number))) : null;
}

function normalizedThreshold(account) {
  if (account?.alert?.enabled !== true) return null;
  const threshold = Number(account.alert.thresholdPercent);
  if (!Number.isInteger(threshold) || threshold < 1 || threshold > 100) return null;
  return threshold;
}

function windowKey(window) {
  const label = typeof window?.label === "string" ? window.label.trim().toLowerCase() : "usage";
  const scope = typeof window?.scope === "string" ? window.scope.trim().toLowerCase() : "";
  return scope ? `${label}:${scope}` : label;
}

function windowsByKey(account) {
  const result = new Map();
  const windows = Array.isArray(account?.windows) ? account.windows : [];
  for (const window of windows) {
    const usedPercent = normalizedPercent(window?.usedPercent);
    if (usedPercent === null) continue;
    result.set(windowKey(window), { ...window, usedPercent });
  }
  return result;
}

function findThresholdCrossings(previousSnapshot, currentSnapshot) {
  if (!previousSnapshot || typeof previousSnapshot !== "object") return [];
  const previousAccounts = new Map(
    (Array.isArray(previousSnapshot.accounts) ? previousSnapshot.accounts : [])
      .filter((account) => account && typeof account.id === "string")
      .map((account) => [account.id.toLowerCase(), account]),
  );
  const crossings = [];

  for (const account of Array.isArray(currentSnapshot?.accounts) ? currentSnapshot.accounts : []) {
    if (!account || typeof account.id !== "string") continue;
    const threshold = normalizedThreshold(account);
    if (threshold === null) continue;

    const previousAccount = previousAccounts.get(account.id.toLowerCase());
    if (!previousAccount || normalizedThreshold(previousAccount) !== threshold) continue;
    const previousWindows = windowsByKey(previousAccount);

    for (const [key, currentWindow] of windowsByKey(account)) {
      const previousWindow = previousWindows.get(key);
      if (!previousWindow) continue;

      const crossed = previousWindow.usedPercent < threshold && currentWindow.usedPercent >= threshold;
      const oldReset = Date.parse(previousWindow.resetsAt);
      const newReset = Date.parse(currentWindow.resetsAt);
      const newCycleAboveThreshold =
        Number.isFinite(oldReset) &&
        Number.isFinite(newReset) &&
        newReset > oldReset &&
        currentWindow.usedPercent < previousWindow.usedPercent &&
        currentWindow.usedPercent >= threshold;
      if (!crossed && !newCycleAboveThreshold) continue;

      crossings.push({
        accountId: account.id,
        accountName: typeof account.name === "string" && account.name ? account.name : account.id,
        windowKey: key,
        windowLabel: typeof currentWindow.label === "string" && currentWindow.label
          ? currentWindow.label
          : "Usage",
        scope: typeof currentWindow.scope === "string" && currentWindow.scope
          ? currentWindow.scope
          : null,
        usedPercent: currentWindow.usedPercent,
        thresholdPercent: threshold,
      });
    }
  }

  return crossings;
}


const encoder = new TextEncoder();

function base64UrlEncode(value) {
  const bytes = value instanceof Uint8Array ? value : new Uint8Array(value);
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function base64UrlDecode(value) {
  if (typeof value !== "string" || !/^[A-Za-z0-9_-]+$/.test(value)) {
    throw new Error("invalid_base64url");
  }
  const padded = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
  const binary = atob(padded);
  return Uint8Array.from(binary, (character) => character.charCodeAt(0));
}

function concat(...parts) {
  const length = parts.reduce((total, part) => total + part.length, 0);
  const result = new Uint8Array(length);
  let offset = 0;
  for (const part of parts) {
    result.set(part, offset);
    offset += part.length;
  }
  return result;
}

async function hmac(keyBytes, value) {
  const key = await crypto.subtle.importKey(
    "raw",
    keyBytes,
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
  return new Uint8Array(await crypto.subtle.sign("HMAC", key, value));
}

async function hkdfExtract(salt, inputKeyMaterial) {
  return hmac(salt, inputKeyMaterial);
}

async function hkdfExpand(pseudoRandomKey, info, length) {
  const output = [];
  let previous = new Uint8Array();
  let produced = 0;
  for (let counter = 1; produced < length; counter += 1) {
    previous = await hmac(
      pseudoRandomKey,
      concat(previous, info, Uint8Array.of(counter)),
    );
    output.push(previous);
    produced += previous.length;
  }
  return concat(...output).slice(0, length);
}

function vapidJwk(publicKey, privateKey) {
  const publicBytes = base64UrlDecode(publicKey);
  const privateBytes = base64UrlDecode(privateKey);
  if (publicBytes.length !== 65 || publicBytes[0] !== 4 || privateBytes.length !== 32) {
    throw new Error("invalid_vapid_key");
  }
  return {
    kty: "EC",
    crv: "P-256",
    x: base64UrlEncode(publicBytes.slice(1, 33)),
    y: base64UrlEncode(publicBytes.slice(33, 65)),
    d: base64UrlEncode(privateBytes),
  };
}

async function vapidAuthorization(endpoint, configuration, now) {
  const audience = new URL(endpoint).origin;
  const header = base64UrlEncode(encoder.encode(JSON.stringify({ typ: "JWT", alg: "ES256" })));
  const claims = base64UrlEncode(encoder.encode(JSON.stringify({
    aud: audience,
    exp: Math.floor(now / 1000) + 12 * 60 * 60,
    sub: configuration.subject,
  })));
  const unsigned = `${header}.${claims}`;
  const key = await crypto.subtle.importKey(
    "jwk",
    vapidJwk(configuration.publicKey, configuration.privateKey),
    { name: "ECDSA", namedCurve: "P-256" },
    false,
    ["sign"],
  );
  const signature = await crypto.subtle.sign(
    { name: "ECDSA", hash: "SHA-256" },
    key,
    encoder.encode(unsigned),
  );
  return `vapid t=${unsigned}.${base64UrlEncode(signature)}, k=${configuration.publicKey}`;
}

async function encryptPayload(subscription, payload) {
  const userPublic = base64UrlDecode(subscription.keys.p256dh);
  const authSecret = base64UrlDecode(subscription.keys.auth);
  if (userPublic.length !== 65 || userPublic[0] !== 4 || authSecret.length !== 16) {
    throw new Error("invalid_subscription_key");
  }

  const userKey = await crypto.subtle.importKey(
    "raw",
    userPublic,
    { name: "ECDH", namedCurve: "P-256" },
    false,
    [],
  );
  const senderKeys = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" },
    true,
    ["deriveBits"],
  );
  const senderPublic = new Uint8Array(await crypto.subtle.exportKey("raw", senderKeys.publicKey));
  const sharedSecret = new Uint8Array(await crypto.subtle.deriveBits(
    { name: "ECDH", public: userKey },
    senderKeys.privateKey,
    256,
  ));

  const authPrk = await hkdfExtract(authSecret, sharedSecret);
  const inputKeyMaterial = await hkdfExpand(
    authPrk,
    concat(encoder.encode("WebPush: info\0"), userPublic, senderPublic),
    32,
  );
  const salt = crypto.getRandomValues(new Uint8Array(16));
  const pseudoRandomKey = await hkdfExtract(salt, inputKeyMaterial);
  const contentEncryptionKey = await hkdfExpand(
    pseudoRandomKey,
    encoder.encode("Content-Encoding: aes128gcm\0"),
    16,
  );
  const nonce = await hkdfExpand(
    pseudoRandomKey,
    encoder.encode("Content-Encoding: nonce\0"),
    12,
  );
  const plaintext = concat(encoder.encode(payload), Uint8Array.of(2));
  const encryptionKey = await crypto.subtle.importKey(
    "raw",
    contentEncryptionKey,
    "AES-GCM",
    false,
    ["encrypt"],
  );
  const ciphertext = new Uint8Array(await crypto.subtle.encrypt(
    { name: "AES-GCM", iv: nonce, tagLength: 128 },
    encryptionKey,
    plaintext,
  ));
  const recordSize = new Uint8Array(4);
  new DataView(recordSize.buffer).setUint32(0, 4096, false);

  return concat(salt, recordSize, Uint8Array.of(senderPublic.length), senderPublic, ciphertext);
}

function validatePushSubscription(subscription) {
  try {
    if (subscription === null || typeof subscription !== "object" || Array.isArray(subscription)) return false;
    const endpoint = new URL(subscription.endpoint);
    if (endpoint.protocol !== "https:") return false;
    if (subscription.endpoint.length > 2048) return false;
    const p256dh = base64UrlDecode(subscription.keys?.p256dh);
    const auth = base64UrlDecode(subscription.keys?.auth);
    return p256dh.length === 65 && p256dh[0] === 4 && auth.length === 16;
  } catch {
    return false;
  }
}

function validateVapidConfiguration(configuration) {
  try {
    if (!configuration || typeof configuration.subject !== "string") return false;
    if (!/^(mailto:|https:)/.test(configuration.subject)) return false;
    vapidJwk(configuration.publicKey, configuration.privateKey);
    return true;
  } catch {
    return false;
  }
}

async function sendWebPush(
  subscription,
  message,
  configuration,
  fetchImplementation = fetch,
  now = Date.now(),
) {
  if (!validatePushSubscription(subscription)) throw new Error("invalid_subscription");
  if (!validateVapidConfiguration(configuration)) throw new Error("invalid_vapid_configuration");

  const payload = typeof message === "string" ? message : JSON.stringify(message);
  const encrypted = await encryptPayload(subscription, payload);
  const authorization = await vapidAuthorization(subscription.endpoint, configuration, now);
  return fetchImplementation(subscription.endpoint, {
    method: "POST",
    headers: {
      Authorization: authorization,
      "Content-Encoding": "aes128gcm",
      "Content-Type": "application/octet-stream",
      TTL: "300",
      Urgency: "high",
    },
    body: encrypted,
  });
}


// AI Usage Tray - remote view worker.
// Stores a small JSON snapshot per id and serves it back to the web page.
//
// Protocol v2 splits the single id in two:
//   writeId  128-bit secret held only by the app, 32 lowercase hex characters.
//   readId   sha256(utf8(writeId))[0..16] as 32 lowercase hex characters, i.e.
//            the first 32 characters of the hex digest of the writeId STRING
//            (the 32 ASCII characters, not the 16 bytes they encode).
// The app uploads to PUT /u/{writeId}; the share link carries only the readId,
// so whoever holds a share link can read the snapshot but can never overwrite
// or delete it. The KV key is always the readId.
//
// The single exception is GET /u/demo, a built-in read-only sample payload.


const ID_RE = /^[a-f0-9]{32}$/;
const MAX_BODY = 16 * 1024; // 16 KB
const TTL_SECONDS = 604800; // 7 days

// Payload limits. Deliberately generous: an app newer than this worker must
// keep working, so unknown fields are ignored and only the shape the viewer
// actually depends on is enforced.
const MAX_ACCOUNTS = 32;
const MAX_WINDOWS = 32;
const MAX_STRING = 256;
const MAX_DEPTH = 8;
const MAX_PUSH_SUBSCRIPTIONS = 8;

// A public sample so the project can be linked to without exposing a real id.
// It is built per request, never stored, and cannot be written to.
const DEMO_ID = "demo";

const CORS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, PUT, POST, DELETE, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type",
  "Access-Control-Expose-Headers": "X-Read-Id",
};

// Applied to every response this file produces. The bundled viewer page layers
// a Content-Security-Policy and framing rules on top; see bundle.mjs.
const SECURITY = {
  "X-Content-Type-Options": "nosniff",
  "Referrer-Policy": "no-referrer",
};

function json(status, obj) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: {
      ...CORS,
      ...SECURITY,
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
    },
  });
}

function empty(status, extra) {
  return new Response(null, { status, headers: { ...CORS, ...SECURITY, ...extra } });
}

// readId = first 32 hex characters of SHA-256 over the UTF-8 bytes of the
// lowercase 32-hex writeId string. One-way, so a share link never yields the
// write credential.
async function deriveReadId(writeId) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(writeId));
  const bytes = new Uint8Array(digest);
  let hex = "";
  for (let i = 0; i < 16; i++) hex += bytes[i].toString(16).padStart(2, "0");
  return hex;
}

// Guards against a single oversized string being smuggled through in a field
// nobody validates, and against absurdly nested payloads.
function stringsWithinLimits(value, depth) {
  if (typeof value === "string") return value.length <= MAX_STRING;
  if (depth >= MAX_DEPTH) return false;
  if (Array.isArray(value)) {
    return value.every((item) => stringsWithinLimits(item, depth + 1));
  }
  if (value !== null && typeof value === "object") {
    for (const key of Object.keys(value)) {
      if (key.length > MAX_STRING) return false;
      if (!stringsWithinLimits(value[key], depth + 1)) return false;
    }
  }
  return true;
}

// Returns null when the payload is acceptable, otherwise a short reason code.
// Only the shape web/app.js reads is checked; everything else passes through.
function validatePayload(data) {
  if (data === null || typeof data !== "object" || Array.isArray(data)) {
    return "not_an_object";
  }
  if (typeof data.version !== "number" || !Number.isFinite(data.version)) {
    return "bad_version";
  }
  if (!Array.isArray(data.accounts)) return "bad_accounts";
  if (data.accounts.length > MAX_ACCOUNTS) return "too_many_accounts";

  for (const account of data.accounts) {
    if (account === null || typeof account !== "object" || Array.isArray(account)) {
      return "bad_account";
    }
    if (account.windows !== undefined) {
      if (!Array.isArray(account.windows)) return "bad_windows";
      if (account.windows.length > MAX_WINDOWS) return "too_many_windows";
    }
  }

  if (!stringsWithinLimits(data, 0)) return "field_too_long";
  return null;
}

function vapidConfiguration(env) {
  const configuration = {
    publicKey: env.VAPID_PUBLIC_KEY,
    privateKey: env.VAPID_PRIVATE_KEY,
    subject: env.VAPID_SUBJECT,
  };
  return validateVapidConfiguration(configuration) ? configuration : null;
}

async function subscriptionHash(endpoint) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(endpoint));
  return base64UrlEncode(digest);
}

function subscriptionPrefix(readId) {
  return `push:${readId}:`;
}

function endpointKey(hash) {
  return `push-endpoint:${hash}`;
}

async function deleteEndpointMappingIfOwned(env, hash, readId) {
  const key = endpointKey(hash);
  if (await env.USAGE.get(key) === readId) await env.USAGE.delete(key);
}

async function refreshSubscriptions(env, readId) {
  const listed = await env.USAGE.list({ prefix: subscriptionPrefix(readId) });
  await Promise.all(listed.keys.map(async (entry) => {
    const subscription = await env.USAGE.get(entry.name, { type: "json" });
    if (!validatePushSubscription(subscription)) {
      await env.USAGE.delete(entry.name);
      return;
    }
    const hash = await subscriptionHash(subscription.endpoint);
    await Promise.all([
      env.USAGE.put(entry.name, JSON.stringify(subscription), { expirationTtl: TTL_SECONDS }),
      env.USAGE.put(endpointKey(hash), readId, { expirationTtl: TTL_SECONDS }),
    ]);
  }));
}

async function removeSubscriptions(env, readId) {
  const listed = await env.USAGE.list({ prefix: subscriptionPrefix(readId) });
  await Promise.all(listed.keys.map(async (entry) => {
    const subscription = await env.USAGE.get(entry.name, { type: "json" });
    await env.USAGE.delete(entry.name);
    if (subscription?.endpoint) {
      const hash = await subscriptionHash(subscription.endpoint);
      await deleteEndpointMappingIfOwned(env, hash, readId);
    }
  }));
}

async function deliverAlerts(env, readId, data, crossings) {
  const configuration = vapidConfiguration(env);
  if (configuration === null || crossings.length === 0) return;
  const listed = await env.USAGE.list({ prefix: subscriptionPrefix(readId) });
  const message = {
    type: "usage-alerts",
    readId,
    displayMode: data.displayMode === "remaining" ? "remaining" : "used",
    alerts: crossings,
  };
  await Promise.all(listed.keys.map(async (entry) => {
    const subscription = await env.USAGE.get(entry.name, { type: "json" });
    if (!validatePushSubscription(subscription)) {
      await env.USAGE.delete(entry.name);
      return;
    }
    try {
      const sender = typeof env.PUSH_SENDER === "function" ? env.PUSH_SENDER : sendWebPush;
      const result = await sender(subscription, message, configuration);
      if (result.status === 404 || result.status === 410) {
        await env.USAGE.delete(entry.name);
        const hash = await subscriptionHash(subscription.endpoint);
        await deleteEndpointMappingIfOwned(env, hash, readId);
      } else if (!result.ok) {
        console.error("Web Push delivery failed", result.status);
      }
    } catch (error) {
      console.error("Web Push delivery failed", error);
    }
  }));
}

async function put(request, env, context, writeId) {
  const type = request.headers.get("Content-Type") || "";
  if (!type.toLowerCase().includes("application/json")) {
    return json(415, { error: "unsupported_media_type" });
  }

  const declared = Number(request.headers.get("Content-Length"));
  if (Number.isFinite(declared) && declared > MAX_BODY) {
    return json(413, { error: "too_large" });
  }

  const body = await request.text();
  // Byte length, not character count: the payload may contain non-ASCII names.
  if (new TextEncoder().encode(body).length > MAX_BODY) {
    return json(413, { error: "too_large" });
  }

  let data;
  try {
    data = JSON.parse(body);
  } catch {
    return json(400, { error: "invalid_json" });
  }

  const reason = validatePayload(data);
  if (reason !== null) {
    return json(422, { error: "invalid_payload", reason });
  }

  const readId = await deriveReadId(writeId);
  const previousBody = await env.USAGE.get(readId);
  let previous = null;
  if (previousBody !== null) {
    try {
      previous = JSON.parse(previousBody);
    } catch {
      previous = null;
    }
  }
  const crossings = findThresholdCrossings(previous, data);
  await env.USAGE.put(readId, body, { expirationTtl: TTL_SECONDS });
  await refreshSubscriptions(env, readId);
  const delivery = deliverAlerts(env, readId, data, crossings);
  if (context && typeof context.waitUntil === "function") context.waitUntil(delivery);
  else await delivery;
  // Diagnostics only: the app derives the same value locally.
  return empty(204, { "X-Read-Id": readId });
}

// Deleting an absent snapshot answers 204 as well: a probe must not learn
// whether a given writeId was ever in use.
async function remove(env, writeId) {
  const readId = await deriveReadId(writeId);
  await env.USAGE.delete(readId);
  await removeSubscriptions(env, readId);
  return empty(204, { "X-Read-Id": readId });
}

async function readJsonBody(request) {
  const type = request.headers.get("Content-Type") || "";
  if (!type.toLowerCase().includes("application/json")) {
    return { response: json(415, { error: "unsupported_media_type" }) };
  }
  const body = await request.text();
  if (new TextEncoder().encode(body).length > MAX_BODY) {
    return { response: json(413, { error: "too_large" }) };
  }
  try {
    return { data: JSON.parse(body) };
  } catch {
    return { response: json(400, { error: "invalid_json" }) };
  }
}

async function manageSubscription(request, env, readId) {
  const configuration = vapidConfiguration(env);
  if (configuration === null) return json(503, { error: "push_not_configured" });
  const parsed = await readJsonBody(request);
  if (parsed.response) return parsed.response;

  if (request.method === "DELETE") {
    if (typeof parsed.data?.endpoint !== "string") {
      return json(422, { error: "invalid_subscription" });
    }
    const hash = await subscriptionHash(parsed.data.endpoint);
    await env.USAGE.delete(`${subscriptionPrefix(readId)}${hash}`);
    await deleteEndpointMappingIfOwned(env, hash, readId);
    return empty(204);
  }

  if (await env.USAGE.get(readId) === null) return json(404, { error: "not_found" });
  if (!validatePushSubscription(parsed.data)) {
    return json(422, { error: "invalid_subscription" });
  }
  const hash = await subscriptionHash(parsed.data.endpoint);
  const key = `${subscriptionPrefix(readId)}${hash}`;
  const existingReadId = await env.USAGE.get(endpointKey(hash));
  const listed = await env.USAGE.list({ prefix: subscriptionPrefix(readId) });
  if (!listed.keys.some((entry) => entry.name === key) && listed.keys.length >= MAX_PUSH_SUBSCRIPTIONS) {
    return json(429, { error: "too_many_subscriptions" });
  }
  if (existingReadId && existingReadId !== readId) {
    await env.USAGE.delete(`${subscriptionPrefix(existingReadId)}${hash}`);
  }
  await env.USAGE.put(key, JSON.stringify(parsed.data), { expirationTtl: TTL_SECONDS });
  await env.USAGE.put(endpointKey(hash), readId, { expirationTtl: TTL_SECONDS });
  return empty(204);
}

// Timestamps are relative to the request, so the sample never reads as stale.
function demoSnapshot() {
  const now = Date.now();
  const HOUR = 3600000;
  const DAY = 24 * HOUR;
  const at = (offset) => new Date(now + offset).toISOString();

  return {
    version: 2,
    generatedAt: new Date(now).toISOString(),
    primary: "claude:demo-personal",
    displayMode: "used",
    accounts: [
      {
        id: "claude:demo-personal",
        provider: "claude",
        name: "Claude Personal",
        plan: "Max 20x",
        windows: [
          { label: "Session", usedPercent: 12, resetsAt: at(2 * HOUR), severity: "normal" },
          { label: "Weekly", usedPercent: 64, resetsAt: at(3 * DAY), severity: "warning" },
          // Scoped to one model: same weekly window, its own budget.
          { label: "Weekly", usedPercent: 91, resetsAt: at(3 * DAY), scope: "Fable", severity: "critical" },
        ],
      },
      {
        id: "claude:demo-work",
        provider: "claude",
        name: "Claude Work",
        plan: "Pro",
        windows: [
          { label: "Session", usedPercent: 3, resetsAt: at(4 * HOUR) },
          { label: "Weekly", usedPercent: 27, resetsAt: at(5 * DAY) },
        ],
      },
      {
        id: "codex:demo",
        provider: "codex",
        name: "Codex",
        plan: "Plus",
        // 82% is the sample's orange window: the four bands (green, yellow,
        // orange, red) are all visible at once on the demo page.
        windows: [{ label: "Weekly", usedPercent: 82, resetsAt: at(2 * DAY) }],
        // Codex is the only provider that hands these out, so the sample shows
        // one on the Codex account.
        resetCredits: { available: 1, expiresAt: at(28 * DAY) },
      },
      {
        id: "zai",
        provider: "zai",
        name: "GLM",
        plan: "",
        windows: [{ label: "Session", usedPercent: 8, resetsAt: at(3 * HOUR) }],
      },
    ],
  };
}

async function get(env, readId) {
  const body = await env.USAGE.get(readId);
  if (body === null) return json(404, { error: "not_found" });
  return new Response(body, {
    status: 200,
    headers: {
      ...CORS,
      ...SECURITY,
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
    },
  });
}

const api = {
  async fetch(request, env, context) {
    if (request.method === "OPTIONS") return empty(204);

    const path = new URL(request.url).pathname;
    if (request.method === "GET" && path === "/push/vapid-public-key") {
      const configuration = vapidConfiguration(env);
      return configuration === null
        ? json(503, { error: "push_not_configured" })
        : json(200, { publicKey: configuration.publicKey });
    }

    const subscriptionMatch = /^\/u\/([^/]+)\/push-subscription\/?$/.exec(path);
    if (subscriptionMatch) {
      if (request.method !== "POST" && request.method !== "DELETE") {
        return json(405, { error: "method_not_allowed" });
      }
      if (!ID_RE.test(subscriptionMatch[1])) return json(400, { error: "invalid_id" });
      return manageSubscription(request, env, subscriptionMatch[1]);
    }

    const match = /^\/u\/([^/]+)\/?$/.exec(path);
    if (!match) return json(404, { error: "not_found" });

    const method = request.method;
    if (method !== "GET" && method !== "PUT" && method !== "DELETE") {
      return json(405, { error: "method_not_allowed" });
    }

    // The demo is read-only: a write must never be able to touch the sample.
    if (match[1] === DEMO_ID) {
      return method === "GET"
        ? json(200, demoSnapshot())
        : json(405, { error: "method_not_allowed" });
    }

    if (!ID_RE.test(match[1])) return json(400, { error: "invalid_id" });

    // GET takes a readId and looks it up directly. PUT and DELETE take the
    // secret writeId and address the same entry through the derivation.
    if (method === "PUT") return put(request, env, context, match[1]);
    if (method === "DELETE") return remove(env, match[1]);
    return get(env, match[1]);
  },
};

const STATIC_CACHE = "public, max-age=300";

// Only the page needs these; SECURITY (nosniff, no-referrer) comes from the
// API section above and is applied to every response.
const HTML_HEADERS = {
  "Content-Security-Policy": "default-src 'none'; base-uri 'none'; script-src 'self' 'sha256-iXOs36kKnW4yh4Y+/FCzNMJlwKSeAf4FG07t+z5Up38='; style-src 'self'; img-src 'self'; connect-src 'self'; manifest-src 'self'; worker-src 'self'; form-action 'none'; frame-ancestors 'none'",
  "X-Frame-Options": "DENY",
  "Permissions-Policy": "camera=(), microphone=(), geolocation=()",
};

const ASSETS = {
  "/index.html": {
    type: "text/html; charset=utf-8",
    html: true,
    body: "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<meta name=\"color-scheme\" content=\"light dark\">\n<meta name=\"robots\" content=\"noindex, nofollow\">\n<meta name=\"theme-color\" data-scheme=\"light\" media=\"(prefers-color-scheme: light)\" content=\"#EDE8E0\">\n<meta name=\"theme-color\" data-scheme=\"dark\" media=\"(prefers-color-scheme: dark)\" content=\"#1A2233\">\n<title>AI Usage Tray</title>\n<link rel=\"icon\" type=\"image/png\" href=\"icon-192.png\">\n<link rel=\"apple-touch-icon\" href=\"icon-192.png\">\n<link rel=\"manifest\" href=\"manifest.webmanifest\">\n<link rel=\"stylesheet\" href=\"styles.css\">\n<script>\n  // Applied before first paint so a forced theme never flashes the other palette.\n  try {\n    var saved = localStorage.getItem(\"aiUsageTray.theme\");\n    if (saved === \"light\" || saved === \"dark\") {\n      document.documentElement.setAttribute(\"data-theme\", saved);\n    }\n  } catch (e) { /* private mode: stay on the system theme */ }\n</script>\n</head>\n<body>\n<main class=\"page\">\n  <header class=\"page-header\">\n    <div class=\"header-row\">\n      <h1>AI usage</h1>\n      <span class=\"demo-badge\" id=\"demo-badge\" hidden>Demo data</span>\n      <div class=\"header-actions\">\n        <div class=\"percent-toggle\" id=\"percent-toggle\" role=\"group\"\n             aria-label=\"Percentage display\" hidden>\n          <button type=\"button\" class=\"percent-option\" data-percent-mode=\"used\"\n                  aria-pressed=\"true\">% used</button>\n          <button type=\"button\" class=\"percent-option\" data-percent-mode=\"left\"\n                  aria-pressed=\"false\">% left</button>\n        </div>\n        <button type=\"button\" class=\"notification-toggle\" id=\"notification-toggle\" hidden>\n          <svg viewBox=\"0 0 24 24\" width=\"17\" height=\"17\" fill=\"none\" stroke=\"currentColor\"\n               stroke-width=\"1.9\" stroke-linecap=\"round\" stroke-linejoin=\"round\"\n               aria-hidden=\"true\" focusable=\"false\">\n            <path d=\"M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9\"/>\n            <path d=\"M10 21h4\"/>\n          </svg>\n          <span id=\"notification-label\">Enable alerts</span>\n        </button>\n        <button type=\"button\" class=\"theme-toggle\" id=\"theme-toggle\" data-mode=\"auto\" hidden>\n          <svg class=\"theme-icon theme-icon-auto\" viewBox=\"0 0 24 24\" width=\"18\" height=\"18\"\n               fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" aria-hidden=\"true\" focusable=\"false\">\n            <circle cx=\"12\" cy=\"12\" r=\"8.2\"/>\n            <path d=\"M12 3.8a8.2 8.2 0 0 1 0 16.4z\" fill=\"currentColor\" stroke=\"none\"/>\n          </svg>\n          <svg class=\"theme-icon theme-icon-light\" viewBox=\"0 0 24 24\" width=\"18\" height=\"18\"\n               fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\"\n               aria-hidden=\"true\" focusable=\"false\">\n            <circle cx=\"12\" cy=\"12\" r=\"4.2\"/>\n            <path d=\"M12 2.5v2.2M12 19.3v2.2M4.22 4.22l1.56 1.56M18.22 18.22l1.56 1.56M2.5 12h2.2M19.3 12h2.2M4.22 19.78l1.56-1.56M18.22 5.78l1.56-1.56\"/>\n          </svg>\n          <svg class=\"theme-icon theme-icon-dark\" viewBox=\"0 0 24 24\" width=\"18\" height=\"18\"\n               fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linejoin=\"round\"\n               aria-hidden=\"true\" focusable=\"false\">\n            <path d=\"M20 13.6A8.2 8.2 0 0 1 10.4 4a8.2 8.2 0 1 0 9.6 9.6z\"/>\n          </svg>\n        </button>\n      </div>\n    </div>\n    <p class=\"updated\" id=\"updated\" hidden></p>\n  </header>\n\n  <p class=\"notice notice-stale\" id=\"staleness\" role=\"status\" hidden></p>\n  <p class=\"notice notice-quiet\" id=\"connection\" role=\"status\" hidden></p>\n  <p class=\"notice notice-quiet\" id=\"notifications\" role=\"status\" hidden></p>\n\n  <div id=\"content\"></div>\n</main>\n\n<script src=\"config.js\"></script>\n<script src=\"app.js\"></script>\n</body>\n</html>\n",
  },
  "/styles.css": {
    type: "text/css; charset=utf-8",
    body: "/* AI Usage Tray - remote view.\n   Light and dark are two hand-picked palettes, not an inversion of one another:\n   light is \"Warm stone\" (sand surfaces, deep amber accent) and dark is\n   \"Blue steel\" (navy surfaces, sky accent). Both come from the app, so the\n   page and the Windows widget read as one product. No purple anywhere.\n\n   Theme resolution:\n     no data-theme         -> follow prefers-color-scheme (the \"Auto\" mode)\n     data-theme=\"light\"    -> stay light even on a dark system\n     data-theme=\"dark\"     -> dark everywhere\n   The dark tokens live in two blocks with identical bodies: the media query\n   (Auto) and the explicit attribute. Keep them in sync when editing. */\n\n:root {\n  color-scheme: light dark;\n\n  /* --- Warm stone (light) --- */\n  --page: #EDE8E0;\n  --panel: #F7F3EC;\n  --card: rgba(255, 255, 255, 0.50);\n  --border: #DAD1C2;\n  --chip-bg: rgba(0, 0, 0, 0.05);\n\n  --ink: #2A2318;\n  --ink-2: #4C4234;\n  --ink-3: #5E5548;\n\n  --accent: #B45309;\n  --accent-hover: #92400E;\n  --on-accent: #FFFFFF;\n\n  --track: #DFD6C7;\n  --none: #706659;\n\n  /* --- Usage bands ---------------------------------------------------\n     The number alone picks the band: green 0-49, yellow 50-74,\n     orange 75-89, red 90-100. The vivid fill colours are identical in\n     both themes; only the ink changes. Bars are plain fills: no outline,\n     no inset edge, in either theme. */\n  --band-green: #10B981;\n  --band-yellow: #EAB308;\n  --band-orange: #F97316;\n  --band-red: #EF4444;\n\n  /* The percent chip: the band colour at 18% over whatever sits behind it.\n     15% flattened red over the dark card into the banned purple range; 18%\n     stays clear of it. */\n  --tint-green: rgba(16, 185, 129, 0.18);\n  --tint-yellow: rgba(234, 179, 8, 0.18);\n  --tint-orange: rgba(249, 115, 22, 0.18);\n  --tint-red: rgba(239, 68, 68, 0.18);\n\n  /* Percent ink: the same hue, one step deeper on light. */\n  --ink-green: #065F46;\n  --ink-yellow: #854D0E;\n  --ink-orange: #9A3412;\n  --ink-red: #991B1B;\n\n  --stale-bg: var(--tint-yellow);\n  --stale-border: #A16207;\n  --stale-ink: #854D0E;\n\n  --shadow: 0 1px 2px rgba(42, 35, 24, 0.06);\n}\n\n/* Auto: the system decides, unless the user has explicitly forced light. */\n@media (prefers-color-scheme: dark) {\n  :root:not([data-theme=\"light\"]) {\n    /* --- Blue steel (dark) --- */\n    --page: #1A2233;\n    --panel: #1F2839;\n    --card: rgba(255, 255, 255, 0.08);\n    --border: #33405A;\n    --chip-bg: rgba(255, 255, 255, 0.08);\n\n    --ink: #EFF4FB;\n    --ink-2: #C4CFE0;\n    --ink-3: #9EABC0;\n\n    --accent: #38BDF8;\n    --accent-hover: #7DD3FC;\n    --on-accent: #16202E;\n\n    --track: #33405A;\n    --none: #8996AC;\n\n    --ink-green: #6EE7B7;\n    --ink-yellow: #FDE047;\n    --ink-orange: #FDBA74;\n    --ink-red: #FCA5A5;\n\n    --stale-border: rgba(253, 224, 71, 0.35);\n    --stale-ink: #FDE047;\n\n    --shadow: none;\n  }\n}\n\n/* Forced dark: same body as the media query above. */\n:root[data-theme=\"dark\"] {\n  --page: #1A2233;\n  --panel: #1F2839;\n  --card: rgba(255, 255, 255, 0.08);\n  --border: #33405A;\n  --chip-bg: rgba(255, 255, 255, 0.08);\n\n  --ink: #EFF4FB;\n  --ink-2: #C4CFE0;\n  --ink-3: #9EABC0;\n\n  --accent: #38BDF8;\n  --accent-hover: #7DD3FC;\n  --on-accent: #16202E;\n\n  --track: #33405A;\n  --none: #8996AC;\n\n  --ink-green: #6EE7B7;\n  --ink-yellow: #FDE047;\n  --ink-orange: #FDBA74;\n  --ink-red: #FCA5A5;\n\n  --stale-border: rgba(253, 224, 71, 0.35);\n  --stale-ink: #FDE047;\n\n  --shadow: none;\n}\n\n/* Forced modes also pin the UA colours (scrollbars, form controls). */\n:root[data-theme=\"light\"] { color-scheme: light; }\n:root[data-theme=\"dark\"] { color-scheme: dark; }\n\n* { box-sizing: border-box; }\n\nbody {\n  margin: 0;\n  padding: 20px 16px 48px;\n  background: var(--page);\n  color: var(--ink);\n  font-family: \"Segoe UI\", -apple-system, BlinkMacSystemFont, system-ui, sans-serif;\n  font-size: 15px;\n  line-height: 1.45;\n  -webkit-text-size-adjust: 100%;\n}\n\n.page {\n  max-width: 1100px;\n  margin: 0 auto;\n}\n\n.page-header {\n  margin: 0 0 16px;\n}\n\n.header-row {\n  display: flex;\n  align-items: center;\n  gap: 12px;\n  flex-wrap: wrap;\n}\n\nh1 {\n  margin: 0;\n  font-size: 20px;\n  font-weight: 600;\n  letter-spacing: -0.01em;\n}\n\n.updated {\n  margin: 2px 0 0;\n  color: var(--ink-3);\n  font-size: 13px;\n}\n\n/* Sample data, not a real account: quiet enough to ignore, present enough to\n   stop anyone reading the numbers as their own. */\n.demo-badge {\n  padding: 2px 8px;\n  border: 1px solid var(--border);\n  border-radius: 999px;\n  background: var(--chip-bg);\n  color: var(--ink-3);\n  font-size: 12px;\n  font-weight: 500;\n  white-space: nowrap;\n}\n\n:where(a, button):focus-visible {\n  outline: 2px solid var(--accent);\n  outline-offset: 2px;\n  border-radius: 8px;\n}\n\n/* Header controls ------------------------------------------------------- */\n\n.header-actions {\n  margin-left: auto;\n  display: flex;\n  align-items: center;\n  gap: 8px;\n}\n\n.percent-toggle {\n  flex: none;\n  display: inline-flex;\n  align-items: center;\n  padding: 2px;\n  border: 1px solid var(--border);\n  border-radius: 9px;\n  background: var(--panel);\n  box-shadow: var(--shadow);\n}\n\n.percent-option {\n  min-height: 28px;\n  padding: 0 8px;\n  border: 0;\n  border-radius: 6px;\n  background: transparent;\n  color: var(--ink-3);\n  font: inherit;\n  font-size: 11.5px;\n  font-weight: 600;\n  white-space: nowrap;\n  cursor: pointer;\n}\n\n.percent-option:hover { color: var(--ink); }\n\n.percent-option[aria-pressed=\"true\"] {\n  background: var(--chip-bg);\n  color: var(--ink);\n}\n\n.notification-toggle {\n  flex: none;\n  display: inline-flex;\n  align-items: center;\n  justify-content: center;\n  gap: 6px;\n  min-height: 34px;\n  padding: 0 10px;\n  border: 1px solid var(--border);\n  border-radius: 9px;\n  background: var(--panel);\n  box-shadow: var(--shadow);\n  color: var(--ink-2);\n  font: inherit;\n  font-size: 11.5px;\n  font-weight: 600;\n  cursor: pointer;\n}\n\n.notification-toggle[hidden] { display: none; }\n\n.notification-toggle:hover:not(:disabled) {\n  color: var(--ink);\n  border-color: var(--ink-3);\n}\n\n.notification-toggle[data-state=\"on\"] { color: var(--ink-green); }\n\n.notification-toggle:disabled {\n  cursor: default;\n  opacity: 0.7;\n}\n\n.theme-toggle {\n  flex: none;\n  display: inline-flex;\n  align-items: center;\n  justify-content: center;\n  width: 34px;\n  height: 34px;\n  padding: 0;\n  border: 1px solid var(--border);\n  border-radius: 9px;\n  background: var(--panel);\n  box-shadow: var(--shadow);\n  color: var(--ink-2);\n  cursor: pointer;\n}\n\n.theme-toggle:hover {\n  color: var(--ink);\n  border-color: var(--ink-3);\n}\n\n.theme-icon { display: none; }\n.theme-toggle[data-mode=\"auto\"] .theme-icon-auto,\n.theme-toggle[data-mode=\"light\"] .theme-icon-light,\n.theme-toggle[data-mode=\"dark\"] .theme-icon-dark { display: block; }\n\n/* Notices ------------------------------------------------------------- */\n\n.notice {\n  margin: 0 0 12px;\n  padding: 10px 12px;\n  border-radius: 10px;\n  font-size: 13px;\n}\n\n.notice-stale {\n  background: var(--stale-bg);\n  border: 1px solid var(--stale-border);\n  color: var(--stale-ink);\n}\n\n.notice-quiet {\n  padding: 0 2px;\n  color: var(--ink-3);\n}\n\n/* Cards --------------------------------------------------------------- */\n\n/* One column on phones; as many ~320px columns as fit on wider screens.\n   min(100%, 320px) keeps the track from overflowing very narrow viewports. */\n.cards {\n  display: grid;\n  grid-template-columns: repeat(auto-fit, minmax(min(100%, 320px), 1fr));\n  gap: 12px;\n  align-items: start;\n  justify-items: center;\n}\n\n.card {\n  width: 100%;\n  /* A single card on a wide screen would otherwise stretch across the page. */\n  max-width: 560px;\n  background: var(--card);\n  border: 1px solid var(--border);\n  border-radius: 12px;\n  box-shadow: var(--shadow);\n  padding: 14px 16px;\n}\n\n.card-head {\n  display: flex;\n  align-items: center;\n  gap: 8px;\n  flex-wrap: wrap;\n}\n\n.dot {\n  width: 9px;\n  height: 9px;\n  border-radius: 50%;\n  flex: none;\n  background: var(--none);\n}\n\n.dot.is-green { background: var(--band-green); }\n.dot.is-yellow { background: var(--band-yellow); }\n.dot.is-orange { background: var(--band-orange); }\n.dot.is-red { background: var(--band-red); }\n\n.provider-icon {\n  width: 18px;\n  height: 18px;\n  flex: none;\n  color: var(--ink-2);\n  display: inline-grid;\n  place-items: center;\n}\n\n.provider-icon svg {\n  width: 100%;\n  height: 100%;\n  display: block;\n  fill: currentColor;\n}\n\n.provider-icon.is-monogram {\n  border: 1px solid var(--ink-3);\n  border-radius: 5px;\n  font-size: 11px;\n  font-weight: 650;\n  line-height: 1;\n}\n\n.account-name {\n  margin: 0;\n  font-size: 15px;\n  font-weight: 600;\n  color: var(--ink);\n  min-width: 0;\n  overflow-wrap: anywhere;\n}\n\n.star {\n  color: var(--ink-3);\n  font-size: 13px;\n  margin-left: 2px;\n}\n\n.chip {\n  margin-left: auto;\n  padding: 2px 8px;\n  border-radius: 999px;\n  background: var(--chip-bg);\n  color: var(--ink-2);\n  font-size: 12px;\n  font-weight: 500;\n  white-space: nowrap;\n}\n\n.meters {\n  margin-top: 12px;\n  display: flex;\n  flex-direction: column;\n  gap: 12px;\n}\n\n.meter-row + .meter-row {\n  border-top: 1px solid var(--border);\n  padding-top: 12px;\n}\n\n.meter-top {\n  display: flex;\n  align-items: baseline;\n  justify-content: space-between;\n  gap: 12px;\n}\n\n.meter-label-row {\n  display: flex;\n  align-items: baseline;\n  gap: 6px;\n  min-width: 0;\n  flex-wrap: wrap;\n}\n\n.meter-label {\n  color: var(--ink-2);\n  font-size: 13px;\n  min-width: 0;\n  overflow-wrap: anywhere;\n}\n\n/* Names the model a window is limited to, e.g. a weekly cap that only applies\n   to one model. Absent on account-wide windows. */\n.scope-chip {\n  padding: 1px 7px;\n  border: 1px solid var(--border);\n  border-radius: 999px;\n  background: var(--chip-bg);\n  color: var(--ink-2);\n  font-size: 11px;\n  font-weight: 600;\n  white-space: nowrap;\n}\n\n/* The provider is refusing requests, which no percentage on its own conveys. */\n.blocked-banner {\n  margin: 10px 0 0;\n  padding: 8px 10px;\n  border: 1px solid var(--band-red);\n  border-radius: 9px;\n  background: var(--tint-red);\n  color: var(--ink);\n  font-size: 12.5px;\n  font-weight: 600;\n}\n\n/* Redeemable usage-limit resets: good news, and not urgent, so it stays in the\n   card's quiet register instead of borrowing a band colour. */\n.resets {\n  margin-top: 10px;\n  display: flex;\n  align-items: baseline;\n  flex-wrap: wrap;\n  gap: 4px 8px;\n}\n\n.reset-chip {\n  padding: 1px 8px;\n  border-radius: 999px;\n  background: var(--chip-bg);\n  color: var(--ink-2);\n  font-size: 12px;\n  font-weight: 600;\n  white-space: nowrap;\n}\n\n/* A soft chip of the band colour behind the number: enough to carry the\n   state at a glance, quiet enough not to shout on a card full of rows.\n   Tint and ink only, never a border. */\n.meter-value {\n  padding: 1px 6px;\n  border-radius: 999px;\n  color: var(--ink);\n  font-size: 13.5px;\n  font-weight: 600;\n  white-space: nowrap;\n}\n\n.meter-track {\n  margin-top: 6px;\n  height: 7px;\n  border-radius: 999px;\n  background: var(--track);\n  overflow: hidden;\n}\n\n.meter-fill {\n  height: 100%;\n  border-radius: 999px;\n  background: var(--none);\n  transition: width 240ms ease;\n}\n\n/* A plain vivid fill in both themes: no outline, no inset edge. */\n.meter-row.is-green .meter-value { background: var(--tint-green); color: var(--ink-green); }\n.meter-row.is-green .meter-fill { background: var(--band-green); }\n.meter-row.is-yellow .meter-value { background: var(--tint-yellow); color: var(--ink-yellow); }\n.meter-row.is-yellow .meter-fill { background: var(--band-yellow); }\n.meter-row.is-orange .meter-value { background: var(--tint-orange); color: var(--ink-orange); }\n.meter-row.is-orange .meter-fill { background: var(--band-orange); }\n.meter-row.is-red .meter-value { background: var(--tint-red); color: var(--ink-red); }\n.meter-row.is-red .meter-fill { background: var(--band-red); }\n\n.meter-reset {\n  margin-top: 5px;\n  color: var(--ink-3);\n  font-size: 12px;\n}\n\n.card-empty {\n  margin: 10px 0 0;\n  color: var(--ink-3);\n  font-size: 13px;\n}\n\n/* Standalone messages -------------------------------------------------- */\n\n.message {\n  max-width: 640px;\n  background: var(--panel);\n  border: 1px solid var(--border);\n  border-radius: 12px;\n  box-shadow: var(--shadow);\n  padding: 18px 16px;\n}\n\n.message h2 {\n  margin: 0 0 6px;\n  font-size: 16px;\n  font-weight: 600;\n}\n\n.message p {\n  margin: 0 0 8px;\n  color: var(--ink-2);\n  font-size: 14px;\n}\n\n.message p:last-child { margin-bottom: 0; }\n\n.message .example {\n  display: inline-block;\n  padding: 2px 6px;\n  border-radius: 6px;\n  background: var(--chip-bg);\n  color: var(--ink-2);\n  font-family: ui-monospace, \"Cascadia Mono\", Consolas, monospace;\n  font-size: 12.5px;\n  overflow-wrap: anywhere;\n}\n\n/* Landing (no id in the link) ------------------------------------------ */\n\n.landing {\n  max-width: 520px;\n  margin: 4px auto 0;\n  padding: 28px 22px 26px;\n  text-align: center;\n  background: var(--panel);\n  border: 1px solid var(--border);\n  border-radius: 14px;\n  box-shadow: var(--shadow);\n}\n\n.landing h2 {\n  margin: 0 0 10px;\n  font-size: 21px;\n  font-weight: 600;\n  letter-spacing: -0.01em;\n}\n\n.landing p {\n  margin: 0 auto 20px;\n  max-width: 44ch;\n  color: var(--ink-2);\n  font-size: 14px;\n}\n\n.landing-actions {\n  display: flex;\n  flex-wrap: wrap;\n  justify-content: center;\n  gap: 10px;\n}\n\n.button {\n  display: inline-flex;\n  align-items: center;\n  justify-content: center;\n  min-height: 40px;\n  padding: 0 16px;\n  border: 1px solid transparent;\n  border-radius: 10px;\n  font-size: 14px;\n  font-weight: 600;\n  text-decoration: none;\n}\n\n.button-primary {\n  background: var(--accent);\n  color: var(--on-accent);\n}\n\n.button-primary:hover { background: var(--accent-hover); }\n\n.button-secondary {\n  background: var(--chip-bg);\n  border-color: var(--border);\n  color: var(--ink);\n}\n\n.button-secondary:hover { border-color: var(--ink-3); }\n\n.sr-only {\n  position: absolute;\n  width: 1px;\n  height: 1px;\n  margin: -1px;\n  padding: 0;\n  border: 0;\n  overflow: hidden;\n  clip: rect(0 0 0 0);\n  clip-path: inset(50%);\n  white-space: nowrap;\n}\n\n@media (prefers-reduced-motion: reduce) {\n  .meter-fill { transition: none; }\n}\n\n@media (max-width: 520px) {\n  .notification-toggle span { display: none; }\n  .notification-toggle { width: 34px; padding: 0; }\n}\n",
  },
  "/app.js": {
    type: "text/javascript; charset=utf-8",
    body: "// AI Usage Tray - remote view.\n// Reads ?id=<32 hex> from the URL, fetches {apiBase}/u/{id} and renders one card\n// per account. Refetches every 60s; relative times re-render every 30s.\n// Without an id it shows a landing page (see start()).\n// ?id=demo is the one exception: a public sample payload built by the worker.\n\n// The payload uses the product terms \"used\" and \"remaining\". The browser\n// keeps its older \"left\" storage value so existing explicit overrides survive.\nfunction resolvePercentMode(storedMode, remoteDisplayMode) {\n  if (storedMode === \"left\" || storedMode === \"used\") return storedMode;\n  return remoteDisplayMode === \"remaining\" ? \"left\" : \"used\";\n}\n\nfunction hasEnabledAlertAccounts(data) {\n  return Array.isArray(data && data.accounts) && data.accounts.some(function (account) {\n    return account && account.alert && account.alert.enabled === true;\n  });\n}\n\nif (typeof module !== \"undefined\" && module.exports) {\n  module.exports = {\n    resolvePercentMode: resolvePercentMode,\n    hasEnabledAlertAccounts: hasEnabledAlertAccounts\n  };\n}\n\n(function () {\n  \"use strict\";\n\n  // Node loads this file to test the pure preference resolver above.\n  if (typeof window === \"undefined\" || typeof document === \"undefined\") return;\n\n  var CONFIG = window.REMOTE_VIEW_CONFIG || {};\n  var API_BASE = String(CONFIG.apiBase || \"\").replace(/\\/+$/, \"\");\n\n  var ID_PATTERN = /^[0-9a-f]{32}$/;\n  // The one id that is not a 32-hex secret: a public sample served by the worker.\n  var DEMO_ID = \"demo\";\n  var REFRESH_MS = 60000;\n  var TICK_MS = 30000;\n  var STALE_MS = 30 * 60000;\n\n  var THEME_KEY = \"aiUsageTray.theme\";\n  var PERCENT_MODE_KEY = \"aiUsageTray.percentMode\";\n  var LAST_ID_KEY = \"aiUsageTray.lastId\";\n\n  var RELEASES_URL = \"https://github.com/ShlomiPorush/ai-usage-tray/releases/latest\";\n  var REPO_URL = \"https://github.com/ShlomiPorush/ai-usage-tray\";\n\n  var SVG_NS = \"http://www.w3.org/2000/svg\";\n  var PROVIDER_ICONS = {\n    claude: {\n      viewBox: \"0 0 100 100\",\n      path: \"M25.71 63.22L41.44 54.39L41.7 53.62L41.44 53.2H40.67L38.04 53.04L29.05 52.79L21.26 52.47L13.71 52.06L11.81 51.66L10.03 49.31L10.21 48.14L11.81 47.07L14.1 47.27L19.16 47.61L26.75 48.14L32.25 48.46L40.41 49.31H41.7L41.88 48.79L41.44 48.46L41.1 48.14L33.24 42.82L24.74 37.19L20.29 33.95L17.88 32.31L16.67 30.77L16.14 27.41L18.33 25.01L21.26 25.21L22.01 25.41L24.99 27.7L31.34 32.62L39.64 38.73L40.85 39.74L41.34 39.4L41.4 39.15L40.85 38.24L36.34 30.09L31.52 21.79L29.38 18.35L28.81 16.28C28.61 15.43 28.47 14.73 28.47 13.85L30.96 10.48L32.33 10.03L35.65 10.48L37.05 11.69L39.11 16.41L42.45 23.83L47.63 33.93L49.15 36.93L49.96 39.7L50.26 40.55H50.79V40.06L51.21 34.38L52 27.39L52.77 18.41L53.04 15.88L54.29 12.84L56.78 11.2L58.72 12.14L60.32 14.42L60.1 15.9L59.15 22.07L57.29 31.75L56.07 38.22H56.78L57.59 37.41L60.87 33.06L66.37 26.18L68.8 23.45L71.63 20.43L73.46 19H76.9L79.43 22.76L78.29 26.65L74.75 31.14L71.82 34.94L67.61 40.61L64.98 45.14L65.22 45.51L65.85 45.45L75.36 43.42L80.5 42.49L86.63 41.44L89.4 42.73L89.71 44.05L88.61 46.74L82.06 48.36L74.37 49.9L62.91 52.61L62.77 52.71L62.93 52.91L68.09 53.4L70.3 53.52H75.7L85.76 54.27L88.39 56.01L89.97 58.14L89.71 59.75L85.66 61.82L80.19 60.52L67.45 57.49L63.07 56.4H62.47V56.76L66.11 60.32L72.79 66.35L81.15 74.12L81.57 76.05L80.5 77.56L79.36 77.4L72.02 71.88L69.19 69.39L62.77 63.98H62.35V64.55L63.82 66.72L71.63 78.45L72.04 82.06L71.47 83.23L69.45 83.94L67.22 83.53L62.65 77.12L57.93 69.89L54.13 63.42L53.66 63.68L51.42 87.87L50.36 89.1L47.94 90.03L45.91 88.49L44.84 86L45.91 81.09L47.21 74.67L48.26 69.57L49.21 63.24L49.78 61.13L49.74 60.99L49.27 61.05L44.5 67.61L37.23 77.42L31.48 83.57L30.11 84.12L27.72 82.89L27.94 80.68L29.28 78.72L37.23 68.6L42.03 62.32L45.12 58.7L45.1 58.18H44.92L23.79 71.9L20.03 72.38L18.41 70.87L18.61 68.38L19.38 67.57L25.73 63.2L25.71 63.22Z\"\n    },\n    codex: {\n      viewBox: \"0 0 100 100\",\n      path: \"M83.77 42.81C84.67 40.11 84.98 37.26 84.68 34.44C84.38 31.62 83.49 28.89 82.05 26.44C77.69 18.84 68.92 14.94 60.35 16.77C57.98 14.13 54.96 12.17 51.59 11.07C48.21 9.97 44.61 9.77 41.14 10.51C37.67 11.24 34.45 12.88 31.81 15.25C29.17 17.62 27.2 20.64 26.1 24.01C23.32 24.58 20.69 25.74 18.4 27.41C16.1 29.07 14.18 31.21 12.78 33.68C8.37 41.26 9.37 50.83 15.25 57.33C14.35 60.03 14.04 62.88 14.34 65.7C14.63 68.52 15.52 71.25 16.96 73.7C21.33 81.3 30.1 85.21 38.67 83.37C40.56 85.49 42.87 87.19 45.46 88.34C48.05 89.5 50.86 90.09 53.7 90.07C62.48 90.08 70.26 84.41 72.94 76.05C75.72 75.48 78.35 74.32 80.64 72.66C82.94 70.99 84.86 68.85 86.26 66.38C90.62 58.81 89.62 49.3 83.77 42.81ZM53.7 84.84C50.2 84.84 46.8 83.61 44.11 81.37L44.58 81.1L60.51 71.9C60.91 71.67 61.24 71.34 61.47 70.94C61.7 70.54 61.82 70.09 61.82 69.63V47.18L68.56 51.07C68.62 51.11 68.67 51.17 68.68 51.25V69.85C68.66 78.12 61.97 84.82 53.7 84.84ZM21.5 71.08C19.74 68.05 19.11 64.49 19.72 61.04L20.19 61.32L36.13 70.52C36.53 70.75 36.98 70.87 37.43 70.87C37.89 70.87 38.34 70.75 38.73 70.52L58.21 59.29V67.06C58.21 67.1 58.2 67.14 58.18 67.18C58.16 67.21 58.13 67.24 58.1 67.27L41.97 76.57C34.8 80.7 25.64 78.25 21.5 71.08ZM17.3 36.39C19.07 33.34 21.87 31.01 25.19 29.81V48.74C25.18 49.19 25.3 49.65 25.53 50.04C25.75 50.44 26.08 50.77 26.48 50.99L45.86 62.17L39.13 66.07C39.09 66.09 39.05 66.1 39.01 66.1C38.97 66.1 38.93 66.09 38.89 66.07L22.79 56.78C15.64 52.63 13.18 43.48 17.3 36.31V36.39ZM72.62 49.24L53.18 37.95L59.9 34.07C59.93 34.05 59.97 34.04 60.02 34.04C60.06 34.04 60.1 34.05 60.13 34.07L76.24 43.38C78.7 44.8 80.7 46.89 82.02 49.41C83.34 51.92 83.91 54.77 83.68 57.6C83.44 60.43 82.4 63.14 80.69 65.4C78.97 67.67 76.64 69.4 73.98 70.39V51.47C73.97 51.01 73.83 50.56 73.6 50.17C73.36 49.79 73.02 49.46 72.62 49.24ZM79.33 39.17L78.85 38.88L62.94 29.61C62.54 29.38 62.09 29.25 61.63 29.25C61.17 29.25 60.72 29.38 60.32 29.61L40.86 40.84V33.06C40.86 33.02 40.87 32.98 40.88 32.95C40.9 32.91 40.92 32.88 40.96 32.86L57.06 23.57C59.53 22.15 62.35 21.46 65.19 21.58C68.04 21.7 70.79 22.63 73.13 24.26C75.46 25.89 77.28 28.15 78.38 30.78C79.48 33.41 79.81 36.3 79.33 39.1V39.17ZM37.19 52.95L30.46 49.07C30.42 49.05 30.39 49.02 30.37 48.99C30.35 48.96 30.33 48.92 30.33 48.88V30.32C30.33 27.47 31.15 24.68 32.68 22.28C34.21 19.88 36.39 17.96 38.97 16.76C41.54 15.55 44.41 15.1 47.24 15.46C50.06 15.83 52.72 16.99 54.91 18.81L54.44 19.07L38.51 28.27C38.12 28.5 37.79 28.83 37.56 29.23C37.33 29.63 37.21 30.08 37.2 30.54L37.19 52.95ZM40.85 45.06L49.52 40.06L58.21 45.06V55.06L49.55 60.06L40.86 55.06L40.85 45.06Z\"\n    },\n    copilot: {\n      viewBox: \"0 0 96 96\",\n      path: \"M95.667 67.954C92.225 73.933 72.24 88.04 47.997 88.04 23.754 88.04 3.769 73.933.328 67.954c-.216-.375-.307-.796-.328-1.226V55.661c.019-.371.089-.736.226-1.081 1.489-3.738 5.386-9.166 10.417-10.623.667-1.712 1.655-4.215 2.576-6.062-.154-1.414-.208-2.872-.208-4.345 0-5.322 1.128-9.99 4.527-13.466 1.587-1.623 3.557-2.869 5.893-3.805 5.595-4.545 13.563-8.369 24.48-8.369s19.057 3.824 24.652 8.369c2.337.936 4.306 2.182 5.894 3.805 3.399 3.476 4.527 8.144 4.527 13.466 0 1.473-.054 2.931-.208 4.345.921 1.847 1.909 4.35 2.576 6.062 5.03 1.457 8.928 6.885 10.417 10.623.163.41.231.848.231 1.289v10.644c0 .504-.081 1.004-.333 1.441ZM48.686 43.993l-.3.001-1.077-.001c-.423.709-.894 1.39-1.418 2.035-3.078 3.787-7.672 5.964-14.026 5.964-6.897 0-11.952-1.435-15.123-5.032a7.886 7.886 0 0 1-.342-.419l-.39.419v26.326c5.737 3.118 18.05 8.713 31.987 8.713 13.938 0 26.251-5.595 31.988-8.713V46.96l-.39-.419s-.132.181-.342.419c-3.171 3.597-8.226 5.032-15.123 5.032-6.354 0-10.949-2.177-14.026-5.964a17.178 17.178 0 0 1-1.418-2.034h-.066l.066-.001Zm-3.94-11.733c.17-1.326.251-2.513.253-3.573v-.084c-.005-3.077-.678-5.079-1.752-6.308-1.365-1.562-4.184-2.758-10.127-2.115-6.021.652-9.386 2.146-11.294 4.098-1.847 1.889-2.818 4.715-2.818 9.272 0 4.842.698 7.703 2.232 9.443 1.459 1.655 4.332 3.001 10.625 3.001 4.837 0 7.603-1.573 9.371-3.749 1.899-2.336 2.967-5.759 3.51-9.985Zm6.503 0c.543 4.226 1.611 7.649 3.51 9.985 1.768 2.176 4.533 3.749 9.371 3.749 6.292 0 9.165-1.346 10.624-3.001 1.535-1.74 2.232-4.601 2.232-9.443 0-4.557-.97-7.383-2.817-9.272-1.908-1.952-5.274-3.446-11.294-4.098-5.943-.643-8.763.553-10.127 2.115-1.074 1.229-1.747 3.231-1.752 6.308v.084c.002 1.06.083 2.247.253 3.573Zm-2.563 11.734h.066l-.066-.001v.001Z\"\n    }\n  };\n\n  var content = document.getElementById(\"content\");\n  var demoBadge = document.getElementById(\"demo-badge\");\n  var updatedEl = document.getElementById(\"updated\");\n  var staleEl = document.getElementById(\"staleness\");\n  var connectionEl = document.getElementById(\"connection\");\n  var notificationEl = document.getElementById(\"notifications\");\n  var notificationButton = document.getElementById(\"notification-toggle\");\n  var notificationLabel = document.getElementById(\"notification-label\");\n\n  var id = null;\n  var payload = null; // last payload we managed to render\n  var loading = false;\n  var lastFetchAt = 0;\n  var serviceWorkerPromise = null;\n  var pushSubscription = null;\n  var notificationBusy = false;\n  var associatedPushReadId = null;\n\n  // --- small helpers ---------------------------------------------------\n\n  function el(tag, className, text) {\n    var node = document.createElement(tag);\n    if (className) node.className = className;\n    if (text !== undefined && text !== null) node.textContent = text;\n    return node;\n  }\n\n  function setText(node, text) {\n    // Only touch the DOM when the text changes: these are live regions and we\n    // do not want a screen reader to re-announce an unchanged notice.\n    if (node.textContent !== text) node.textContent = text;\n  }\n\n  function show(node, text) {\n    setText(node, text);\n    node.hidden = false;\n  }\n\n  function hide(node) {\n    node.hidden = true;\n    setText(node, \"\");\n  }\n\n  function clear(node) {\n    while (node.firstChild) node.removeChild(node.firstChild);\n  }\n\n  function renderProviderIcon(provider) {\n    var key = typeof provider === \"string\" ? provider.toLowerCase() : \"\";\n    var icon = el(\"span\", \"provider-icon provider-icon-\" + (key || \"unknown\"));\n    icon.setAttribute(\"aria-hidden\", \"true\");\n\n    var definition = PROVIDER_ICONS[key];\n    if (!definition) {\n      icon.classList.add(\"is-monogram\");\n      icon.textContent = key === \"zai\" ? \"Z\" : (key.charAt(0).toUpperCase() || \"?\");\n      return icon;\n    }\n\n    var svg = document.createElementNS(SVG_NS, \"svg\");\n    svg.setAttribute(\"viewBox\", definition.viewBox);\n    svg.setAttribute(\"focusable\", \"false\");\n    var path = document.createElementNS(SVG_NS, \"path\");\n    path.setAttribute(\"d\", definition.path);\n    svg.appendChild(path);\n    icon.appendChild(svg);\n    return icon;\n  }\n\n  // localStorage throws in some privacy modes; treat it as best-effort.\n  function readStored(key) {\n    try {\n      return window.localStorage.getItem(key);\n    } catch (error) {\n      return null;\n    }\n  }\n\n  function writeStored(key, value) {\n    try {\n      window.localStorage.setItem(key, value);\n    } catch (error) { /* nothing we can do, and nothing depends on it */ }\n  }\n\n  function pushSupported() {\n    return window.isSecureContext &&\n      \"Notification\" in window &&\n      \"PushManager\" in window &&\n      \"serviceWorker\" in navigator;\n  }\n\n  function applicationServerKey(value) {\n    var padding = \"=\".repeat((4 - value.length % 4) % 4);\n    var binary = atob((value + padding).replace(/-/g, \"+\").replace(/_/g, \"/\"));\n    return Uint8Array.from(binary, function (character) { return character.charCodeAt(0); });\n  }\n\n  function setNotificationButton(state, label, title, disabled) {\n    if (!notificationButton) return;\n    notificationButton.hidden = false;\n    notificationButton.disabled = Boolean(disabled);\n    notificationButton.setAttribute(\"data-state\", state);\n    notificationButton.setAttribute(\"aria-label\", title);\n    notificationButton.title = title;\n    if (notificationLabel) notificationLabel.textContent = label;\n  }\n\n  function syncNotificationControl() {\n    if (!notificationButton || id === DEMO_ID || !pushSupported() || !payload) {\n      if (notificationButton) notificationButton.hidden = true;\n      return Promise.resolve();\n    }\n\n    var alertsConfigured = hasEnabledAlertAccounts(payload);\n    return (serviceWorkerPromise || Promise.reject(new Error(\"service_worker_unavailable\")))\n      .then(function (registration) {\n        if (!registration) throw new Error(\"service_worker_unavailable\");\n        return registration.pushManager.getSubscription();\n      })\n      .then(function (subscription) {\n        pushSubscription = subscription;\n        if (subscription) {\n          var association = associatedPushReadId === id\n            ? Promise.resolve()\n            : fetch(API_BASE + \"/u/\" + id + \"/push-subscription\", {\n              method: \"POST\",\n              headers: { \"Content-Type\": \"application/json\" },\n              body: JSON.stringify(subscription.toJSON())\n            }).then(function (response) {\n              if (!response.ok) throw new Error(\"subscription_rejected\");\n              associatedPushReadId = id;\n            });\n          return association.then(function () {\n            setNotificationButton(\n              \"on\",\n              alertsConfigured ? \"Alerts on\" : \"Alerts paused\",\n              alertsConfigured\n                ? \"Browser alerts are on. Click to turn them off.\"\n                : \"Browser alerts are subscribed but no desktop account alert is enabled. Click to turn them off.\",\n              false\n            );\n          });\n        } else if (!alertsConfigured) {\n          notificationButton.hidden = true;\n        } else if (Notification.permission === \"denied\") {\n          setNotificationButton(\n            \"blocked\",\n            \"Alerts blocked\",\n            \"Notifications are blocked in this browser's site settings.\",\n            true\n          );\n        } else {\n          setNotificationButton(\n            \"off\",\n            \"Enable alerts\",\n            \"Enable browser alerts on this device.\",\n            false\n          );\n        }\n      })\n      .catch(function () {\n        if (hasEnabledAlertAccounts(payload)) {\n          setNotificationButton(\n            \"unavailable\",\n            \"Alerts unavailable\",\n            \"Browser alerts are unavailable on this device.\",\n            true\n          );\n        }\n      });\n  }\n\n  function unregisterBrowserAlerts() {\n    if (!pushSubscription) return Promise.resolve();\n    var subscription = pushSubscription;\n    return fetch(API_BASE + \"/u/\" + id + \"/push-subscription\", {\n      method: \"DELETE\",\n      headers: { \"Content-Type\": \"application/json\" },\n      body: JSON.stringify({ endpoint: subscription.endpoint })\n    })\n      .catch(function () { /* unsubscribe locally even if server cleanup fails */ })\n      .then(function () { return subscription.unsubscribe(); })\n      .then(function () {\n        pushSubscription = null;\n        associatedPushReadId = null;\n        show(notificationEl, \"Browser alerts are off on this device.\");\n      });\n  }\n\n  function registerBrowserAlerts() {\n    if (!hasEnabledAlertAccounts(payload)) return Promise.resolve();\n    return Notification.requestPermission()\n      .then(function (permission) {\n        if (permission !== \"granted\") throw new Error(\"permission_denied\");\n        return Promise.all([\n          serviceWorkerPromise,\n          fetch(API_BASE + \"/push/vapid-public-key\", {\n            cache: \"no-store\",\n            headers: { Accept: \"application/json\" }\n          }).then(function (response) {\n            if (!response.ok) throw new Error(\"push_not_configured\");\n            return response.json();\n          })\n        ]);\n      })\n      .then(function (results) {\n        var registration = results[0];\n        var configuration = results[1];\n        if (!registration) throw new Error(\"service_worker_unavailable\");\n        return registration.pushManager.subscribe({\n          userVisibleOnly: true,\n          applicationServerKey: applicationServerKey(configuration.publicKey)\n        }).then(function (subscription) {\n          return fetch(API_BASE + \"/u/\" + id + \"/push-subscription\", {\n            method: \"POST\",\n            headers: { \"Content-Type\": \"application/json\" },\n            body: JSON.stringify(subscription.toJSON())\n          }).then(function (response) {\n            if (!response.ok) {\n              return subscription.unsubscribe().then(function () {\n                throw new Error(\"subscription_rejected\");\n              });\n            }\n            pushSubscription = subscription;\n            associatedPushReadId = id;\n            return registration.showNotification(\"Browser alerts are ready\", {\n              body: \"This device will notify you when a selected quota crosses its threshold.\",\n              icon: \"icon-192.png\",\n              badge: \"icon-192.png\",\n              tag: \"usage-alert-test\",\n              data: { url: window.location.href }\n            });\n          });\n        });\n      })\n      .then(function () {\n        show(notificationEl, \"Browser alerts are on for this device.\");\n      });\n  }\n\n  function setupNotifications() {\n    if (!notificationButton || !pushSupported()) return;\n    notificationButton.addEventListener(\"click\", function () {\n      if (notificationBusy) return;\n      notificationBusy = true;\n      notificationButton.disabled = true;\n      var action = pushSubscription ? unregisterBrowserAlerts() : registerBrowserAlerts();\n      action.catch(function (error) {\n        var message = error && error.message === \"permission_denied\"\n          ? \"Notifications were not allowed. You can change this in the browser's site settings.\"\n          : \"Browser alerts could not be changed. Try again.\";\n        show(notificationEl, message);\n      }).then(function () {\n        notificationBusy = false;\n        return syncNotificationControl();\n      });\n    });\n  }\n\n  // --- theme -----------------------------------------------------------\n  // Auto (system) -> Light -> Dark -> Auto. Only the two forced modes set\n  // data-theme on <html>; Auto removes it and lets the media query decide.\n\n  var THEME_MODES = [\"auto\", \"light\", \"dark\"];\n  var THEME_LABELS = { auto: \"System\", light: \"Light\", dark: \"Dark\" };\n  var PAGE_COLORS = { light: \"#EDE8E0\", dark: \"#1A2233\" };\n\n  var themeButton = document.getElementById(\"theme-toggle\");\n  var themeMode = \"auto\";\n\n  // The two <meta name=\"theme-color\"> tags carry the Auto defaults. For a forced\n  // mode both are pinned to the same colour and their media queries dropped, so\n  // the browser chrome follows the page instead of the system.\n  function syncThemeColor(mode) {\n    var metas = document.querySelectorAll('meta[name=\"theme-color\"][data-scheme]');\n    for (var i = 0; i < metas.length; i++) {\n      var meta = metas[i];\n      var scheme = meta.getAttribute(\"data-scheme\");\n      if (mode === \"light\" || mode === \"dark\") {\n        meta.setAttribute(\"content\", PAGE_COLORS[mode]);\n        meta.removeAttribute(\"media\");\n      } else {\n        meta.setAttribute(\"content\", PAGE_COLORS[scheme]);\n        meta.setAttribute(\"media\", \"(prefers-color-scheme: \" + scheme + \")\");\n      }\n    }\n  }\n\n  function applyTheme(mode) {\n    themeMode = THEME_MODES.indexOf(mode) === -1 ? \"auto\" : mode;\n\n    if (themeMode === \"auto\") {\n      document.documentElement.removeAttribute(\"data-theme\");\n    } else {\n      document.documentElement.setAttribute(\"data-theme\", themeMode);\n    }\n    syncThemeColor(themeMode);\n\n    if (themeButton) {\n      var label = \"Theme: \" + THEME_LABELS[themeMode];\n      themeButton.setAttribute(\"data-mode\", themeMode);\n      themeButton.setAttribute(\"aria-label\", label);\n      themeButton.setAttribute(\"title\", label + \" (click to change)\");\n    }\n  }\n\n  function setupTheme() {\n    applyTheme(readStored(THEME_KEY) || \"auto\");\n    if (!themeButton) return;\n\n    themeButton.hidden = false;\n    themeButton.addEventListener(\"click\", function () {\n      var next = THEME_MODES[(THEME_MODES.indexOf(themeMode) + 1) % THEME_MODES.length];\n      applyTheme(next);\n      writeStored(THEME_KEY, next);\n    });\n  }\n\n  // --- percentage display ---------------------------------------------\n  // Number and fill show either usage or capacity left. The band still comes\n  // from canonical usage, so green always means capacity is available and red\n  // always means the quota is near exhaustion.\n\n  var percentToggle = document.getElementById(\"percent-toggle\");\n  var percentButtons = percentToggle\n    ? percentToggle.querySelectorAll(\"[data-percent-mode]\")\n    : [];\n  var percentMode = \"used\";\n  var hasPercentModeOverride = false;\n\n  function applyPercentMode(mode, renderPayload) {\n    percentMode = mode === \"left\" ? \"left\" : \"used\";\n\n    for (var i = 0; i < percentButtons.length; i++) {\n      var button = percentButtons[i];\n      button.setAttribute(\n        \"aria-pressed\",\n        button.getAttribute(\"data-percent-mode\") === percentMode ? \"true\" : \"false\"\n      );\n    }\n\n    if (payload && renderPayload !== false) render();\n  }\n\n  function setupPercentToggle() {\n    var storedMode = readStored(PERCENT_MODE_KEY);\n    hasPercentModeOverride = storedMode === \"left\" || storedMode === \"used\";\n    applyPercentMode(resolvePercentMode(storedMode, null), false);\n    if (!percentToggle) return;\n\n    percentToggle.hidden = false;\n    for (var i = 0; i < percentButtons.length; i++) {\n      percentButtons[i].addEventListener(\"click\", function () {\n        var next = this.getAttribute(\"data-percent-mode\");\n        hasPercentModeOverride = true;\n        applyPercentMode(next);\n        writeStored(PERCENT_MODE_KEY, percentMode);\n      });\n    }\n  }\n\n  // \"1d 12h\" / \"4h 21m\" / \"12m\"\n  function formatDuration(ms) {\n    var minutes = Math.floor(ms / 60000);\n    if (minutes < 1) return \"under a minute\";\n    var days = Math.floor(minutes / 1440);\n    var hours = Math.floor((minutes % 1440) / 60);\n    if (days > 0) return days + \"d \" + hours + \"h\";\n    if (hours > 0) return hours + \"h \" + (minutes % 60) + \"m\";\n    return minutes + \"m\";\n  }\n\n  function formatWhen(date) {\n    var sameDay = date.toDateString() === new Date().toDateString();\n    if (sameDay) {\n      return date.toLocaleTimeString([], { hour: \"2-digit\", minute: \"2-digit\" });\n    }\n    return date.toLocaleString([], {\n      month: \"short\", day: \"numeric\", hour: \"2-digit\", minute: \"2-digit\"\n    });\n  }\n\n  function parseDate(value) {\n    if (typeof value !== \"string\" || !value) return null;\n    var date = new Date(value);\n    return isNaN(date.getTime()) ? null : date;\n  }\n\n  // \"expires in 28 days\". Deliberately not a formatted date: the browser's own\n  // locale turned that into Hebrew on an otherwise English page. A day count\n  // reads the same everywhere.\n  //\n  // Counted in calendar days, not in 24h blocks, so \"today\" means today\n  // whatever the hour, and a daylight-saving shift cannot move the number.\n  // Returns null once the expiry day itself is behind us; the caller then drops\n  // the clause and keeps the rest of the chip.\n  function formatDaysUntil(date, now) {\n    var days = Math.round((startOfDay(date) - startOfDay(new Date(now))) / 86400000);\n    if (days < 0) return null;\n    if (days === 0) return \"expires today\";\n    if (days === 1) return \"expires in 1 day\";\n    return \"expires in \" + days + \" days\";\n  }\n\n  function startOfDay(date) {\n    return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();\n  }\n\n  // used -> band. The number alone decides: green 0-49, yellow 50-74,\n  // orange 75-89, red 90-100. The same rule runs on every surface of the\n  // product, so one percentage can never wear two colours.\n  //\n  // Provider-reported severity is still carried in the payload (the schema is\n  // untouched) but deliberately has no say here: a provider that called 71%\n  // \"normal\" used to paint it green while the widget painted it yellow.\n  function classify(used) {\n    if (used >= 90) return \"red\";\n    if (used >= 75) return \"orange\";\n    if (used >= 50) return \"yellow\";\n    return \"green\";\n  }\n\n  var STATE_RANK = { green: 1, yellow: 2, orange: 3, red: 4 };\n\n  function clampPercent(value) {\n    var number = typeof value === \"number\" ? value : Number(value);\n    if (!isFinite(number)) return 0;\n    return Math.min(100, Math.max(0, number));\n  }\n\n  // --- rendering -------------------------------------------------------\n\n  function renderMessage(heading, paragraphs) {\n    clear(content);\n    var box = el(\"section\", \"message\");\n    box.appendChild(el(\"h2\", null, heading));\n    paragraphs.forEach(function (part) {\n      var p = el(\"p\");\n      if (typeof part === \"string\") {\n        p.textContent = part;\n      } else {\n        p.appendChild(document.createTextNode(part.before || \"\"));\n        p.appendChild(el(\"span\", \"example\", part.example));\n        p.appendChild(document.createTextNode(part.after || \"\"));\n      }\n      box.appendChild(p);\n    });\n    content.appendChild(box);\n  }\n\n  function link(className, href, text) {\n    var node = el(\"a\", className, text);\n    node.href = href;\n    node.rel = \"noopener\";\n    return node;\n  }\n\n  // Shown when the link carries no id: explain what the page is and where the\n  // link comes from, nothing more.\n  function renderLanding() {\n    clear(content);\n\n    var box = el(\"section\", \"landing\");\n    box.appendChild(el(\"h2\", null, \"AI Usage Tray\"));\n    box.appendChild(el(\"p\", null,\n      \"This page shows live AI subscription usage (Claude, Codex, Z.AI and \" +\n      \"Copilot) shared from the AI Usage Tray app for Windows. To get your own \" +\n      \"link, install the app, enable Settings → Remote view and press Copy link.\"));\n\n    var actions = el(\"div\", \"landing-actions\");\n    actions.appendChild(link(\"button button-primary\", RELEASES_URL, \"Download for Windows\"));\n    actions.appendChild(link(\"button button-secondary\", REPO_URL, \"GitHub\"));\n    box.appendChild(actions);\n\n    content.appendChild(box);\n  }\n\n  function windowsOf(account) {\n    return Array.isArray(account.windows) ? account.windows : [];\n  }\n\n  // The account dot follows its worst window, by band.\n  function worstState(account) {\n    var rows = windowsOf(account);\n    var worst = null;\n    rows.forEach(function (row) {\n      if (!row || typeof row !== \"object\") return;\n      var state = classify(clampPercent(row.usedPercent));\n      if (worst === null || STATE_RANK[state] > STATE_RANK[worst]) worst = state;\n    });\n    return worst;\n  }\n\n  function orderAccounts(data) {\n    var accounts = Array.isArray(data.accounts) ? data.accounts.slice() : [];\n    var primaryIndex = -1;\n    if (typeof data.primary === \"string\" && data.primary) {\n      for (var i = 0; i < accounts.length; i++) {\n        if (accounts[i] && accounts[i].id === data.primary) {\n          primaryIndex = i;\n          break;\n        }\n      }\n    }\n    if (primaryIndex > 0) {\n      accounts.unshift(accounts.splice(primaryIndex, 1)[0]);\n    }\n    return accounts;\n  }\n\n  // Number and fill follow the selected view. The band stays tied to canonical\n  // usage so its warning meaning remains stable in both modes.\n  function renderMeterRow(accountName, source, now) {\n    var used = clampPercent(source.usedPercent);\n    var left = 100 - used;\n    var state = classify(used);\n    var label = typeof source.label === \"string\" && source.label ? source.label : \"Usage\";\n    var scope = typeof source.scope === \"string\" && source.scope ? source.scope : null;\n    var displayed = percentMode === \"left\" ? left : used;\n    var shown = Math.round(displayed);\n    var displayLabel = percentMode === \"left\" ? \"remaining\" : \"used\";\n\n    var row = el(\"div\", \"meter-row is-\" + state);\n\n    var top = el(\"div\", \"meter-top\");\n    var labelBox = el(\"div\", \"meter-label-row\");\n    labelBox.appendChild(el(\"span\", \"meter-label\", label));\n    // A model-scoped window: the chip is what separates \"Weekly\" for the whole\n    // account from \"Weekly\" for one model.\n    if (scope) labelBox.appendChild(el(\"span\", \"scope-chip\", scope));\n    top.appendChild(labelBox);\n    top.appendChild(el(\"span\", \"meter-value\", shown + \"%\"));\n    row.appendChild(top);\n\n    var track = el(\"div\", \"meter-track\");\n    track.setAttribute(\"role\", \"meter\");\n    track.setAttribute(\"aria-valuemin\", \"0\");\n    track.setAttribute(\"aria-valuemax\", \"100\");\n    track.setAttribute(\"aria-valuenow\", String(shown));\n    track.setAttribute(\"aria-valuetext\", shown + \"% \" + displayLabel);\n    track.setAttribute(\"aria-label\", accountName + \": \" + (scope ? scope + \" \" + label : label));\n\n    var fill = el(\"div\", \"meter-fill\");\n    fill.style.width = displayed + \"%\";\n    track.appendChild(fill);\n    row.appendChild(track);\n\n    var resetsAt = parseDate(source.resetsAt);\n    if (resetsAt) {\n      var delta = resetsAt.getTime() - now;\n      row.appendChild(el(\n        \"div\",\n        \"meter-reset\",\n        delta > 0 ? \"Resets in \" + formatDuration(delta) : \"Resetting now\"\n      ));\n    }\n\n    return row;\n  }\n\n  // Codex hands out redeemable \"usage limit reset\" credits. They belong to the\n  // account rather than to any one window, so they sit under the header as a\n  // quiet line of their own. Absent from the payload when there is none.\n  function renderResetCredits(source, now) {\n    if (!source || typeof source !== \"object\") return null;\n\n    var available = Math.floor(Number(source.available));\n    if (!isFinite(available) || available < 1) return null;\n\n    var text = available === 1\n      ? \"1 reset available\"\n      : available + \" resets available\";\n\n    var expiresAt = parseDate(source.expiresAt);\n    if (expiresAt) {\n      var expiry = formatDaysUntil(expiresAt, now);\n      if (expiry) text += \", \" + expiry;\n    }\n\n    var box = el(\"div\", \"resets\");\n    box.appendChild(el(\"span\", \"reset-chip\", text));\n    return box;\n  }\n\n  function renderCard(account, isPrimary, now) {\n    var name = typeof account.name === \"string\" && account.name ? account.name : \"Account\";\n    var card = el(\"article\", \"card\");\n\n    var head = el(\"div\", \"card-head\");\n    var worst = worstState(account);\n    var dotState = account.blocked ? \"red\" : (worst === null ? \"none\" : worst);\n    var dot = el(\"span\", \"dot is-\" + dotState);\n    dot.setAttribute(\"aria-hidden\", \"true\");\n    head.appendChild(dot);\n    head.appendChild(renderProviderIcon(account.provider));\n\n    var heading = el(\"h2\", \"account-name\", name);\n    if (isPrimary) {\n      var star = el(\"span\", \"star\", \"★\");\n      star.setAttribute(\"aria-hidden\", \"true\");\n      heading.appendChild(star);\n      heading.appendChild(el(\"span\", \"sr-only\", \" (primary account)\"));\n    }\n    head.appendChild(heading);\n\n    if (typeof account.plan === \"string\" && account.plan) {\n      head.appendChild(el(\"span\", \"chip\", account.plan));\n    }\n    card.appendChild(head);\n\n    // The provider is refusing requests right now. That is a different fact\n    // from any single window reading 100%, so it gets its own line.\n    if (account.blocked) {\n      card.appendChild(el(\"p\", \"blocked-banner\", \"Limit reached - requests are being refused.\"));\n    }\n\n    var resets = renderResetCredits(account.resetCredits, now);\n    if (resets) card.appendChild(resets);\n\n    var rows = windowsOf(account);\n    if (rows.length === 0) {\n      card.appendChild(el(\"p\", \"card-empty\", \"No usage windows reported.\"));\n      return card;\n    }\n\n    var meters = el(\"div\", \"meters\");\n    rows.forEach(function (source) {\n      if (source && typeof source === \"object\") {\n        meters.appendChild(renderMeterRow(name, source, now));\n      }\n    });\n    card.appendChild(meters);\n    return card;\n  }\n\n  function render() {\n    if (!payload) return;\n\n    var now = Date.now();\n    var generatedAt = parseDate(payload.generatedAt);\n\n    if (generatedAt) {\n      var age = now - generatedAt.getTime();\n      if (age < 0) age = 0;\n      show(updatedEl, age < 60000\n        ? \"Updated just now\"\n        : \"Updated \" + formatDuration(age) + \" ago\");\n\n      if (age > STALE_MS) {\n        show(staleEl, \"The app hasn't reported since \" + formatWhen(generatedAt) + \".\");\n      } else {\n        hide(staleEl);\n      }\n    } else {\n      hide(updatedEl);\n      hide(staleEl);\n    }\n\n    var accounts = orderAccounts(payload);\n    if (accounts.length === 0) {\n      renderMessage(\"No accounts yet\", [\n        \"The app is connected but hasn't reported any accounts.\"\n      ]);\n      return;\n    }\n\n    var list = el(\"div\", \"cards\");\n    accounts.forEach(function (account) {\n      if (!account || typeof account !== \"object\") return;\n      var isPrimary = typeof payload.primary === \"string\" && account.id === payload.primary;\n      list.appendChild(renderCard(account, isPrimary, now));\n    });\n\n    clear(content);\n    content.appendChild(list);\n  }\n\n  // --- data ------------------------------------------------------------\n\n  function load() {\n    if (loading || !id) return;\n    loading = true;\n\n    fetch(API_BASE + \"/u/\" + id, {\n      cache: \"no-store\",\n      headers: { Accept: \"application/json\" }\n    })\n      .then(function (response) {\n        if (response.status === 404) {\n          var missing = new Error(\"not_found\");\n          missing.code = 404;\n          throw missing;\n        }\n        if (!response.ok) throw new Error(\"http_\" + response.status);\n        return response.json();\n      })\n      .then(function (data) {\n        if (!data || typeof data !== \"object\") throw new Error(\"bad_payload\");\n        payload = data;\n        if (!hasPercentModeOverride) {\n          applyPercentMode(resolvePercentMode(null, data.displayMode), false);\n        }\n        lastFetchAt = Date.now();\n        hide(connectionEl);\n        render();\n        syncNotificationControl();\n      })\n      .catch(function (error) {\n        if (error && error.code === 404) {\n          payload = null;\n          hide(updatedEl);\n          hide(staleEl);\n          hide(connectionEl);\n          renderMessage(\"No data\", [\n            \"The link may have expired (data expires after about a week \" +\n            \"without the app running) or remote view is disabled.\"\n          ]);\n          return;\n        }\n\n        // Network or server hiccup: keep whatever is on screen and say so quietly.\n        if (payload) {\n          show(connectionEl, \"Couldn't refresh just now, retrying shortly.\");\n        } else {\n          renderMessage(\"Can't reach the server\", [\n            \"The usage data couldn't be loaded. This page keeps trying every minute.\",\n            \"If it never loads, check that the remote view address in config.js is correct.\"\n          ]);\n        }\n      })\n      .then(function () {\n        loading = false;\n      });\n  }\n\n  // --- start -----------------------------------------------------------\n\n  // Offline shell only; it needs a secure context and is never required.\n  function registerServiceWorker() {\n    if (!window.isSecureContext || !navigator.serviceWorker) return;\n    serviceWorkerPromise = navigator.serviceWorker.register(\"sw.js\")\n      .then(function () { return navigator.serviceWorker.ready; })\n      .catch(function () { return null; });\n  }\n\n  function resolveId() {\n    var params = new URLSearchParams(window.location.search);\n    var raw = (params.get(\"id\") || \"\").trim();\n    if (raw === DEMO_ID || ID_PATTERN.test(raw)) return raw;\n\n    // An explicit but unusable ?id= means \"show me the landing page\". Only a\n    // link with no id at all falls back to the last id this device saw. That\n    // is what an installed app opens, because start_url carries no id.\n    if (params.has(\"id\")) return null;\n\n    var stored = (readStored(LAST_ID_KEY) || \"\").trim();\n    return ID_PATTERN.test(stored) ? stored : null;\n  }\n\n  function start() {\n    setupTheme();\n    registerServiceWorker();\n    setupNotifications();\n\n    var resolved = resolveId();\n    if (!resolved) {\n      renderLanding();\n      return;\n    }\n\n    // An empty apiBase is allowed: it means the worker is proxied on this origin.\n    if (typeof CONFIG.apiBase !== \"string\" ||\n        API_BASE.indexOf(\"REPLACE-WITH-YOUR-WORKER-URL\") !== -1) {\n      renderMessage(\"Not configured yet\", [\n        \"This page hasn't been pointed at a remote view address.\",\n        { before: \"Set \", example: \"apiBase\", after: \" in config.js on the server.\" }\n      ]);\n      return;\n    }\n\n    setupPercentToggle();\n    id = resolved;\n    // The demo is never remembered: an installed app must not be left pointing\n    // at the sample because someone once opened the demo link on this device.\n    if (id === DEMO_ID) {\n      if (demoBadge) demoBadge.hidden = false;\n    } else {\n      writeStored(LAST_ID_KEY, id);\n    }\n    renderMessage(\"Loading…\", [\"Fetching the latest usage snapshot.\"]);\n    load();\n\n    window.setInterval(load, REFRESH_MS);\n    window.setInterval(render, TICK_MS);\n\n    document.addEventListener(\"visibilitychange\", function () {\n      if (document.visibilityState !== \"visible\") return;\n      render();\n      if (Date.now() - lastFetchAt >= REFRESH_MS) load();\n    });\n  }\n\n  start();\n})();\n",
  },
  "/config.js": {
    type: "text/javascript; charset=utf-8",
    body: "window.REMOTE_VIEW_CONFIG = { apiBase: \"\" };\n",
  },
  "/manifest.webmanifest": {
    type: "application/manifest+json; charset=utf-8",
    body: "{\n  \"name\": \"AI Usage Tray\",\n  \"short_name\": \"AI Usage\",\n  \"description\": \"Live AI subscription usage shared from the AI Usage Tray app for Windows.\",\n  \"start_url\": \"./\",\n  \"scope\": \"./\",\n  \"display\": \"standalone\",\n  \"orientation\": \"portrait-primary\",\n  \"background_color\": \"#1A2233\",\n  \"theme_color\": \"#1A2233\",\n  \"icons\": [\n    { \"src\": \"./icon-192.png\", \"sizes\": \"192x192\", \"type\": \"image/png\", \"purpose\": \"any\" },\n    { \"src\": \"./icon-512.png\", \"sizes\": \"512x512\", \"type\": \"image/png\", \"purpose\": \"any\" }\n  ]\n}\n",
  },
  "/sw.js": {
    type: "text/javascript; charset=utf-8",
    cache: "no-store",
    body: "// AI Usage Tray - remote view service worker.\n//\n// Its only job is to make the page installable and to survive a flaky\n// connection: the static shell is cached, the usage snapshot never is.\n// Bump CACHE whenever a shell file changes: a new cache name is what makes\n// the update land.\n\n// v11: adds opt-in Web Push notifications for per-account quota thresholds.\nvar CACHE = \"ai-usage-tray-shell-v11\";\n\nvar SHELL = [\n  \"./\",\n  \"./index.html\",\n  \"./styles.css\",\n  \"./app.js\",\n  \"./config.js\",\n  \"./manifest.webmanifest\",\n  \"./icon-192.png\",\n  \"./icon-512.png\"\n];\n\nself.addEventListener(\"install\", function (event) {\n  event.waitUntil(\n    caches.open(CACHE)\n      .then(function (cache) { return cache.addAll(SHELL); })\n      // A single missing file must not block installation.\n      .catch(function () { /* ignore */ })\n      .then(function () { return self.skipWaiting(); })\n  );\n});\n\nself.addEventListener(\"activate\", function (event) {\n  event.waitUntil(\n    caches.keys()\n      .then(function (keys) {\n        return Promise.all(keys.map(function (key) {\n          return key === CACHE ? null : caches.delete(key);\n        }));\n      })\n      .then(function () { return self.clients.claim(); })\n  );\n});\n\nself.addEventListener(\"fetch\", function (event) {\n  var request = event.request;\n  if (request.method !== \"GET\") return;\n\n  var url;\n  try {\n    url = new URL(request.url);\n  } catch (error) {\n    return;\n  }\n\n  // Usage snapshots and anything cross-origin go straight to the network,\n  // uncached: stale usage numbers would be worse than none.\n  if (url.origin !== self.location.origin) return;\n  if (url.pathname.indexOf(\"/u/\") !== -1) return;\n\n  // ignoreSearch so a shared link (/?id=…) still matches the cached shell.\n  event.respondWith(\n    caches.match(request, { ignoreSearch: true }).then(function (hit) {\n      if (hit) return hit;\n\n      return fetch(request).then(function (response) {\n        if (response && response.ok && response.type === \"basic\") {\n          var copy = response.clone();\n          caches.open(CACHE).then(function (cache) {\n            cache.put(request, copy);\n          }).catch(function () { /* quota or private mode */ });\n        }\n        return response;\n      });\n    })\n  );\n});\n\nself.addEventListener(\"push\", function (event) {\n  var message;\n  try {\n    message = event.data ? event.data.json() : null;\n  } catch (error) {\n    message = null;\n  }\n\n  var alerts = Array.isArray(message && message.alerts) ? message.alerts : [];\n  var displayMode = message && message.displayMode === \"remaining\" ? \"remaining\" : \"used\";\n  var viewerUrl = new URL(\"./\", self.registration.scope);\n  if (message && typeof message.readId === \"string\") {\n    viewerUrl.searchParams.set(\"id\", message.readId);\n  }\n\n  if (alerts.length === 0) {\n    event.waitUntil(self.registration.showNotification(\"AI Usage Tray alert\", {\n      body: \"A configured usage threshold was reached.\",\n      icon: \"icon-192.png\",\n      badge: \"icon-192.png\",\n      tag: \"usage-alert\",\n      data: { url: viewerUrl.href }\n    }));\n    return;\n  }\n\n  event.waitUntil(Promise.all(alerts.map(function (alert) {\n    var used = Math.max(0, Math.min(100, Math.round(Number(alert.usedPercent) || 0)));\n    var shown = displayMode === \"remaining\" ? 100 - used : used;\n    var accountName = typeof alert.accountName === \"string\" && alert.accountName\n      ? alert.accountName\n      : \"Account\";\n    var windowName = typeof alert.windowLabel === \"string\" && alert.windowLabel\n      ? alert.windowLabel\n      : \"Usage\";\n    if (typeof alert.scope === \"string\" && alert.scope) {\n      windowName += \" · \" + alert.scope;\n    }\n    return self.registration.showNotification(accountName + \" usage alert\", {\n      body: windowName + \" is at \" + shown + \"% \" + displayMode + \".\",\n      icon: \"icon-192.png\",\n      badge: \"icon-192.png\",\n      tag: \"usage-alert:\" + String(alert.accountId || \"account\") + \":\" + String(alert.windowKey || \"window\"),\n      renotify: false,\n      data: { url: viewerUrl.href }\n    });\n  })));\n});\n\nself.addEventListener(\"notificationclick\", function (event) {\n  event.notification.close();\n  var url = event.notification.data && event.notification.data.url\n    ? event.notification.data.url\n    : new URL(\"./\", self.registration.scope).href;\n  event.waitUntil(\n    clients.matchAll({ type: \"window\", includeUncontrolled: true })\n      .then(function (windows) {\n        for (var i = 0; i < windows.length; i++) {\n          if (\"navigate\" in windows[i]) {\n            return windows[i].navigate(url).then(function (client) { return client.focus(); });\n          }\n        }\n        return clients.openWindow(url);\n      })\n  );\n});\n",
  },
  "/icon-192.png": {
    type: "image/png",
    base64: "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",
  },
  "/icon-512.png": {
    type: "image/png",
    base64: "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",
  },
};

// Binary assets travel as base64 and are decoded once, on first request.
function decode(entry) {
  if (!entry.bytes) {
    const binary = atob(entry.base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    entry.bytes = bytes;
  }
  return entry.bytes;
}

function asset(entry) {
  const headers = {
    ...SECURITY,
    "Content-Type": entry.type,
    "Cache-Control": entry.cache || STATIC_CACHE,
  };
  if (entry.html) Object.assign(headers, HTML_HEADERS);
  return new Response(entry.base64 === undefined ? entry.body : decode(entry), {
    status: 200,
    headers,
  });
}

export default {
  async fetch(request, env) {
    if (request.method === "GET") {
      const path = new URL(request.url).pathname;
      const key = path === "/" ? "/index.html" : path;
      if (Object.prototype.hasOwnProperty.call(ASSETS, key)) {
        return asset(ASSETS[key]);
      }
    }
    // /u/{id}, CORS preflight, and every 404/405 stay with the API logic above.
    return api.fetch(request, env);
  },
};
