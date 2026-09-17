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

function resetAlertsEnabled(account) {
  return account?.alert?.enabled === true && account?.alert?.resetEnabled === true;
}

function windowKey(window) {
  const label = typeof window?.label === "string" ? window.label.trim().toLowerCase() : "usage";
  const scope = typeof window?.scope === "string" ? window.scope.trim().toLowerCase() : "";
  return scope ? `${label}:${scope}` : label;
}

function isWeeklyWindow(window) {
  return typeof window?.label === "string" && window.label.trim().toLowerCase() === "weekly";
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

function findResetAlerts(previousSnapshot, currentSnapshot) {
  if (!previousSnapshot || typeof previousSnapshot !== "object") return [];
  const previousAccounts = new Map(
    (Array.isArray(previousSnapshot.accounts) ? previousSnapshot.accounts : [])
      .filter((account) => account && typeof account.id === "string")
      .map((account) => [account.id.toLowerCase(), account]),
  );
  const resets = [];

  for (const account of Array.isArray(currentSnapshot?.accounts) ? currentSnapshot.accounts : []) {
    if (!account || typeof account.id !== "string" || !resetAlertsEnabled(account)) continue;
    const previousAccount = previousAccounts.get(account.id.toLowerCase());
    if (!previousAccount || !resetAlertsEnabled(previousAccount)) continue;
    const previousWindows = windowsByKey(previousAccount);
    const accountResets = [];

    for (const [key, currentWindow] of windowsByKey(account)) {
      if (!isWeeklyWindow(currentWindow)) continue;
      const previousWindow = previousWindows.get(key);
      if (!previousWindow || previousWindow.usedPercent <= 0 || currentWindow.usedPercent !== 0) continue;

      accountResets.push({
        accountId: account.id,
        accountName: typeof account.name === "string" && account.name ? account.name : account.id,
        windowKey: key,
        windowLabel: typeof currentWindow.label === "string" && currentWindow.label
          ? currentWindow.label
          : "Usage",
        scope: typeof currentWindow.scope === "string" && currentWindow.scope
          ? currentWindow.scope
          : null,
      });
    }

    // Scoped model windows reset together with the account-wide weekly
    // window; when both fire in one snapshot, one alert per account is enough.
    const accountWide = accountResets.find((reset) => reset.scope === null);
    if (accountWide) {
      resets.push(accountWide);
    } else {
      resets.push(...accountResets);
    }
  }

  return resets;
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

async function deliverAlerts(env, readId, data, crossings, resets) {
  const configuration = vapidConfiguration(env);
  if (configuration === null || (crossings.length === 0 && resets.length === 0)) return;
  const listed = await env.USAGE.list({ prefix: subscriptionPrefix(readId) });
  const message = {
    type: "usage-alerts",
    readId,
    displayMode: data.displayMode === "remaining" ? "remaining" : "used",
    alerts: crossings,
    resets,
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
  const resets = findResetAlerts(previous, data);
  await env.USAGE.put(readId, body, { expirationTtl: TTL_SECONDS });
  await refreshSubscriptions(env, readId);
  const delivery = deliverAlerts(env, readId, data, crossings, resets);
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
    if (request.method === "GET" && path === "/version") {
      return json(200, { version: env.REMOTE_VIEW_VERSION || "1.1.4" });
    }
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
    body: "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<meta name=\"color-scheme\" content=\"light dark\">\n<meta name=\"robots\" content=\"noindex, nofollow\">\n<meta name=\"theme-color\" data-scheme=\"light\" media=\"(prefers-color-scheme: light)\" content=\"#EDE8E0\">\n<meta name=\"theme-color\" data-scheme=\"dark\" media=\"(prefers-color-scheme: dark)\" content=\"#1A2233\">\n<title>AI Usage Tray</title>\n<link rel=\"icon\" type=\"image/png\" href=\"icon-192.png\">\n<link rel=\"apple-touch-icon\" href=\"icon-192.png\">\n<link rel=\"manifest\" href=\"manifest.webmanifest\">\n<link rel=\"stylesheet\" href=\"styles.css\">\n<script>\n  // Applied before first paint so a forced theme never flashes the other palette.\n  try {\n    var saved = localStorage.getItem(\"aiUsageTray.theme\");\n    if (saved === \"light\" || saved === \"dark\") {\n      document.documentElement.setAttribute(\"data-theme\", saved);\n    }\n  } catch (e) { /* private mode: stay on the system theme */ }\n</script>\n</head>\n<body>\n<main class=\"page\">\n  <header class=\"page-header\">\n    <div class=\"header-row\">\n      <div class=\"title-group\">\n        <h1>AI usage</h1>\n        <span class=\"version-badge\" id=\"remote-version\" hidden></span>\n      </div>\n      <span class=\"demo-badge\" id=\"demo-badge\" hidden>Demo data</span>\n      <div class=\"header-actions\">\n        <div class=\"percent-toggle\" id=\"percent-toggle\" role=\"group\"\n             aria-label=\"Percentage display\" hidden>\n          <button type=\"button\" class=\"percent-option\" data-percent-mode=\"used\"\n                  aria-pressed=\"true\">% used</button>\n          <button type=\"button\" class=\"percent-option\" data-percent-mode=\"left\"\n                  aria-pressed=\"false\">% left</button>\n        </div>\n        <button type=\"button\" class=\"notification-toggle\" id=\"notification-toggle\" hidden>\n          <svg viewBox=\"0 0 24 24\" width=\"17\" height=\"17\" fill=\"none\" stroke=\"currentColor\"\n               stroke-width=\"1.9\" stroke-linecap=\"round\" stroke-linejoin=\"round\"\n               aria-hidden=\"true\" focusable=\"false\">\n            <path d=\"M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9\"/>\n            <path d=\"M10 21h4\"/>\n          </svg>\n          <span id=\"notification-label\">Enable alerts</span>\n        </button>\n        <button type=\"button\" class=\"notification-test\" id=\"notification-test\" hidden>\n          <svg viewBox=\"0 0 24 24\" width=\"17\" height=\"17\" fill=\"none\" stroke=\"currentColor\"\n               stroke-width=\"1.9\" stroke-linecap=\"round\" stroke-linejoin=\"round\"\n               aria-hidden=\"true\" focusable=\"false\">\n            <path d=\"M9 3h6\"/>\n            <path d=\"M10 3v5.4L5.4 17a2.7 2.7 0 0 0 2.4 4h8.4a2.7 2.7 0 0 0 2.4-4L14 8.4V3\"/>\n            <path d=\"M7.5 15h9\"/>\n          </svg>\n          <span>Test notification</span>\n        </button>\n        <button type=\"button\" class=\"theme-toggle\" id=\"theme-toggle\" data-mode=\"auto\" hidden>\n          <svg class=\"theme-icon theme-icon-auto\" viewBox=\"0 0 24 24\" width=\"18\" height=\"18\"\n               fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" aria-hidden=\"true\" focusable=\"false\">\n            <circle cx=\"12\" cy=\"12\" r=\"8.2\"/>\n            <path d=\"M12 3.8a8.2 8.2 0 0 1 0 16.4z\" fill=\"currentColor\" stroke=\"none\"/>\n          </svg>\n          <svg class=\"theme-icon theme-icon-light\" viewBox=\"0 0 24 24\" width=\"18\" height=\"18\"\n               fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\"\n               aria-hidden=\"true\" focusable=\"false\">\n            <circle cx=\"12\" cy=\"12\" r=\"4.2\"/>\n            <path d=\"M12 2.5v2.2M12 19.3v2.2M4.22 4.22l1.56 1.56M18.22 18.22l1.56 1.56M2.5 12h2.2M19.3 12h2.2M4.22 19.78l1.56-1.56M18.22 5.78l1.56-1.56\"/>\n          </svg>\n          <svg class=\"theme-icon theme-icon-dark\" viewBox=\"0 0 24 24\" width=\"18\" height=\"18\"\n               fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linejoin=\"round\"\n               aria-hidden=\"true\" focusable=\"false\">\n            <path d=\"M20 13.6A8.2 8.2 0 0 1 10.4 4a8.2 8.2 0 1 0 9.6 9.6z\"/>\n          </svg>\n        </button>\n      </div>\n    </div>\n    <p class=\"updated\" id=\"updated\" hidden></p>\n  </header>\n\n  <p class=\"notice notice-stale\" id=\"staleness\" role=\"status\" hidden></p>\n  <p class=\"notice notice-quiet\" id=\"connection\" role=\"status\" hidden></p>\n  <p class=\"notice notice-quiet\" id=\"notifications\" role=\"status\" hidden></p>\n\n  <div id=\"content\"></div>\n</main>\n\n<script src=\"config.js\"></script>\n<script src=\"app.js\"></script>\n</body>\n</html>\n",
  },
  "/styles.css": {
    type: "text/css; charset=utf-8",
    body: "/* AI Usage Tray - remote view.\r\n   Light and dark are two hand-picked palettes, not an inversion of one another:\r\n   light is \"Warm stone\" (sand surfaces, deep amber accent) and dark is\r\n   \"Blue steel\" (navy surfaces, sky accent). Both come from the app, so the\r\n   page and the Windows widget read as one product. No purple anywhere.\r\n\r\n   Theme resolution:\r\n     no data-theme         -> follow prefers-color-scheme (the \"Auto\" mode)\r\n     data-theme=\"light\"    -> stay light even on a dark system\r\n     data-theme=\"dark\"     -> dark everywhere\r\n   The dark tokens live in two blocks with identical bodies: the media query\r\n   (Auto) and the explicit attribute. Keep them in sync when editing. */\r\n\r\n:root {\r\n  color-scheme: light dark;\r\n\r\n  /* --- Warm stone (light) --- */\r\n  --page: #EDE8E0;\r\n  --panel: #F7F3EC;\r\n  --card: rgba(255, 255, 255, 0.50);\r\n  --border: #DAD1C2;\r\n  --chip-bg: rgba(0, 0, 0, 0.05);\r\n\r\n  --ink: #2A2318;\r\n  --ink-2: #4C4234;\r\n  --ink-3: #5E5548;\r\n\r\n  --accent: #B45309;\r\n  --accent-hover: #92400E;\r\n  --on-accent: #FFFFFF;\r\n  --notification-on: #047857;\r\n  --notification-on-hover: #065F46;\r\n  --notification-on-ink: #FFFFFF;\r\n\r\n  --track: #DFD6C7;\r\n  --none: #706659;\r\n\r\n  /* --- Usage bands ---------------------------------------------------\r\n     The number alone picks the band: green 0-49, yellow 50-74,\r\n     orange 75-89, red 90-100. The vivid fill colours are identical in\r\n     both themes; only the ink changes. Bars are plain fills: no outline,\r\n     no inset edge, in either theme. */\r\n  --band-green: #10B981;\r\n  --band-yellow: #EAB308;\r\n  --band-orange: #F97316;\r\n  --band-red: #EF4444;\r\n\r\n  /* The percent chip: the band colour at 18% over whatever sits behind it.\r\n     15% flattened red over the dark card into the banned purple range; 18%\r\n     stays clear of it. */\r\n  --tint-green: rgba(16, 185, 129, 0.18);\r\n  --tint-yellow: rgba(234, 179, 8, 0.18);\r\n  --tint-orange: rgba(249, 115, 22, 0.18);\r\n  --tint-red: rgba(239, 68, 68, 0.18);\r\n\r\n  /* Percent ink: the same hue, one step deeper on light. */\r\n  --ink-green: #065F46;\r\n  --ink-yellow: #854D0E;\r\n  --ink-orange: #9A3412;\r\n  --ink-red: #991B1B;\r\n\r\n  --stale-bg: var(--tint-yellow);\r\n  --stale-border: #A16207;\r\n  --stale-ink: #854D0E;\r\n\r\n  --shadow: 0 1px 2px rgba(42, 35, 24, 0.06);\r\n}\r\n\r\n/* Auto: the system decides, unless the user has explicitly forced light. */\r\n@media (prefers-color-scheme: dark) {\r\n  :root:not([data-theme=\"light\"]) {\r\n    /* --- Blue steel (dark) --- */\r\n    --page: #1A2233;\r\n    --panel: #1F2839;\r\n    --card: rgba(255, 255, 255, 0.08);\r\n    --border: #33405A;\r\n    --chip-bg: rgba(255, 255, 255, 0.08);\r\n\r\n    --ink: #EFF4FB;\r\n    --ink-2: #C4CFE0;\r\n    --ink-3: #9EABC0;\r\n\r\n    --accent: #38BDF8;\r\n    --accent-hover: #7DD3FC;\r\n    --on-accent: #16202E;\r\n    --notification-on: #10B981;\r\n    --notification-on-hover: #0EA371;\r\n    --notification-on-ink: #052E24;\r\n\r\n    --track: #33405A;\r\n    --none: #8996AC;\r\n\r\n    --ink-green: #6EE7B7;\r\n    --ink-yellow: #FDE047;\r\n    --ink-orange: #FDBA74;\r\n    --ink-red: #FCA5A5;\r\n\r\n    --stale-border: rgba(253, 224, 71, 0.35);\r\n    --stale-ink: #FDE047;\r\n\r\n    --shadow: none;\r\n  }\r\n}\r\n\r\n/* Forced dark: same body as the media query above. */\r\n:root[data-theme=\"dark\"] {\r\n  --page: #1A2233;\r\n  --panel: #1F2839;\r\n  --card: rgba(255, 255, 255, 0.08);\r\n  --border: #33405A;\r\n  --chip-bg: rgba(255, 255, 255, 0.08);\r\n\r\n  --ink: #EFF4FB;\r\n  --ink-2: #C4CFE0;\r\n  --ink-3: #9EABC0;\r\n\r\n  --accent: #38BDF8;\r\n  --accent-hover: #7DD3FC;\r\n  --on-accent: #16202E;\r\n  --notification-on: #10B981;\r\n  --notification-on-hover: #0EA371;\r\n  --notification-on-ink: #052E24;\r\n\r\n  --track: #33405A;\r\n  --none: #8996AC;\r\n\r\n  --ink-green: #6EE7B7;\r\n  --ink-yellow: #FDE047;\r\n  --ink-orange: #FDBA74;\r\n  --ink-red: #FCA5A5;\r\n\r\n  --stale-border: rgba(253, 224, 71, 0.35);\r\n  --stale-ink: #FDE047;\r\n\r\n  --shadow: none;\r\n}\r\n\r\n/* Forced modes also pin the UA colours (scrollbars, form controls). */\r\n:root[data-theme=\"light\"] { color-scheme: light; }\r\n:root[data-theme=\"dark\"] { color-scheme: dark; }\r\n\r\n* { box-sizing: border-box; }\r\n\r\nbody {\r\n  margin: 0;\r\n  padding: 20px 16px 48px;\r\n  background: var(--page);\r\n  color: var(--ink);\r\n  font-family: \"Segoe UI\", -apple-system, BlinkMacSystemFont, system-ui, sans-serif;\r\n  font-size: 15px;\r\n  line-height: 1.45;\r\n  -webkit-text-size-adjust: 100%;\r\n}\r\n\r\n.page {\r\n  max-width: 1100px;\r\n  margin: 0 auto;\r\n}\r\n\r\n.page-header {\r\n  margin: 0 0 16px;\r\n}\r\n\r\n.header-row {\r\n  display: flex;\r\n  align-items: center;\r\n  gap: 12px;\r\n  flex-wrap: wrap;\r\n}\r\n\r\n.title-group {\r\n  flex: none;\r\n  display: inline-flex;\r\n  align-items: baseline;\r\n  gap: 5px;\r\n}\r\n\r\nh1 {\r\n  margin: 0;\r\n  font-size: 20px;\r\n  font-weight: 600;\r\n  letter-spacing: -0.01em;\r\n}\r\n\r\n.updated {\r\n  margin: 2px 0 0;\r\n  color: var(--ink-3);\r\n  font-size: 13px;\r\n}\r\n\r\n/* Sample data, not a real account: quiet enough to ignore, present enough to\r\n   stop anyone reading the numbers as their own. */\r\n.version-badge,\r\n.demo-badge {\r\n  padding: 2px 8px;\r\n  border: 1px solid var(--border);\r\n  border-radius: 999px;\r\n  background: var(--chip-bg);\r\n  color: var(--ink-3);\r\n  font-size: 12px;\r\n  font-weight: 500;\r\n  white-space: nowrap;\r\n}\r\n\r\n.version-badge {\r\n  padding: 1px 5px;\r\n  font-size: 10px;\r\n  line-height: 1.3;\r\n  font-variant-numeric: tabular-nums;\r\n  letter-spacing: 0.01em;\r\n}\r\n\r\n:where(a, button):focus-visible {\r\n  outline: 2px solid var(--accent);\r\n  outline-offset: 2px;\r\n  border-radius: 8px;\r\n}\r\n\r\n/* Header controls ------------------------------------------------------- */\r\n\r\n.header-actions {\r\n  margin-left: auto;\r\n  display: flex;\r\n  align-items: center;\r\n  gap: 8px;\r\n}\r\n\r\n.percent-toggle {\r\n  flex: none;\r\n  display: inline-flex;\r\n  align-items: center;\r\n  padding: 2px;\r\n  border: 1px solid var(--border);\r\n  border-radius: 9px;\r\n  background: var(--panel);\r\n  box-shadow: var(--shadow);\r\n}\r\n\r\n.percent-option {\r\n  min-height: 28px;\r\n  padding: 0 8px;\r\n  border: 0;\r\n  border-radius: 6px;\r\n  background: transparent;\r\n  color: var(--ink-3);\r\n  font: inherit;\r\n  font-size: 11.5px;\r\n  font-weight: 600;\r\n  white-space: nowrap;\r\n  cursor: pointer;\r\n}\r\n\r\n.percent-option:hover { color: var(--ink); }\r\n\r\n.percent-option[aria-pressed=\"true\"] {\r\n  background: var(--chip-bg);\r\n  color: var(--ink);\r\n}\r\n\r\n.notification-toggle {\r\n  --expanded-label-width: 96px;\r\n  --expanded-width: 137px;\r\n  flex: none;\r\n  display: inline-flex;\r\n  align-items: center;\r\n  justify-content: center;\r\n  gap: 0;\r\n  width: 35px;\r\n  min-width: 34px;\r\n  min-height: 34px;\r\n  padding: 0 8px;\r\n  overflow: hidden;\r\n  border: 1px solid var(--border);\r\n  border-radius: 9px;\r\n  background: var(--panel);\r\n  box-shadow: var(--shadow);\r\n  color: var(--ink-2);\r\n  font: inherit;\r\n  font-size: 11.5px;\r\n  font-weight: 600;\r\n  white-space: nowrap;\r\n  cursor: pointer;\r\n  transition:\r\n    width 420ms cubic-bezier(0.4, 0, 0.2, 1),\r\n    background-color 240ms ease,\r\n    border-color 240ms ease,\r\n    color 240ms ease;\r\n}\r\n\r\n.notification-toggle[hidden] { display: none; }\r\n\r\n.notification-toggle svg { flex: none; }\r\n\r\n.notification-toggle:hover:not(:disabled),\r\n.notification-toggle:focus-visible:not(:disabled) {\r\n  color: var(--ink);\r\n  border-color: var(--ink-3);\r\n}\r\n\r\n.notification-toggle[data-state=\"on\"] {\r\n  background: var(--notification-on);\r\n  border-color: var(--notification-on);\r\n  color: var(--notification-on-ink);\r\n}\r\n\r\n.notification-toggle[data-state=\"on\"]:hover:not(:disabled),\r\n.notification-toggle[data-state=\"on\"]:focus-visible:not(:disabled) {\r\n  background: var(--notification-on-hover);\r\n  border-color: var(--notification-on-hover);\r\n  color: var(--notification-on-ink);\r\n}\r\n\r\n.notification-toggle:disabled {\r\n  cursor: default;\r\n  opacity: 0.7;\r\n}\r\n\r\n.notification-test {\r\n  --expanded-label-width: 83px;\r\n  --expanded-width: 124px;\r\n  flex: none;\r\n  display: inline-flex;\r\n  align-items: center;\r\n  justify-content: center;\r\n  gap: 0;\r\n  width: 35px;\r\n  min-width: 34px;\r\n  min-height: 34px;\r\n  padding: 0 8px;\r\n  overflow: hidden;\r\n  border: 1px solid var(--border);\r\n  border-radius: 9px;\r\n  background: transparent;\r\n  color: var(--ink-3);\r\n  font: inherit;\r\n  font-size: 11.5px;\r\n  font-weight: 600;\r\n  white-space: nowrap;\r\n  cursor: pointer;\r\n  transition:\r\n    width 420ms cubic-bezier(0.4, 0, 0.2, 1),\r\n    background-color 240ms ease,\r\n    border-color 240ms ease,\r\n    color 240ms ease;\r\n}\r\n\r\n.notification-test[hidden] { display: none; }\r\n\r\n.notification-test svg { flex: none; }\r\n\r\n.notification-toggle span,\r\n.notification-test span {\r\n  width: 0;\r\n  margin-left: 0;\r\n  overflow: hidden;\r\n  opacity: 0;\r\n  transform: translateX(-3px);\r\n  transition:\r\n    width 420ms cubic-bezier(0.4, 0, 0.2, 1),\r\n    margin-left 420ms cubic-bezier(0.4, 0, 0.2, 1),\r\n    opacity 140ms ease,\r\n    transform 420ms cubic-bezier(0.4, 0, 0.2, 1);\r\n}\r\n\r\n.notification-toggle:hover,\r\n.notification-toggle:focus-visible,\r\n.notification-test:hover,\r\n.notification-test:focus-visible {\r\n  width: var(--expanded-width);\r\n}\r\n\r\n.notification-toggle:hover span,\r\n.notification-toggle:focus-visible span,\r\n.notification-test:hover span,\r\n.notification-test:focus-visible span {\r\n  width: var(--expanded-label-width);\r\n  margin-left: 6px;\r\n  opacity: 1;\r\n  transform: translateX(0);\r\n  transition:\r\n    width 420ms cubic-bezier(0.4, 0, 0.2, 1),\r\n    margin-left 420ms cubic-bezier(0.4, 0, 0.2, 1),\r\n    opacity 220ms ease 90ms,\r\n    transform 420ms cubic-bezier(0.4, 0, 0.2, 1);\r\n}\r\n\r\n.notification-test:hover:not(:disabled),\r\n.notification-test:focus-visible:not(:disabled) {\r\n  color: var(--ink);\r\n  border-color: var(--ink-3);\r\n}\r\n\r\n.notification-test:disabled {\r\n  cursor: default;\r\n  opacity: 0.7;\r\n}\r\n\r\n.theme-toggle {\r\n  flex: none;\r\n  display: inline-flex;\r\n  align-items: center;\r\n  justify-content: center;\r\n  width: 34px;\r\n  height: 34px;\r\n  padding: 0;\r\n  border: 1px solid var(--border);\r\n  border-radius: 9px;\r\n  background: var(--panel);\r\n  box-shadow: var(--shadow);\r\n  color: var(--ink-2);\r\n  cursor: pointer;\r\n}\r\n\r\n.theme-toggle:hover {\r\n  color: var(--ink);\r\n  border-color: var(--ink-3);\r\n}\r\n\r\n.theme-icon { display: none; }\r\n.theme-toggle[data-mode=\"auto\"] .theme-icon-auto,\r\n.theme-toggle[data-mode=\"light\"] .theme-icon-light,\r\n.theme-toggle[data-mode=\"dark\"] .theme-icon-dark { display: block; }\r\n\r\n/* Notices ------------------------------------------------------------- */\r\n\r\n.notice {\r\n  margin: 0 0 12px;\r\n  padding: 10px 12px;\r\n  border-radius: 10px;\r\n  font-size: 13px;\r\n}\r\n\r\n.notice-stale {\r\n  background: var(--stale-bg);\r\n  border: 1px solid var(--stale-border);\r\n  color: var(--stale-ink);\r\n}\r\n\r\n.notice-quiet {\r\n  padding: 0 2px;\r\n  color: var(--ink-3);\r\n}\r\n\r\n/* Cards --------------------------------------------------------------- */\r\n\r\n/* One column on phones; as many ~320px columns as fit on wider screens.\r\n   min(100%, 320px) keeps the track from overflowing very narrow viewports. */\r\n.cards {\r\n  display: grid;\r\n  grid-template-columns: repeat(auto-fit, minmax(min(100%, 320px), 1fr));\r\n  gap: 12px;\r\n  align-items: start;\r\n  justify-items: center;\r\n}\r\n\r\n.card {\r\n  width: 100%;\r\n  /* A single card on a wide screen would otherwise stretch across the page. */\r\n  max-width: 560px;\r\n  background: var(--card);\r\n  border: 1px solid var(--border);\r\n  border-radius: 12px;\r\n  box-shadow: var(--shadow);\r\n  padding: 14px 16px;\r\n}\r\n\r\n.card-head {\r\n  display: flex;\r\n  align-items: center;\r\n  gap: 8px;\r\n  flex-wrap: wrap;\r\n}\r\n\r\n.dot {\r\n  width: 9px;\r\n  height: 9px;\r\n  border-radius: 50%;\r\n  flex: none;\r\n  background: var(--none);\r\n}\r\n\r\n.dot.is-green { background: var(--band-green); }\r\n.dot.is-yellow { background: var(--band-yellow); }\r\n.dot.is-orange { background: var(--band-orange); }\r\n.dot.is-red { background: var(--band-red); }\r\n\r\n.provider-icon {\r\n  width: 18px;\r\n  height: 18px;\r\n  flex: none;\r\n  color: var(--ink-2);\r\n  display: inline-grid;\r\n  place-items: center;\r\n}\r\n\r\n.provider-icon svg {\r\n  width: 100%;\r\n  height: 100%;\r\n  display: block;\r\n  fill: currentColor;\r\n}\r\n\r\n.provider-icon.is-monogram {\r\n  border: 1px solid var(--ink-3);\r\n  border-radius: 5px;\r\n  font-size: 11px;\r\n  font-weight: 650;\r\n  line-height: 1;\r\n}\r\n\r\n.account-name {\r\n  margin: 0;\r\n  font-size: 15px;\r\n  font-weight: 600;\r\n  color: var(--ink);\r\n  min-width: 0;\r\n  overflow-wrap: anywhere;\r\n}\r\n\r\n.star {\r\n  color: var(--ink-3);\r\n  font-size: 13px;\r\n  margin-left: 2px;\r\n}\r\n\r\n.chip {\r\n  margin-left: auto;\r\n  padding: 2px 8px;\r\n  border-radius: 999px;\r\n  background: var(--chip-bg);\r\n  color: var(--ink-2);\r\n  font-size: 12px;\r\n  font-weight: 500;\r\n  white-space: nowrap;\r\n}\r\n\r\n.meters {\r\n  margin-top: 12px;\r\n  display: flex;\r\n  flex-direction: column;\r\n  gap: 12px;\r\n}\r\n\r\n.meter-row + .meter-row {\r\n  border-top: 1px solid var(--border);\r\n  padding-top: 12px;\r\n}\r\n\r\n.meter-top {\r\n  display: flex;\r\n  align-items: baseline;\r\n  justify-content: space-between;\r\n  gap: 12px;\r\n}\r\n\r\n.meter-label-row {\r\n  display: flex;\r\n  align-items: baseline;\r\n  gap: 6px;\r\n  min-width: 0;\r\n  flex-wrap: wrap;\r\n}\r\n\r\n.meter-label {\r\n  color: var(--ink-2);\r\n  font-size: 13px;\r\n  min-width: 0;\r\n  overflow-wrap: anywhere;\r\n}\r\n\r\n/* Names the model a window is limited to, e.g. a weekly cap that only applies\r\n   to one model. Absent on account-wide windows. */\r\n.scope-chip {\r\n  padding: 1px 7px;\r\n  border: 1px solid var(--border);\r\n  border-radius: 999px;\r\n  background: var(--chip-bg);\r\n  color: var(--ink-2);\r\n  font-size: 11px;\r\n  font-weight: 600;\r\n  white-space: nowrap;\r\n}\r\n\r\n/* The provider is refusing requests, which no percentage on its own conveys. */\r\n.blocked-banner {\r\n  margin: 10px 0 0;\r\n  padding: 8px 10px;\r\n  border: 1px solid var(--band-red);\r\n  border-radius: 9px;\r\n  background: var(--tint-red);\r\n  color: var(--ink);\r\n  font-size: 12.5px;\r\n  font-weight: 600;\r\n}\r\n\r\n/* Reset details and expiry notices stay separate from quota band colours. */\n.resets {\r\n  margin-top: 10px;\r\n  display: flex;\r\n  align-items: baseline;\r\n  flex-wrap: wrap;\r\n  gap: 4px 8px;\r\n}\r\n\r\n.reset-chip {\r\n  padding: 1px 8px;\r\n  border-radius: 999px;\r\n  background: var(--chip-bg);\r\n  color: var(--ink-2);\r\n  font-size: 12px;\r\n  font-weight: 600;\r\n  white-space: nowrap;\r\n}\r\n\r\n/* A soft chip of the band colour behind the number: enough to carry the\r\n   state at a glance, quiet enough not to shout on a card full of rows.\r\n   Tint and ink only, never a border. */\r\n.meter-value {\r\n  padding: 1px 6px;\r\n  border-radius: 999px;\r\n  color: var(--ink);\r\n  font-size: 13.5px;\r\n  font-weight: 600;\r\n  white-space: nowrap;\r\n}\r\n\r\n.meter-track {\r\n  margin-top: 6px;\r\n  height: 7px;\r\n  border-radius: 999px;\r\n  background: var(--track);\r\n  overflow: hidden;\r\n}\r\n\r\n.meter-fill {\r\n  height: 100%;\r\n  border-radius: 999px;\r\n  background: var(--none);\r\n  transition: width 240ms ease;\r\n}\r\n\r\n/* A plain vivid fill in both themes: no outline, no inset edge. */\r\n.meter-row.is-green .meter-value { background: var(--tint-green); color: var(--ink-green); }\r\n.meter-row.is-green .meter-fill { background: var(--band-green); }\r\n.meter-row.is-yellow .meter-value { background: var(--tint-yellow); color: var(--ink-yellow); }\r\n.meter-row.is-yellow .meter-fill { background: var(--band-yellow); }\r\n.meter-row.is-orange .meter-value { background: var(--tint-orange); color: var(--ink-orange); }\r\n.meter-row.is-orange .meter-fill { background: var(--band-orange); }\r\n.meter-row.is-red .meter-value { background: var(--tint-red); color: var(--ink-red); }\r\n.meter-row.is-red .meter-fill { background: var(--band-red); }\r\n\r\n.meter-reset {\r\n  margin-top: 5px;\r\n  color: var(--ink-3);\r\n  font-size: 12px;\r\n}\r\n\r\n.card-empty {\r\n  margin: 10px 0 0;\r\n  color: var(--ink-3);\r\n  font-size: 13px;\r\n}\r\n\r\n/* Standalone messages -------------------------------------------------- */\r\n\r\n.message {\r\n  max-width: 640px;\r\n  background: var(--panel);\r\n  border: 1px solid var(--border);\r\n  border-radius: 12px;\r\n  box-shadow: var(--shadow);\r\n  padding: 18px 16px;\r\n}\r\n\r\n.message h2 {\r\n  margin: 0 0 6px;\r\n  font-size: 16px;\r\n  font-weight: 600;\r\n}\r\n\r\n.message p {\r\n  margin: 0 0 8px;\r\n  color: var(--ink-2);\r\n  font-size: 14px;\r\n}\r\n\r\n.message p:last-child { margin-bottom: 0; }\r\n\r\n.message .example {\r\n  display: inline-block;\r\n  padding: 2px 6px;\r\n  border-radius: 6px;\r\n  background: var(--chip-bg);\r\n  color: var(--ink-2);\r\n  font-family: ui-monospace, \"Cascadia Mono\", Consolas, monospace;\r\n  font-size: 12.5px;\r\n  overflow-wrap: anywhere;\r\n}\r\n\r\n/* Landing (no id in the link) ------------------------------------------ */\r\n\r\n.landing {\r\n  max-width: 520px;\r\n  margin: 4px auto 0;\r\n  padding: 28px 22px 26px;\r\n  text-align: center;\r\n  background: var(--panel);\r\n  border: 1px solid var(--border);\r\n  border-radius: 14px;\r\n  box-shadow: var(--shadow);\r\n}\r\n\r\n.landing h2 {\r\n  margin: 0 0 10px;\r\n  font-size: 21px;\r\n  font-weight: 600;\r\n  letter-spacing: -0.01em;\r\n}\r\n\r\n.landing p {\r\n  margin: 0 auto 20px;\r\n  max-width: 44ch;\r\n  color: var(--ink-2);\r\n  font-size: 14px;\r\n}\r\n\r\n.landing-actions {\r\n  display: flex;\r\n  flex-wrap: wrap;\r\n  justify-content: center;\r\n  gap: 10px;\r\n}\r\n\r\n.button {\r\n  display: inline-flex;\r\n  align-items: center;\r\n  justify-content: center;\r\n  min-height: 40px;\r\n  padding: 0 16px;\r\n  border: 1px solid transparent;\r\n  border-radius: 10px;\r\n  font-size: 14px;\r\n  font-weight: 600;\r\n  text-decoration: none;\r\n}\r\n\r\n.button-primary {\r\n  background: var(--accent);\r\n  color: var(--on-accent);\r\n}\r\n\r\n.button-primary:hover { background: var(--accent-hover); }\r\n\r\n.button-secondary {\r\n  background: var(--chip-bg);\r\n  border-color: var(--border);\r\n  color: var(--ink);\r\n}\r\n\r\n.button-secondary:hover { border-color: var(--ink-3); }\r\n\r\n.sr-only {\r\n  position: absolute;\r\n  width: 1px;\r\n  height: 1px;\r\n  margin: -1px;\r\n  padding: 0;\r\n  border: 0;\r\n  overflow: hidden;\r\n  clip: rect(0 0 0 0);\r\n  clip-path: inset(50%);\r\n  white-space: nowrap;\r\n}\r\n\r\n@media (prefers-reduced-motion: reduce) {\r\n  .meter-fill,\r\n  .notification-toggle,\r\n  .notification-toggle span,\r\n  .notification-test,\r\n  .notification-test span { transition: none; }\r\n}\r\n\r\n\r\nbutton.reset-chip { font: inherit; font-size: 0.75rem; cursor: pointer; text-align: start; border: 1px solid var(--border); white-space: normal; }\nbutton.reset-chip:hover { border-color: var(--accent); }\r\nbutton.reset-chip:focus-visible, .reset-close:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }\r\n.reset-notice { margin: 8px 0 0; padding: 8px 10px; border-inline-start: 3px solid var(--accent); border-radius: 4px; background: var(--chip-bg); color: var(--ink-2); font-size: 0.8rem; }\r\n.reset-dialog { box-sizing: border-box; width: min(480px, calc(100% - 24px)); max-height: calc(100dvh - 32px); padding: 24px; border: 1px solid var(--border); border-radius: 16px; background: var(--panel); color: var(--ink); box-shadow: 0 16px 64px #0006; overflow-y: auto; overflow-wrap: anywhere; }\r\n.reset-dialog::backdrop { background: #0008; }\r\n.reset-close { float: inline-end; padding: 7px 12px; margin-inline-start: 12px; border: 1px solid var(--border); border-radius: 6px; background: var(--chip-bg); color: var(--ink); cursor: pointer; font: inherit; }\r\n.reset-dialog h2 { margin: 0 0 4px; font-size: 1.3rem; }\r\n.reset-account { margin: 0; color: var(--ink-2); }\r\n.reset-summary, .reset-date { font-size: 0.8rem; color: var(--ink-2); }\r\n.reset-detail { margin-top: 12px; padding: 14px; border: 1px solid var(--border); border-radius: 9px; background: var(--card); }\r\n.reset-detail h3 { font-size: 0.95rem; margin: 0 0 8px; }\r\n.reset-detail p { white-space: pre-wrap; margin: 6px 0 0; }\n.resets .reset-notice { flex-basis: 100%; }\n.reset-dialog[open] { display: flex; flex-direction: column; overflow: hidden; }\n.reset-close { float: none; align-self: flex-end; flex-shrink: 0; margin-bottom: 12px; background: var(--panel); }\n.reset-dialog-body { min-height: 0; overflow-y: auto; }\n",
  },
  "/app.js": {
    type: "text/javascript; charset=utf-8",
    body: "// AI Usage Tray - remote view.\r\n// Reads ?id=<32 hex> from the URL, fetches {apiBase}/u/{id} and renders one card\r\n// per account. Refetches every 60s; relative times re-render every 30s.\r\n// Without an id it shows a landing page (see start()).\r\n// ?id=demo is the one exception: a public sample payload built by the worker.\r\n\r\n// The payload uses the product terms \"used\" and \"remaining\". The browser\r\n// keeps its older \"left\" storage value so existing explicit overrides survive.\r\nfunction resolvePercentMode(storedMode, remoteDisplayMode) {\r\n  if (storedMode === \"left\" || storedMode === \"used\") return storedMode;\r\n  return remoteDisplayMode === \"remaining\" ? \"left\" : \"used\";\r\n}\r\n\r\nfunction hasEnabledAlertAccounts(data) {\r\n  return Array.isArray(data && data.accounts) && data.accounts.some(function (account) {\r\n    return account && account.alert && account.alert.enabled === true;\r\n  });\r\n}\r\n\r\nfunction resolveNotificationControl(options) {\r\n  if (!options.supported || (!options.alertsConfigured && !options.subscribed)) {\r\n    return {\r\n      disabled: true,\r\n      label: \"\",\r\n      state: \"hidden\",\r\n      testEnabled: false,\r\n      testVisible: false,\r\n      title: \"\",\r\n      visible: false\r\n    };\r\n  }\r\n  if (options.subscribed) {\r\n    return {\r\n      disabled: false,\r\n      label: \"Disable alerts\",\r\n      state: \"on\",\r\n      testEnabled: options.permission === \"granted\",\r\n      testVisible: true,\r\n      title: options.alertsConfigured\r\n        ? \"Browser alerts are on. Click to turn them off.\"\r\n        : \"Browser alerts are subscribed but account alerts are paused. Click to turn them off.\",\r\n      visible: true\r\n    };\r\n  }\r\n  if (options.permission === \"denied\") {\r\n    return {\r\n      disabled: true,\r\n      label: \"Alerts blocked\",\r\n      state: \"blocked\",\r\n      testEnabled: false,\r\n      testVisible: false,\r\n      title: \"Notifications are blocked in this browser's site settings.\",\r\n      visible: true\r\n    };\r\n  }\r\n  if (!options.pushReady) {\r\n    return {\r\n      disabled: true,\r\n      label: \"Alerts unavailable\",\r\n      state: \"unavailable\",\r\n      testEnabled: false,\r\n      testVisible: false,\r\n      title: \"Browser alerts are not configured on this server.\",\r\n      visible: true\r\n    };\r\n  }\r\n  return {\r\n    disabled: false,\r\n    label: \"Enable alerts\",\r\n    state: \"off\",\r\n    testEnabled: false,\r\n    testVisible: false,\r\n    title: \"Enable browser alerts on this device.\",\r\n    visible: true\r\n  };\r\n}\r\n\r\nfunction describeNotificationError(error) {\r\n  var code = error && error.message;\r\n  if (code === \"permission_denied\") {\r\n    return \"Notifications were not allowed. Change this in the browser's site settings.\";\r\n  }\r\n  if (code === \"push_not_configured\") {\r\n    return \"Browser alerts are not configured on this server.\";\r\n  }\r\n  if (code === \"service_worker_unavailable\") {\r\n    return \"Browser notifications are unavailable on this device.\";\r\n  }\r\n  if (code === \"push_service_unavailable\" || (error && error.name === \"AbortError\")) {\r\n    return \"Browser push is unavailable. Check this browser's notification settings and try again.\";\r\n  }\r\n  if (code === \"not_found\") {\r\n    return \"This shared view expired. Refresh it from the desktop app before enabling alerts.\";\r\n  }\r\n  if (code === \"subscription_rejected\" || code === \"invalid_subscription\") {\r\n    return \"This browser could not register alerts for the shared view. Try again.\";\r\n  }\r\n  return \"Browser alerts could not be changed. Try again.\";\r\n}\r\n\r\n// Calendar days in the viewer's local time zone, independent of DST day length.\r\nfunction resetExpiryInfo(value, now) {\r\n  var date = value ? new Date(value) : null;\r\n  if (!date || !isFinite(date.getTime())) return null;\r\n  var current = new Date(now);\r\n  var days = Math.round((Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()) -\r\n    Date.UTC(current.getFullYear(), current.getMonth(), current.getDate())) / 86400000);\r\n  var expired = date.getTime() <= now;\r\n  return { date: date, days: days, soon: !expired && days <= 7,\r\n    text: expired ? \"Expired\" : days === 0 ? \"0 days left, expires today\" : days === 1 ? \"1 day left\" : days + \" days left\" };\r\n}\r\n\r\nfunction resetExpiryNotice(source, now) {\r\n  if (!source || !(Number(source.available) > 0)) return \"\";\r\n  var dates = Array.isArray(source.credits)\r\n    ? source.credits.map(function (credit) { return credit && credit.expiresAt; }) : [source.expiresAt];\r\n  var count = dates.filter(function (value) { var info = resetExpiryInfo(value, now); return info && info.soon; }).length;\r\n  return count === 0 ? \"\" : count === 1 ? \"1 reset expires within 7 days.\" : count + \" resets expire within 7 days.\";\r\n}\r\n\r\nif (typeof module !== \"undefined\" && module.exports) {\r\n  module.exports = {\r\n    resetExpiryInfo: resetExpiryInfo,\r\n    resetExpiryNotice: resetExpiryNotice,\r\n    describeNotificationError: describeNotificationError,\r\n    resolvePercentMode: resolvePercentMode,\r\n    hasEnabledAlertAccounts: hasEnabledAlertAccounts,\r\n    resolveNotificationControl: resolveNotificationControl\r\n  };\r\n}\r\n\r\n(function () {\r\n  \"use strict\";\r\n\r\n  // Node loads this file to test the pure preference resolver above.\r\n  if (typeof window === \"undefined\" || typeof document === \"undefined\") return;\r\n\r\n  var CONFIG = window.REMOTE_VIEW_CONFIG || {};\r\n  var API_BASE = String(CONFIG.apiBase || \"\").replace(/\\/+$/, \"\");\r\n\r\n  var ID_PATTERN = /^[0-9a-f]{32}$/;\r\n  // The one id that is not a 32-hex secret: a public sample served by the worker.\r\n  var DEMO_ID = \"demo\";\r\n  var REFRESH_MS = 60000;\r\n  var TICK_MS = 30000;\r\n  var STALE_MS = 30 * 60000;\r\n\r\n  var THEME_KEY = \"aiUsageTray.theme\";\r\n  var PERCENT_MODE_KEY = \"aiUsageTray.percentMode\";\r\n  var LAST_ID_KEY = \"aiUsageTray.lastId\";\r\n\r\n  var RELEASES_URL = \"https://github.com/ShlomiPorush/ai-usage-tray/releases/latest\";\r\n  var REPO_URL = \"https://github.com/ShlomiPorush/ai-usage-tray\";\r\n\r\n  var SVG_NS = \"http://www.w3.org/2000/svg\";\r\n  var PROVIDER_ICONS = {\r\n    claude: {\r\n      viewBox: \"0 0 100 100\",\r\n      path: \"M25.71 63.22L41.44 54.39L41.7 53.62L41.44 53.2H40.67L38.04 53.04L29.05 52.79L21.26 52.47L13.71 52.06L11.81 51.66L10.03 49.31L10.21 48.14L11.81 47.07L14.1 47.27L19.16 47.61L26.75 48.14L32.25 48.46L40.41 49.31H41.7L41.88 48.79L41.44 48.46L41.1 48.14L33.24 42.82L24.74 37.19L20.29 33.95L17.88 32.31L16.67 30.77L16.14 27.41L18.33 25.01L21.26 25.21L22.01 25.41L24.99 27.7L31.34 32.62L39.64 38.73L40.85 39.74L41.34 39.4L41.4 39.15L40.85 38.24L36.34 30.09L31.52 21.79L29.38 18.35L28.81 16.28C28.61 15.43 28.47 14.73 28.47 13.85L30.96 10.48L32.33 10.03L35.65 10.48L37.05 11.69L39.11 16.41L42.45 23.83L47.63 33.93L49.15 36.93L49.96 39.7L50.26 40.55H50.79V40.06L51.21 34.38L52 27.39L52.77 18.41L53.04 15.88L54.29 12.84L56.78 11.2L58.72 12.14L60.32 14.42L60.1 15.9L59.15 22.07L57.29 31.75L56.07 38.22H56.78L57.59 37.41L60.87 33.06L66.37 26.18L68.8 23.45L71.63 20.43L73.46 19H76.9L79.43 22.76L78.29 26.65L74.75 31.14L71.82 34.94L67.61 40.61L64.98 45.14L65.22 45.51L65.85 45.45L75.36 43.42L80.5 42.49L86.63 41.44L89.4 42.73L89.71 44.05L88.61 46.74L82.06 48.36L74.37 49.9L62.91 52.61L62.77 52.71L62.93 52.91L68.09 53.4L70.3 53.52H75.7L85.76 54.27L88.39 56.01L89.97 58.14L89.71 59.75L85.66 61.82L80.19 60.52L67.45 57.49L63.07 56.4H62.47V56.76L66.11 60.32L72.79 66.35L81.15 74.12L81.57 76.05L80.5 77.56L79.36 77.4L72.02 71.88L69.19 69.39L62.77 63.98H62.35V64.55L63.82 66.72L71.63 78.45L72.04 82.06L71.47 83.23L69.45 83.94L67.22 83.53L62.65 77.12L57.93 69.89L54.13 63.42L53.66 63.68L51.42 87.87L50.36 89.1L47.94 90.03L45.91 88.49L44.84 86L45.91 81.09L47.21 74.67L48.26 69.57L49.21 63.24L49.78 61.13L49.74 60.99L49.27 61.05L44.5 67.61L37.23 77.42L31.48 83.57L30.11 84.12L27.72 82.89L27.94 80.68L29.28 78.72L37.23 68.6L42.03 62.32L45.12 58.7L45.1 58.18H44.92L23.79 71.9L20.03 72.38L18.41 70.87L18.61 68.38L19.38 67.57L25.73 63.2L25.71 63.22Z\"\r\n    },\r\n    codex: {\r\n      viewBox: \"0 0 100 100\",\r\n      path: \"M83.77 42.81C84.67 40.11 84.98 37.26 84.68 34.44C84.38 31.62 83.49 28.89 82.05 26.44C77.69 18.84 68.92 14.94 60.35 16.77C57.98 14.13 54.96 12.17 51.59 11.07C48.21 9.97 44.61 9.77 41.14 10.51C37.67 11.24 34.45 12.88 31.81 15.25C29.17 17.62 27.2 20.64 26.1 24.01C23.32 24.58 20.69 25.74 18.4 27.41C16.1 29.07 14.18 31.21 12.78 33.68C8.37 41.26 9.37 50.83 15.25 57.33C14.35 60.03 14.04 62.88 14.34 65.7C14.63 68.52 15.52 71.25 16.96 73.7C21.33 81.3 30.1 85.21 38.67 83.37C40.56 85.49 42.87 87.19 45.46 88.34C48.05 89.5 50.86 90.09 53.7 90.07C62.48 90.08 70.26 84.41 72.94 76.05C75.72 75.48 78.35 74.32 80.64 72.66C82.94 70.99 84.86 68.85 86.26 66.38C90.62 58.81 89.62 49.3 83.77 42.81ZM53.7 84.84C50.2 84.84 46.8 83.61 44.11 81.37L44.58 81.1L60.51 71.9C60.91 71.67 61.24 71.34 61.47 70.94C61.7 70.54 61.82 70.09 61.82 69.63V47.18L68.56 51.07C68.62 51.11 68.67 51.17 68.68 51.25V69.85C68.66 78.12 61.97 84.82 53.7 84.84ZM21.5 71.08C19.74 68.05 19.11 64.49 19.72 61.04L20.19 61.32L36.13 70.52C36.53 70.75 36.98 70.87 37.43 70.87C37.89 70.87 38.34 70.75 38.73 70.52L58.21 59.29V67.06C58.21 67.1 58.2 67.14 58.18 67.18C58.16 67.21 58.13 67.24 58.1 67.27L41.97 76.57C34.8 80.7 25.64 78.25 21.5 71.08ZM17.3 36.39C19.07 33.34 21.87 31.01 25.19 29.81V48.74C25.18 49.19 25.3 49.65 25.53 50.04C25.75 50.44 26.08 50.77 26.48 50.99L45.86 62.17L39.13 66.07C39.09 66.09 39.05 66.1 39.01 66.1C38.97 66.1 38.93 66.09 38.89 66.07L22.79 56.78C15.64 52.63 13.18 43.48 17.3 36.31V36.39ZM72.62 49.24L53.18 37.95L59.9 34.07C59.93 34.05 59.97 34.04 60.02 34.04C60.06 34.04 60.1 34.05 60.13 34.07L76.24 43.38C78.7 44.8 80.7 46.89 82.02 49.41C83.34 51.92 83.91 54.77 83.68 57.6C83.44 60.43 82.4 63.14 80.69 65.4C78.97 67.67 76.64 69.4 73.98 70.39V51.47C73.97 51.01 73.83 50.56 73.6 50.17C73.36 49.79 73.02 49.46 72.62 49.24ZM79.33 39.17L78.85 38.88L62.94 29.61C62.54 29.38 62.09 29.25 61.63 29.25C61.17 29.25 60.72 29.38 60.32 29.61L40.86 40.84V33.06C40.86 33.02 40.87 32.98 40.88 32.95C40.9 32.91 40.92 32.88 40.96 32.86L57.06 23.57C59.53 22.15 62.35 21.46 65.19 21.58C68.04 21.7 70.79 22.63 73.13 24.26C75.46 25.89 77.28 28.15 78.38 30.78C79.48 33.41 79.81 36.3 79.33 39.1V39.17ZM37.19 52.95L30.46 49.07C30.42 49.05 30.39 49.02 30.37 48.99C30.35 48.96 30.33 48.92 30.33 48.88V30.32C30.33 27.47 31.15 24.68 32.68 22.28C34.21 19.88 36.39 17.96 38.97 16.76C41.54 15.55 44.41 15.1 47.24 15.46C50.06 15.83 52.72 16.99 54.91 18.81L54.44 19.07L38.51 28.27C38.12 28.5 37.79 28.83 37.56 29.23C37.33 29.63 37.21 30.08 37.2 30.54L37.19 52.95ZM40.85 45.06L49.52 40.06L58.21 45.06V55.06L49.55 60.06L40.86 55.06L40.85 45.06Z\"\r\n    },\r\n    copilot: {\r\n      viewBox: \"0 0 96 96\",\r\n      path: \"M95.667 67.954C92.225 73.933 72.24 88.04 47.997 88.04 23.754 88.04 3.769 73.933.328 67.954c-.216-.375-.307-.796-.328-1.226V55.661c.019-.371.089-.736.226-1.081 1.489-3.738 5.386-9.166 10.417-10.623.667-1.712 1.655-4.215 2.576-6.062-.154-1.414-.208-2.872-.208-4.345 0-5.322 1.128-9.99 4.527-13.466 1.587-1.623 3.557-2.869 5.893-3.805 5.595-4.545 13.563-8.369 24.48-8.369s19.057 3.824 24.652 8.369c2.337.936 4.306 2.182 5.894 3.805 3.399 3.476 4.527 8.144 4.527 13.466 0 1.473-.054 2.931-.208 4.345.921 1.847 1.909 4.35 2.576 6.062 5.03 1.457 8.928 6.885 10.417 10.623.163.41.231.848.231 1.289v10.644c0 .504-.081 1.004-.333 1.441ZM48.686 43.993l-.3.001-1.077-.001c-.423.709-.894 1.39-1.418 2.035-3.078 3.787-7.672 5.964-14.026 5.964-6.897 0-11.952-1.435-15.123-5.032a7.886 7.886 0 0 1-.342-.419l-.39.419v26.326c5.737 3.118 18.05 8.713 31.987 8.713 13.938 0 26.251-5.595 31.988-8.713V46.96l-.39-.419s-.132.181-.342.419c-3.171 3.597-8.226 5.032-15.123 5.032-6.354 0-10.949-2.177-14.026-5.964a17.178 17.178 0 0 1-1.418-2.034h-.066l.066-.001Zm-3.94-11.733c.17-1.326.251-2.513.253-3.573v-.084c-.005-3.077-.678-5.079-1.752-6.308-1.365-1.562-4.184-2.758-10.127-2.115-6.021.652-9.386 2.146-11.294 4.098-1.847 1.889-2.818 4.715-2.818 9.272 0 4.842.698 7.703 2.232 9.443 1.459 1.655 4.332 3.001 10.625 3.001 4.837 0 7.603-1.573 9.371-3.749 1.899-2.336 2.967-5.759 3.51-9.985Zm6.503 0c.543 4.226 1.611 7.649 3.51 9.985 1.768 2.176 4.533 3.749 9.371 3.749 6.292 0 9.165-1.346 10.624-3.001 1.535-1.74 2.232-4.601 2.232-9.443 0-4.557-.97-7.383-2.817-9.272-1.908-1.952-5.274-3.446-11.294-4.098-5.943-.643-8.763.553-10.127 2.115-1.074 1.229-1.747 3.231-1.752 6.308v.084c.002 1.06.083 2.247.253 3.573Zm-2.563 11.734h.066l-.066-.001v.001Z\"\r\n    }\r\n  };\r\n\r\n  var content = document.getElementById(\"content\");\r\n  var demoBadge = document.getElementById(\"demo-badge\");\r\n  var updatedEl = document.getElementById(\"updated\");\r\n  var staleEl = document.getElementById(\"staleness\");\r\n  var connectionEl = document.getElementById(\"connection\");\r\n  var notificationEl = document.getElementById(\"notifications\");\r\n  var notificationButton = document.getElementById(\"notification-toggle\");\r\n  var notificationLabel = document.getElementById(\"notification-label\");\r\n  var notificationTestButton = document.getElementById(\"notification-test\");\r\n  var versionEl = document.getElementById(\"remote-version\");\r\n\r\n  var id = null;\r\n  var payload = null; // last payload we managed to render\r\n  var loading = false;\r\n  var lastFetchAt = 0;\r\n  var serviceWorkerPromise = null;\r\n  var pushSubscription = null;\r\n  var notificationBusy = false;\r\n  var associatedPushReadId = null;\r\n\r\n  // --- small helpers ---------------------------------------------------\r\n\r\n  function el(tag, className, text) {\r\n    var node = document.createElement(tag);\r\n    if (className) node.className = className;\r\n    if (text !== undefined && text !== null) node.textContent = text;\r\n    return node;\r\n  }\r\n\r\n  function setText(node, text) {\r\n    // Only touch the DOM when the text changes: these are live regions and we\r\n    // do not want a screen reader to re-announce an unchanged notice.\r\n    if (node.textContent !== text) node.textContent = text;\r\n  }\r\n\r\n  function show(node, text) {\r\n    setText(node, text);\r\n    node.hidden = false;\r\n  }\r\n\r\n  function hide(node) {\r\n    node.hidden = true;\r\n    setText(node, \"\");\r\n  }\r\n\r\n  function clear(node) {\r\n    while (node.firstChild) node.removeChild(node.firstChild);\r\n  }\r\n\r\n  function renderProviderIcon(provider) {\r\n    var key = typeof provider === \"string\" ? provider.toLowerCase() : \"\";\r\n    var icon = el(\"span\", \"provider-icon provider-icon-\" + (key || \"unknown\"));\r\n    icon.setAttribute(\"aria-hidden\", \"true\");\r\n\r\n    var definition = PROVIDER_ICONS[key];\r\n    if (!definition) {\r\n      icon.classList.add(\"is-monogram\");\r\n      icon.textContent = key === \"zai\" ? \"Z\" : (key.charAt(0).toUpperCase() || \"?\");\r\n      return icon;\r\n    }\r\n\r\n    var svg = document.createElementNS(SVG_NS, \"svg\");\r\n    svg.setAttribute(\"viewBox\", definition.viewBox);\r\n    svg.setAttribute(\"focusable\", \"false\");\r\n    var path = document.createElementNS(SVG_NS, \"path\");\r\n    path.setAttribute(\"d\", definition.path);\r\n    svg.appendChild(path);\r\n    icon.appendChild(svg);\r\n    return icon;\r\n  }\r\n\r\n  // localStorage throws in some privacy modes; treat it as best-effort.\r\n  function readStored(key) {\r\n    try {\r\n      return window.localStorage.getItem(key);\r\n    } catch (error) {\r\n      return null;\r\n    }\r\n  }\r\n\r\n  function writeStored(key, value) {\r\n    try {\r\n      window.localStorage.setItem(key, value);\r\n    } catch (error) { /* nothing we can do, and nothing depends on it */ }\r\n  }\r\n\r\n  function pushSupported() {\r\n    return window.isSecureContext &&\r\n      \"Notification\" in window &&\r\n      \"PushManager\" in window &&\r\n      \"serviceWorker\" in navigator;\r\n  }\r\n\r\n  function applicationServerKey(value) {\r\n    var padding = \"=\".repeat((4 - value.length % 4) % 4);\r\n    var binary = atob((value + padding).replace(/-/g, \"+\").replace(/_/g, \"/\"));\r\n    return Uint8Array.from(binary, function (character) { return character.charCodeAt(0); });\r\n  }\r\n\r\n  function responseError(response, fallback) {\r\n    return response.json().catch(function () { return null; }).then(function (body) {\r\n      throw new Error(body && typeof body.error === \"string\" ? body.error : fallback);\r\n    });\r\n  }\r\n\r\n  function fetchPushConfiguration() {\r\n    return fetch(API_BASE + \"/push/vapid-public-key\", {\r\n      cache: \"no-store\",\r\n      headers: { Accept: \"application/json\" }\r\n    }).then(function (response) {\r\n      if (!response.ok) return responseError(response, \"push_not_configured\");\r\n      return response.json();\r\n    });\r\n  }\r\n\r\n  function applyNotificationControl(control) {\r\n    if (!control.visible) {\r\n      notificationButton.hidden = true;\r\n      setNotificationTestButton(false, true);\r\n      return;\r\n    }\r\n    setNotificationButton(control.state, control.label, control.title, control.disabled);\r\n    setNotificationTestButton(control.testVisible, !control.testEnabled);\r\n  }\r\n\r\n  function setExpandableButtonWidth(button, label) {\r\n    if (!button || !label) return;\r\n    var labelWidth = label.scrollWidth;\r\n    if (!labelWidth) return;\r\n    button.style.setProperty(\"--expanded-label-width\", labelWidth + \"px\");\r\n    button.style.setProperty(\"--expanded-width\", (labelWidth + 41) + \"px\");\r\n  }\r\n\r\n  function setNotificationButton(state, label, title, disabled) {\r\n    if (!notificationButton) return;\r\n    notificationButton.hidden = false;\r\n    notificationButton.disabled = Boolean(disabled);\r\n    notificationButton.setAttribute(\"data-state\", state);\r\n    notificationButton.setAttribute(\"aria-pressed\", state === \"on\" ? \"true\" : \"false\");\r\n    notificationButton.setAttribute(\"aria-label\", title || label);\r\n    notificationButton.removeAttribute(\"title\");\r\n    if (notificationLabel) {\r\n      notificationLabel.textContent = label;\r\n      setExpandableButtonWidth(notificationButton, notificationLabel);\r\n    }\r\n  }\r\n\r\n  function setNotificationTestButton(visible, disabled) {\r\n    if (!notificationTestButton) return;\r\n    notificationTestButton.hidden = !visible;\r\n    notificationTestButton.disabled = Boolean(disabled);\r\n    notificationTestButton.setAttribute(\"aria-label\", disabled\r\n      ? \"Enable browser alerts before testing a notification.\"\r\n      : \"Test notification\");\r\n    notificationTestButton.removeAttribute(\"title\");\r\n    if (visible) {\r\n      setExpandableButtonWidth(\r\n        notificationTestButton,\r\n        notificationTestButton.querySelector(\"span\")\r\n      );\r\n    }\r\n  }\r\n\r\n  function syncNotificationControl() {\r\n    if (!notificationButton || id === DEMO_ID || !pushSupported() || !payload) {\r\n      if (notificationButton) notificationButton.hidden = true;\r\n      setNotificationTestButton(false, true);\r\n      return Promise.resolve();\r\n    }\r\n\r\n    var alertsConfigured = hasEnabledAlertAccounts(payload);\r\n    setNotificationTestButton(false, true);\r\n    return (serviceWorkerPromise || Promise.reject(new Error(\"service_worker_unavailable\")))\r\n      .then(function (registration) {\r\n        if (!registration) throw new Error(\"service_worker_unavailable\");\r\n        return registration.pushManager.getSubscription();\r\n      })\r\n      .then(function (subscription) {\r\n        pushSubscription = subscription;\r\n        if (subscription) {\r\n          var association = associatedPushReadId === id\r\n            ? Promise.resolve()\r\n            : fetch(API_BASE + \"/u/\" + id + \"/push-subscription\", {\r\n              method: \"POST\",\r\n              headers: { \"Content-Type\": \"application/json\" },\r\n              body: JSON.stringify(subscription.toJSON())\r\n            }).then(function (response) {\r\n              if (!response.ok) return responseError(response, \"subscription_rejected\");\r\n              associatedPushReadId = id;\r\n            });\r\n          return association.then(function () {\r\n            applyNotificationControl(resolveNotificationControl({\r\n              alertsConfigured: alertsConfigured,\r\n              permission: Notification.permission,\r\n              pushReady: true,\r\n              subscribed: true,\r\n              supported: true\r\n            }));\r\n          });\r\n        } else if (!alertsConfigured) {\r\n          notificationButton.hidden = true;\r\n        } else {\r\n          return fetchPushConfiguration().then(function () {\r\n            applyNotificationControl(resolveNotificationControl({\r\n              alertsConfigured: alertsConfigured,\r\n              permission: Notification.permission,\r\n              pushReady: true,\r\n              subscribed: false,\r\n              supported: true\r\n            }));\r\n          });\r\n        }\r\n      })\r\n      .catch(function (error) {\r\n        var message = describeNotificationError(error);\r\n        var control = resolveNotificationControl({\r\n          alertsConfigured: hasEnabledAlertAccounts(payload),\r\n          permission: Notification.permission,\r\n          pushReady: false,\r\n          subscribed: Boolean(pushSubscription),\r\n          supported: true\r\n        });\r\n        if (control.state === \"unavailable\") control.title = message;\r\n        applyNotificationControl(control);\r\n        show(notificationEl, message);\r\n      });\r\n  }\r\n\r\n  function unregisterBrowserAlerts() {\r\n    if (!pushSubscription) return Promise.resolve();\r\n    var subscription = pushSubscription;\r\n    return fetch(API_BASE + \"/u/\" + id + \"/push-subscription\", {\r\n      method: \"DELETE\",\r\n      headers: { \"Content-Type\": \"application/json\" },\r\n      body: JSON.stringify({ endpoint: subscription.endpoint })\r\n    })\r\n      .catch(function () { /* unsubscribe locally even if server cleanup fails */ })\r\n      .then(function () { return subscription.unsubscribe(); })\r\n      .then(function () {\r\n        pushSubscription = null;\r\n        associatedPushReadId = null;\r\n        show(notificationEl, \"Browser alerts are off on this device.\");\r\n      });\r\n  }\r\n\r\n  function registerBrowserAlerts() {\r\n    if (!hasEnabledAlertAccounts(payload)) return Promise.resolve();\r\n    return Notification.requestPermission()\r\n      .then(function (permission) {\r\n        if (permission !== \"granted\") throw new Error(\"permission_denied\");\r\n        return Promise.all([\r\n          serviceWorkerPromise,\r\n          fetchPushConfiguration()\r\n        ]);\r\n      })\r\n      .then(function (results) {\r\n        var registration = results[0];\r\n        var configuration = results[1];\r\n        if (!registration) throw new Error(\"service_worker_unavailable\");\r\n        return registration.pushManager.subscribe({\r\n          userVisibleOnly: true,\r\n          applicationServerKey: applicationServerKey(configuration.publicKey)\r\n        }).then(function (subscription) {\r\n          return fetch(API_BASE + \"/u/\" + id + \"/push-subscription\", {\r\n            method: \"POST\",\r\n            headers: { \"Content-Type\": \"application/json\" },\r\n            body: JSON.stringify(subscription.toJSON())\r\n          }).then(function (response) {\r\n            if (!response.ok) {\r\n              return subscription.unsubscribe().then(function () {\r\n                return responseError(response, \"subscription_rejected\");\r\n              });\r\n            }\r\n            pushSubscription = subscription;\r\n            associatedPushReadId = id;\r\n            return registration.showNotification(\"Browser alerts are ready\", {\r\n              body: \"This device will notify you when a selected quota crosses its threshold or has a weekly reset.\",\r\n              icon: \"icon-192.png\",\r\n              badge: \"icon-192.png\",\r\n              tag: \"usage-alert-test\",\r\n              data: { url: window.location.href }\r\n            });\r\n          });\r\n        });\r\n      })\r\n      .then(function () {\r\n        show(notificationEl, \"Browser alerts are on for this device.\");\r\n      });\r\n  }\r\n\r\n  function setupNotifications() {\r\n    if (!notificationButton || !pushSupported()) return;\r\n    notificationButton.addEventListener(\"click\", function () {\r\n      if (notificationBusy) return;\r\n      notificationBusy = true;\r\n      notificationButton.disabled = true;\r\n      var action = pushSubscription ? unregisterBrowserAlerts() : registerBrowserAlerts();\r\n      action.catch(function (error) {\r\n        show(notificationEl, describeNotificationError(error));\r\n      }).then(function () {\r\n        notificationBusy = false;\r\n        return syncNotificationControl();\r\n      });\r\n    });\r\n    if (notificationTestButton) {\r\n      notificationTestButton.addEventListener(\"click\", function () {\r\n        if (Notification.permission !== \"granted\") {\r\n          show(notificationEl, \"Enable browser alerts first, then test the notification.\");\r\n          return;\r\n        }\r\n        notificationTestButton.disabled = true;\r\n        (serviceWorkerPromise || Promise.reject(new Error(\"service_worker_unavailable\")))\r\n          .then(function (registration) {\r\n            if (!registration) throw new Error(\"service_worker_unavailable\");\r\n            return registration.showNotification(\"AI Usage Tray test notification\", {\r\n              body: \"Test successful. Weekly usage reset alerts will appear here.\",\r\n              icon: \"icon-192.png\",\r\n              badge: \"icon-192.png\",\r\n              tag: \"usage-alert-test\",\r\n              data: { url: window.location.href }\r\n            });\r\n          })\r\n          .then(function () {\r\n            show(notificationEl, \"Test notification sent to this device.\");\r\n          })\r\n          .catch(function () {\r\n            show(notificationEl, \"The test notification could not be shown. Try enabling alerts again.\");\r\n          })\r\n          .then(function () {\r\n            notificationTestButton.disabled = false;\r\n          });\r\n      });\r\n    }\r\n  }\r\n\r\n  // --- theme -----------------------------------------------------------\r\n  // Auto (system) -> Light -> Dark -> Auto. Only the two forced modes set\r\n  // data-theme on <html>; Auto removes it and lets the media query decide.\r\n\r\n  var THEME_MODES = [\"auto\", \"light\", \"dark\"];\r\n  var THEME_LABELS = { auto: \"System\", light: \"Light\", dark: \"Dark\" };\r\n  var PAGE_COLORS = { light: \"#EDE8E0\", dark: \"#1A2233\" };\r\n\r\n  var themeButton = document.getElementById(\"theme-toggle\");\r\n  var themeMode = \"auto\";\r\n\r\n  // The two <meta name=\"theme-color\"> tags carry the Auto defaults. For a forced\r\n  // mode both are pinned to the same colour and their media queries dropped, so\r\n  // the browser chrome follows the page instead of the system.\r\n  function syncThemeColor(mode) {\r\n    var metas = document.querySelectorAll('meta[name=\"theme-color\"][data-scheme]');\r\n    for (var i = 0; i < metas.length; i++) {\r\n      var meta = metas[i];\r\n      var scheme = meta.getAttribute(\"data-scheme\");\r\n      if (mode === \"light\" || mode === \"dark\") {\r\n        meta.setAttribute(\"content\", PAGE_COLORS[mode]);\r\n        meta.removeAttribute(\"media\");\r\n      } else {\r\n        meta.setAttribute(\"content\", PAGE_COLORS[scheme]);\r\n        meta.setAttribute(\"media\", \"(prefers-color-scheme: \" + scheme + \")\");\r\n      }\r\n    }\r\n  }\r\n\r\n  function applyTheme(mode) {\r\n    themeMode = THEME_MODES.indexOf(mode) === -1 ? \"auto\" : mode;\r\n\r\n    if (themeMode === \"auto\") {\r\n      document.documentElement.removeAttribute(\"data-theme\");\r\n    } else {\r\n      document.documentElement.setAttribute(\"data-theme\", themeMode);\r\n    }\r\n    syncThemeColor(themeMode);\r\n\r\n    if (themeButton) {\r\n      var label = \"Theme: \" + THEME_LABELS[themeMode];\r\n      themeButton.setAttribute(\"data-mode\", themeMode);\r\n      themeButton.setAttribute(\"aria-label\", label);\r\n      themeButton.setAttribute(\"title\", label + \" (click to change)\");\r\n    }\r\n  }\r\n\r\n  function setupTheme() {\r\n    applyTheme(readStored(THEME_KEY) || \"auto\");\r\n    if (!themeButton) return;\r\n\r\n    themeButton.hidden = false;\r\n    themeButton.addEventListener(\"click\", function () {\r\n      var next = THEME_MODES[(THEME_MODES.indexOf(themeMode) + 1) % THEME_MODES.length];\r\n      applyTheme(next);\r\n      writeStored(THEME_KEY, next);\r\n    });\r\n  }\r\n\r\n  // --- percentage display ---------------------------------------------\r\n  // Number and fill show either usage or capacity left. The band still comes\r\n  // from canonical usage, so green always means capacity is available and red\r\n  // always means the quota is near exhaustion.\r\n\r\n  var percentToggle = document.getElementById(\"percent-toggle\");\r\n  var percentButtons = percentToggle\r\n    ? percentToggle.querySelectorAll(\"[data-percent-mode]\")\r\n    : [];\r\n  var percentMode = \"used\";\r\n  var hasPercentModeOverride = false;\r\n\r\n  function applyPercentMode(mode, renderPayload) {\r\n    percentMode = mode === \"left\" ? \"left\" : \"used\";\r\n\r\n    for (var i = 0; i < percentButtons.length; i++) {\r\n      var button = percentButtons[i];\r\n      button.setAttribute(\r\n        \"aria-pressed\",\r\n        button.getAttribute(\"data-percent-mode\") === percentMode ? \"true\" : \"false\"\r\n      );\r\n    }\r\n\r\n    if (payload && renderPayload !== false) render();\r\n  }\r\n\r\n  function setupPercentToggle() {\r\n    var storedMode = readStored(PERCENT_MODE_KEY);\r\n    hasPercentModeOverride = storedMode === \"left\" || storedMode === \"used\";\r\n    applyPercentMode(resolvePercentMode(storedMode, null), false);\r\n    if (!percentToggle) return;\r\n\r\n    percentToggle.hidden = false;\r\n    for (var i = 0; i < percentButtons.length; i++) {\r\n      percentButtons[i].addEventListener(\"click\", function () {\r\n        var next = this.getAttribute(\"data-percent-mode\");\r\n        hasPercentModeOverride = true;\r\n        applyPercentMode(next);\r\n        writeStored(PERCENT_MODE_KEY, percentMode);\r\n      });\r\n    }\r\n  }\r\n\r\n  // \"1d 12h\" / \"4h 21m\" / \"12m\"\r\n  function formatDuration(ms) {\r\n    var minutes = Math.floor(ms / 60000);\r\n    if (minutes < 1) return \"under a minute\";\r\n    var days = Math.floor(minutes / 1440);\r\n    var hours = Math.floor((minutes % 1440) / 60);\r\n    if (days > 0) return days + \"d \" + hours + \"h\";\r\n    if (hours > 0) return hours + \"h \" + (minutes % 60) + \"m\";\r\n    return minutes + \"m\";\r\n  }\r\n\r\n  function formatWhen(date) {\r\n    var sameDay = date.toDateString() === new Date().toDateString();\r\n    if (sameDay) {\r\n      return date.toLocaleTimeString([], { hour: \"2-digit\", minute: \"2-digit\" });\r\n    }\r\n    return date.toLocaleString([], {\r\n      month: \"short\", day: \"numeric\", hour: \"2-digit\", minute: \"2-digit\"\r\n    });\r\n  }\r\n\r\n  function parseDate(value) {\r\n    if (typeof value !== \"string\" || !value) return null;\r\n    var date = new Date(value);\r\n    return isNaN(date.getTime()) ? null : date;\r\n  }\r\n\r\n  // \"expires in 28 days\". Deliberately not a formatted date: the browser's own\r\n  // locale turned that into Hebrew on an otherwise English page. A day count\r\n  // reads the same everywhere.\r\n  //\r\n  // Counted in calendar days, not in 24h blocks, so \"today\" means today\r\n  // whatever the hour, and a daylight-saving shift cannot move the number.\r\n  // Returns null once the expiry day itself is behind us; the caller then drops\r\n  // the clause and keeps the rest of the chip.\r\n  function formatDaysUntil(date, now) {\r\n    var days = Math.round((startOfDay(date) - startOfDay(new Date(now))) / 86400000);\r\n    if (days < 0) return null;\r\n    if (days === 0) return \"expires today\";\r\n    if (days === 1) return \"expires in 1 day\";\r\n    return \"expires in \" + days + \" days\";\r\n  }\r\n\r\n  function startOfDay(date) {\r\n    return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();\r\n  }\r\n\r\n  // used -> band. The number alone decides: green 0-49, yellow 50-74,\r\n  // orange 75-89, red 90-100. The same rule runs on every surface of the\r\n  // product, so one percentage can never wear two colours.\r\n  //\r\n  // Provider-reported severity is still carried in the payload (the schema is\r\n  // untouched) but deliberately has no say here: a provider that called 71%\r\n  // \"normal\" used to paint it green while the widget painted it yellow.\r\n  function classify(used) {\r\n    if (used >= 90) return \"red\";\r\n    if (used >= 75) return \"orange\";\r\n    if (used >= 50) return \"yellow\";\r\n    return \"green\";\r\n  }\r\n\r\n  var STATE_RANK = { green: 1, yellow: 2, orange: 3, red: 4 };\r\n\r\n  function clampPercent(value) {\r\n    var number = typeof value === \"number\" ? value : Number(value);\r\n    if (!isFinite(number)) return 0;\r\n    return Math.min(100, Math.max(0, number));\r\n  }\r\n\r\n  // --- rendering -------------------------------------------------------\r\n\r\n  function renderMessage(heading, paragraphs) {\r\n    clear(content);\r\n    var box = el(\"section\", \"message\");\r\n    box.appendChild(el(\"h2\", null, heading));\r\n    paragraphs.forEach(function (part) {\r\n      var p = el(\"p\");\r\n      if (typeof part === \"string\") {\r\n        p.textContent = part;\r\n      } else {\r\n        p.appendChild(document.createTextNode(part.before || \"\"));\r\n        p.appendChild(el(\"span\", \"example\", part.example));\r\n        p.appendChild(document.createTextNode(part.after || \"\"));\r\n      }\r\n      box.appendChild(p);\r\n    });\r\n    content.appendChild(box);\r\n  }\r\n\r\n  function link(className, href, text) {\r\n    var node = el(\"a\", className, text);\r\n    node.href = href;\r\n    node.rel = \"noopener\";\r\n    return node;\r\n  }\r\n\r\n  // Shown when the link carries no id: explain what the page is and where the\r\n  // link comes from, nothing more.\r\n  function renderLanding() {\r\n    clear(content);\r\n\r\n    var box = el(\"section\", \"landing\");\r\n    box.appendChild(el(\"h2\", null, \"AI Usage Tray\"));\r\n    box.appendChild(el(\"p\", null,\r\n      \"This page shows live AI subscription usage (Claude, Codex, Z.AI and \" +\r\n      \"Copilot) shared from the AI Usage Tray app for Windows. To get your own \" +\r\n      \"link, install the app, enable Settings → Remote view and press Copy link.\"));\r\n\r\n    var actions = el(\"div\", \"landing-actions\");\r\n    actions.appendChild(link(\"button button-primary\", RELEASES_URL, \"Download for Windows\"));\r\n    actions.appendChild(link(\"button button-secondary\", REPO_URL, \"GitHub\"));\r\n    box.appendChild(actions);\r\n\r\n    content.appendChild(box);\r\n  }\r\n\r\n  function windowsOf(account) {\r\n    return Array.isArray(account.windows) ? account.windows : [];\r\n  }\r\n\r\n  // The account dot follows its worst window, by band.\r\n  function worstState(account) {\r\n    var rows = windowsOf(account);\r\n    var worst = null;\r\n    rows.forEach(function (row) {\r\n      if (!row || typeof row !== \"object\") return;\r\n      var state = classify(clampPercent(row.usedPercent));\r\n      if (worst === null || STATE_RANK[state] > STATE_RANK[worst]) worst = state;\r\n    });\r\n    return worst;\r\n  }\r\n\r\n  function orderAccounts(data) {\r\n    var accounts = Array.isArray(data.accounts) ? data.accounts.slice() : [];\r\n    var primaryIndex = -1;\r\n    if (typeof data.primary === \"string\" && data.primary) {\r\n      for (var i = 0; i < accounts.length; i++) {\r\n        if (accounts[i] && accounts[i].id === data.primary) {\r\n          primaryIndex = i;\r\n          break;\r\n        }\r\n      }\r\n    }\r\n    if (primaryIndex > 0) {\r\n      accounts.unshift(accounts.splice(primaryIndex, 1)[0]);\r\n    }\r\n    return accounts;\r\n  }\r\n\r\n  // Number and fill follow the selected view. The band stays tied to canonical\r\n  // usage so its warning meaning remains stable in both modes.\r\n  function renderMeterRow(accountName, source, now) {\r\n    var used = clampPercent(source.usedPercent);\r\n    var left = 100 - used;\r\n    var state = classify(used);\r\n    var label = typeof source.label === \"string\" && source.label ? source.label : \"Usage\";\r\n    var scope = typeof source.scope === \"string\" && source.scope ? source.scope : null;\r\n    var displayed = percentMode === \"left\" ? left : used;\r\n    var shown = Math.round(displayed);\r\n    var displayLabel = percentMode === \"left\" ? \"remaining\" : \"used\";\r\n\r\n    var row = el(\"div\", \"meter-row is-\" + state);\r\n\r\n    var top = el(\"div\", \"meter-top\");\r\n    var labelBox = el(\"div\", \"meter-label-row\");\r\n    labelBox.appendChild(el(\"span\", \"meter-label\", label));\r\n    // A model-scoped window: the chip is what separates \"Weekly\" for the whole\r\n    // account from \"Weekly\" for one model.\r\n    if (scope) labelBox.appendChild(el(\"span\", \"scope-chip\", scope));\r\n    top.appendChild(labelBox);\r\n    top.appendChild(el(\"span\", \"meter-value\", shown + \"%\"));\r\n    row.appendChild(top);\r\n\r\n    var track = el(\"div\", \"meter-track\");\r\n    track.setAttribute(\"role\", \"meter\");\r\n    track.setAttribute(\"aria-valuemin\", \"0\");\r\n    track.setAttribute(\"aria-valuemax\", \"100\");\r\n    track.setAttribute(\"aria-valuenow\", String(shown));\r\n    track.setAttribute(\"aria-valuetext\", shown + \"% \" + displayLabel);\r\n    track.setAttribute(\"aria-label\", accountName + \": \" + (scope ? scope + \" \" + label : label));\r\n\r\n    var fill = el(\"div\", \"meter-fill\");\r\n    fill.style.width = displayed + \"%\";\r\n    track.appendChild(fill);\r\n    row.appendChild(track);\r\n\r\n    var resetsAt = parseDate(source.resetsAt);\r\n    if (resetsAt) {\r\n      var delta = resetsAt.getTime() - now;\r\n      row.appendChild(el(\r\n        \"div\",\r\n        \"meter-reset\",\r\n        delta > 0 ? \"Resets in \" + formatDuration(delta) : \"Resetting now\"\r\n      ));\r\n    }\r\n\r\n    return row;\r\n  }\r\n\r\n  // Codex hands out redeemable \"usage limit reset\" credits. They belong to the\r\n  // account rather than to any one window, so they sit under the header as a\r\n  // quiet line of their own. Absent from the payload when there is none.\r\n  function renderResetCredits(source, now, account) {\r\n    if (!source || typeof source !== \"object\") return null;\r\n\r\n    var available = Math.floor(Number(source.available));\r\n    if (!isFinite(available) || available < 1) return null;\r\n\r\n    var text = available === 1\r\n      ? \"1 reset available\"\r\n      : available + \" resets available\";\r\n\r\n    var expiresAt = parseDate(source.expiresAt);\r\n    if (expiresAt) {\r\n      var expiry = formatDaysUntil(expiresAt, now);\r\n      if (expiry) text += \", \" + expiry;\r\n    }\r\n\r\n    var box = el(\"div\", \"resets\");\r\n    var button = el(\"button\", \"reset-chip\", text);\r\n    button.type = \"button\";\r\n    button.dataset.resetAccount = account.id;\r\n    button.setAttribute(\"aria-haspopup\", \"dialog\");\r\n    button.setAttribute(\"aria-label\", \"View resets for \" + (account.name || \"Account\"));\r\n    button.addEventListener(\"click\", function () { openResetDetails(account); });\r\n    box.appendChild(button);\r\n    var notice = resetExpiryNotice(source, now);\r\n    if (notice) box.appendChild(el(\"p\", \"reset-notice\", notice));\r\n    return box;\r\n  }\r\n\r\n  function renderCard(account, isPrimary, now) {\r\n    var name = typeof account.name === \"string\" && account.name ? account.name : \"Account\";\r\n    var card = el(\"article\", \"card\");\r\n\r\n    var head = el(\"div\", \"card-head\");\r\n    var worst = worstState(account);\r\n    var dotState = account.blocked ? \"red\" : (worst === null ? \"none\" : worst);\r\n    var dot = el(\"span\", \"dot is-\" + dotState);\r\n    dot.setAttribute(\"aria-hidden\", \"true\");\r\n    head.appendChild(dot);\r\n    head.appendChild(renderProviderIcon(account.provider));\r\n\r\n    var heading = el(\"h2\", \"account-name\", name);\r\n    if (isPrimary) {\r\n      var star = el(\"span\", \"star\", \"★\");\r\n      star.setAttribute(\"aria-hidden\", \"true\");\r\n      heading.appendChild(star);\r\n      heading.appendChild(el(\"span\", \"sr-only\", \" (primary account)\"));\r\n    }\r\n    head.appendChild(heading);\r\n\r\n    if (typeof account.plan === \"string\" && account.plan) {\r\n      head.appendChild(el(\"span\", \"chip\", account.plan));\r\n    }\r\n    card.appendChild(head);\r\n\r\n    // The provider is refusing requests right now. That is a different fact\r\n    // from any single window reading 100%, so it gets its own line.\r\n    if (account.blocked) {\r\n      card.appendChild(el(\"p\", \"blocked-banner\", \"Limit reached - requests are being refused.\"));\r\n    }\r\n\r\n    var resets = renderResetCredits(account.resetCredits, now, account);\r\n    if (resets) card.appendChild(resets);\r\n\r\n    var rows = windowsOf(account);\r\n    if (rows.length === 0) {\r\n      card.appendChild(el(\"p\", \"card-empty\", \"No usage windows reported.\"));\r\n      return card;\r\n    }\r\n\r\n    var meters = el(\"div\", \"meters\");\r\n    rows.forEach(function (source) {\r\n      if (source && typeof source === \"object\") {\r\n        meters.appendChild(renderMeterRow(name, source, now));\r\n      }\r\n    });\r\n    card.appendChild(meters);\r\n    return card;\r\n  }\r\n\r\n  var resetDialog = null;\r\n  var resetDialogBody = null;\r\n  var resetAccountId = null;\r\n\r\n  function fillResetDetails(account, now) {\r\n    resetDialogBody.replaceChildren();\r\n    var heading = el(\"h2\", \"\", \"Usage resets\");\r\n    heading.id = \"reset-dialog-title\";\r\n    resetDialogBody.appendChild(heading);\r\n    resetDialogBody.appendChild(el(\"p\", \"reset-account\", account.name || \"Account\"));\r\n    var source = account.resetCredits || {};\r\n    var credits = Array.isArray(source.credits) ? source.credits.filter(function (credit) { return credit && typeof credit === \"object\"; }) : null;\r\n    var count = Math.max(0, Math.floor(Number(source.available) || 0));\r\n    var complete = credits && source.detailsComplete === true && credits.length === count;\r\n    resetDialogBody.appendChild(el(\"p\", \"reset-summary\", !count ? \"No resets available in this account.\" : !credits\r\n      ? count + \" resets available. Details are unavailable. Refresh the desktop app to publish them.\"\r\n      : complete ? \"All \" + count + \" resets loaded.\" : credits.length + \" of \" + count + \" resets loaded. Details are incomplete.\"));\r\n    var notice = resetExpiryNotice(source, now);\r\n    if (notice) resetDialogBody.appendChild(el(\"p\", \"reset-notice\", notice));\r\n    (credits || []).slice().sort(function (a, b) {\r\n      return (parseDate(a.expiresAt) || Infinity) - (parseDate(b.expiresAt) || Infinity);\r\n    }).forEach(function (credit) {\r\n      var card = el(\"article\", \"reset-detail\");\r\n      card.appendChild(el(\"h3\", \"\", typeof credit.title === \"string\" && credit.title.trim() ? credit.title : \"Usage limit reset\"));\r\n      if (typeof credit.description === \"string\" && credit.description) card.appendChild(el(\"p\", \"\", credit.description));\r\n      var expiry = resetExpiryInfo(credit.expiresAt, now);\r\n      card.appendChild(el(\"p\", \"reset-date\", expiry ? \"Expires \" + expiry.date.toLocaleString() + \" (\" + expiry.text + \")\" : \"No expiration\"));\r\n      var granted = parseDate(credit.grantedAt);\r\n      card.appendChild(el(\"p\", \"reset-date\", granted ? \"Granted \" + granted.toLocaleString() : \"Grant date unavailable\"));\r\n      if (credit.resetType !== \"codexRateLimits\") card.appendChild(el(\"p\", \"reset-date\", \"This reset type cannot be used in the desktop app.\"));\r\n      resetDialogBody.appendChild(card);\r\n    });\r\n    if (count) resetDialogBody.appendChild(el(\"p\", \"reset-summary\", \"To use a reset, open this account in the desktop app.\"));\r\n  }\r\n\r\n  function openResetDetails(account) {\r\n    if (!resetDialog) {\r\n      resetDialog = el(\"dialog\", \"reset-dialog\");\r\n      resetDialog.setAttribute(\"aria-labelledby\", \"reset-dialog-title\");\r\n      var close = el(\"button\", \"reset-close\", \"Close\");\r\n      close.type = \"button\";\r\n      close.autofocus = true;\r\n      close.addEventListener(\"click\", function () { resetDialog.close(); });\r\n      resetDialog.appendChild(close);\r\n      resetDialogBody = el(\"div\", \"reset-dialog-body\");\r\n      resetDialog.appendChild(resetDialogBody);\r\n      resetDialog.addEventListener(\"click\", function (event) {\r\n        var bounds = resetDialog.getBoundingClientRect();\r\n        if (event.target === resetDialog && (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom)) resetDialog.close();\r\n      });\r\n      resetDialog.addEventListener(\"close\", function () {\r\n        var id = resetAccountId;\r\n        resetAccountId = null;\r\n        var trigger = Array.from(document.querySelectorAll(\"[data-reset-account]\")).find(function (button) { return button.dataset.resetAccount === String(id); });\r\n        if (trigger) trigger.focus();\r\n      });\r\n      document.body.appendChild(resetDialog);\r\n    }\r\n    resetAccountId = account.id;\r\n    fillResetDetails(account, Date.now());\r\n    resetDialog.showModal();\r\n  }\r\n\r\n  function render() {\r\n    if (!payload) return;\r\n\r\n    var now = Date.now();\r\n    if (resetDialog && resetDialog.open) {\r\n      var openAccount = (payload.accounts || []).find(function (account) { return account.id === resetAccountId; });\r\n      if (openAccount) fillResetDetails(openAccount, now);\r\n      else resetDialog.close();\r\n    }\r\n    var generatedAt = parseDate(payload.generatedAt);\r\n\r\n    if (generatedAt) {\r\n      var age = now - generatedAt.getTime();\r\n      if (age < 0) age = 0;\r\n      show(updatedEl, age < 60000\r\n        ? \"Updated just now\"\r\n        : \"Updated \" + formatDuration(age) + \" ago\");\r\n\r\n      if (age > STALE_MS) {\r\n        show(staleEl, \"The app hasn't reported since \" + formatWhen(generatedAt) + \".\");\r\n      } else {\r\n        hide(staleEl);\r\n      }\r\n    } else {\r\n      hide(updatedEl);\r\n      hide(staleEl);\r\n    }\r\n\r\n    var accounts = orderAccounts(payload);\r\n    if (accounts.length === 0) {\r\n      renderMessage(\"No accounts yet\", [\r\n        \"The app is connected but hasn't reported any accounts.\"\r\n      ]);\r\n      return;\r\n    }\r\n\r\n    var list = el(\"div\", \"cards\");\r\n    accounts.forEach(function (account) {\r\n      if (!account || typeof account !== \"object\") return;\r\n      var isPrimary = typeof payload.primary === \"string\" && account.id === payload.primary;\r\n      list.appendChild(renderCard(account, isPrimary, now));\r\n    });\r\n\r\n    clear(content);\r\n    content.appendChild(list);\r\n  }\r\n\r\n  // --- data ------------------------------------------------------------\r\n\r\n  function load() {\r\n    if (loading || !id) return;\r\n    loading = true;\r\n\r\n    fetch(API_BASE + \"/u/\" + id, {\r\n      cache: \"no-store\",\r\n      headers: { Accept: \"application/json\" }\r\n    })\r\n      .then(function (response) {\r\n        if (response.status === 404) {\r\n          var missing = new Error(\"not_found\");\r\n          missing.code = 404;\r\n          throw missing;\r\n        }\r\n        if (!response.ok) throw new Error(\"http_\" + response.status);\r\n        return response.json();\r\n      })\r\n      .then(function (data) {\r\n        if (!data || typeof data !== \"object\") throw new Error(\"bad_payload\");\r\n        payload = data;\r\n        if (!hasPercentModeOverride) {\r\n          applyPercentMode(resolvePercentMode(null, data.displayMode), false);\r\n        }\r\n        lastFetchAt = Date.now();\r\n        hide(connectionEl);\r\n        render();\r\n        syncNotificationControl();\r\n      })\r\n      .catch(function (error) {\r\n        if (error && error.code === 404) {\r\n          payload = null;\r\n          hide(updatedEl);\r\n          hide(staleEl);\r\n          hide(connectionEl);\r\n          renderMessage(\"No data\", [\r\n            \"The link may have expired (data expires after about a week \" +\r\n            \"without the app running) or remote view is disabled.\"\r\n          ]);\r\n          return;\r\n        }\r\n\r\n        // Network or server hiccup: keep whatever is on screen and say so quietly.\r\n        if (payload) {\r\n          show(connectionEl, \"Couldn't refresh just now, retrying shortly.\");\r\n        } else {\r\n          renderMessage(\"Can't reach the server\", [\r\n            \"The usage data couldn't be loaded. This page keeps trying every minute.\",\r\n            \"If it never loads, check that the remote view address in config.js is correct.\"\r\n          ]);\r\n        }\r\n      })\r\n      .then(function () {\r\n        loading = false;\r\n      });\r\n  }\r\n\r\n  // --- start -----------------------------------------------------------\r\n\r\n  // Offline shell only; it needs a secure context and is never required.\r\n  function registerServiceWorker() {\r\n    if (!window.isSecureContext || !navigator.serviceWorker) return;\r\n    serviceWorkerPromise = navigator.serviceWorker.register(\"sw.js\")\r\n      .then(function () { return navigator.serviceWorker.ready; })\r\n      .catch(function () { return null; });\r\n  }\r\n\r\n  function loadVersion() {\r\n    if (!versionEl) return;\r\n    fetch(API_BASE + \"/version\", {\r\n      cache: \"no-store\",\r\n      headers: { Accept: \"application/json\" }\r\n    })\r\n      .then(function (response) {\r\n        if (!response.ok) throw new Error(\"version_unavailable\");\r\n        return response.json();\r\n      })\r\n      .then(function (data) {\r\n        if (!data || typeof data.version !== \"string\" || !data.version.trim()) return;\r\n        var version = data.version.trim();\r\n        versionEl.textContent = \"v\" + version;\r\n        versionEl.setAttribute(\"aria-label\", \"Remote view version \" + version);\r\n        versionEl.hidden = false;\r\n      })\r\n      .catch(function () { /* The viewer remains usable when an older relay has no version route. */ });\r\n  }\r\n\r\n  function resolveId() {\r\n    var params = new URLSearchParams(window.location.search);\r\n    var raw = (params.get(\"id\") || \"\").trim();\r\n    if (raw === DEMO_ID || ID_PATTERN.test(raw)) return raw;\r\n\r\n    // An explicit but unusable ?id= means \"show me the landing page\". Only a\r\n    // link with no id at all falls back to the last id this device saw. That\r\n    // is what an installed app opens, because start_url carries no id.\r\n    if (params.has(\"id\")) return null;\r\n\r\n    var stored = (readStored(LAST_ID_KEY) || \"\").trim();\r\n    return ID_PATTERN.test(stored) ? stored : null;\r\n  }\r\n\r\n  function start() {\r\n    setupTheme();\r\n    loadVersion();\r\n    registerServiceWorker();\r\n    setupNotifications();\r\n\r\n    var resolved = resolveId();\r\n    if (!resolved) {\r\n      renderLanding();\r\n      return;\r\n    }\r\n\r\n    // An empty apiBase is allowed: it means the worker is proxied on this origin.\r\n    if (typeof CONFIG.apiBase !== \"string\" ||\r\n        API_BASE.indexOf(\"REPLACE-WITH-YOUR-WORKER-URL\") !== -1) {\r\n      renderMessage(\"Not configured yet\", [\r\n        \"This page hasn't been pointed at a remote view address.\",\r\n        { before: \"Set \", example: \"apiBase\", after: \" in config.js on the server.\" }\r\n      ]);\r\n      return;\r\n    }\r\n\r\n    setupPercentToggle();\r\n    id = resolved;\r\n    // The demo is never remembered: an installed app must not be left pointing\r\n    // at the sample because someone once opened the demo link on this device.\r\n    if (id === DEMO_ID) {\r\n      if (demoBadge) demoBadge.hidden = false;\r\n    } else {\r\n      writeStored(LAST_ID_KEY, id);\r\n    }\r\n    renderMessage(\"Loading…\", [\"Fetching the latest usage snapshot.\"]);\r\n    load();\r\n\r\n    window.setInterval(load, REFRESH_MS);\r\n    window.setInterval(render, TICK_MS);\r\n\r\n    document.addEventListener(\"visibilitychange\", function () {\r\n      if (document.visibilityState !== \"visible\") return;\r\n      render();\r\n      if (Date.now() - lastFetchAt >= REFRESH_MS) load();\r\n    });\r\n  }\r\n\r\n  start();\r\n})();\r\n",
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
    body: "// AI Usage Tray - remote view service worker.\r\n//\r\n// Its only job is to make the page installable and to survive a flaky\r\n// connection: the static shell is cached, the usage snapshot never is.\r\n// Bump CACHE whenever a shell file changes: a new cache name is what makes\r\n// the update land.\r\n\r\n// v17: adds reset details and expiry notices.\r\nvar CACHE = \"ai-usage-tray-shell-v17\";\r\n\r\nvar SHELL = [\r\n  \"./\",\r\n  \"./index.html\",\r\n  \"./styles.css\",\r\n  \"./app.js\",\r\n  \"./config.js\",\r\n  \"./manifest.webmanifest\",\r\n  \"./icon-192.png\",\r\n  \"./icon-512.png\"\r\n];\r\n\r\nself.addEventListener(\"install\", function (event) {\r\n  event.waitUntil(\r\n    caches.open(CACHE)\r\n      .then(function (cache) { return cache.addAll(SHELL); })\r\n      // A single missing file must not block installation.\r\n      .catch(function () { /* ignore */ })\r\n      .then(function () { return self.skipWaiting(); })\r\n  );\r\n});\r\n\r\nself.addEventListener(\"activate\", function (event) {\r\n  event.waitUntil(\r\n    caches.keys()\r\n      .then(function (keys) {\r\n        return Promise.all(keys.map(function (key) {\r\n          return key === CACHE ? null : caches.delete(key);\r\n        }));\r\n      })\r\n      .then(function () { return self.clients.claim(); })\r\n  );\r\n});\r\n\r\nself.addEventListener(\"fetch\", function (event) {\r\n  var request = event.request;\r\n  if (request.method !== \"GET\") return;\r\n\r\n  var url;\r\n  try {\r\n    url = new URL(request.url);\r\n  } catch (error) {\r\n    return;\r\n  }\r\n\r\n  // Usage snapshots and anything cross-origin go straight to the network,\r\n  // uncached: stale usage numbers would be worse than none.\r\n  if (url.origin !== self.location.origin) return;\r\n  if (url.pathname.indexOf(\"/u/\") !== -1) return;\r\n\r\n  // ignoreSearch so a shared link (/?id=…) still matches the cached shell.\r\n  event.respondWith(\r\n    caches.match(request, { ignoreSearch: true }).then(function (hit) {\r\n      if (hit) return hit;\r\n\r\n      return fetch(request).then(function (response) {\r\n        if (response && response.ok && response.type === \"basic\") {\r\n          var copy = response.clone();\r\n          caches.open(CACHE).then(function (cache) {\r\n            cache.put(request, copy);\r\n          }).catch(function () { /* quota or private mode */ });\r\n        }\r\n        return response;\r\n      });\r\n    })\r\n  );\r\n});\r\n\r\nself.addEventListener(\"push\", function (event) {\r\n  var message;\r\n  try {\r\n    message = event.data ? event.data.json() : null;\r\n  } catch (error) {\r\n    message = null;\r\n  }\r\n\r\n  var alerts = Array.isArray(message && message.alerts) ? message.alerts : [];\r\n  var resets = Array.isArray(message && message.resets) ? message.resets : [];\r\n  var displayMode = message && message.displayMode === \"remaining\" ? \"remaining\" : \"used\";\r\n  var viewerUrl = new URL(\"./\", self.registration.scope);\r\n  if (message && typeof message.readId === \"string\") {\r\n    viewerUrl.searchParams.set(\"id\", message.readId);\r\n  }\r\n\r\n  if (alerts.length === 0 && resets.length === 0) {\r\n    event.waitUntil(self.registration.showNotification(\"AI Usage Tray alert\", {\r\n      body: \"A configured usage threshold was reached.\",\r\n      icon: \"icon-192.png\",\r\n      badge: \"icon-192.png\",\r\n      tag: \"usage-alert\",\r\n      data: { url: viewerUrl.href }\r\n    }));\r\n    return;\r\n  }\r\n\r\n  var notifications = alerts.map(function (alert) {\r\n    var used = Math.max(0, Math.min(100, Math.round(Number(alert.usedPercent) || 0)));\r\n    var shown = displayMode === \"remaining\" ? 100 - used : used;\r\n    var accountName = typeof alert.accountName === \"string\" && alert.accountName\r\n      ? alert.accountName\r\n      : \"Account\";\r\n    var windowName = typeof alert.windowLabel === \"string\" && alert.windowLabel\r\n      ? alert.windowLabel\r\n      : \"Usage\";\r\n    if (typeof alert.scope === \"string\" && alert.scope) {\r\n      windowName += \" · \" + alert.scope;\r\n    }\r\n    return self.registration.showNotification(accountName + \" usage alert\", {\r\n      body: windowName + \" is at \" + shown + \"% \" + displayMode + \".\",\r\n      icon: \"icon-192.png\",\r\n      badge: \"icon-192.png\",\r\n      tag: \"usage-alert:\" + String(alert.accountId || \"account\") + \":\" + String(alert.windowKey || \"window\"),\r\n      renotify: false,\r\n      data: { url: viewerUrl.href }\r\n    });\r\n  });\r\n  notifications = notifications.concat(resets.map(function (reset) {\r\n    var accountName = typeof reset.accountName === \"string\" && reset.accountName\r\n      ? reset.accountName\r\n      : \"Account\";\r\n    var windowName = typeof reset.windowLabel === \"string\" && reset.windowLabel\r\n      ? reset.windowLabel\r\n      : \"Usage\";\r\n    if (typeof reset.scope === \"string\" && reset.scope) {\r\n      windowName += \" · \" + reset.scope;\r\n    }\r\n    return self.registration.showNotification(accountName + \" usage reset\", {\r\n      body: windowName + \" usage reset to 0%.\",\r\n      icon: \"icon-192.png\",\r\n      badge: \"icon-192.png\",\r\n      tag: \"usage-reset:\" + String(reset.accountId || \"account\") + \":\" + String(reset.windowKey || \"window\"),\r\n      renotify: false,\r\n      data: { url: viewerUrl.href }\r\n    });\r\n  }));\r\n  event.waitUntil(Promise.all(notifications));\r\n});\r\n\r\nself.addEventListener(\"notificationclick\", function (event) {\r\n  event.notification.close();\r\n  var url = event.notification.data && event.notification.data.url\r\n    ? event.notification.data.url\r\n    : new URL(\"./\", self.registration.scope).href;\r\n  event.waitUntil(\r\n    clients.matchAll({ type: \"window\", includeUncontrolled: true })\r\n      .then(function (windows) {\r\n        for (var i = 0; i < windows.length; i++) {\r\n          if (\"navigate\" in windows[i]) {\r\n            return windows[i].navigate(url).then(function (client) { return client.focus(); });\r\n          }\r\n        }\r\n        return clients.openWindow(url);\r\n      })\r\n  );\r\n});\r\n",
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
