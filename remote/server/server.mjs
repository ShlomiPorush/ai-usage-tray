import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync } from "node:fs";
import { createServer } from "node:http";
import { dirname, isAbsolute, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { DatabaseSync } from "node:sqlite";
import { findResetAlerts, findThresholdCrossings } from "../shared/usage-alerts.mjs";
import {
  MAX_CLOCK_SKEW_SECONDS,
  resolveSigningKey,
  SIGNATURE_HEADER,
  TIMESTAMP_HEADER,
  verifyRequestSignature,
} from "../shared/request-signing.mjs";
import {
  parsePushEndpointHosts,
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

// --- Usage reports -----------------------------------------------------------
// The usage report (cost and token analytics) is a separate, much larger
// payload than the quota snapshot, so it has its own body cap, its own byte
// budget per client address and a global storage cap. The snapshot route keeps
// MAX_BODY.
export const MAX_USAGE_BODY = 1024 * 1024;
export const USAGE_BUDGET_WINDOW_MS = 10 * 60_000;
export const DEFAULT_USAGE_WRITE_BUDGET_BYTES = 8 * 1024 * 1024;
export const DEFAULT_USAGE_STORAGE_MAX_BYTES = 256 * 1024 * 1024;

// A tombstone keeps the highest write sequence after a delete so a delayed or
// retried older PUT cannot bring a removed report back. It is charged a fixed
// size against the storage cap.
export const USAGE_TOMBSTONE_BYTES = 256;
export const USAGE_SEQ_HEADER = "X-Usage-Seq";

export const USAGE_LIMITS = Object.freeze({
  accounts: 64,
  models: 512,
  dayRows: 60_000,
  dailySpanDays: 1_100,
  hourlySpanDays: 8,
  hourGrids: 4,
  hourRows: 40_000,
  rollingRows: 5_000,
  string: 256,
});

// The write-ahead log grows with the largest transaction (a sweep of many expired
// rows), and SQLite keeps that size forever unless a limit is set. The limit is
// applied when a checkpoint completes, so the file shrinks back afterwards.
export const JOURNAL_SIZE_LIMIT_BYTES = 8 * 1024 * 1024;

// VACUUM rewrites the whole database, so it only pays off once a large part of
// the file is free pages and the file is big enough for the rewrite to matter.
export const VACUUM_MIN_DATABASE_BYTES = 32 * 1024 * 1024;
export const VACUUM_MIN_FREELIST_RATIO = 0.5;

// /health is public and its probe writes, so reuse a recent result instead of
// letting a flood of requests turn into a flood of transactions. The container
// health check runs every 30 seconds, well outside this window.
export const HEALTH_WRITE_PROBE_INTERVAL_MS = 5_000;

const STATIC_CACHE = "public, max-age=300";
const CONFIG_BODY = 'window.REMOTE_VIEW_CONFIG = { apiBase: "" };\n';

const CORS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, PUT, POST, DELETE, OPTIONS",
  "Access-Control-Allow-Headers": `Content-Type, ${TIMESTAMP_HEADER}, ${SIGNATURE_HEADER}`,
  "Access-Control-Expose-Headers": "X-Read-Id, Retry-After",
};

const SECURITY = {
  "X-Content-Type-Options": "nosniff",
  "Referrer-Policy": "no-referrer",
};

const ASSET_DEFINITIONS = [
  { path: "/index.html", file: "index.html", type: "text/html; charset=utf-8", html: true },
  { path: "/styles.css", file: "styles.css", type: "text/css; charset=utf-8" },
  { path: "/usage.js", file: "usage.js", type: "text/javascript; charset=utf-8" },
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

const DAY_MS = 86_400_000;
const USAGE_METRIC_COUNT = 10;
// Position of savingsMicroUsd among the ten metrics: the only signed one.
const USAGE_SAVINGS_METRIC = 7;
const USAGE_MAX_METRIC = 1e15;
const USAGE_PROVIDERS = new Set(["claude", "codex"]);
const USAGE_PRICE_FIELDS = ["input", "cacheRead", "cacheWrite5m", "cacheWrite1h", "output"];
const USAGE_MAX_PRICE = 1e6;
const LOCAL_DATE_RE = /^(\d{4})-(\d{2})-(\d{2})$/;
const TIME_ZONE_RE = /^[A-Za-z][A-Za-z0-9_+\-/]*$/;
const MAX_INSTANT_LENGTH = 64;
// A local day lasts 22 to 26 hours in every zone in use today (Antarctica/Troll
// moves its clock by two hours). The bound keeps bucket keys packable.
const MIN_LOCAL_DAY_MINUTES = 20 * 60;
const MAX_LOCAL_DAY_MINUTES = 28 * 60;
const HOUR_BUCKET_RADIX = 16_384;
// Epoch minutes whose millisecond value is still a valid JavaScript Date.
const MAX_DATE_MINUTES = 8.64e15 / 60_000;
// Zone offsets in use range from -12 to +14 hours, so a local midnight lies
// within 15 hours of the UTC midnight of the same date.
const MAX_MIDNIGHT_OFFSET_MINUTES = 15 * 60;

function isPlainObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

/** Epoch day number of a valid `YYYY-MM-DD` calendar date, else null. */
function parseLocalDate(value) {
  if (typeof value !== "string") return null;
  const match = LOCAL_DATE_RE.exec(value);
  if (match === null) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const date = new Date(Date.UTC(year, month - 1, day));
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) {
    return null;
  }
  return date.getTime() / DAY_MS;
}

/** Milliseconds of an ISO-8601 UTC instant ending in `Z`, else null. */
function parseUtcInstant(value) {
  if (typeof value !== "string" || value.length > MAX_INSTANT_LENGTH || !value.endsWith("Z")) {
    return null;
  }
  const ms = Date.parse(value);
  return Number.isFinite(ms) ? ms : null;
}

function isTimeZone(value) {
  if (typeof value !== "string" || value.length > USAGE_LIMITS.string || !TIME_ZONE_RE.test(value)) {
    return false;
  }
  try {
    new Intl.DateTimeFormat("en", { timeZone: value });
    return true;
  } catch {
    return false;
  }
}

function isIndex(value, count) {
  return Number.isInteger(value) && value >= 0 && value < count;
}

/** The ten metrics of section 1.1, starting at `offset` in `row`. */
function metricsValid(row, offset) {
  for (let metric = 0; metric < USAGE_METRIC_COUNT; metric += 1) {
    const value = row[offset + metric];
    if (!Number.isSafeInteger(value) || value > USAGE_MAX_METRIC) return false;
    if (value < (metric === USAGE_SAVINGS_METRIC ? -USAGE_MAX_METRIC : 0)) return false;
  }
  return true;
}

function validateUsageAccounts(accounts) {
  if (!Array.isArray(accounts)) return "bad_accounts";
  if (accounts.length > USAGE_LIMITS.accounts) return "too_many_accounts";
  for (const account of accounts) {
    if (!isPlainObject(account)) return "bad_account";
    if (typeof account.name !== "string") return "bad_account";
    if (account.name.length > USAGE_LIMITS.string) return "field_too_long";
    if (!USAGE_PROVIDERS.has(account.provider)) return "bad_account";
  }
  return null;
}

function validateUsageModels(models) {
  if (!Array.isArray(models)) return "bad_models";
  if (models.length > USAGE_LIMITS.models) return "too_many_models";
  for (const model of models) {
    if (!isPlainObject(model)) return "bad_model";
    if (typeof model.id !== "string" || model.id.length === 0) return "bad_model";
    if (model.id.length > USAGE_LIMITS.string) return "field_too_long";
    if (!USAGE_PROVIDERS.has(model.provider)) return "bad_model";
    if (typeof model.priced !== "boolean") return "bad_model";
    if (!model.priced) {
      if (model.price != null) return "bad_model_price";
      continue;
    }
    if (!isPlainObject(model.price)) return "bad_model_price";
    for (const field of USAGE_PRICE_FIELDS) {
      const rate = model.price[field];
      if (rate == null) continue;
      if (typeof rate !== "number" || !Number.isFinite(rate) || rate < 0 || rate > USAGE_MAX_PRICE) {
        return "bad_model_price";
      }
    }
  }
  return null;
}

/**
 * Validates a usage report (payload version 1). Returns null when it may be
 * stored, else a short reason. One pass over every row, with duplicate keys
 * packed into numbers, so a 1 MiB report validates in milliseconds. Unknown
 * extra fields are ignored; the body cap bounds them.
 */
export function validateUsagePayload(data) {
  if (!isPlainObject(data)) return "not_an_object";
  if (data.version !== 1) return "bad_version";
  if (!Number.isSafeInteger(data.seq) || data.seq <= 0) return "bad_seq";
  if (parseUtcInstant(data.generatedAt) === null) return "bad_generated_at";
  if (!isTimeZone(data.timeZone)) return "bad_time_zone";

  const coverage = data.coverage;
  if (!isPlainObject(coverage)) return "bad_coverage";
  const dailyFrom = parseLocalDate(coverage.dailyFrom);
  const dailyTo = parseLocalDate(coverage.dailyTo);
  if (dailyFrom === null || dailyTo === null || dailyFrom > dailyTo) return "bad_coverage";
  const dailySpan = dailyTo - dailyFrom + 1;
  if (dailySpan > USAGE_LIMITS.dailySpanDays) return "coverage_too_long";

  let hourlyFrom = null;
  let hourlySpan = 0;
  if (coverage.hourlyFrom !== null || coverage.hourlyTo !== null) {
    hourlyFrom = parseLocalDate(coverage.hourlyFrom);
    const hourlyTo = parseLocalDate(coverage.hourlyTo);
    if (hourlyFrom === null || hourlyTo === null || hourlyFrom > hourlyTo) return "bad_hourly_coverage";
    if (hourlyFrom < dailyFrom || hourlyTo > dailyTo) return "bad_hourly_coverage";
    hourlySpan = hourlyTo - hourlyFrom + 1;
    if (hourlySpan > USAGE_LIMITS.hourlySpanDays) return "hourly_coverage_too_long";
  }

  const accountsReason = validateUsageAccounts(data.accounts);
  if (accountsReason !== null) return accountsReason;
  const modelsReason = validateUsageModels(data.models);
  if (modelsReason !== null) return modelsReason;
  const accountCount = data.accounts.length;
  const modelCount = data.models.length;

  // days: [dayNumber, accountIdx, modelIdx, ...10 metrics]
  const days = data.days;
  if (!Array.isArray(days)) return "bad_days";
  if (days.length > USAGE_LIMITS.dayRows) return "too_many_day_rows";
  const dayKeys = new Set();
  for (const row of days) {
    if (!Array.isArray(row) || row.length !== 3 + USAGE_METRIC_COUNT) return "bad_day_row";
    const [day, account, model] = row;
    if (!isIndex(day, dailySpan)) return "bad_day_number";
    if (!isIndex(account, accountCount) || !isIndex(model, modelCount)) return "bad_index";
    if (!metricsValid(row, 3)) return "bad_metric";
    const key = (day * USAGE_LIMITS.accounts + account) * USAGE_LIMITS.models + model;
    if (dayKeys.has(key)) return "duplicate_day_row";
    dayKeys.add(key);
  }

  // hourDayStarts: one more entry than hourly days, strictly increasing.
  const starts = data.hourDayStarts;
  if (!Array.isArray(starts)) return "bad_hour_day_starts";
  const hours = data.hours;
  if (!Array.isArray(hours)) return "bad_hours";
  if (hourlyFrom === null) {
    if (starts.length !== 0) return "bad_hour_day_starts";
    if (hours.length !== 0) return "bad_hours";
  } else {
    if (starts.length !== hourlySpan + 1) return "bad_hour_day_starts";
    if (Math.abs(starts[0] - hourlyFrom * 1440) > MAX_MIDNIGHT_OFFSET_MINUTES) return "bad_hour_day_starts";
    for (let index = 0; index < starts.length; index += 1) {
      if (!Number.isSafeInteger(starts[index]) || starts[index] < 0 || starts[index] > MAX_DATE_MINUTES) {
        return "bad_hour_day_starts";
      }
      if (index > 0) {
        const length = starts[index] - starts[index - 1];
        if (length < MIN_LOCAL_DAY_MINUTES || length > MAX_LOCAL_DAY_MINUTES) return "bad_hour_day_starts";
      }
    }
    if (hours.length > USAGE_LIMITS.hourGrids) return "too_many_hour_grids";

    // hours: [{ phaseMinutes, rows: [[dayNumber, bucketStart, accountIdx, modelIdx, ...10 metrics]] }]
    const lowest = starts[0] - 60;
    const highest = starts[starts.length - 1] + 60;
    const firstHourlyDay = hourlyFrom - dailyFrom;
    const phases = new Set();
    let totalRows = 0;
    for (const grid of hours) {
      if (!isPlainObject(grid) || !Array.isArray(grid.rows)) return "bad_hour_grid";
      totalRows += grid.rows.length;
    }
    if (totalRows > USAGE_LIMITS.hourRows) return "too_many_hour_rows";

    const hourKeys = new Set();
    for (let gridIndex = 0; gridIndex < hours.length; gridIndex += 1) {
      const { phaseMinutes: phase, rows } = hours[gridIndex];
      if (!Number.isInteger(phase) || phase < 0 || phase > 59 || phases.has(phase)) return "bad_hour_grid";
      phases.add(phase);
      for (const row of rows) {
        if (!Array.isArray(row) || row.length !== 4 + USAGE_METRIC_COUNT) return "bad_hour_row";
        const [day, bucketStart, account, model] = row;
        if (!Number.isInteger(day) || day < firstHourlyDay || day >= firstHourlyDay + hourlySpan) {
          return "bad_day_number";
        }
        if (!Number.isSafeInteger(bucketStart) || bucketStart < lowest || bucketStart > highest
          || (bucketStart - phase) % 60 !== 0) {
          return "bad_bucket_start";
        }
        if (!isIndex(account, accountCount) || !isIndex(model, modelCount)) return "bad_index";
        if (!metricsValid(row, 4)) return "bad_metric";
        const key = ((((gridIndex * USAGE_LIMITS.hourlySpanDays) + (day - firstHourlyDay))
          * HOUR_BUCKET_RADIX + (bucketStart - lowest))
          * USAGE_LIMITS.accounts + account) * USAGE_LIMITS.models + model;
        if (hourKeys.has(key)) return "duplicate_hour_row";
        hourKeys.add(key);
      }
    }
  }

  // rolling: two adjacent 24-hour windows of [hourIndex, accountIdx, modelIdx, ...10 metrics]
  const rolling = data.rolling;
  if (!isPlainObject(rolling)) return "bad_rolling";
  const windows = [rolling.current, rolling.previous];
  const bounds = [];
  for (const window of windows) {
    if (!isPlainObject(window) || !Array.isArray(window.rows)) return "bad_rolling";
    const since = parseUtcInstant(window.since);
    const until = parseUtcInstant(window.until);
    if (since === null || until === null || until - since !== DAY_MS) return "bad_rolling_window";
    if (window.rows.length > USAGE_LIMITS.rollingRows) return "too_many_rolling_rows";
    bounds.push({ since, until });
  }
  if (bounds[1].until !== bounds[0].since) return "bad_rolling_window";
  const rollingKeys = new Set();
  for (let windowIndex = 0; windowIndex < windows.length; windowIndex += 1) {
    for (const row of windows[windowIndex].rows) {
      if (!Array.isArray(row) || row.length !== 3 + USAGE_METRIC_COUNT) return "bad_rolling_row";
      const [hour, account, model] = row;
      if (!isIndex(hour, 24)) return "bad_rolling_row";
      if (!isIndex(account, accountCount) || !isIndex(model, modelCount)) return "bad_index";
      if (!metricsValid(row, 3)) return "bad_metric";
      const key = ((windowIndex * 24 + hour) * USAGE_LIMITS.accounts + account) * USAGE_LIMITS.models + model;
      if (rollingKeys.has(key)) return "duplicate_rolling_row";
      rollingKeys.add(key);
    }
  }

  return null;
}

export function deriveReadId(writeId) {
  return createHash("sha256").update(writeId, "utf8").digest("hex").slice(0, 32);
}

// Deleting rows only moves pages to the freelist, so a burst of writes keeps the
// file at its high-water mark. Reclaim it only when the waste is both relatively
// and absolutely large.
export function shouldVacuum({ freelistCount = 0, pageCount = 0, databaseBytes = 0 } = {}) {
  if (!Number.isFinite(pageCount) || pageCount <= 0) return false;
  if (!Number.isFinite(freelistCount) || freelistCount <= 0) return false;
  if (!Number.isFinite(databaseBytes) || databaseBytes <= VACUUM_MIN_DATABASE_BYTES) return false;
  return freelistCount / pageCount > VACUUM_MIN_FREELIST_RATIO;
}

// A default data directory keeps the push signing key next to the disposable
// snapshots. VAPID_KEY_PATH moves it out without changing existing deployments.
export function resolveVapidKeyPath(databasePath, environment = process.env) {
  const configured = environment.VAPID_KEY_PATH?.trim();
  if (configured) {
    if (!isAbsolute(configured)) {
      throw new Error("VAPID_KEY_PATH must be an absolute path");
    }
    return configured;
  }
  return databasePath === ":memory:" ? null : join(dirname(databasePath), "vapid.json");
}

// Every write to /u/{writeId} creates or refreshes a row that lives for the
// whole TTL, and the endpoint is unauthenticated by design, so a scripted flood
// of invented ids is the one way to turn this service into storage. The limit
// below is the actual defence; the signature only decides which budget applies.
export const RATE_LIMIT_WINDOW_MS = 60_000;
export const DEFAULT_UNSIGNED_WRITES_PER_MINUTE = 10;
export const DEFAULT_SIGNED_WRITES_PER_MINUTE = 120;

// One entry is a short string key and two numbers. The cap exists so a spray
// across many source addresses cannot grow the map without bound; reaching it
// drops the oldest entries, which at worst forgives some counted requests.
export const MAX_RATE_LIMIT_BUCKETS = 20_000;

/**
 * Fixed one-minute window per client, with the limit chosen per request.
 *
 * A single bucket per client counts every write, whatever its tier, so the
 * total a client can push through in a minute is the largest configured limit
 * and not the sum of both. Only accepted writes are counted: a client already
 * over the strict budget cannot keep inflating the counter and lock out a
 * correctly signed request from the same address.
 */
export class WriteRateLimiter {
  constructor({ windowMs = RATE_LIMIT_WINDOW_MS, maxBuckets = MAX_RATE_LIMIT_BUCKETS } = {}) {
    this.windowMs = windowMs;
    this.maxBuckets = maxBuckets;
    this.buckets = new Map();
  }

  get size() {
    return this.buckets.size;
  }

  /** Forgets windows that have already elapsed. */
  prune(now) {
    for (const [key, bucket] of this.buckets) {
      if (now - bucket.startedAt >= this.windowMs) this.buckets.delete(key);
    }
  }

  /**
   * Returns `{ allowed, retryAfterSeconds, count }`. `retryAfterSeconds` is the
   * whole number of seconds until the current window ends, never below 1.
   */
  check(key, limit, now) {
    this.prune(now);

    let bucket = this.buckets.get(key);
    if (bucket === undefined || now - bucket.startedAt >= this.windowMs) {
      bucket = { startedAt: now, count: 0 };
      this.buckets.set(key, bucket);
    }

    if (bucket.count >= limit) {
      const remaining = this.windowMs - (now - bucket.startedAt);
      return {
        allowed: false,
        count: bucket.count,
        retryAfterSeconds: Math.max(1, Math.ceil(remaining / 1000)),
      };
    }

    bucket.count += 1;

    // Pruning already removed everything stale, so anything left is live and
    // the oldest insertion is the least bad thing to forget.
    while (this.buckets.size > this.maxBuckets) {
      const oldest = this.buckets.keys().next();
      if (oldest.done || oldest.value === key) break;
      this.buckets.delete(oldest.value);
    }

    return { allowed: true, count: bucket.count, retryAfterSeconds: 0 };
  }
}

/**
 * Bytes of usage reports stored per client address in a fixed window. Usage
 * reports are up to 1 MiB, so the request count limit alone would still let one
 * address rewrite a lot of storage. `check` never charges: the caller charges
 * only after the report was actually stored, so refused or stale writes do not
 * use up a legitimate client's budget. Nothing awaits between the two calls.
 */
export class UsageByteBudget {
  constructor({ windowMs = USAGE_BUDGET_WINDOW_MS, maxBuckets = MAX_RATE_LIMIT_BUCKETS } = {}) {
    this.windowMs = windowMs;
    this.maxBuckets = maxBuckets;
    this.buckets = new Map();
  }

  get size() {
    return this.buckets.size;
  }

  prune(now) {
    for (const [key, bucket] of this.buckets) {
      if (now - bucket.startedAt >= this.windowMs) this.buckets.delete(key);
    }
  }

  /** Returns `{ allowed, retryAfterSeconds, usedBytes }` for storing `bytes` more. */
  check(key, bytes, limit, now) {
    this.prune(now);
    const bucket = this.buckets.get(key);
    const usedBytes = bucket?.bytes ?? 0;
    if (usedBytes + bytes <= limit) {
      return { allowed: true, retryAfterSeconds: 0, usedBytes };
    }
    const remaining = bucket === undefined ? this.windowMs : this.windowMs - (now - bucket.startedAt);
    return {
      allowed: false,
      retryAfterSeconds: Math.max(1, Math.ceil(remaining / 1000)),
      usedBytes,
    };
  }

  charge(key, bytes, now) {
    this.prune(now);
    let bucket = this.buckets.get(key);
    if (bucket === undefined) {
      bucket = { startedAt: now, bytes: 0 };
      this.buckets.set(key, bucket);
    }
    bucket.bytes += bytes;

    while (this.buckets.size > this.maxBuckets) {
      const oldest = this.buckets.keys().next();
      if (oldest.done || oldest.value === key) break;
      this.buckets.delete(oldest.value);
    }
  }
}

/**
 * The address the rate limit is keyed on. The socket peer is the only value a
 * client cannot choose, so it is the default. Behind a reverse proxy every
 * request would share the proxy's address, so `TRUST_PROXY=1` switches to the
 * LAST `X-Forwarded-For` entry: the one appended by the trusted proxy in front
 * of this server. Earlier entries are whatever the client sent and would let a
 * flood claim a fresh address per request. Correct for exactly one trusted
 * proxy, whether it appends to or replaces the incoming header.
 */
export function resolveClientAddress(request, { trustProxy = false } = {}) {
  if (trustProxy) {
    const header = request?.headers?.["x-forwarded-for"];
    const raw = Array.isArray(header) ? header[header.length - 1] : header;
    const entries = typeof raw === "string" ? raw.split(",") : [];
    const last = entries.length > 0 ? entries[entries.length - 1].trim() : "";
    if (last !== "") return normalizeAddress(last);
  }
  return normalizeAddress(request?.socket?.remoteAddress ?? "") || "unknown";
}

// "::ffff:203.0.113.7" and "203.0.113.7" are the same client, and a forwarded
// entry may arrive bracketed or with a port.
function normalizeAddress(value) {
  let address = value.trim().toLowerCase();
  const bracketed = /^\[(.+)\](?::\d+)?$/.exec(address);
  if (bracketed) address = bracketed[1];
  else if (/^\d{1,3}(\.\d{1,3}){3}:\d+$/.test(address)) address = address.slice(0, address.lastIndexOf(":"));
  if (address.startsWith("::ffff:") && address.includes(".")) address = address.slice(7);
  return address;
}

export class SnapshotStore {
  constructor(databasePath, { usageTombstoneTtlMs = TTL_MS } = {}) {
    if (databasePath !== ":memory:") mkdirSync(dirname(databasePath), { recursive: true });
    this.usageTombstoneTtlMs = usageTombstoneTtlMs;

    this.database = new DatabaseSync(databasePath);
    this.database.exec("PRAGMA busy_timeout = 5000");
    this.database.exec("PRAGMA journal_mode = WAL");
    this.database.exec("PRAGMA synchronous = NORMAL");
    this.database.exec(`PRAGMA journal_size_limit = ${JOURNAL_SIZE_LIMIT_BYTES}`);
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
      CREATE TABLE IF NOT EXISTS health_probe (
        id INTEGER PRIMARY KEY,
        checked_at INTEGER NOT NULL
      ) STRICT;
      CREATE TABLE IF NOT EXISTS usage_reports (
        read_id TEXT PRIMARY KEY,
        seq INTEGER NOT NULL,
        expires_at INTEGER NOT NULL,
        bytes INTEGER NOT NULL,
        payload TEXT NULL
      ) STRICT;
      CREATE INDEX IF NOT EXISTS usage_reports_expiry ON usage_reports (expires_at);
      CREATE INDEX IF NOT EXISTS usage_reports_bytes ON usage_reports (bytes);
    `);
    // usage_reports.bytes is length(CAST(payload AS BLOB)), or the fixed
    // tombstone size, recorded on write. Summing it from a small index keeps
    // the storage-cap check from reading every stored report; the column sits
    // before the payload so reading it never walks the payload's overflow pages.

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
    this.writeProbeStatement = this.database.prepare(`
      INSERT INTO health_probe (id, checked_at)
      VALUES (1, ?)
      ON CONFLICT (id) DO UPDATE SET checked_at = excluded.checked_at
    `);

    this.liveSnapshotExpiryStatement = this.database.prepare(
      "SELECT expires_at FROM snapshots WHERE read_id = ? AND expires_at > ?",
    );
    this.usageRowStatement = this.database.prepare(
      "SELECT seq, bytes, payload IS NULL AS tombstone FROM usage_reports WHERE read_id = ?",
    );
    this.usageStoredBytesStatement = this.database.prepare(
      "SELECT COALESCE(SUM(bytes), 0) AS total FROM usage_reports",
    );
    this.upsertUsageStatement = this.database.prepare(`
      INSERT INTO usage_reports (read_id, seq, expires_at, bytes, payload)
      VALUES (?, ?, ?, ?, ?)
      ON CONFLICT (read_id) DO UPDATE SET
        seq = excluded.seq,
        expires_at = excluded.expires_at,
        bytes = excluded.bytes,
        payload = excluded.payload
    `);
    this.getUsageStatement = this.database.prepare(`
      SELECT u.payload AS payload
      FROM usage_reports AS u
      JOIN snapshots AS s ON s.read_id = u.read_id
      WHERE u.read_id = ? AND u.payload IS NOT NULL AND u.expires_at > ? AND s.expires_at > ?
    `);
    this.tombstoneStaleUsageStatement = this.database.prepare(`
      UPDATE usage_reports
      SET payload = NULL, bytes = ${USAGE_TOMBSTONE_BYTES}, expires_at = ?
      WHERE payload IS NOT NULL AND (
        expires_at <= ?
        OR NOT EXISTS (
          SELECT 1 FROM snapshots AS s
          WHERE s.read_id = usage_reports.read_id AND s.expires_at > ?
        )
      )
    `);
    this.deleteExpiredTombstonesStatement = this.database.prepare(
      "DELETE FROM usage_reports WHERE payload IS NULL AND expires_at <= ?",
    );
  }

  /**
   * Runs `body` inside BEGIN IMMEDIATE, so a check and the write that depends
   * on it cannot interleave with another connection's write.
   */
  transaction(body) {
    this.database.exec("BEGIN IMMEDIATE");
    try {
      const result = body();
      this.database.exec("COMMIT");
      return result;
    } catch (error) {
      try {
        this.database.exec("ROLLBACK");
      } catch {
        // The failed statement may already have ended the transaction.
      }
      throw error;
    }
  }

  put(readId, payload, expiresAt) {
    this.upsertStatement.run(readId, payload, expiresAt);
  }

  get(readId, now) {
    return this.getStatement.get(readId, now)?.payload ?? null;
  }

  /**
   * Deletes the snapshot and its browser subscriptions. A stored usage report
   * becomes a tombstone that keeps the highest write sequence seen (the stored
   * one or `usageSeq` from the X-Usage-Seq header), so a delayed older usage
   * PUT cannot restore it once the snapshot is uploaded again.
   */
  delete(readId, { usageSeq = null, now = Date.now() } = {}) {
    this.transaction(() => {
      this.deleteSubscriptionsStatement.run(readId);
      this.deleteStatement.run(readId);
      const row = this.usageRowStatement.get(readId);
      if (row === undefined) return;
      const seq = usageSeq === null ? row.seq : Math.max(row.seq, usageSeq);
      this.upsertUsageStatement.run(readId, seq, now + this.usageTombstoneTtlMs, USAGE_TOMBSTONE_BYTES, null);
    });
  }

  /**
   * Sweeps expired data. Usage reports that expired or lost their live
   * snapshot become tombstones; tombstones are removed only when their own
   * expiry passes. Returns the number of snapshots removed.
   */
  deleteExpired(now) {
    return this.transaction(() => {
      this.tombstoneStaleUsageStatement.run(now + this.usageTombstoneTtlMs, now, now);
      this.deleteExpiredTombstonesStatement.run(now);
      this.deleteExpiredSubscriptionsStatement.run(now);
      return Number(this.deleteExpiredStatement.run(now).changes);
    });
  }

  /**
   * Stores a usage report in one BEGIN IMMEDIATE transaction. Returns null on
   * success, else "snapshot_missing", "stale_write", "usage_budget" (when
   * `admit` refuses) or "storage_full", checked in that order. The report
   * never outlives its snapshot.
   */
  putUsage(readId, {
    payload,
    seq,
    bytes,
    now,
    ttlMs = TTL_MS,
    maxStorageBytes = DEFAULT_USAGE_STORAGE_MAX_BYTES,
    admit = () => true,
  }) {
    return this.transaction(() => {
      const snapshot = this.liveSnapshotExpiryStatement.get(readId, now);
      if (snapshot === undefined) return "snapshot_missing";
      const row = this.usageRowStatement.get(readId);
      if (row !== undefined && seq <= row.seq) return "stale_write";
      if (!admit()) return "usage_budget";
      const stored = Number(this.usageStoredBytesStatement.get().total);
      if (stored - (row?.bytes ?? 0) + bytes > maxStorageBytes) return "storage_full";
      const expiresAt = Math.min(now + ttlMs, snapshot.expires_at);
      this.upsertUsageStatement.run(readId, seq, expiresAt, bytes, payload);
      return null;
    });
  }

  /** The stored report text when it and its snapshot are both live, else null. */
  getUsage(readId, now) {
    return this.getUsageStatement.get(readId, now, now)?.payload ?? null;
  }

  /**
   * Turns the usage report into a tombstone (contract section 2). Returns
   * "deleted", "ignored" (a lower sequence than the stored one, or nothing to
   * protect) or "storage_full" (a new tombstone would exceed the cap; turning
   * a stored report into a tombstone never does).
   */
  deleteUsage(readId, { usageSeq = null, now, maxStorageBytes = DEFAULT_USAGE_STORAGE_MAX_BYTES }) {
    return this.transaction(() => {
      const row = this.usageRowStatement.get(readId);
      const expiresAt = now + this.usageTombstoneTtlMs;
      if (row === undefined) {
        if (this.liveSnapshotExpiryStatement.get(readId, now) === undefined) return "ignored";
        const stored = Number(this.usageStoredBytesStatement.get().total);
        if (stored + USAGE_TOMBSTONE_BYTES > maxStorageBytes) return "storage_full";
        this.upsertUsageStatement.run(readId, usageSeq ?? 0, expiresAt, USAGE_TOMBSTONE_BYTES, null);
        return "deleted";
      }
      if (usageSeq !== null && usageSeq < row.seq) return "ignored";
      const seq = usageSeq === null ? row.seq : Math.max(row.seq, usageSeq);
      this.upsertUsageStatement.run(readId, seq, expiresAt, USAGE_TOMBSTONE_BYTES, null);
      return "deleted";
    });
  }

  /** The stored usage row without its payload, for tests and diagnostics. */
  usageRow(readId) {
    const row = this.usageRowStatement.get(readId);
    return row === undefined ? null : { seq: row.seq, bytes: row.bytes, tombstone: row.tombstone === 1 };
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
    try {
      return this.pingStatement.get()?.healthy === 1;
    } catch (error) {
      console.error("Remote-view database read probe failed", error);
      return false;
    }
  }

  // A full disk or a read-only mount leaves reads working while every write
  // fails, so liveness needs a real write to stay meaningful.
  canWrite(now = Date.now()) {
    try {
      this.writeProbeStatement.run(now);
      return true;
    } catch (error) {
      console.error("Remote-view database write probe failed", error);
      return false;
    }
  }

  readPragma(name) {
    const row = this.database.prepare(`PRAGMA ${name}`).get();
    const value = row === undefined ? undefined : Object.values(row)[0];
    return Number(value ?? 0);
  }

  spaceStats() {
    const pageCount = this.readPragma("page_count");
    const pageSize = this.readPragma("page_size");
    return {
      pageCount,
      freelistCount: this.readPragma("freelist_count"),
      databaseBytes: pageCount * pageSize,
    };
  }

  checkpoint() {
    this.database.exec("PRAGMA wal_checkpoint(TRUNCATE)");
  }

  // Returns the write-ahead log to its floor and, only when the file is mostly
  // free pages, returns that space to the filesystem.
  compact() {
    const result = { checkpointed: false, vacuumed: false };
    try {
      this.checkpoint();
      result.checkpointed = true;
    } catch (error) {
      console.error("Remote-view WAL checkpoint failed", error);
    }

    let stats = null;
    try {
      stats = this.spaceStats();
    } catch (error) {
      console.error("Remote-view database size probe failed", error);
      return result;
    }
    if (!shouldVacuum(stats)) return result;

    try {
      this.database.exec("VACUUM");
      result.vacuumed = true;
      this.checkpoint();
    } catch (error) {
      console.error("Remote-view VACUUM failed", error);
    }
    return result;
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
    usage: { shared: true, seq: demoUsageSeq(now), checkedAt: new Date(now).toISOString() },
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
      {
        id: "antigravity",
        provider: "antigravity",
        name: "Antigravity",
        plan: "Google AI Plus",
        windows: [
          { label: "Weekly", usedPercent: 35, resetsAt: at(4 * DAY) },
          { label: "Weekly", usedPercent: 10, resetsAt: at(6 * DAY), scope: "Claude & GPT" },
        ],
      },
    ],
  };
}

// --- Demo usage report -------------------------------------------------------
// A generated, deterministic report for /u/demo/usage: three accounts, five
// models (one unpriced), 90 days of daily rows, the last 7 days hourly and both
// rolling windows, in Europe/London so the demo crosses DST changes. The values
// for a given day depend only on that day, so the demo does not jump around
// between requests. The report is "as of" the start of the current hour.
const DEMO_USAGE_TIME_ZONE = "Europe/London";
const DEMO_USAGE_DAYS = 90;
const DEMO_USAGE_HOURLY_DAYS = 7;
const HOUR_MS = 3_600_000;

const DEMO_USAGE_ACCOUNTS = [
  { name: "Claude Personal", provider: "claude" },
  { name: "Claude Work", provider: "claude" },
  { name: "Codex (all accounts)", provider: "codex" },
];

const DEMO_USAGE_MODELS = [
  {
    id: "claude-haiku-4-5", provider: "claude", priced: true,
    price: { input: 1, cacheRead: 0.1, cacheWrite5m: 1.25, cacheWrite1h: 2, output: 5 },
  },
  {
    id: "claude-opus-4-1", provider: "claude", priced: true,
    price: { input: 15, cacheRead: 1.5, cacheWrite5m: 18.75, cacheWrite1h: 30, output: 75 },
  },
  {
    id: "claude-sonnet-4-5", provider: "claude", priced: true,
    price: { input: 3, cacheRead: 0.3, cacheWrite5m: 3.75, cacheWrite1h: 6, output: 15 },
  },
  {
    id: "gpt-5-codex", provider: "codex", priced: true,
    price: { input: 1.25, cacheRead: 0.125, cacheWrite5m: null, cacheWrite1h: null, output: 10 },
  },
  { id: "gpt-5-codex-preview", provider: "codex", priced: false, price: null },
];

// Per (account, model) in sorted order: typical requests per active day and the
// mean tokens of one request.
const DEMO_USAGE_SERIES = [
  { account: 0, model: 0, requests: 40, uncached: 900, cacheRead: 9_000, cacheWrite5m: 1_500, cacheWrite1h: 0, output: 400 },
  { account: 0, model: 1, requests: 70, uncached: 600, cacheRead: 52_000, cacheWrite5m: 2_600, cacheWrite1h: 400, output: 1_100 },
  { account: 0, model: 2, requests: 160, uncached: 700, cacheRead: 38_000, cacheWrite5m: 2_200, cacheWrite1h: 300, output: 900 },
  { account: 1, model: 1, requests: 30, uncached: 500, cacheRead: 61_000, cacheWrite5m: 3_100, cacheWrite1h: 0, output: 1_300 },
  { account: 1, model: 2, requests: 220, uncached: 800, cacheRead: 41_000, cacheWrite5m: 2_400, cacheWrite1h: 200, output: 950 },
  { account: 2, model: 3, requests: 120, uncached: 4_000, cacheRead: 30_000, cacheWrite5m: 0, cacheWrite1h: 0, output: 1_400, reasoning: 0.45 },
  { account: 2, model: 4, requests: 12, uncached: 3_000, cacheRead: 12_000, cacheWrite5m: 0, cacheWrite1h: 0, output: 900, reasoning: 0.3 },
];

// Relative activity per local hour of the day.
const DEMO_HOUR_CURVE = [
  0, 0, 0, 0, 0, 0, 0, 0.2, 0.6, 1, 1, 0.9, 0.5, 0.8, 1, 1, 0.9, 0.7, 0.4, 0.3, 0.5, 0.6, 0.3, 0.1,
];
const DEMO_HOUR_CURVE_TOTAL = DEMO_HOUR_CURVE.reduce((sum, weight) => sum + weight, 0);

let demoZoneFormatter = null;

function demoZoneParts(ms) {
  demoZoneFormatter ??= new Intl.DateTimeFormat("en-US", {
    timeZone: DEMO_USAGE_TIME_ZONE,
    hourCycle: "h23",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
  const parts = {};
  for (const part of demoZoneFormatter.formatToParts(ms)) parts[part.type] = Number(part.value);
  return parts;
}

function demoLocalEpochDay(ms) {
  const parts = demoZoneParts(ms);
  return Date.UTC(parts.year, parts.month - 1, parts.day) / DAY_MS;
}

// UTC instant of local midnight. London midnight always exists (its clock
// changes at 01:00 and 02:00), so two offset lookups settle it.
function demoStartOfLocalDay(epochDay) {
  const offsetAt = (ms) => {
    const parts = demoZoneParts(ms);
    const local = Date.UTC(parts.year, parts.month - 1, parts.day, parts.hour, parts.minute, parts.second);
    return local - Math.floor(ms / 1000) * 1000;
  };
  const guess = epochDay * DAY_MS;
  const first = guess - offsetAt(guess);
  return guess - offsetAt(first);
}

function demoNoise(...values) {
  let hash = 2_166_136_261;
  for (const value of values) {
    hash ^= value | 0;
    hash = Math.imul(hash, 16_777_619);
    hash ^= hash >>> 13;
  }
  return (hash >>> 0) / 4_294_967_296;
}

function demoUsageSeq(now) {
  return Math.floor(now / HOUR_MS) * HOUR_MS;
}

function demoDateString(epochDay) {
  return new Date(epochDay * DAY_MS).toISOString().slice(0, 10);
}

function demoTokens(series, requests, noise) {
  const scale = requests * (0.8 + 0.4 * noise);
  const output = Math.round(series.output * scale);
  return [
    Math.round(series.uncached * scale),
    Math.round(series.cacheRead * scale),
    Math.round(series.cacheWrite5m * scale),
    Math.round(series.cacheWrite1h * scale),
    output,
    Math.round(output * (series.reasoning ?? 0)),
    requests,
  ];
}

// Tokens and requests -> the ten metrics, costed like the desktop: per-MTok
// prices times tokens is micro-dollars, and a missing rate counts as zero.
function demoMetrics(model, totals) {
  const [uncached, cacheRead, cacheWrite5m, cacheWrite1h, output, reasoning, requests] = totals;
  const definition = DEMO_USAGE_MODELS[model];
  let cost = 0;
  let savings = 0;
  let unpriced = 0;
  if (definition.priced) {
    const price = definition.price;
    cost = Math.round(
      uncached * (price.input ?? 0) + cacheRead * (price.cacheRead ?? 0)
      + cacheWrite5m * (price.cacheWrite5m ?? 0) + cacheWrite1h * (price.cacheWrite1h ?? 0)
      + output * (price.output ?? 0),
    );
    savings = Math.round(cacheRead * ((price.input ?? 0) - (price.cacheRead ?? 0)));
  } else {
    unpriced = uncached + cacheRead + cacheWrite5m + cacheWrite1h + output;
  }
  return [uncached, cacheRead, cacheWrite5m, cacheWrite1h, output, reasoning, cost, savings, requests, unpriced];
}

function demoDayRequests(series, seriesIndex, epochDay) {
  const weekday = (epochDay + 4) % 7; // 0 = Sunday
  const weekend = weekday === 0 || weekday === 6;
  if (demoNoise(epochDay, seriesIndex, 1) < (weekend ? 0.3 : 0.1)) return 0;
  const factor = (weekend ? 0.35 : 1) * (0.5 + demoNoise(epochDay, seriesIndex, 2));
  return Math.max(1, Math.round(series.requests * factor));
}

export function demoUsagePayload(now) {
  const asOf = demoUsageSeq(now);
  const asOfMinutes = asOf / 60_000;
  const today = demoLocalEpochDay(asOf);
  const dailyFrom = today - DEMO_USAGE_DAYS + 1;
  const hourlyFrom = today - DEMO_USAGE_HOURLY_DAYS + 1;

  const hourDayStarts = [];
  for (let day = hourlyFrom; day <= today + 1; day += 1) {
    hourDayStarts.push(demoStartOfLocalDay(day) / 60_000);
  }

  const days = [];
  const hourRows = [];
  const tokensByBucket = new Map();
  for (let epochDay = dailyFrom; epochDay <= today; epochDay += 1) {
    const dayNumber = epochDay - dailyFrom;
    if (epochDay < hourlyFrom) {
      DEMO_USAGE_SERIES.forEach((series, seriesIndex) => {
        const requests = demoDayRequests(series, seriesIndex, epochDay);
        if (requests === 0) return;
        const totals = demoTokens(series, requests, demoNoise(epochDay, seriesIndex, 3));
        days.push([dayNumber, series.account, series.model, ...demoMetrics(series.model, totals)]);
      });
      continue;
    }

    // Hourly days: hour buckets first, and the day row is their sum.
    const dayStart = hourDayStarts[epochDay - hourlyFrom];
    const dayEnd = hourDayStarts[epochDay - hourlyFrom + 1];
    const dayTotals = DEMO_USAGE_SERIES.map(() => [0, 0, 0, 0, 0, 0, 0]);
    for (let bucket = dayStart; bucket < dayEnd && bucket < asOfMinutes; bucket += 60) {
      const hourOfDay = Math.min(23, Math.floor((bucket - dayStart) / 60));
      DEMO_USAGE_SERIES.forEach((series, seriesIndex) => {
        const dayRequests = demoDayRequests(series, seriesIndex, epochDay);
        const expected = dayRequests * DEMO_HOUR_CURVE[hourOfDay] / DEMO_HOUR_CURVE_TOTAL;
        const requests = Math.round(expected * (0.4 + 1.2 * demoNoise(bucket, seriesIndex, 4)));
        if (requests === 0) return;
        const totals = demoTokens(series, requests, demoNoise(bucket, seriesIndex, 5));
        totals.forEach((value, index) => { dayTotals[seriesIndex][index] += value; });
        tokensByBucket.set(`${bucket}:${seriesIndex}`, totals);
        hourRows.push([dayNumber, bucket, series.account, series.model, ...demoMetrics(series.model, totals)]);
      });
    }
    DEMO_USAGE_SERIES.forEach((series, seriesIndex) => {
      if (dayTotals[seriesIndex][6] === 0) return;
      days.push([dayNumber, series.account, series.model, ...demoMetrics(series.model, dayTotals[seriesIndex])]);
    });
  }

  // London is a whole-hour offset, so the hour grid has phase 0 and the rolling
  // windows (which end at the start of an hour) line up with its buckets.
  const rollingWindow = (sinceMinutes) => {
    const rows = [];
    for (let hour = 0; hour < 24; hour += 1) {
      const bucket = sinceMinutes + hour * 60;
      DEMO_USAGE_SERIES.forEach((series, seriesIndex) => {
        const totals = tokensByBucket.get(`${bucket}:${seriesIndex}`);
        if (totals !== undefined) rows.push([hour, series.account, series.model, ...demoMetrics(series.model, totals)]);
      });
    }
    return {
      since: new Date(sinceMinutes * 60_000).toISOString(),
      until: new Date((sinceMinutes + 24 * 60) * 60_000).toISOString(),
      rows,
    };
  };

  return {
    version: 1,
    seq: asOf,
    generatedAt: new Date(asOf).toISOString(),
    timeZone: DEMO_USAGE_TIME_ZONE,
    coverage: {
      dailyFrom: demoDateString(dailyFrom),
      dailyTo: demoDateString(today),
      hourlyFrom: demoDateString(hourlyFrom),
      hourlyTo: demoDateString(today),
    },
    accounts: DEMO_USAGE_ACCOUNTS,
    models: DEMO_USAGE_MODELS,
    days,
    hourDayStarts,
    hours: [{ phaseMinutes: 0, rows: hourRows }],
    rolling: {
      current: rollingWindow(asOfMinutes - 24 * 60),
      previous: rollingWindow(asOfMinutes - 48 * 60),
    },
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

async function readRequestBody(request, maxBytes = MAX_BODY) {
  const chunks = [];
  let length = 0;
  let tooLarge = false;
  for await (const chunk of request) {
    length += chunk.length;
    if (length > maxBytes) {
      tooLarge = true;
      chunks.length = 0;
    } else if (!tooLarge) {
      chunks.push(chunk);
    }
  }
  return tooLarge ? null : Buffer.concat(chunks).toString("utf8");
}

/**
 * X-Usage-Seq: null when absent, the value when it is a decimal safe integer,
 * undefined when it is present but malformed.
 */
function parseUsageSeqHeader(raw) {
  if (raw === undefined) return null;
  const value = (Array.isArray(raw) ? raw.join(",") : String(raw)).trim();
  if (!/^[0-9]{1,16}$/.test(value)) return undefined;
  const seq = Number(value);
  return Number.isSafeInteger(seq) ? seq : undefined;
}

function sendAsset(response, entry, csp) {
  const headers = {
    "Content-Type": entry.type,
    "Cache-Control": entry.cache ?? STATIC_CACHE,
  };
  if (entry.html) {
    // Keep proxies from injecting third-party scripts into the self-contained viewer.
    headers["Cache-Control"] += ", no-transform";
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
  allowedEndpointHosts = null,
  pushSender = sendWebPush,
  signingKey = resolveSigningKey(),
  unsignedWritesPerMinute = DEFAULT_UNSIGNED_WRITES_PER_MINUTE,
  signedWritesPerMinute = DEFAULT_SIGNED_WRITES_PER_MINUTE,
  maxClockSkewSeconds = MAX_CLOCK_SKEW_SECONDS,
  rateLimitWindowMs = RATE_LIMIT_WINDOW_MS,
  trustProxy = false,
  maxUsageBodyBytes = MAX_USAGE_BODY,
  usageWriteBudgetBytes = DEFAULT_USAGE_WRITE_BUDGET_BYTES,
  usageBudgetWindowMs = USAGE_BUDGET_WINDOW_MS,
  usageStorageMaxBytes = DEFAULT_USAGE_STORAGE_MAX_BYTES,
  usageTombstoneTtlMs = TTL_MS,
  // Test seam: awaited after a usage PUT was admitted and validated, right
  // before its store transaction, so tests can interleave other writes.
  beforeUsageStore = null,
} = {}) {
  if (!databasePath) throw new Error("databasePath is required");
  if (!webRoot || !existsSync(join(webRoot, "index.html"))) {
    throw new Error(`Web root is missing index.html: ${webRoot ?? "<unset>"}`);
  }

  const store = new SnapshotStore(databasePath, { usageTombstoneTtlMs });
  const { assets, csp } = loadAssets(webRoot);
  store.deleteExpired(now());

  if (vapidConfiguration !== null && !validateVapidConfiguration(vapidConfiguration)) {
    throw new Error("Invalid VAPID configuration");
  }

  // Empty or unset keeps the built-in browser push-service list.
  const pushOptions = { allowedEndpointHosts: allowedEndpointHosts ?? undefined };

  const writeLimiter = new WriteRateLimiter({ windowMs: rateLimitWindowMs });
  const usageBudget = new UsageByteBudget({ windowMs: usageBudgetWindowMs });

  let demoUsageCache = { seq: null, body: null };
  function demoUsageBody(at) {
    const seq = demoUsageSeq(at);
    if (demoUsageCache.seq !== seq) {
      demoUsageCache = { seq, body: JSON.stringify(demoUsagePayload(at)) };
    }
    return demoUsageCache.body;
  }

  function sendStoredJson(response, payload) {
    writeHead(response, 200, {
      ...CORS,
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
    });
    response.end(payload);
  }

  /**
   * /u/{id}/usage. GET takes the read id; PUT and DELETE take the write id and
   * go through the same signing and per-address write limit as snapshots.
   */
  async function handleUsage(request, response, id, path) {
    const method = request.method;
    if (method !== "GET" && method !== "PUT" && method !== "DELETE") {
      return sendJson(response, 405, { error: "method_not_allowed" });
    }
    if (id === DEMO_ID) {
      return method === "GET"
        ? sendStoredJson(response, demoUsageBody(now()))
        : sendJson(response, 405, { error: "method_not_allowed" });
    }
    if (!ID_RE.test(id)) return sendJson(response, 400, { error: "invalid_id" });

    if (method === "GET") {
      const payload = store.getUsage(id, now());
      if (payload === null) return sendJson(response, 404, { error: "not_found" });
      return sendStoredJson(response, payload);
    }

    const readId = deriveReadId(id);
    if (method === "DELETE") {
      const usageSeq = parseUsageSeqHeader(request.headers[USAGE_SEQ_HEADER.toLowerCase()]);
      if (usageSeq === undefined) return sendJson(response, 400, { error: "invalid_usage_seq" });
      if (!await admitWrite(request, response, { method, path, body: "" })) return;
      const outcome = store.deleteUsage(readId, {
        usageSeq,
        now: now(),
        maxStorageBytes: usageStorageMaxBytes,
      });
      if (outcome === "storage_full") return sendJson(response, 507, { error: "storage_full" });
      return sendEmpty(response, 204, { "X-Read-Id": readId });
    }

    const parsed = await readJsonRequest(request, response, maxUsageBodyBytes);
    if (parsed === null) return;
    const { body, data } = parsed;
    if (!await admitWrite(request, response, { method, path, body })) return;

    const reason = validateUsagePayload(data);
    if (reason !== null) return sendJson(response, 422, { error: "invalid_payload", reason });

    if (beforeUsageStore !== null) await beforeUsageStore({ readId, seq: data.seq });

    // Nothing below awaits, so the budget check, the store transaction and the
    // charge cannot interleave with another request.
    const bytes = Buffer.byteLength(body, "utf8");
    const address = resolveClientAddress(request, { trustProxy });
    const at = now();
    let verdict = null;
    const refusal = store.putUsage(readId, {
      payload: body,
      seq: data.seq,
      bytes,
      now: at,
      ttlMs,
      maxStorageBytes: usageStorageMaxBytes,
      admit: () => {
        verdict = usageBudget.check(address, bytes, usageWriteBudgetBytes, at);
        return verdict.allowed;
      },
    });
    if (refusal === "usage_budget") {
      writeHead(response, 429, {
        ...CORS,
        "Content-Type": "application/json; charset=utf-8",
        "Cache-Control": "no-store",
        "Retry-After": String(verdict.retryAfterSeconds),
      });
      return response.end(JSON.stringify({
        error: "usage_budget",
        retryAfterSeconds: verdict.retryAfterSeconds,
      }));
    }
    if (refusal === "storage_full") return sendJson(response, 507, { error: "storage_full" });
    if (refusal !== null) return sendJson(response, 409, { error: refusal });
    usageBudget.charge(address, bytes, at);
    return sendEmpty(response, 204, { "X-Read-Id": readId });
  }

  /**
   * Decides whether a write to /u/{writeId} may proceed, and answers 429 itself
   * when it may not. Unsigned requests keep working exactly as before as long
   * as they fit the strict budget, which a desktop app uploading once a minute
   * never approaches.
   */
  async function admitWrite(request, response, { method, path, body }) {
    const verification = await verifyRequestSignature({
      key: signingKey,
      timestamp: request.headers[TIMESTAMP_HEADER.toLowerCase()],
      signature: request.headers[SIGNATURE_HEADER.toLowerCase()],
      method,
      path,
      body,
      nowSeconds: now() / 1000,
      maxSkewSeconds: maxClockSkewSeconds,
    });

    const limit = verification.signed ? signedWritesPerMinute : unsignedWritesPerMinute;
    const address = resolveClientAddress(request, { trustProxy });
    const verdict = writeLimiter.check(address, limit, now());
    if (verdict.allowed) return true;

    writeHead(response, 429, {
      ...CORS,
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
      "Retry-After": String(verdict.retryAfterSeconds),
    });
    response.end(JSON.stringify({
      error: "rate_limited",
      retryAfterSeconds: verdict.retryAfterSeconds,
    }));
    return false;
  }

  let writeProbe = { at: null, writable: true };
  function probeWritable(at) {
    if (writeProbe.at !== null && at - writeProbe.at < HEALTH_WRITE_PROBE_INTERVAL_MS) {
      return writeProbe.writable;
    }
    writeProbe = { at, writable: store.canWrite(at) };
    return writeProbe.writable;
  }

  async function readJsonRequest(request, response, maxBytes = MAX_BODY) {
    const contentType = request.headers["content-type"] ?? "";
    if (!contentType.toLowerCase().includes("application/json")) {
      sendJson(response, 415, { error: "unsupported_media_type" });
      return null;
    }

    const declaredLength = Number(request.headers["content-length"]);
    if (Number.isFinite(declaredLength) && declaredLength > maxBytes) {
      request.resume();
      sendJson(response, 413, { error: "too_large" });
      return null;
    }

    const body = await readRequestBody(request, maxBytes);
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
      // Drops rows a narrowed host list, or an older build, would no longer accept.
      if (!validatePushSubscription(subscription, pushOptions)) {
        if (typeof subscription?.endpoint === "string") {
          store.deleteSubscription(readId, subscription.endpoint);
        }
        return;
      }
      try {
        const result = await pushSender(
          subscription,
          message,
          vapidConfiguration,
          undefined,
          undefined,
          pushOptions,
        );
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
        // Reads keep working when the volume is full or read-only; without this
        // probe the relay reports "ok" while every upload fails.
        if (!probeWritable(now())) return sendJson(response, 503, { status: "degraded" });
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
          if (!validatePushSubscription(parsed.data, pushOptions)) {
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

      const usageMatch = /^\/u\/([^/]+)\/usage\/?$/.exec(path);
      if (usageMatch) return await handleUsage(request, response, usageMatch[1], path);

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
        // A DELETE carries no body, so its canonical string is fixed.
        if (!await admitWrite(request, response, { method, path, body: "" })) return;
        // Deleting the snapshot must never fail on the optional usage header,
        // so a malformed value counts as absent here.
        const usageSeq = parseUsageSeqHeader(request.headers[USAGE_SEQ_HEADER.toLowerCase()]) ?? null;
        store.delete(readId, { usageSeq, now: now() });
        return sendEmpty(response, 204, { "X-Read-Id": readId });
      }

      const parsed = await readJsonRequest(request, response);
      if (parsed === null) return;
      const { body, data } = parsed;

      // The signature covers the raw body, so the tier is only known once the
      // body has been read. Reading is already capped at MAX_BODY, and what the
      // limit protects is the stored row, not the parse.
      if (!await admitWrite(request, response, { method, path, body })) return;

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
      // Runs even when the sweep failed: the log can still need truncating.
      store.compact();
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

  return { server, store, writeLimiter, usageBudget, close };
}

// Replaces the built-in push-service host list when set, for operators who run
// another push service or want a narrower list.
export function readAllowedEndpointHosts(environment = process.env) {
  return parsePushEndpointHosts(environment.PUSH_ENDPOINT_ALLOWED_HOSTS);
}

function readPositiveInteger(name, fallback, environment = process.env) {
  const raw = environment[name];
  if (raw === undefined) return fallback;
  const value = Number(raw);
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new Error(`${name} must be a positive integer`);
  }
  return value;
}

/** Usage-report limits from the environment, with the documented defaults. */
export function readUsageLimits(environment = process.env) {
  return {
    maxUsageBodyBytes: readPositiveInteger("MAX_USAGE_BODY_BYTES", MAX_USAGE_BODY, environment),
    usageWriteBudgetBytes: readPositiveInteger(
      "USAGE_WRITE_BUDGET_BYTES",
      DEFAULT_USAGE_WRITE_BUDGET_BYTES,
      environment,
    ),
    usageStorageMaxBytes: readPositiveInteger(
      "USAGE_STORAGE_MAX_BYTES",
      DEFAULT_USAGE_STORAGE_MAX_BYTES,
      environment,
    ),
  };
}

// Only behind a proxy you control. See resolveClientAddress.
export function readTrustProxy(environment = process.env) {
  return environment.TRUST_PROXY?.trim() === "1";
}

// --- Cloudflare cache purge on startup ---
// A deploy replaces the viewer shell, but a caching proxy in front of the
// relay keeps serving the old files until its edge TTL expires. When enabled,
// the container asks Cloudflare to drop those cached copies once at startup,
// so a deploy is visible immediately.
const CLOUDFLARE_API_BASE = "https://api.cloudflare.com/client/v4";
const CLOUDFLARE_ZONE_ID_RE = /^[a-f0-9]{32}$/i;

// Broken configuration stops startup instead of silently skipping the purge,
// like the VAPID settings: an operator who asked for it must not believe it
// runs while it quietly does nothing.
export function readCloudflarePurgeConfig(environment = process.env) {
  const mode = (environment.CLOUDFLARE_PURGE_ON_START ?? "").trim().toLowerCase();
  if (mode === "" || mode === "0" || mode === "off") return null;
  if (mode !== "files" && mode !== "everything") {
    throw new Error('CLOUDFLARE_PURGE_ON_START must be "files", "everything", "off" or unset');
  }

  const zoneId = (environment.CLOUDFLARE_ZONE_ID ?? "").trim();
  if (!CLOUDFLARE_ZONE_ID_RE.test(zoneId)) {
    throw new Error("CLOUDFLARE_PURGE_ON_START is set, but CLOUDFLARE_ZONE_ID is missing or is not a 32-character hex zone id");
  }

  const apiToken = (environment.CLOUDFLARE_API_TOKEN ?? "").trim();
  if (apiToken === "") {
    throw new Error("CLOUDFLARE_PURGE_ON_START is set, but CLOUDFLARE_API_TOKEN is missing");
  }

  let baseUrl = null;
  if (mode === "files") {
    const raw = (environment.PUBLIC_BASE_URL ?? "").trim();
    let parsed;
    try {
      parsed = new URL(raw);
    } catch {
      throw new Error('CLOUDFLARE_PURGE_ON_START="files" requires PUBLIC_BASE_URL, the address readers use (e.g. https://ai.yaaps.net)');
    }
    if (parsed.protocol !== "https:" && parsed.protocol !== "http:") {
      throw new Error("PUBLIC_BASE_URL must be an http(s) URL");
    }
    if ((parsed.pathname !== "/" && parsed.pathname !== "") || parsed.search !== "" || parsed.hash !== "") {
      // The viewer is served from the root, so a path here would purge
      // addresses that are never actually served.
      throw new Error("PUBLIC_BASE_URL must be an origin without a path, query or fragment");
    }
    baseUrl = parsed.origin;
  }

  return { mode, zoneId, apiToken, baseUrl };
}

// The exact addresses the relay serves the shell from; nothing else on the
// zone is touched. Well under Cloudflare's 30-URLs-per-request limit.
export function cloudflarePurgeUrls(baseUrl) {
  const paths = ["/", "/config.js", ...ASSET_DEFINITIONS.map((definition) => definition.path)];
  return paths.map((path) => baseUrl + path);
}

export async function purgeCloudflareCache(config, {
  fetchImpl = fetch,
  log = console,
  attempts = 3,
  delayMs = 2_000,
  sleep = (ms) => new Promise((resolveSleep) => setTimeout(resolveSleep, ms)),
} = {}) {
  const body = config.mode === "everything"
    ? { purge_everything: true }
    : { files: cloudflarePurgeUrls(config.baseUrl) };
  const url = `${CLOUDFLARE_API_BASE}/zones/${config.zoneId}/purge_cache`;

  for (let attempt = 1; attempt <= attempts; attempt++) {
    let status = null;
    let detail = "network error";
    try {
      const response = await fetchImpl(url, {
        method: "POST",
        headers: {
          "Authorization": `Bearer ${config.apiToken}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify(body),
      });
      status = response.status;
      const result = await response.json().catch(() => null);
      if (response.ok && result?.success === true) {
        log.log(`Cloudflare cache purge (${config.mode}) succeeded.`);
        return true;
      }
      // Cloudflare's own error rows name the problem (bad token, wrong zone,
      // missing permission) better than the bare status code does.
      detail = Array.isArray(result?.errors) && result.errors.length > 0
        ? result.errors.map((error) => `${error.code}: ${error.message}`).join("; ")
        : `HTTP ${status}`;
    } catch (error) {
      detail = error?.message ?? "network error";
    }

    // A rejected token or zone id will not heal by retrying.
    if (status !== null && status >= 400 && status < 500 && status !== 429) {
      log.error(`Cloudflare cache purge failed (${detail}); check the zone id and the token's cache-purge permission.`);
      return false;
    }

    if (attempt < attempts) {
      log.error(`Cloudflare cache purge attempt ${attempt} failed (${detail}); retrying.`);
      await sleep(delayMs * attempt);
    } else {
      log.error(`Cloudflare cache purge failed (${detail}); the viewer may serve stale files until the edge cache expires.`);
    }
  }
  return false;
}

