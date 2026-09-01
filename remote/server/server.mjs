import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync } from "node:fs";
import { createServer } from "node:http";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { DatabaseSync } from "node:sqlite";
import { findResetAlerts, findThresholdCrossings } from "../shared/usage-alerts.mjs";
import {
  sendWebPush,
  validatePushSubscription,
  validateVapidConfiguration,
} from "../shared/web-push.mjs";
import { ensureVapidConfiguration } from "./vapid-configuration.mjs";

export const ID_RE = /^[a-f0-9]{32}$/;
export const MAX_BODY = 16 * 1024;
export const TTL_MS = 7 * 24 * 60 * 60 * 1000;

const MAX_ACCOUNTS = 32;
const MAX_WINDOWS = 32;
const MAX_STRING = 256;
const MAX_DEPTH = 8;
const MAX_PUSH_SUBSCRIPTIONS = 8;
const DEMO_ID = "demo";
const STATIC_CACHE = "public, max-age=300";
const CONFIG_BODY = 'window.REMOTE_VIEW_CONFIG = { apiBase: "" };\n';

const CORS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, PUT, POST, DELETE, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type",
  "Access-Control-Expose-Headers": "X-Read-Id",
};

const SECURITY = {
  "X-Content-Type-Options": "nosniff",
  "Referrer-Policy": "no-referrer",
};

const ASSET_DEFINITIONS = [
  { path: "/index.html", file: "index.html", type: "text/html; charset=utf-8", html: true },
  { path: "/styles.css", file: "styles.css", type: "text/css; charset=utf-8" },
  { path: "/app.js", file: "app.js", type: "text/javascript; charset=utf-8" },
  { path: "/manifest.webmanifest", file: "manifest.webmanifest", type: "application/manifest+json; charset=utf-8" },
  { path: "/sw.js", file: "sw.js", type: "text/javascript; charset=utf-8", cache: "no-store" },
  { path: "/icon-192.png", file: "icon-192.png", type: "image/png", binary: true },
  { path: "/icon-512.png", file: "icon-512.png", type: "image/png", binary: true },
];

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

export function validatePayload(data) {
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

  return stringsWithinLimits(data, 0) ? null : "field_too_long";
}

export function deriveReadId(writeId) {
  return createHash("sha256").update(writeId, "utf8").digest("hex").slice(0, 32);
}

export class SnapshotStore {
  constructor(databasePath) {
    if (databasePath !== ":memory:") mkdirSync(dirname(databasePath), { recursive: true });

    this.database = new DatabaseSync(databasePath);
    this.database.exec("PRAGMA busy_timeout = 5000");
    this.database.exec("PRAGMA journal_mode = WAL");
    this.database.exec("PRAGMA synchronous = NORMAL");
    this.database.exec(`
      CREATE TABLE IF NOT EXISTS snapshots (
        read_id TEXT PRIMARY KEY,
        payload TEXT NOT NULL,
        expires_at INTEGER NOT NULL
      ) STRICT;
      CREATE INDEX IF NOT EXISTS snapshots_expiry ON snapshots (expires_at);
      CREATE TABLE IF NOT EXISTS push_subscriptions (
        endpoint TEXT PRIMARY KEY,
        read_id TEXT NOT NULL,
        subscription TEXT NOT NULL
      ) STRICT;
      CREATE INDEX IF NOT EXISTS push_subscriptions_read_id ON push_subscriptions (read_id);
    `);

    this.upsertStatement = this.database.prepare(`
      INSERT INTO snapshots (read_id, payload, expires_at)
      VALUES (?, ?, ?)
      ON CONFLICT (read_id) DO UPDATE SET
        payload = excluded.payload,
        expires_at = excluded.expires_at
    `);
    this.getStatement = this.database.prepare(
      "SELECT payload FROM snapshots WHERE read_id = ? AND expires_at > ?",
    );
    this.deleteStatement = this.database.prepare("DELETE FROM snapshots WHERE read_id = ?");
    this.deleteExpiredStatement = this.database.prepare("DELETE FROM snapshots WHERE expires_at <= ?");
    this.upsertSubscriptionStatement = this.database.prepare(`
      INSERT INTO push_subscriptions (endpoint, read_id, subscription)
      VALUES (?, ?, ?)
      ON CONFLICT (endpoint) DO UPDATE SET
        read_id = excluded.read_id,
        subscription = excluded.subscription
    `);
    this.countSubscriptionsStatement = this.database.prepare(
      "SELECT COUNT(*) AS count FROM push_subscriptions WHERE read_id = ?",
    );
    this.findSubscriptionStatement = this.database.prepare(
      "SELECT read_id FROM push_subscriptions WHERE endpoint = ?",
    );
    this.listSubscriptionsStatement = this.database.prepare(
      "SELECT subscription FROM push_subscriptions WHERE read_id = ? ORDER BY endpoint",
    );
    this.deleteSubscriptionStatement = this.database.prepare(
      "DELETE FROM push_subscriptions WHERE read_id = ? AND endpoint = ?",
    );
    this.deleteSubscriptionsStatement = this.database.prepare(
      "DELETE FROM push_subscriptions WHERE read_id = ?",
    );
    this.deleteExpiredSubscriptionsStatement = this.database.prepare(`
      DELETE FROM push_subscriptions
      WHERE read_id IN (SELECT read_id FROM snapshots WHERE expires_at <= ?)
    `);
    this.pingStatement = this.database.prepare("SELECT 1 AS healthy");
  }

