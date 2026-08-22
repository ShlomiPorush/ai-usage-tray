// AI Usage Tray - remote view worker.
// Stores a small JSON snapshot per random id and serves it back to the web page.
// The 128-bit id in the path is the only credential, for reads and writes alike.
// The single exception is GET /u/demo, a built-in read-only sample payload.

const ID_RE = /^[a-f0-9]{32}$/;
const MAX_BODY = 16 * 1024; // 16 KB
const TTL_SECONDS = 604800; // 7 days

// A public sample so the project can be linked to without exposing a real id.
// It is built per request, never stored, and cannot be written to.
const DEMO_ID = "demo";

const CORS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, PUT, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type",
};

function json(status, obj) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: {
      ...CORS,
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
    },
  });
}

function empty(status) {
  return new Response(null, { status, headers: { ...CORS } });
}

async function put(request, env, id) {
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

  try {
    JSON.parse(body);
  } catch {
    return json(400, { error: "invalid_json" });
  }

  await env.USAGE.put(id, body, { expirationTtl: TTL_SECONDS });
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

async function get(env, id) {
  const body = await env.USAGE.get(id);
  if (body === null) return json(404, { error: "not_found" });
  return new Response(body, {
    status: 200,
    headers: {
      ...CORS,
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

    if (request.method !== "GET" && request.method !== "PUT") {
      return json(405, { error: "method_not_allowed" });
    }

    // The demo is read-only: a PUT must never be able to replace the sample.
    if (match[1] === DEMO_ID) {
      return request.method === "PUT"
        ? json(405, { error: "method_not_allowed" })
        : json(200, demoSnapshot());
    }

    if (!ID_RE.test(match[1])) return json(400, { error: "invalid_id" });

    return request.method === "PUT"
      ? put(request, env, match[1])
      : get(env, match[1]);
  },
};
