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

// A public sample so the project can be linked to without exposing a real id.
// It is built per request, never stored, and cannot be written to.
const DEMO_ID = "demo";

const CORS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, PUT, DELETE, OPTIONS",
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

async function put(request, env, writeId) {
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
  await env.USAGE.put(readId, body, { expirationTtl: TTL_SECONDS });
  // Diagnostics only: the app derives the same value locally.
  return empty(204, { "X-Read-Id": readId });
}

// Deleting an absent snapshot answers 204 as well: a probe must not learn
// whether a given writeId was ever in use.
async function remove(env, writeId) {
  const readId = await deriveReadId(writeId);
  await env.USAGE.delete(readId);
  return empty(204, { "X-Read-Id": readId });
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

export default {
  async fetch(request, env) {
    if (request.method === "OPTIONS") return empty(204);

    const path = new URL(request.url).pathname;
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
    if (method === "PUT") return put(request, env, match[1]);
    if (method === "DELETE") return remove(env, match[1]);
    return get(env, match[1]);
  },
};