  put(readId, payload, expiresAt) {
    this.upsertStatement.run(readId, payload, expiresAt);
  }

  get(readId, now) {
    return this.getStatement.get(readId, now)?.payload ?? null;
  }

  delete(readId) {
    this.deleteSubscriptionsStatement.run(readId);
    this.deleteStatement.run(readId);
  }

  deleteExpired(now) {
    this.deleteExpiredSubscriptionsStatement.run(now);
    return Number(this.deleteExpiredStatement.run(now).changes);
  }

  putSubscription(readId, subscription) {
    const existing = this.findSubscriptionStatement.get(subscription.endpoint);
    const count = Number(this.countSubscriptionsStatement.get(readId)?.count ?? 0);
    if (existing?.read_id !== readId && count >= MAX_PUSH_SUBSCRIPTIONS) return false;
    this.upsertSubscriptionStatement.run(
      subscription.endpoint,
      readId,
      JSON.stringify(subscription),
    );
    return true;
  }

  listSubscriptions(readId) {
    return this.listSubscriptionsStatement.all(readId).flatMap((row) => {
      try {
        return [JSON.parse(row.subscription)];
      } catch {
        return [];
      }
    });
  }

  deleteSubscription(readId, endpoint) {
    this.deleteSubscriptionStatement.run(readId, endpoint);
  }

  ping() {
    return this.pingStatement.get()?.healthy === 1;
  }

  close() {
    this.database.close();
  }
}

