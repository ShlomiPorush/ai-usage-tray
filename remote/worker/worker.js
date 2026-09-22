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

import { findResetAlerts, findThresholdCrossings } from "../shared/usage-alerts.mjs";
import {
  base64UrlEncode,
  parsePushEndpointHosts,
  sendWebPush,
  validatePushSubscription,
  validateVapidConfiguration,
} from "../shared/web-push.mjs";

const ID_RE = /^[a-f0-9]{32}$/;
const MAX_BODY = 16 * 1024; // 16 KB
const TTL_SECONDS = 604800; // 7 days

// A stored push subscription only has to have its TTL pushed forward often
// enough to stay well inside the 7-day window, so an upload rewrites a
// subscription key at most once a day instead of on every PUT. A daily refresh
// still leaves six days of margin.
const REFRESH_INTERVAL_SECONDS = 86400; // 24 hours

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

// A push endpoint is fetched by this worker, so only the browser push services
// in the shared default list are reachable. The optional
// PUSH_ENDPOINT_ALLOWED_HOSTS variable (comma separated) replaces that list.
function pushOptions(env) {
  const hosts = parsePushEndpointHosts(env.PUSH_ENDPOINT_ALLOWED_HOSTS);
  return { allowedEndpointHosts: hosts ?? undefined };
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

// Wall clock in unix seconds. env.NOW is a test-only injection point, in the
// same style as env.PUSH_SENDER; a deployment never sets it.
function nowSeconds(env) {
  const clock = typeof env?.NOW === "function" ? env.NOW : Date.now;
  return Math.floor(clock() / 1000);
}

// A stored subscription value carries two worker-internal fields next to the
// browser subscription: createdAt (first registration) and refreshedAt (last
// TTL rewrite), both unix seconds. They are always written by this worker, so a
// client cannot forge them, nothing outside this file reads these keys, and the
// push sender and the subscription validator ignore unknown fields.
function storeSubscription(env, key, subscription, createdAt, refreshedAt) {
  return env.USAGE.put(
    key,
    JSON.stringify({ ...subscription, createdAt, refreshedAt }),
    { expirationTtl: TTL_SECONDS },
  );
}

// Values stored before createdAt existed fall back to the caller's default; for
// eviction order that default is 0, which ranks them as the oldest entries and
// keeps a burst of new registrations from displacing long-standing ones.
function createdAtOf(stored, fallback) {
  const createdAt = Number(stored?.createdAt);
  return Number.isFinite(createdAt) ? createdAt : fallback;
}

function compareByAge(left, right) {
  if (left.createdAt !== right.createdAt) return left.createdAt - right.createdAt;
  if (left.name === right.name) return 0;
  return left.name < right.name ? -1 : 1;
}

async function refreshSubscriptions(env, readId) {
  const listed = await env.USAGE.list({ prefix: subscriptionPrefix(readId) });
  const options = pushOptions(env);
  const now = nowSeconds(env);
  await Promise.all(listed.keys.map(async (entry) => {
    const subscription = await env.USAGE.get(entry.name, { type: "json" });
    if (!validatePushSubscription(subscription, options)) {
      await env.USAGE.delete(entry.name);
      return;
    }
    // Rewriting an entry that was refreshed recently costs two KV writes per
    // upload and buys nothing: its TTL still has days left. A value stored
    // without refreshedAt counts as stale, so it is rewritten once and then
    // settles into the same interval.
    const refreshedAt = Number(subscription.refreshedAt);
    if (Number.isFinite(refreshedAt) && now - refreshedAt <= REFRESH_INTERVAL_SECONDS) return;
    const hash = await subscriptionHash(subscription.endpoint);
    await Promise.all([
      storeSubscription(env, entry.name, subscription, createdAtOf(subscription, now), now),
      env.USAGE.put(endpointKey(hash), readId, { expirationTtl: TTL_SECONDS }),
    ]);
  }));
}

// The pre-put list check in manageSubscription reads an eventually consistent
// KV list, so a burst of concurrent registrations can all pass it and store
// more entries than the cap. Re-listing after the write converges the view:
// everything past MAX_PUSH_SUBSCRIPTIONS, newest first by stored createdAt and
// then by key, is deleted. This is bounded self-healing rather than a
// transaction, and the size of the window in front of a real KV namespace is
// owner-observed. Returns false when the caller's own entry was evicted.
async function enforceSubscriptionCap(env, readId, key) {
  const prefix = subscriptionPrefix(readId);
  const listed = await env.USAGE.list({ prefix });
  if (listed.keys.length <= MAX_PUSH_SUBSCRIPTIONS) return true;
  const entries = await Promise.all(listed.keys.map(async (entry) => ({
    name: entry.name,
    createdAt: createdAtOf(await env.USAGE.get(entry.name, { type: "json" }), 0),
  })));
  entries.sort(compareByAge);
  const overflow = entries.slice(MAX_PUSH_SUBSCRIPTIONS);
  await Promise.all(overflow.map(async (entry) => {
    await env.USAGE.delete(entry.name);
    await deleteEndpointMappingIfOwned(env, entry.name.slice(prefix.length), readId);
  }));
  return !overflow.some((entry) => entry.name === key);
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
  const options = pushOptions(env);
  const message = {
    type: "usage-alerts",
    readId,
    displayMode: data.displayMode === "remaining" ? "remaining" : "used",
    alerts: crossings,
    resets,
  };
  await Promise.all(listed.keys.map(async (entry) => {
    const subscription = await env.USAGE.get(entry.name, { type: "json" });
    if (!validatePushSubscription(subscription, options)) {
      await env.USAGE.delete(entry.name);
      return;
    }
    try {
      const sender = typeof env.PUSH_SENDER === "function" ? env.PUSH_SENDER : sendWebPush;
      const result = await sender(subscription, message, configuration, undefined, undefined, options);
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
  if (!validatePushSubscription(parsed.data, pushOptions(env))) {
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
  // Re-registering the same endpoint keeps its original createdAt, so refreshing
  // a subscription never makes it look new to the cap.
  const now = nowSeconds(env);
  const stored = await env.USAGE.get(key, { type: "json" });
  await storeSubscription(env, key, parsed.data, createdAtOf(stored, now), now);
  await env.USAGE.put(endpointKey(hash), readId, { expirationTtl: TTL_SECONDS });
  if (!await enforceSubscriptionCap(env, readId, key)) {
    return json(429, { error: "too_many_subscriptions" });
  }
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
        resetCredits: { available: 1, expiresAt: at(30 * DAY) },
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

export default {
  async fetch(request, env, context) {
    if (request.method === "OPTIONS") return empty(204);

    const path = new URL(request.url).pathname;
    if (request.method === "GET" && path === "/version") {
      return json(200, { version: env.REMOTE_VIEW_VERSION || "__REMOTE_VIEW_VERSION__" });
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
