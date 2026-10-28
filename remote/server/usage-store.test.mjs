// Usage reports on the container relay: payload validation, the /u/{id}/usage
// routes, write ordering through sequence numbers and tombstones, expiry, and
// the byte limits. Contract: the "Usage payload" and "Relay API" sections of
// the web usage specification.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import {
  createRemoteViewServer,
  DEFAULT_USAGE_STORAGE_MAX_BYTES,
  DEFAULT_USAGE_WRITE_BUDGET_BYTES,
  demoUsagePayload,
  MAX_USAGE_BODY,
  readUsageLimits,
  USAGE_TOMBSTONE_BYTES,
  validateUsagePayload,
} from "./server.mjs";
import { signRequest } from "../shared/request-signing.mjs";

const WEB_ROOT = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "web");
const WRITE_ID = "0123456789abcdef0123456789abcdef";
const READ_ID = "3eb1bd439947eb762998e566ccc2e099";
const OTHER_WRITE_ID = "fedcba9876543210fedcba9876543210";
const START = Date.parse("2026-10-28T10:00:00Z");
const DAY = 24 * 60 * 60 * 1000;

const SNAPSHOT = JSON.stringify({
  version: 2,
  generatedAt: "2026-10-28T10:00:00Z",
  accounts: [{ id: "codex:test", provider: "codex", windows: [] }],
});

const METRICS = [1200, 50000, 3000, 0, 800, 120, 412345, 67000, 7, 0];
// Epoch minutes of local midnight on 2026-10-22 in Asia/Jerusalem (UTC+3), a
// multiple of 60, so grid phase 0 aligns with it.
const FIRST_HOUR_DAY_START = Date.UTC(2026, 9, 22) / 60_000 - 180;

function usageReport(seq = 1_761_650_000_123, overrides = {}) {
  const hourDayStarts = Array.from({ length: 8 }, (_, day) => FIRST_HOUR_DAY_START + day * 1440);
  return {
    version: 1,
    seq,
    generatedAt: "2026-10-28T10:00:00Z",
    timeZone: "Asia/Jerusalem",
    coverage: {
      dailyFrom: "2026-10-01",
      dailyTo: "2026-10-28",
      hourlyFrom: "2026-10-22",
      hourlyTo: "2026-10-28",
    },
    accounts: [
      { name: "Personal", provider: "claude" },
      { name: "Codex (all accounts)", provider: "codex" },
    ],
    models: [
      {
        id: "claude-opus-4-1",
        provider: "claude",
        priced: true,
        price: { input: 15, cacheRead: 1.5, cacheWrite5m: 18.75, cacheWrite1h: 30, output: 75 },
      },
      { id: "some-new-model", provider: "codex", priced: false, price: null },
    ],
    days: [
      [0, 0, 0, ...METRICS],
      [27, 1, 1, 10, 20, 0, 0, 30, 5, 0, 0, 2, 60],
    ],
    hourDayStarts,
    hours: [{ phaseMinutes: 0, rows: [[21, FIRST_HOUR_DAY_START + 60, 0, 0, ...METRICS]] }],
    rolling: {
      current: { since: "2026-10-27T10:00:00Z", until: "2026-10-28T10:00:00Z", rows: [[0, 0, 0, ...METRICS]] },
      previous: { since: "2026-10-26T10:00:00Z", until: "2026-10-27T10:00:00Z", rows: [] },
    },
    ...overrides,
  };
}

/** A copy of the valid report with `change` applied to it. */
function mutated(change) {
  const report = structuredClone(usageReport());
  change(report);
  return report;
}