async function startFromEnvironment() {
  const here = dirname(fileURLToPath(import.meta.url));
  const port = readPositiveInteger("PORT", 8080);
  const databasePath = resolve(process.env.DATABASE_PATH ?? "/data/usage.db");
  const webRoot = resolve(process.env.WEB_ROOT ?? join(here, "..", "..", "web"));
  const ttlMs = readPositiveInteger("SNAPSHOT_TTL_SECONDS", TTL_MS / 1000) * 1000;
  const cleanupIntervalMs = readPositiveInteger("CLEANUP_INTERVAL_SECONDS", 3600) * 1000;
  const runtimeVersion = readFileSync(join(here, "VERSION"), "utf8").trim();
  const allowedEndpointHosts = readAllowedEndpointHosts();
  const unsignedWritesPerMinute = readPositiveInteger(
    "UNSIGNED_PUT_PER_MINUTE",
    DEFAULT_UNSIGNED_WRITES_PER_MINUTE,
  );
  const signedWritesPerMinute = readPositiveInteger(
    "SIGNED_PUT_PER_MINUTE",
    DEFAULT_SIGNED_WRITES_PER_MINUTE,
  );
  const vapidConfiguration = await ensureVapidConfiguration({
    path: resolveVapidKeyPath(databasePath),
  });
  // Validated before the server starts so a broken purge configuration is a
  // loud startup failure, not a silently skipped purge.
  const cloudflarePurge = readCloudflarePurgeConfig();
  const app = createRemoteViewServer({
    databasePath,
    webRoot,
    ttlMs,
    cleanupIntervalMs,
    runtimeVersion,
    vapidConfiguration,
    allowedEndpointHosts,
    signingKey: resolveSigningKey(process.env),
    unsignedWritesPerMinute,
    signedWritesPerMinute,
    trustProxy: readTrustProxy(),
    ...readUsageLimits(),
  });

  await new Promise((resolveListen, rejectListen) => {
    app.server.once("error", rejectListen);
    app.server.listen(port, "0.0.0.0", resolveListen);
  });
  console.log(`Remote view listening on port ${port}`);

  if (cloudflarePurge !== null) {
    // Fire-and-forget: an unreachable Cloudflare API must not take the relay
    // down with it. Failures are logged inside, loudly.
    purgeCloudflareCache(cloudflarePurge).catch((error) => {
      console.error("Cloudflare cache purge crashed", error);
    });
  }

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