function demoSnapshot(now) {
  const HOUR = 3_600_000;
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

function loadAssets(webRoot) {
  const indexHtml = readFileSync(join(webRoot, "index.html"), "utf8");
  const inlineScriptPattern = /<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/gi;
  const scriptHashes = [...indexHtml.matchAll(inlineScriptPattern)].map(
    (match) => `'sha256-${createHash("sha256").update(match[1], "utf8").digest("base64")}'`,
  );
  const csp = [
    "default-src 'none'",
    "base-uri 'none'",
    `script-src 'self'${scriptHashes.length ? ` ${scriptHashes.join(" ")}` : ""}`,
    "style-src 'self'",
    "img-src 'self'",
    "connect-src 'self'",
    "manifest-src 'self'",
    "worker-src 'self'",
    "form-action 'none'",
    "frame-ancestors 'none'",
  ].join("; ");

  const assets = new Map();
  for (const definition of ASSET_DEFINITIONS) {
    const body = definition.path === "/index.html"
      ? Buffer.from(indexHtml, "utf8")
      : readFileSync(join(webRoot, definition.file));
    assets.set(definition.path, { ...definition, body });
  }
  assets.set("/config.js", {
    path: "/config.js",
    type: "text/javascript; charset=utf-8",
    body: Buffer.from(CONFIG_BODY, "utf8"),
  });

  return { assets, csp };
}

function writeHead(response, status, headers = {}) {
  response.writeHead(status, { ...SECURITY, ...headers });
}

function sendJson(response, status, value) {
  writeHead(response, status, {
    ...CORS,
    "Content-Type": "application/json; charset=utf-8",
    "Cache-Control": "no-store",
  });
  response.end(JSON.stringify(value));
}

function sendEmpty(response, status, extraHeaders = {}) {
  writeHead(response, status, { ...CORS, ...extraHeaders });
  response.end();
}

async function readRequestBody(request) {
  const chunks = [];
  let length = 0;
  let tooLarge = false;
  for await (const chunk of request) {
    length += chunk.length;
    if (length > MAX_BODY) {
      tooLarge = true;
      chunks.length = 0;
    } else if (!tooLarge) {
      chunks.push(chunk);
    }
  }
  return tooLarge ? null : Buffer.concat(chunks).toString("utf8");
}

function sendAsset(response, entry, csp) {
  const headers = {
    "Content-Type": entry.type,
    "Cache-Control": entry.cache ?? STATIC_CACHE,
  };
  if (entry.html) {
    headers["Content-Security-Policy"] = csp;
    headers["X-Frame-Options"] = "DENY";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
  }
  writeHead(response, 200, headers);
  response.end(entry.body);
}

export function createRemoteViewServer({
  databasePath,
  webRoot,
  now = () => Date.now(),
  ttlMs = TTL_MS,
  cleanupIntervalMs = 60 * 60 * 1000,
  runtimeVersion = "dev",
  vapidConfiguration = null,
  pushSender = sendWebPush,
} = {}) {
  if (!databasePath) throw new Error("databasePath is required");
  if (!webRoot || !existsSync(join(webRoot, "index.html"))) {
    throw new Error(`Web root is missing index.html: ${webRoot ?? "<unset>"}`);
  }

  const store = new SnapshotStore(databasePath);
  const { assets, csp } = loadAssets(webRoot);
  store.deleteExpired(now());

  if (vapidConfiguration !== null && !validateVapidConfiguration(vapidConfiguration)) {
    throw new Error("Invalid VAPID configuration");
  }

  async function readJsonRequest(request, response) {
    const contentType = request.headers["content-type"] ?? "";
    if (!contentType.toLowerCase().includes("application/json")) {
      sendJson(response, 415, { error: "unsupported_media_type" });
      return null;
    }

    const declaredLength = Number(request.headers["content-length"]);
    if (Number.isFinite(declaredLength) && declaredLength > MAX_BODY) {
      request.resume();
      sendJson(response, 413, { error: "too_large" });
      return null;
    }

    const body = await readRequestBody(request);
    if (body === null) {
      sendJson(response, 413, { error: "too_large" });
      return null;
    }
    try {
      return { body, data: JSON.parse(body) };
    } catch {
      sendJson(response, 400, { error: "invalid_json" });
      return null;
    }
  }

  async function deliverAlerts(readId, data, crossings, resets) {
    if (vapidConfiguration === null || (crossings.length === 0 && resets.length === 0)) return;
    const message = {
      type: "usage-alerts",
      readId,
      displayMode: data.displayMode === "remaining" ? "remaining" : "used",
      alerts: crossings,
      resets,
    };
    const subscriptions = store.listSubscriptions(readId);
    await Promise.all(subscriptions.map(async (subscription) => {
      try {
        const result = await pushSender(subscription, message, vapidConfiguration);
        if (result.status === 404 || result.status === 410) {
          store.deleteSubscription(readId, subscription.endpoint);
        } else if (!result.ok) {
          console.error("Web Push delivery failed", result.status);
        }
      } catch (error) {
        console.error("Web Push delivery failed", error);
      }
    }));
  }

  const server = createServer(async (request, response) => {
    try {
      const url = new URL(request.url ?? "/", "http://localhost");
      const path = url.pathname;

      if (request.method === "GET" && path === "/health") {
        if (!store.ping()) return sendJson(response, 503, { status: "unhealthy" });
        return sendJson(response, 200, { status: "ok" });
      }

      if (request.method === "GET" && path === "/version") {
        return sendJson(response, 200, { version: runtimeVersion });
      }

      if (request.method === "GET" && path === "/push/vapid-public-key") {
        return vapidConfiguration === null
          ? sendJson(response, 503, { error: "push_not_configured" })
          : sendJson(response, 200, { publicKey: vapidConfiguration.publicKey });
      }

      if (request.method === "GET") {
        const assetPath = path === "/" ? "/index.html" : path;
        const entry = assets.get(assetPath);
        if (entry) return sendAsset(response, entry, csp);
      }

      if (request.method === "OPTIONS") return sendEmpty(response, 204);

      const subscriptionMatch = /^\/u\/([^/]+)\/push-subscription\/?$/.exec(path);
      if (subscriptionMatch) {
        if (request.method !== "POST" && request.method !== "DELETE") {
          return sendJson(response, 405, { error: "method_not_allowed" });
        }
        const readId = subscriptionMatch[1];
        if (!ID_RE.test(readId)) return sendJson(response, 400, { error: "invalid_id" });
        if (vapidConfiguration === null) {
          return sendJson(response, 503, { error: "push_not_configured" });
        }

        const parsed = await readJsonRequest(request, response);
        if (parsed === null) return;
        if (request.method === "POST") {
          if (store.get(readId, now()) === null) {
            return sendJson(response, 404, { error: "not_found" });
          }
          if (!validatePushSubscription(parsed.data)) {
            return sendJson(response, 422, { error: "invalid_subscription" });
          }
          return store.putSubscription(readId, parsed.data)
            ? sendEmpty(response, 204)
            : sendJson(response, 429, { error: "too_many_subscriptions" });
        }

        if (typeof parsed.data?.endpoint !== "string") {
          return sendJson(response, 422, { error: "invalid_subscription" });
        }
        store.deleteSubscription(readId, parsed.data.endpoint);
        return sendEmpty(response, 204);
      }

      const match = /^\/u\/([^/]+)\/?$/.exec(path);
      if (!match) return sendJson(response, 404, { error: "not_found" });

      const method = request.method;
      if (method !== "GET" && method !== "PUT" && method !== "DELETE") {
        return sendJson(response, 405, { error: "method_not_allowed" });
      }

      const id = match[1];
      if (id === DEMO_ID) {
        return method === "GET"
          ? sendJson(response, 200, demoSnapshot(now()))
          : sendJson(response, 405, { error: "method_not_allowed" });
      }

      if (!ID_RE.test(id)) return sendJson(response, 400, { error: "invalid_id" });

      if (method === "GET") {
        const payload = store.get(id, now());
        if (payload === null) return sendJson(response, 404, { error: "not_found" });
        writeHead(response, 200, {
          ...CORS,
          "Content-Type": "application/json; charset=utf-8",
          "Cache-Control": "no-store",
        });
        return response.end(payload);
      }

      const readId = deriveReadId(id);
      if (method === "DELETE") {
        store.delete(readId);
        return sendEmpty(response, 204, { "X-Read-Id": readId });
      }

      const parsed = await readJsonRequest(request, response);
      if (parsed === null) return;
      const { body, data } = parsed;

      const reason = validatePayload(data);
      if (reason !== null) {
        return sendJson(response, 422, { error: "invalid_payload", reason });
      }

      const previousBody = store.get(readId, now());
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
      store.put(readId, body, now() + ttlMs);
      await deliverAlerts(readId, data, crossings, resets);
      return sendEmpty(response, 204, { "X-Read-Id": readId });
    } catch (error) {
      console.error("Remote-view request failed", error);
      if (!response.headersSent) sendJson(response, 500, { error: "internal_error" });
      else response.destroy();
    }
  });
  server.requestTimeout = 15_000;
  server.headersTimeout = 10_000;
  server.keepAliveTimeout = 5_000;
  server.maxHeadersCount = 50;

  let cleanupTimer;
  if (cleanupIntervalMs > 0) {
    cleanupTimer = setInterval(() => {
      try {
        store.deleteExpired(now());
      } catch (error) {
        console.error("Remote-view expiry cleanup failed", error);
      }
    }, cleanupIntervalMs);
    cleanupTimer.unref();
  }

  let closed = false;
  async function close() {
    if (closed) return;
    closed = true;
    if (cleanupTimer) clearInterval(cleanupTimer);
    if (server.listening) {
      await new Promise((resolveClose, rejectClose) => {
        server.close((error) => error ? rejectClose(error) : resolveClose());
      });
    }
    store.close();
  }

  return { server, store, close };
}

function readPositiveInteger(name, fallback) {
  const raw = process.env[name];
  if (raw === undefined) return fallback;
  const value = Number(raw);
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new Error(`${name} must be a positive integer`);
  }
  return value;
}