async function startApp(t, options = {}) {
  const directory = await mkdtemp(join(tmpdir(), "ai-usage-usage-store-"));
  let currentTime = START;
  const app = createRemoteViewServer({
    databasePath: join(directory, "usage.db"),
    webRoot: WEB_ROOT,
    now: () => currentTime,
    cleanupIntervalMs: 0,
    ...options,
  });
  await new Promise((listening) => app.server.listen(0, "127.0.0.1", listening));
  t.after(async () => {
    await app.close();
    await rm(directory, { recursive: true, force: true });
  });
  const baseUrl = `http://127.0.0.1:${app.server.address().port}`;
  return {
    app,
    baseUrl,
    now: () => currentTime,
    advance(milliseconds) {
      currentTime += milliseconds;
    },
    putSnapshot(writeId = WRITE_ID) {
      return fetch(`${baseUrl}/u/${writeId}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: SNAPSHOT,
      });
    },
    putUsage(body, { writeId = WRITE_ID, headers = {} } = {}) {
      return fetch(`${baseUrl}/u/${writeId}/usage`, {
        method: "PUT",
        headers: { "Content-Type": "application/json", ...headers },
        body: typeof body === "string" ? body : JSON.stringify(body),
      });
    },
    deleteUsage(seq, { writeId = WRITE_ID } = {}) {
      return fetch(`${baseUrl}/u/${writeId}/usage`, {
        method: "DELETE",
        headers: seq === undefined ? {} : { "X-Usage-Seq": String(seq) },
      });
    },
    deleteSnapshot(seq, { writeId = WRITE_ID } = {}) {
      return fetch(`${baseUrl}/u/${writeId}`, {
        method: "DELETE",
        headers: seq === undefined ? {} : { "X-Usage-Seq": String(seq) },
      });
    },
    getUsage(readId = READ_ID) {
      return fetch(`${baseUrl}/u/${readId}/usage`);
    },
  };
}

// --- Validator -----------------------------------------------------------------

test("validator accepts the contract example and ignores unknown fields", () => {
  assert.equal(validateUsagePayload(usageReport()), null);
  assert.equal(validateUsagePayload({ ...usageReport(), extra: { nested: ["anything"] } }), null);
  // Savings is the one signed metric.
  assert.equal(validateUsagePayload(mutated((r) => { r.days[0][10] = -5000; })), null);
  // Seven fractional digits, as .NET writes them.
  assert.equal(validateUsagePayload(usageReport(1, { generatedAt: "2026-10-28T10:00:00.0000000Z" })), null);
  // No hourly section.
  assert.equal(validateUsagePayload(mutated((r) => {
    r.coverage.hourlyFrom = null;
    r.coverage.hourlyTo = null;
    r.hourDayStarts = [];
    r.hours = [];
  })), null);
  // A priced model may leave a rate it does not know as null.
  assert.equal(validateUsagePayload(mutated((r) => { r.models[0].price.cacheWrite1h = null; })), null);
});

test("validator rejects each rule class with its reason", () => {
  const cases = [
    ["not an object", [], "not_an_object"],
    ["version", usageReport(1, { version: 2 }), "bad_version"],
    ["seq zero", usageReport(0), "bad_seq"],
    ["seq fraction", usageReport(1.5), "bad_seq"],
    ["seq unsafe", usageReport(2 ** 53), "bad_seq"],
    ["generatedAt without Z", usageReport(1, { generatedAt: "2026-10-28T10:00:00" }), "bad_generated_at"],
    ["generatedAt garbage", usageReport(1, { generatedAt: "yesterdayZ" }), "bad_generated_at"],
    ["Windows zone id", usageReport(1, { timeZone: "Israel Standard Time" }), "bad_time_zone"],
    ["unknown zone", usageReport(1, { timeZone: "Mars/Olympus_Mons" }), "bad_time_zone"],
    ["offset zone", usageReport(1, { timeZone: "+02:00" }), "bad_time_zone"],
    ["invalid date", mutated((r) => { r.coverage.dailyFrom = "2026-02-30"; }), "bad_coverage"],
    ["reversed daily", mutated((r) => { r.coverage.dailyFrom = "2026-10-29"; }), "bad_coverage"],
    ["daily span", mutated((r) => { r.coverage.dailyFrom = "2023-10-23"; }), "coverage_too_long"],
    ["half hourly", mutated((r) => { r.coverage.hourlyTo = null; }), "bad_hourly_coverage"],
    ["hourly outside daily", mutated((r) => { r.coverage.hourlyTo = "2026-10-29"; }), "bad_hourly_coverage"],
    ["hourly span", mutated((r) => {
      r.coverage.hourlyFrom = "2026-10-20";
      r.hourDayStarts = Array.from({ length: 10 }, (_, day) => FIRST_HOUR_DAY_START + day * 1440);
    }), "hourly_coverage_too_long"],
    ["hourDayStarts length", mutated((r) => { r.hourDayStarts.pop(); }), "bad_hour_day_starts"],
    ["hourDayStarts order", mutated((r) => { r.hourDayStarts[3] = r.hourDayStarts[2]; }), "bad_hour_day_starts"],
    ["hourDayStarts without hourly", mutated((r) => {
      r.coverage.hourlyFrom = null;
      r.coverage.hourlyTo = null;
      r.hours = [];
    }), "bad_hour_day_starts"],
    ["account provider", mutated((r) => { r.accounts[0].provider = "zai"; }), "bad_account"],
    ["account name length", mutated((r) => { r.accounts[0].name = "n".repeat(257); }), "field_too_long"],
    ["too many accounts", mutated((r) => {
      r.accounts = Array.from({ length: 65 }, (_, index) => ({ name: `A${index}`, provider: "claude" }));
    }), "too_many_accounts"],
    ["too many models", mutated((r) => {
      r.models = Array.from({ length: 513 }, (_, index) => ({
        id: `m${index}`, provider: "codex", priced: false, price: null,
      }));
    }), "too_many_models"],
    ["priced without price", mutated((r) => { r.models[0].price = null; }), "bad_model_price"],
    ["unpriced with price", mutated((r) => { r.models[1].price = { input: 1 }; }), "bad_model_price"],
    ["negative price", mutated((r) => { r.models[0].price.output = -1; }), "bad_model_price"],
    ["day row width", mutated((r) => { r.days[0].pop(); }), "bad_day_row"],
    ["day out of range", mutated((r) => { r.days[0][0] = 28; }), "bad_day_number"],
    ["account index", mutated((r) => { r.days[0][1] = 2; }), "bad_index"],
    ["model index", mutated((r) => { r.days[0][2] = -1; }), "bad_index"],
    ["fractional index", mutated((r) => { r.rolling.current.rows[0][1] = 0.5; }), "bad_index"],
    ["unsafe metric", mutated((r) => { r.days[0][3] = 2 ** 53; }), "bad_metric"],
    ["fractional metric", mutated((r) => { r.days[0][4] = 1.5; }), "bad_metric"],
    ["metric over 1e15", mutated((r) => { r.days[0][9] = 1e15 + 1; }), "bad_metric"],
    ["negative cost", mutated((r) => { r.days[0][9] = -1; }), "bad_metric"],
    ["savings below -1e15", mutated((r) => { r.days[0][10] = -1e15 - 1; }), "bad_metric"],
    ["string metric", mutated((r) => { r.hours[0].rows[0][5] = "1"; }), "bad_metric"],
    ["duplicate day key", mutated((r) => { r.days.push([0, 0, 0, ...METRICS]); }), "duplicate_day_row"],
    ["too many day rows", mutated((r) => {
      r.days = Array.from({ length: 60_001 }, () => [0, 0, 0, ...METRICS]);
    }), "too_many_day_rows"],
    ["hour row width", mutated((r) => { r.hours[0].rows[0].push(0); }), "bad_hour_row"],
    ["hour day before hourly coverage", mutated((r) => { r.hours[0].rows[0][0] = 20; }), "bad_day_number"],
    ["bucket off phase", mutated((r) => { r.hours[0].rows[0][1] += 30; }), "bad_bucket_start"],
    ["bucket for another phase", mutated((r) => { r.hours[0].phaseMinutes = 30; }), "bad_bucket_start"],
    ["bucket before coverage", mutated((r) => { r.hours[0].rows[0][1] = FIRST_HOUR_DAY_START - 120; }), "bad_bucket_start"],
    ["bucket after coverage", mutated((r) => {
      r.hours[0].rows[0][1] = FIRST_HOUR_DAY_START + 7 * 1440 + 120;
    }), "bad_bucket_start"],
    ["phase out of range", mutated((r) => { r.hours[0].phaseMinutes = 60; }), "bad_hour_grid"],
    ["duplicate phase", mutated((r) => { r.hours.push({ phaseMinutes: 0, rows: [] }); }), "bad_hour_grid"],
    ["too many grids", mutated((r) => {
      r.hours = [0, 15, 30, 45, 50].map((phaseMinutes) => ({ phaseMinutes, rows: [] }));
    }), "too_many_hour_grids"],
    ["too many hour rows", mutated((r) => {
      r.hours = [
        { phaseMinutes: 0, rows: Array.from({ length: 20_001 }, () => []) },
        { phaseMinutes: 30, rows: Array.from({ length: 20_000 }, () => []) },
      ];
    }), "too_many_hour_rows"],
    ["duplicate hour key", mutated((r) => {
      r.hours[0].rows.push([21, FIRST_HOUR_DAY_START + 60, 0, 0, ...METRICS]);
    }), "duplicate_hour_row"],
    ["rolling length", mutated((r) => { r.rolling.current.until = "2026-10-28T11:00:00Z"; }), "bad_rolling_window"],
    ["rolling gap", mutated((r) => {
      r.rolling.previous.since = "2026-10-26T09:00:00Z";
      r.rolling.previous.until = "2026-10-27T09:00:00Z";
    }), "bad_rolling_window"],
    ["rolling missing", usageReport(1, { rolling: { current: usageReport().rolling.current } }), "bad_rolling"],
    ["rolling hour index", mutated((r) => { r.rolling.current.rows[0][0] = 24; }), "bad_rolling_row"],
    ["duplicate rolling key", mutated((r) => {
      r.rolling.current.rows.push([0, 0, 0, ...METRICS]);
    }), "duplicate_rolling_row"],
    ["too many rolling rows", mutated((r) => {
      r.rolling.previous.rows = Array.from({ length: 5_001 }, () => []);
    }), "too_many_rolling_rows"],
  ];
  for (const [name, report, reason] of cases) {
    assert.equal(validateUsagePayload(report), reason, name);
  }
  // The same key in the other rolling window is not a duplicate.
  assert.equal(validateUsagePayload(mutated((r) => {
    r.rolling.previous.rows.push([0, 0, 0, ...METRICS]);
  })), null);
});

// UTC epoch minutes of local midnight on each date, found through Intl the
// same way a reader would check it. Every zone below has a midnight on these
// dates, so moving back by the zone's offset settles it.
function zoneMidnights(timeZone, year, month, firstDay, count) {
  const format = new Intl.DateTimeFormat("en-US", {
    timeZone, hourCycle: "h23", year: "numeric", month: "2-digit", day: "2-digit",
    hour: "2-digit", minute: "2-digit",
  });
  const offsetMinutesAt = (ms) => {
    const parts = Object.fromEntries(format.formatToParts(ms).map((part) => [part.type, Number(part.value)]));
    return (Date.UTC(parts.year, parts.month - 1, parts.day, parts.hour, parts.minute) - ms) / 60_000;
  };
  return Array.from({ length: count }, (_, index) => {
    const utcMidnight = Date.UTC(year, month - 1, firstDay + index) / 60_000;
    const guess = utcMidnight - offsetMinutesAt(utcMidnight * 60_000);
    return utcMidnight - offsetMinutesAt(guess * 60_000);
  });
}

test("hourDayStarts must be real local midnights of the hourly coverage", () => {
  // Real zones across DST changes and fractional offsets: London ends DST on
  // 2026-10-25, Lord_Howe has a 30-minute shift and Kathmandu is UTC+5:45.
  for (const timeZone of ["Europe/London", "Asia/Kathmandu", "Australia/Lord_Howe"]) {
    const starts = zoneMidnights(timeZone, 2026, 10, 22, 8);
    const report = mutated((r) => {
      r.timeZone = timeZone;
      r.hourDayStarts = starts;
      r.hours = [{ phaseMinutes: ((starts[0] % 60) + 60) % 60, rows: [] }];
    });
    assert.equal(validateUsagePayload(report), null, timeZone);
  }
  // Lord_Howe's DST start (2026-10-04) makes a 23.5-hour day.
  const lordHowe = zoneMidnights("Australia/Lord_Howe", 2026, 10, 1, 8);
  assert.ok(lordHowe.some((start, index) => index > 0 && start - lordHowe[index - 1] === 1410));
  assert.equal(validateUsagePayload(mutated((r) => {
    r.coverage.hourlyFrom = "2026-10-01";
    r.coverage.hourlyTo = "2026-10-07";
    r.timeZone = "Australia/Lord_Howe";
    r.hourDayStarts = lordHowe;
    r.hours = [];
  })), null);

  // Outside the JavaScript Date range, though integer and evenly spaced.
  assert.equal(validateUsagePayload(mutated((r) => {
    r.hourDayStarts = Array.from({ length: 8 }, (_, day) => 1e15 + day * 1440);
  })), "bad_hour_day_starts");
  // A day off: not within 15 hours of the UTC midnight of hourlyFrom.
  assert.equal(validateUsagePayload(mutated((r) => {
    r.hourDayStarts = r.hourDayStarts.map((start) => start + 1440);
  })), "bad_hour_day_starts");
  assert.equal(validateUsagePayload(mutated((r) => {
    r.hourDayStarts = r.hourDayStarts.map((start) => start - 1440);
  })), "bad_hour_day_starts");
  // The edges of real offsets: UTC+14 and UTC-12.
  for (const offset of [-14 * 60, 12 * 60]) {
    assert.equal(validateUsagePayload(mutated((r) => {
      const first = Date.UTC(2026, 9, 22) / 60_000 + offset;
      r.hourDayStarts = Array.from({ length: 8 }, (_, day) => first + day * 1440);
      r.hours = [];
    })), null, `offset ${offset}`);
  }
});

test("validator rejects instants outside the Date range", () => {
  assert.equal(validateUsagePayload(usageReport(1, { generatedAt: "+275760-09-13T00:00:00.001Z" })), "bad_generated_at");
  assert.equal(validateUsagePayload(mutated((r) => {
    r.rolling.current.since = "+275760-09-13T00:00:00.001Z";
  })), "bad_rolling_window");
});

// Builds a report the size the desktop composer may send: two years of daily
// rows across many models, a full hourly week and both rolling windows.
function largeRealisticReport(targetBytes) {
  const accounts = Array.from({ length: 8 }, (_, index) => ({
    name: index < 7 ? `Claude account ${index + 1}` : "Codex (all accounts)",
    provider: index < 7 ? "claude" : "codex",
  }));
  const models = Array.from({ length: 60 }, (_, index) => ({
    id: `${index < 50 ? "claude" : "gpt"}-model-${String(index).padStart(3, "0")}-20260101`,
    provider: index < 50 ? "claude" : "codex",
    priced: index % 10 !== 9,
    price: index % 10 !== 9
      ? { input: 3, cacheRead: 0.3, cacheWrite5m: 3.75, cacheWrite1h: 6, output: 15 }
      : null,
  }));
  const metrics = (seed) => [
    1_000 + seed * 7, 400_000 + seed * 131, 20_000 + seed, 3_000, 9_000 + seed * 3, 1_200,
    1_234_567 + seed * 17, 987_654 - seed * 11, 40 + (seed % 9), 0,
  ];
  const report = usageReport(1_761_650_000_123, {
    coverage: { dailyFrom: "2024-10-28", dailyTo: "2026-10-28", hourlyFrom: "2026-10-22", hourlyTo: "2026-10-28" },
    accounts,
    models,
    days: [],
  });
  report.hours[0].rows = [];
  for (let day = 0; day < 7; day += 1) {
    for (let hour = 0; hour < 24; hour += 1) {
      for (let pair = 0; pair < 8; pair += 1) {
        const dayNumber = 724 + day;
        report.hours[0].rows.push([
          dayNumber, FIRST_HOUR_DAY_START + day * 1440 + hour * 60, pair, pair * 7, ...metrics(hour + pair),
        ]);
      }
    }
  }
  report.rolling.current.rows = [];
  report.rolling.previous.rows = [];
  for (let hour = 0; hour < 24; hour += 1) {
    for (let pair = 0; pair < 8; pair += 1) {
      report.rolling.current.rows.push([hour, pair, pair * 7, ...metrics(hour)]);
      report.rolling.previous.rows.push([hour, pair, pair * 7 + 1, ...metrics(hour)]);
    }
  }
  let size = Buffer.byteLength(JSON.stringify(report));
  outer: for (let day = 0; day < 731; day += 1) {
    for (let account = 0; account < accounts.length; account += 1) {
      for (let model = account * 7; model < account * 7 + 7; model += 1) {
        const row = [day, account, model, ...metrics(day + model)];
        size += Buffer.byteLength(JSON.stringify(row)) + 1;
        if (size > targetBytes) break outer;
        report.days.push(row);
      }
    }
  }
  return report;
}

test("validates a ~900 KB report quickly", (t) => {
  const report = largeRealisticReport(900_000);
  const body = JSON.stringify(report);
  assert.ok(Buffer.byteLength(body) > 850_000 && Buffer.byteLength(body) <= 900_000);

  assert.equal(validateUsagePayload(report), null);
  const runs = 20;
  const started = performance.now();
  for (let run = 0; run < runs; run += 1) validateUsagePayload(report);
  const perRun = (performance.now() - started) / runs;
  const parseStarted = performance.now();
  JSON.parse(body);
  const parse = performance.now() - parseStarted;
  t.diagnostic(`${Buffer.byteLength(body)} bytes, ${report.days.length} day rows, `
    + `${report.hours[0].rows.length} hour rows: validate ${perRun.toFixed(2)} ms, JSON.parse ${parse.toFixed(2)} ms`);
  // Generous bound: a regression to quadratic work would take seconds.
  assert.ok(perRun < 250, `validation took ${perRun} ms`);
});

test("the demo report is valid, realistic, and matches the demo snapshot flag", async (t) => {
  for (const at of ["2026-10-28T10:17:00Z", "2026-10-25T00:30:00Z", "2026-03-29T05:00:00Z", "2026-01-01T00:00:00Z"]) {
    assert.equal(validateUsagePayload(demoUsagePayload(Date.parse(at))), null, at);
  }

  const fixture = await startApp(t);
  const response = await fetch(`${fixture.baseUrl}/u/demo/usage`);
  assert.equal(response.status, 200);
  assert.equal(response.headers.get("cache-control"), "no-store");
  assert.equal(response.headers.get("access-control-allow-origin"), "*");
  const demo = await response.json();
  assert.equal(validateUsagePayload(demo), null);
  assert.equal(demo.timeZone, "Europe/London");
  assert.equal(demo.accounts.filter((account) => account.provider === "claude").length, 2);
  assert.equal(demo.accounts.filter((account) => account.provider === "codex").length, 1);
  assert.ok(demo.models.length >= 4);
  assert.ok(demo.models.some((model) => !model.priced));
  assert.ok(new Set(demo.days.map((row) => row[0])).size > 60, "roughly 90 days of daily rows");
  assert.equal(demo.hourDayStarts.length, 8);
  assert.ok(demo.hours[0].rows.length > 0);

  const snapshot = await (await fetch(`${fixture.baseUrl}/u/demo`)).json();
  assert.deepEqual(Object.keys(snapshot.usage).sort(), ["checkedAt", "seq", "shared"]);
  assert.equal(snapshot.usage.shared, true);
  assert.equal(snapshot.usage.seq, demo.seq);
  assert.equal(snapshot.usage.checkedAt, new Date(START).toISOString());

  for (const method of ["PUT", "DELETE"]) {
    const refused = await fetch(`${fixture.baseUrl}/u/demo/usage`, { method });
    assert.equal(refused.status, 405, method);
  }
});

// --- Routes ----------------------------------------------------------------------

test("stores, reads, and deletes a usage report", async (t) => {
  const fixture = await startApp(t);
  assert.equal((await fixture.putSnapshot()).status, 204);

  const body = JSON.stringify(usageReport(100));
  const put = await fixture.putUsage(body);
  assert.equal(put.status, 204);
  assert.equal(put.headers.get("x-read-id"), READ_ID);

  const get = await fixture.getUsage();
  assert.equal(get.status, 200);
  assert.equal(get.headers.get("cache-control"), "no-store");
  assert.equal(get.headers.get("access-control-allow-origin"), "*");
  assert.match(get.headers.get("content-type"), /^application\/json/);
  assert.equal(await get.text(), body, "stored verbatim");

  // The write id is not a read id.
  assert.equal((await fixture.getUsage(WRITE_ID)).status, 404);

  const remove = await fixture.deleteUsage(101);
  assert.equal(remove.status, 204);
  assert.equal(remove.headers.get("x-read-id"), READ_ID);
  const missing = await fixture.getUsage();
  assert.equal(missing.status, 404);
  assert.deepEqual(await missing.json(), { error: "not_found" });
  assert.deepEqual(fixture.app.store.usageRow(READ_ID), {
    seq: 101,
    bytes: USAGE_TOMBSTONE_BYTES,
    tombstone: true,
  });

  // The quota snapshot is untouched by the usage delete.
  assert.equal((await fetch(`${fixture.baseUrl}/u/${READ_ID}`)).status, 200);
});

test("usage routes refuse bad ids, methods, and headers", async (t) => {
  const fixture = await startApp(t);
  assert.equal((await fixture.getUsage("not-an-id")).status, 400);
  assert.equal((await fetch(`${fixture.baseUrl}/u/${WRITE_ID}/usage`, { method: "POST" })).status, 405);
  assert.equal((await fetch(`${fixture.baseUrl}/u/${WRITE_ID}/usage/extra`)).status, 404);

  const unsupported = await fetch(`${fixture.baseUrl}/u/${WRITE_ID}/usage`, { method: "PUT", body: "{}" });
  assert.equal(unsupported.status, 415);
  assert.equal((await fixture.putUsage("{")).status, 400);

  const badSeq = await fetch(`${fixture.baseUrl}/u/${WRITE_ID}/usage`, {
    method: "DELETE",
    headers: { "X-Usage-Seq": "12abc" },
  });
  assert.equal(badSeq.status, 400);
  assert.deepEqual(await badSeq.json(), { error: "invalid_usage_seq" });
});

test("rejects an invalid report with 422 and the validator reason", async (t) => {
  const fixture = await startApp(t);
  await fixture.putSnapshot();
  const response = await fixture.putUsage(usageReport(5, { version: 2 }));
  assert.equal(response.status, 422);
  assert.deepEqual(await response.json(), { error: "invalid_payload", reason: "bad_version" });
  assert.equal(fixture.app.store.usageRow(READ_ID), null);
});

test("usage writes are signed over the /usage path and share the write budget", async (t) => {
  const fixture = await startApp(t, { unsignedWritesPerMinute: 1, signedWritesPerMinute: 5 });
  // Uses the single unsigned write of this minute.
  assert.equal((await fixture.putSnapshot()).status, 204);

  const timestamp = Math.floor(fixture.now() / 1000);
  const signedPut = async (body, path) => fixture.putUsage(body, {
    headers: {
      "X-Costats-Timestamp": String(timestamp),
      "X-Costats-Signature": await signRequest({ timestamp, method: "PUT", path, body }),
    },
  });

  // Signed over the snapshot path: not a valid signature here, so unsigned and refused.
  const wrongPath = JSON.stringify(usageReport(1));
  const refused = await signedPut(wrongPath, `/u/${WRITE_ID}`);
  assert.equal(refused.status, 429);
  assert.equal((await refused.json()).error, "rate_limited");

  const body = JSON.stringify(usageReport(2));
  assert.equal((await signedPut(body, `/u/${WRITE_ID}/usage`)).status, 204);

  const deleteSignature = await signRequest({
    timestamp, method: "DELETE", path: `/u/${WRITE_ID}/usage`, body: "",
  });
  const remove = await fetch(`${fixture.baseUrl}/u/${WRITE_ID}/usage`, {
    method: "DELETE",
    headers: {
      "X-Usage-Seq": "3",
      "X-Costats-Timestamp": String(timestamp),
      "X-Costats-Signature": deleteSignature,
    },
  });
  assert.equal(remove.status, 204);
  assert.equal(fixture.app.store.usageRow(READ_ID).seq, 3);
});

test("usage bodies have their own 1 MiB cap while snapshots keep 16 KB", async (t) => {
  const fixture = await startApp(t);
  await fixture.putSnapshot();

  // 20 KB: fine for usage, too large for a snapshot.
  const usageBody = JSON.stringify({ ...usageReport(10), padding: "x".repeat(20 * 1024) });
  assert.equal((await fixture.putUsage(usageBody)).status, 204);
  const snapshot = await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ version: 2, accounts: [], padding: "x".repeat(20 * 1024) }),
  });
  assert.equal(snapshot.status, 413);

  const tooLarge = await fixture.putUsage(JSON.stringify({ ...usageReport(11), padding: "x".repeat(MAX_USAGE_BODY) }));
  assert.equal(tooLarge.status, 413);
  assert.deepEqual(await tooLarge.json(), { error: "too_large" });
  assert.equal(JSON.parse(await (await fixture.getUsage()).text()).seq, 10);
});

test("a usage report needs a live snapshot", async (t) => {
  const fixture = await startApp(t);
  const missing = await fixture.putUsage(usageReport(1));
  assert.equal(missing.status, 409);
  assert.deepEqual(await missing.json(), { error: "snapshot_missing" });
  assert.equal(fixture.app.store.usageRow(READ_ID), null);
});

test("a write whose seq is not above the stored one is refused and stores nothing", async (t) => {
  const fixture = await startApp(t);
  await fixture.putSnapshot();
  assert.equal((await fixture.putUsage(usageReport(10))).status, 204);

  for (const seq of [10, 9]) {
    const stale = await fixture.putUsage(usageReport(seq, { timeZone: "Europe/London" }));
    assert.equal(stale.status, 409, `seq ${seq}`);
    assert.deepEqual(await stale.json(), { error: "stale_write" });
  }
  const stored = await (await fixture.getUsage()).json();
  assert.equal(stored.seq, 10);
  assert.equal(stored.timeZone, "Asia/Jerusalem");

  assert.equal((await fixture.putUsage(usageReport(11))).status, 204);
});

test("a delete that lands while an older PUT is in flight wins (P1)", async (t) => {
  let entered;
  let release;
  const reachedStore = new Promise((resolveEntered) => { entered = resolveEntered; });
  const gate = new Promise((resolveGate) => { release = resolveGate; });
  const fixture = await startApp(t, {
    beforeUsageStore: async ({ seq }) => {
      if (seq !== 100) return;
      entered();
      await gate;
    },
  });
  await fixture.putSnapshot();

  const pendingPut = fixture.putUsage(usageReport(100));
  await reachedStore;
  assert.equal((await fixture.deleteUsage(200)).status, 204);
  release();

  const put = await pendingPut;
  assert.equal(put.status, 409);
  assert.deepEqual(await put.json(), { error: "stale_write" });
  assert.equal((await fixture.getUsage()).status, 404);
  assert.deepEqual(fixture.app.store.usageRow(READ_ID), { seq: 200, bytes: USAGE_TOMBSTONE_BYTES, tombstone: true });
});

test("a delete with a lower seq than the stored report is a no-op (P2)", async (t) => {
  const fixture = await startApp(t);
  await fixture.putSnapshot();
  await fixture.putUsage(usageReport(100));

  assert.equal((await fixture.deleteUsage(50)).status, 204);
  assert.equal((await fixture.getUsage()).status, 200);
  assert.equal(fixture.app.store.usageRow(READ_ID).tombstone, false);

  // Without the header the delete removes whatever is stored and keeps its seq.
  assert.equal((await fixture.deleteUsage()).status, 204);
  assert.deepEqual(fixture.app.store.usageRow(READ_ID), { seq: 100, bytes: USAGE_TOMBSTONE_BYTES, tombstone: true });

  // A later delete raises the tombstone seq and refreshes its expiry.
  fixture.advance(1000);
  assert.equal((await fixture.deleteUsage(300)).status, 204);
  assert.equal(fixture.app.store.usageRow(READ_ID).seq, 300);
  const { expires_at: expiresAt } = fixture.app.store.database
    .prepare("SELECT expires_at FROM usage_reports WHERE read_id = ?").get(READ_ID);
  assert.equal(expiresAt, fixture.now() + 7 * DAY);
});

test("a delete for an id with no snapshot and no report stores nothing (P18)", async (t) => {
  const fixture = await startApp(t);
  const response = await fixture.deleteUsage(500);
  assert.equal(response.status, 204);
  assert.equal(fixture.app.store.usageRow(READ_ID), null);

  // With a live snapshot the same delete leaves a tombstone, which then
  // refuses an older PUT.
  await fixture.putSnapshot();
  assert.equal((await fixture.deleteUsage(500)).status, 204);
  assert.deepEqual(fixture.app.store.usageRow(READ_ID), { seq: 500, bytes: USAGE_TOMBSTONE_BYTES, tombstone: true });
  assert.equal((await fixture.putUsage(usageReport(400))).status, 409);
  assert.equal((await fixture.putUsage(usageReport(501))).status, 204);
});

test("deleting the snapshot tombstones the report against an older PUT", async (t) => {
  const fixture = await startApp(t);
  await fixture.putSnapshot();
  await fixture.putUsage(usageReport(100));

  assert.equal((await fixture.deleteSnapshot(150)).status, 204);
  assert.deepEqual(fixture.app.store.usageRow(READ_ID), { seq: 150, bytes: USAGE_TOMBSTONE_BYTES, tombstone: true });
  assert.equal((await fixture.getUsage()).status, 404);

  // The snapshot comes back; a delayed older report must not.
  await fixture.putSnapshot();
  const stale = await fixture.putUsage(usageReport(120));
  assert.equal(stale.status, 409);
  assert.deepEqual(await stale.json(), { error: "stale_write" });
  assert.equal((await fixture.getUsage()).status, 404);
  assert.equal((await fixture.putUsage(usageReport(151))).status, 204);
  assert.equal((await fixture.getUsage()).status, 200);

  // Without the header the stored seq is kept; a malformed header never
  // blocks the snapshot delete and counts as absent.
  const malformed = await fetch(`${fixture.baseUrl}/u/${WRITE_ID}`, {
    method: "DELETE",
    headers: { "X-Usage-Seq": "soon" },
  });
  assert.equal(malformed.status, 204);
  assert.equal((await fetch(`${fixture.baseUrl}/u/${READ_ID}`)).status, 404);
  assert.deepEqual(fixture.app.store.usageRow(READ_ID), { seq: 151, bytes: USAGE_TOMBSTONE_BYTES, tombstone: true });
});

test("a report expires no later than its snapshot", async (t) => {
  const fixture = await startApp(t, { ttlMs: 1000 });
  await fixture.putSnapshot();
  fixture.advance(500);
  await fixture.putUsage(usageReport(1));

  const row = fixture.app.store.database
    .prepare("SELECT expires_at FROM usage_reports WHERE read_id = ?").get(READ_ID);
  assert.equal(row.expires_at, START + 1000, "capped at the snapshot's expiry");

  fixture.advance(499);
  assert.equal((await fixture.getUsage()).status, 200);
  fixture.advance(1);
  assert.equal((await fixture.getUsage()).status, 404, "snapshot expired");

  // A new snapshot does not revive the expired report.
  await fixture.putSnapshot();
  assert.equal((await fixture.getUsage()).status, 404);
});

test("the sweep tombstones orphaned reports and keeps tombstones until their own expiry", async (t) => {
  const fixture = await startApp(t, { ttlMs: 1000, usageTombstoneTtlMs: 10_000 });
  const store = fixture.app.store;
  await fixture.putSnapshot();
  await fixture.putUsage(usageReport(7));
  await fixture.putSnapshot(OTHER_WRITE_ID);
  await fixture.putUsage(usageReport(8), { writeId: OTHER_WRITE_ID });
  const otherReadId = (await fixture.putSnapshot(OTHER_WRITE_ID)).headers.get("x-read-id");
  assert.equal((await fixture.deleteUsage(9, { writeId: OTHER_WRITE_ID })).status, 204);

  // Both snapshots expire. The report becomes a tombstone that keeps its seq;
  // the existing tombstone is exempt from the orphan rule.
  fixture.advance(2000);
  assert.equal(store.deleteExpired(fixture.now()), 2);
  assert.deepEqual(store.usageRow(READ_ID), { seq: 7, bytes: USAGE_TOMBSTONE_BYTES, tombstone: true });
  assert.deepEqual(store.usageRow(otherReadId), { seq: 9, bytes: USAGE_TOMBSTONE_BYTES, tombstone: true });

  // The other tombstone was written at START and expires at START + 10 s; the
  // converted one got a fresh expiry at the sweep.
  fixture.advance(8000);
  store.deleteExpired(fixture.now());
  assert.equal(store.usageRow(otherReadId), null);
  assert.notEqual(store.usageRow(READ_ID), null);

  fixture.advance(2000);
  store.deleteExpired(fixture.now());
  assert.equal(store.usageRow(READ_ID), null);
});

test("the per-address byte budget answers 429 usage_budget", async (t) => {
  const reportBytes = Buffer.byteLength(JSON.stringify(usageReport(1)));
  const fixture = await startApp(t, {
    ...readUsageLimits({ USAGE_WRITE_BUDGET_BYTES: String(Math.floor(reportBytes * 2.5)) }),
  });
  await fixture.putSnapshot();

  assert.equal((await fixture.putUsage(usageReport(1))).status, 204);
  // Refused writes are not charged.
  assert.equal((await fixture.putUsage(usageReport(1))).status, 409);
  assert.equal((await fixture.putUsage(usageReport(2))).status, 204);

  const refused = await fixture.putUsage(usageReport(3));
  assert.equal(refused.status, 429);
  assert.equal(refused.headers.get("retry-after"), "600");
  assert.deepEqual(await refused.json(), { error: "usage_budget", retryAfterSeconds: 600 });
  // The sequence check comes first, so a stale write still learns it is stale.
  assert.equal((await fixture.putUsage(usageReport(2))).status, 409);
  assert.equal((await fixture.getUsage()).status, 200);
  assert.equal((await (await fixture.getUsage()).json()).seq, 2);

  fixture.advance(10 * 60_000);
  assert.equal((await fixture.putUsage(usageReport(3))).status, 204);
});

test("the global storage cap answers 507 but never blocks turning a report into a tombstone", async (t) => {
  const reportBytes = Buffer.byteLength(JSON.stringify(usageReport(1)));
  const fixture = await startApp(t, {
    ...readUsageLimits({ USAGE_STORAGE_MAX_BYTES: String(reportBytes) }),
  });
  await fixture.putSnapshot();
  await fixture.putSnapshot(OTHER_WRITE_ID);

  assert.equal((await fixture.putUsage(usageReport(1))).status, 204);
  // The replaced row does not count against its own replacement.
  assert.equal((await fixture.putUsage(usageReport(2))).status, 204);

  const full = await fixture.putUsage(usageReport(1), { writeId: OTHER_WRITE_ID });
  assert.equal(full.status, 507);
  assert.deepEqual(await full.json(), { error: "storage_full" });

  // The cap is exactly full, and the delete still goes through.
  assert.equal((await fixture.deleteUsage(3)).status, 204);
  assert.equal(fixture.app.store.usageRow(READ_ID).tombstone, true);
  assert.equal((await fixture.putUsage(usageReport(4), { writeId: OTHER_WRITE_ID })).status, 507);
});

test("usage limits come from the environment with documented defaults", async (t) => {
  assert.deepEqual(readUsageLimits({}), {
    maxUsageBodyBytes: 1024 * 1024,
    usageWriteBudgetBytes: 8 * 1024 * 1024,
    usageStorageMaxBytes: 256 * 1024 * 1024,
  });
  assert.equal(DEFAULT_USAGE_WRITE_BUDGET_BYTES, 8 * 1024 * 1024);
  assert.equal(DEFAULT_USAGE_STORAGE_MAX_BYTES, 256 * 1024 * 1024);
  assert.deepEqual(readUsageLimits({
    MAX_USAGE_BODY_BYTES: "2048",
    USAGE_WRITE_BUDGET_BYTES: "4096",
    USAGE_STORAGE_MAX_BYTES: "8192",
  }), { maxUsageBodyBytes: 2048, usageWriteBudgetBytes: 4096, usageStorageMaxBytes: 8192 });
  assert.throws(() => readUsageLimits({ MAX_USAGE_BODY_BYTES: "0" }), /positive integer/);
  assert.throws(() => readUsageLimits({ USAGE_STORAGE_MAX_BYTES: "lots" }), /positive integer/);

  const fixture = await startApp(t, { ...readUsageLimits({ MAX_USAGE_BODY_BYTES: "2048" }) });
  await fixture.putSnapshot();
  const response = await fixture.putUsage(JSON.stringify({ ...usageReport(1), padding: "x".repeat(4096) }));
  assert.equal(response.status, 413);
});

test("serves the usage viewer script", async (t) => {
  const fixture = await startApp(t);
  const response = await fetch(`${fixture.baseUrl}/usage.js`);
  assert.equal(response.status, 200);
  assert.equal(response.headers.get("content-type"), "text/javascript; charset=utf-8");
  assert.equal(await response.text(), readFileSync(join(WEB_ROOT, "usage.js"), "utf8"));
});