async function startFromEnvironment() {
  const here = dirname(fileURLToPath(import.meta.url));
  const port = readPositiveInteger("PORT", 8080);
  const databasePath = resolve(process.env.DATABASE_PATH ?? "/data/usage.db");
  const webRoot = resolve(process.env.WEB_ROOT ?? join(here, "..", "..", "web"));
  const ttlMs = readPositiveInteger("SNAPSHOT_TTL_SECONDS", TTL_MS / 1000) * 1000;
  const cleanupIntervalMs = readPositiveInteger("CLEANUP_INTERVAL_SECONDS", 3600) * 1000;
  const runtimeVersion = readFileSync(join(here, "VERSION"), "utf8").trim();
  const vapidConfiguration = await ensureVapidConfiguration({
    path: databasePath === ":memory:" ? null : join(dirname(databasePath), "vapid.json"),
  });
  const app = createRemoteViewServer({
    databasePath,
    webRoot,
    ttlMs,
    cleanupIntervalMs,
    runtimeVersion,
    vapidConfiguration,
  });

  await new Promise((resolveListen, rejectListen) => {
    app.server.once("error", rejectListen);
    app.server.listen(port, "0.0.0.0", resolveListen);
  });
  console.log(`Remote view listening on port ${port}`);

  const shutdown = async () => {
    try {
      await app.close();
      process.exitCode = 0;
    } catch (error) {
      console.error("Remote-view shutdown failed", error);
      process.exitCode = 1;
    }
  };
  process.once("SIGINT", shutdown);
  process.once("SIGTERM", shutdown);
}

const entryPath = process.argv[1] ? pathToFileURL(resolve(process.argv[1])).href : "";
if (import.meta.url === entryPath) {
  startFromEnvironment().catch((error) => {
    console.error("Remote view failed to start", error);
    process.exitCode = 1;
  });
}
