// Tests for the viewer's Usage tab logic (web/usage.js): number formatting
// identical to the desktop's UsageNumberFormat, period resolution through the
// payload's hourDayStarts, comparison, aggregation, coverage, the Lord Howe
// straddle, report discovery, and the cross-check against the desktop
// aggregator (tests/fixtures/remote-usage/crosscheck.json).

import { test } from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const U = require("../../web/usage.js");
const M = U.METRIC;

const FIXTURE = fileURLToPath(new URL("../../tests/fixtures/remote-usage/crosscheck.json", import.meta.url));

const day = (text) => U.parseDay(text);
const minutes = (iso) => Date.parse(iso) / 60000;
const PRICE = { input: 3, cacheRead: 0.3, cacheWrite5m: 3.75, cacheWrite1h: 6, output: 15 };

function metrics({ uncached = 0, cacheRead = 0, w5 = 0, w1 = 0, output = 0, reasoning = 0, cost = 0, savings = 0, requests = 1, unpriced = 0 } = {}) {
  return [uncached, cacheRead, w5, w1, output, reasoning, cost, savings, requests, unpriced];
}

function rolling(generatedAt) {
  const until = Date.parse(generatedAt);
  const iso = (ms) => new Date(ms).toISOString().replace(".000Z", "Z");
  return {
    current: { since: iso(until - 86400000), until: iso(until), rows: [] },
    previous: { since: iso(until - 2 * 86400000), until: iso(until - 86400000), rows: [] },
  };
}

// A hand-built payload (contract section 1). Hour grids and hourDayStarts are
// written out by hand, so no test relies on the viewer doing midnight math.
function payload(overrides) {
  const base = {
    version: 1,
    seq: 10,
    generatedAt: "2026-10-27T12:00:00Z",
    timeZone: "Europe/London",
    coverage: { dailyFrom: "2026-10-01", dailyTo: "2026-10-27", hourlyFrom: null, hourlyTo: null },
    accounts: [{ name: "Personal", provider: "claude" }, { name: "Codex (all accounts)", provider: "codex" }],
    models: [
      { id: "claude-sonnet-4-5", provider: "claude", priced: true, price: PRICE },
      { id: "gpt-5-codex", provider: "codex", priced: true, price: { input: 1.25, cacheRead: 0.125, cacheWrite5m: null, cacheWrite1h: null, output: 10 } },
      { id: "mystery", provider: "codex", priced: false, price: null },
    ],
    days: [],
    hourDayStarts: [],
    hours: [],
  };
  const merged = { ...base, ...overrides };
  if (!merged.rolling) merged.rolling = rolling(merged.generatedAt);
  return merged;
}

function prepared(overrides) {
  const data = U.prepareUsage(payload(overrides));
  assert.ok(data, "the payload is readable");
  return data;
}

// --- number formatting: the vectors of UsageNumberFormatTests.cs -------------

test("tokens keep three significant digits", () => {
  const vectors = [
    [0, "0"], [-5, "0"], [348, "348"], [999, "999"], [1000, "1.00K"], [16500, "16.5K"],
    [948000, "948K"], [106000000, "106M"], [18200000, "18.2M"], [1380000, "1.38M"],
    [7900000000, "7.90B"], [13300000000, "13.3B"], [2080000000, "2.08B"],
  ];
  for (const [tokens, expected] of vectors) assert.equal(U.formatTokens(tokens), expected, String(tokens));
});

test("tokens promote a value that rounds past its own unit", () => {
  assert.equal(U.formatTokens(999600), "1.00M");
  assert.equal(U.formatTokens(999600000), "1.00B");
});

test("money is exact to the cent", () => {
  for (const [amount, expected] of [[0, "$0.00"], [0.05, "$0.05"], [1152.15, "$1,152.15"], [69668.14, "$69,668.14"]]) {
    assert.equal(U.formatMoney(amount), expected);
  }
  assert.equal(U.formatMoneyMicro(1152150000), "$1,152.15");
  assert.equal(U.formatMoneyMicro(5000), "$0.01", "half a cent rounds away from zero");
  assert.equal(U.formatMoneyMicro(4999), "$0.00");
  assert.equal(U.formatMoneyMicro(-3100000), "-$3.10", "savings can be negative");
});

test("cost or unpriced never prices unknown models at zero", () => {
  assert.equal(U.costOrUnpriced(0, 4200000), "unpriced");
  assert.equal(U.costOrUnpriced(0, 0), "$0.00");
  assert.equal(U.costOrUnpriced(12340000, 900000), "$12.34");
  assert.equal(U.costOrUnpriced(12340000, 0), "$12.34");
});

test("axis ticks draw a bare zero baseline", () => {
  assert.equal(U.axisCost(0), "0");
  assert.equal(U.axisCost(1500), "$1,500.00");
  assert.equal(U.axisTokens(0), "0");
  assert.equal(U.axisTokens(1500000000), "1.50B");
});

test("percent shows one decimal and survives nonsense", () => {
  for (const [part, whole, expected] of [[9248.77, 10400.92, "88.9%"], [1152.15, 10400.92, "11.1%"], [0.04, 10400.92, "0.0%"], [1, 0, "0.0%"]]) {
    assert.equal(U.formatPercent(part, whole), expected);
  }
  assert.equal(U.formatFraction(NaN), "0.0%");
  assert.equal(U.formatFraction(Infinity), "0.0%");
  assert.equal(U.formatFraction(0.992), "99.2%");
});

test("multiplier compares savings against cost", () => {
  assert.equal(U.formatMultiplier(69668.14, 10400.92), "6.7x");
  assert.equal(U.formatMultiplier(10, 0), "0.0x");
});

test("day labels match the dashboard captions", () => {
  const from = day("2026-07-25");
  const to = day("2026-08-23");
  assert.equal(U.rangeLabel(from, to), "Jul 25 to Aug 23");
  assert.equal(U.axisDayLabel(from), "JUL 25");
  assert.equal(U.longDayLabel(to), "Sun, Aug 23");
  const today = day("2026-10-26");
  assert.equal(U.rangeLabel(day("2025-12-20"), day("2026-01-05"), today), "Dec 20, 2025 to Jan 5, 2026");
  assert.equal(U.rangeLabel(day("2026-08-23"), day("2026-08-23"), today), "Sun, Aug 23");
  assert.equal(U.longDayLabel(day("2025-10-01"), today), "Wed, Oct 1, 2025");
});

test("an hour a daylight-saving change repeats carries its offset", () => {
  // London repeats 01:00 on 2026-10-25: 00:00Z is 01:00 BST, 01:00Z is 01:00 GMT.
  const today = day("2026-10-26");
  assert.equal(U.longHourLabel(Date.parse("2026-10-25T00:00:00Z"), "Europe/London", today), "Sun, Oct 25, 01:00 (UTC+1)");
  assert.equal(U.longHourLabel(Date.parse("2026-10-25T01:00:00Z"), "Europe/London", today), "Sun, Oct 25, 01:00 (UTC+0)");
  assert.equal(U.longHourLabel(Date.parse("2026-10-25T05:00:00Z"), "Europe/London", today), "Sun, Oct 25, 05:00");
  assert.equal(U.longHourLabel(Date.parse("2025-10-01T13:00:00Z"), "Europe/London", today), "Wed, Oct 1, 2025, 14:00");
});

// --- deltas: the vectors of UsageComparisonTests.cs ---------------------------

test("deltas format with a neutral arrow", () => {
  const vectors = [
    [0.124, "cost", "▲ 12.4%"], [-0.03, "cost", "▼ 3.0%"], [2.5, "cost", "▲ 250%"],
    [12.0, "tokens", "▲ 1,200% tokens"], [0.0001, "cost", "0.0%"], [-1.0, "cost", "▼ 100%"],
  ];
  for (const [fraction, basis, expected] of vectors) {
    assert.equal(U.formatDelta({ kind: "change", fraction, basis }), expected);
  }
  const rise = { kind: "change", fraction: 0.372, basis: "tokens" };
  assert.equal(U.formatDelta(rise), "▲ 37.2% tokens");
  assert.equal(U.formatDelta(rise, false), "▲ 37.2%");
  assert.equal(U.formatDelta({ kind: "noPriorData", fraction: 0, basis: "cost" }), "no prior data");
  assert.equal(U.formatDelta({ kind: "none", fraction: 0, basis: "cost" }), "");
});

test("deltas compare cost when priced, tokens otherwise, and scale lengths", () => {
  const totals = (costUsd, tokens, unpriced = 0) => [0, 0, 0, 0, tokens, 0, costUsd * 1e6, 0, 1, unpriced, 1];
  const cost = U.deltaBetween(totals(15, 10), totals(10, 10));
  assert.equal(cost.basis, "cost");
  assert.ok(Math.abs(cost.fraction - 0.5) < 1e-9);
  const tokens = U.deltaBetween(totals(15, 25, 5), totals(10, 100));
  assert.equal(tokens.basis, "tokens");
  assert.ok(Math.abs(tokens.fraction + 0.75) < 1e-9);
  assert.ok(Math.abs(U.deltaBetween(totals(70, 1), totals(140, 1), 0.5).fraction) < 1e-9);
  assert.equal(U.deltaBetween(totals(5, 10), U.emptyMetrics()).kind, "noPriorData");
  assert.equal(U.deltaBetween(U.emptyMetrics(), U.emptyMetrics()).kind, "none");
  assert.ok(Math.abs(U.deltaBetween(U.emptyMetrics(), totals(5, 10)).fraction + 1) < 1e-9);
});

test("changes run from the earlier period to the later one", () => {
  const data = prepared({});
  const september = U.resolvePeriod(data, { kind: "days", from: day("2026-09-01"), to: day("2026-09-30") });
  const october = U.resolvePeriod(data, { kind: "days", from: day("2026-10-01"), to: day("2026-10-30") });
  const forward = U.changeDirection(september, october);
  assert.equal(forward.currentIsEarlier, true);
  assert.equal(forward.label, "A → B");
  const totals = (usd) => [0, 0, 0, 0, 1, 0, usd * 1e6, 0, 1, 0, 1];
  const rise = U.directionBetween(forward, totals(413), totals(870));
  assert.ok(rise.fraction > 1.1 && rise.fraction < 1.11);
  const backward = U.changeDirection(october, september);
  assert.equal(backward.label, "B → A");
  assert.ok(U.directionBetween(backward, totals(870), totals(413)).fraction > 1.1);
});

// --- sorting: the vectors of UsageBreakdownSortTests.cs --------------------------

function item(label, { cost = 0, tokens = 0, unpriced = false, provider = "claude", delta = { kind: "none", fraction: 0, basis: "cost" }, time = null, previous = null, previousUnpriced = false } = {}) {
  return {
    label, provider, time, totals: [0, 0, 0, 0, tokens, 0, cost * 1e6, 0, 0, 0, 0], isUnpriced: unpriced, delta,
    previousTotals: previous ? [0, 0, 0, 0, previous.tokens, 0, previous.cost * 1e6, 0, 0, 0, 0] : null,
    isPreviousUnpriced: previousUnpriced,
  };
}
const labels = (items) => items.map((entry) => entry.label);

test("breakdown sorts by raw values with unpriced and no-change rows last", () => {
  const tokenItems = [item("small", { tokens: 900000000 }), item("big", { tokens: 1200000000 })];
  assert.deepEqual(labels(U.sortBreakdown(tokenItems, { column: "tokens", descending: true })), ["big", "small"]);
  assert.deepEqual(labels(U.sortBreakdown(tokenItems, { column: "tokens", descending: false })), ["small", "big"]);

  for (const column of ["value", "share"]) {
    const priced = [item("free", { unpriced: true }), item("cheap", { cost: 1 }), item("dear", { cost: 9 })];
    assert.deepEqual(labels(U.sortBreakdown(priced, { column, descending: true })), ["dear", "cheap", "free"]);
    assert.deepEqual(labels(U.sortBreakdown(priced, { column, descending: false })), ["cheap", "dear", "free"]);
  }

  const changes = [
    item("flat"),
    item("up", { delta: { kind: "change", fraction: 5, basis: "cost" } }),
    item("fresh", { delta: { kind: "noPriorData", fraction: 0, basis: "cost" } }),
    item("down", { delta: { kind: "change", fraction: -0.5, basis: "cost" } }),
  ];
  assert.deepEqual(labels(U.sortBreakdown(changes, { column: "change", descending: true })), ["fresh", "up", "down", "flat"]);
  assert.deepEqual(labels(U.sortBreakdown(changes, { column: "change", descending: false })), ["down", "up", "fresh", "flat"]);

  const periodB = [
    item("a", { previous: { cost: 5, tokens: 10 } }),
    item("b", { previous: { cost: 9, tokens: 1 } }),
    item("c", { previous: { cost: 0, tokens: 99 }, previousUnpriced: true }),
  ];
  assert.deepEqual(labels(U.sortBreakdown(periodB, { column: "previousValue", descending: true })), ["b", "a", "c"]);
  assert.deepEqual(labels(U.sortBreakdown(periodB, { column: "previousValue", descending: false })), ["a", "b", "c"]);
  assert.deepEqual(labels(U.sortBreakdown(periodB, { column: "previousTokens", descending: true })), ["c", "a", "b"]);

  const times = [item("Fri, Oct 9", { provider: null, time: day("2026-10-09") }), item("Sat, Oct 10", { provider: null, time: day("2026-10-10") })];
  assert.deepEqual(labels(U.sortBreakdown(times, { column: "label", descending: true })), ["Sat, Oct 10", "Fri, Oct 9"]);

  const ties = [item("b", { cost: 1, provider: "codex" }), item("a", { cost: 1, provider: "codex" }), item("a", { cost: 1, provider: "claude" })];
  const sorted = U.sortBreakdown(ties, { column: "value", descending: true });
  assert.deepEqual(labels(sorted), ["a", "a", "b"]);
  assert.equal(sorted[0].provider, "claude");
});

test("a header click flips the same column and starts new ones descending", () => {
  const sort = { column: "value", descending: true };
  assert.deepEqual(U.toggleSort(sort, "value", false), { column: "value", descending: false });
  assert.deepEqual(U.toggleSort(sort, "tokens", false), { column: "tokens", descending: true });
  assert.deepEqual(U.toggleSort(sort, "label", false), { column: "label", descending: false });
  assert.deepEqual(U.toggleSort(sort, "label", true), { column: "label", descending: true });
  assert.deepEqual(U.effectiveModelSort({ column: "change", descending: true }, false), { column: "value", descending: true });
  assert.deepEqual(U.effectiveModelSort({ column: "share", descending: true }, true), { column: "value", descending: true });
});

// --- periods ----------------------------------------------------------------------

// London hourly coverage across the 2026-10-25 fall back (Oct 25 has 25 hours).
function londonHourly() {
  return {
    coverage: { dailyFrom: "2026-10-01", dailyTo: "2026-10-27", hourlyFrom: "2026-10-21", hourlyTo: "2026-10-27" },
    hourDayStarts: [
      "2026-10-20T23:00:00Z", "2026-10-21T23:00:00Z", "2026-10-22T23:00:00Z", "2026-10-23T23:00:00Z",
      "2026-10-24T23:00:00Z", "2026-10-26T00:00:00Z", "2026-10-27T00:00:00Z", "2026-10-28T00:00:00Z",
    ].map(minutes),
  };
}

test("one to three days are hourly over the payload's day starts; longer runs are daily", () => {
  const data = prepared(londonHourly());
  const one = U.resolvePeriod(data, { kind: "days", from: day("2026-10-25"), to: day("2026-10-25") });
  assert.equal(one.hourly, true);
  assert.equal(one.slots.length, 25, "a fall-back day has 25 hourly buckets");
  assert.equal(one.startMin, minutes("2026-10-24T23:00:00Z"));
  const three = U.resolvePeriod(data, { kind: "days", from: day("2026-10-25"), to: day("2026-10-27") });
  assert.equal(three.hourly, true);
  assert.equal(three.slots.length, 73);
  const four = U.resolvePeriod(data, { kind: "days", from: day("2026-10-24"), to: day("2026-10-27") });
  assert.equal(four.hourly, false);
  assert.equal(four.dayCount, 4);
  const reversed = U.resolvePeriod(data, { kind: "days", from: day("2026-09-30"), to: day("2026-09-01") });
  assert.equal(reversed.from, day("2026-09-01"));
  assert.equal(reversed.dayCount, 30);
});

test("a short range outside hourly coverage falls back to days and says so", () => {
  const data = prepared(londonHourly());
  const old = U.resolvePeriod(data, { kind: "days", from: day("2026-10-10"), to: day("2026-10-11") });
  assert.equal(old.hourly, false);
  assert.equal(old.hourlyFallback, true);
  const view = U.computeView(data, { period: { kind: "days", from: day("2026-10-10"), to: day("2026-10-11") }, comparison: null, accounts: null }, Date.parse("2026-10-27T12:00:00Z"));
  const notes = U.buildNotes(view, U.buildChart(view, "cost"));
  assert.ok(notes.some((note) => note.includes("Hourly data is shared only for Oct 21 to Oct 27")), notes.join(" | "));
});

test("presets resolve against today in the desktop's zone", () => {
  const data = prepared(londonHourly());
  // 23:30Z on Oct 27 is already Oct 28 in Jerusalem but still Oct 27 in London.
  const now = Date.parse("2026-10-27T23:30:00Z");
  const week = U.selectionFor({ range: "7", compare: "off" }, data, now);
  assert.equal(week.today, day("2026-10-27"));
  assert.deepEqual(week.period, { kind: "days", from: day("2026-10-21"), to: day("2026-10-27") });
  assert.deepEqual(U.selectionFor({ range: "24h", compare: "previous" }, data, now).period, { kind: "rolling" });
  const custom = U.selectionFor({ range: "custom", customFrom: day("2026-10-12"), customTo: day("2026-10-10"), compare: "custom", compareFrom: day("2026-10-01"), compareTo: day("2026-10-03"), account: U.accountSelection(data, 1) }, data, now);
  assert.deepEqual(custom.period, { kind: "days", from: day("2026-10-10"), to: day("2026-10-12") });
  assert.deepEqual(custom.comparison, { from: day("2026-10-01"), to: day("2026-10-03") });
  assert.deepEqual(custom.accounts, [1]);
  assert.equal(U.selectionFor({ range: "30", account: { index: 5, provider: "claude", name: "Gone" } }, data, now).accounts, null);
});

test("R3: two accounts with the same name are picked by index and told apart", () => {
  const twins = [
    { name: "Laptop", provider: "claude" },
    { name: "Laptop", provider: "claude" },
    { name: "Codex (all accounts)", provider: "codex" },
  ];
  const data = prepared({
    accounts: twins,
    days: [
      [20, 0, 0, ...metrics({ output: 10, cost: 1000000 })],
      [20, 1, 0, ...metrics({ output: 20, cost: 2000000 })],
    ],
  });
  const now = Date.parse("2026-10-27T12:00:00Z");
  assert.deepEqual(U.accountOptions(data).map((entry) => [entry.value, entry.label]),
    [["0", "Laptop"], ["1", "Laptop (2)"], ["2", "Codex (all accounts)"]]);

  // Picking the second "Laptop" shows the second account, not the first.
  const second = U.accountSelection(data, 1);
  const selection = U.selectionFor({ range: "30", account: second }, data, now);
  assert.deepEqual(selection.accounts, [1]);
  assert.equal(U.computeView(data, selection, now).current.totals[M.cost], 2000000);

  // After a refresh the pick holds while index 1 is still the same account...
  const same = prepared({ accounts: twins.map((account) => ({ ...account })) });
  assert.equal(U.resolveAccount(second, same), 1);
  // ...and falls back to all accounts when that index now names another one.
  const moved = prepared({ accounts: [twins[0], { name: "Desktop", provider: "claude" }, twins[2]] });
  assert.equal(U.resolveAccount(second, moved), null);
  assert.equal(U.selectionFor({ range: "30", account: second }, moved, now).accounts, null);
  const shorter = prepared({ accounts: [twins[0]] });
  assert.equal(U.resolveAccount(second, shorter), null);
  assert.equal(U.accountSelection(data, 9), null);
});

test("the previous period has the same length and ends the day before", () => {
  assert.deepEqual(U.previousPeriod({ kind: "days", from: day("2026-10-20"), to: day("2026-10-26") }), { kind: "days", from: day("2026-10-13"), to: day("2026-10-19") });
  assert.deepEqual(U.previousPeriod({ kind: "rolling" }), { kind: "rollingPrevious" });
  const data = prepared({ generatedAt: "2026-10-26T09:30:00Z" });
  const current = U.resolvePeriod(data, { kind: "rolling" });
  const previous = U.resolvePeriod(data, { kind: "rollingPrevious" });
  assert.equal(previous.endMin, current.startMin);
  assert.equal(current.endMin - current.startMin, 24 * 60);
  assert.equal(current.slots.length, 24);
});

test("custom ranges follow the desktop limits: no future day, 730 days back, 366 days long", () => {
  // UsagePeriodTests.Custom_ranges_stay_inside_the_picker_limits.
  const today = day("2026-10-26");
  assert.equal(U.isValidCustomRange(day("2026-10-01"), day("2026-10-26"), today), true);
  assert.equal(U.isValidCustomRange(day("2026-10-01"), day("2026-10-27"), today), false, "no future day");
  assert.equal(U.isValidCustomRange(today - 730, today - 400, today), true);
  assert.equal(U.isValidCustomRange(today - 731, today - 400, today), false, "lookback");
  assert.equal(U.isValidCustomRange(today - 365, today, today), true, "366 days");
  assert.equal(U.isValidCustomRange(today - 366, today, today), false, "367 days");
  assert.equal(U.isValidCustomRange(today, today - 1, today), false, "reversed");
  assert.equal(U.isValidCustomRange(NaN, today, today), false);
  assert.deepEqual(U.customRangeLimits(today), { min: today - 730, max: today, maxSpan: 366 });
});

// --- the date picker (DateRangeSelectionTests.cs) ------------------------------------------

const oct1 = day("2026-10-01");
const rangeOf = (maxSpan = 366, allowed = () => true) => U.createRangeSelection(false, maxSpan, allowed);

test("two plain clicks make a range in either order", () => {
  const selection = rangeOf();
  selection.click(oct1 + 9);
  assert.equal(selection.awaitingEnd, true);
  assert.equal(selection.from, oct1 + 9);
  assert.equal(selection.to, oct1 + 9);
  selection.click(oct1);
  assert.equal(selection.awaitingEnd, false);
  assert.equal(selection.from, oct1);
  assert.equal(selection.to, oct1 + 9);
});

test("a third click starts over", () => {
  const selection = rangeOf();
  selection.click(oct1);
  selection.click(oct1 + 3);
  selection.click(oct1 + 20);
  assert.equal(selection.from, oct1 + 20);
  assert.equal(selection.to, oct1 + 20);
  assert.equal(selection.awaitingEnd, true);
});

test("clicking the same day twice selects that day", () => {
  const selection = rangeOf();
  selection.click(oct1);
  selection.click(oct1);
  assert.equal(selection.from, oct1);
  assert.equal(selection.to, oct1);
  assert.equal(selection.awaitingEnd, false);
});

test("shift-click closes the range against the starting selection", () => {
  const selection = U.createRangeSelection(false, 366, () => true, oct1, oct1 + 2);
  selection.click(oct1 + 6, true);
  assert.equal(selection.from, oct1);
  assert.equal(selection.to, oct1 + 6);
});

test("days that are not allowed change nothing", () => {
  const selection = rangeOf(366, (value) => value <= oct1 + 5);
  selection.click(oct1);
  assert.equal(selection.click(oct1 + 6), false);
  assert.equal(selection.to, oct1);
  assert.equal(selection.awaitingEnd, true);
});

test("a range longer than the limit is pulled back to the anchor", () => {
  const selection = rangeOf(7);
  selection.click(oct1);
  selection.click(oct1 + 30);
  assert.equal(selection.from, oct1);
  assert.equal(selection.to, oct1 + 6);
});

test("single mode picks one day per click", () => {
  const selection = U.createRangeSelection(true, 1, () => true);
  selection.click(oct1);
  selection.click(oct1 + 4);
  assert.equal(selection.from, oct1 + 4);
  assert.equal(selection.to, oct1 + 4);
  assert.equal(selection.awaitingEnd, false);
});

test("the month grid has six full weeks from the first weekday", () => {
  // October 2026 starts on a Thursday.
  for (const [firstDay, start] of [[0, "2026-09-27"], [1, "2026-09-28"]]) {
    const grid = U.monthGrid(2026, 10, firstDay);
    assert.equal(grid.length, 42);
    assert.equal(grid[0], day(start));
    assert.equal(new Date(grid[0] * 86400000).getUTCDay(), firstDay);
    assert.ok(grid.includes(oct1));
    assert.ok(grid.includes(day("2026-10-31")));
  }
  assert.equal(U.monthTitle(day("2026-10-15")), "October 2026");
});

test("the Custom picker describes its selection like the desktop", () => {
  const today = day("2026-10-28");
  const selection = rangeOf();
  assert.equal(U.rangeSelectionText(U.createRangeSelection(false, 366, () => true), today), "No days picked");
  selection.click(day("2026-09-29"));
  assert.equal(U.rangeSelectionText(selection, today), "Tue, Sep 29, now pick the last day");
  selection.click(day("2026-10-28"));
  assert.equal(U.rangeSelectionText(selection, today), "Sep 29 to Oct 28 (30 days)");
  assert.equal(U.rangeSelectionText(U.createRangeSelection(false, 366, () => true, day("2026-10-04"), day("2026-10-04")), today),
    "Sun, Oct 4 (by hour)");
});

// --- the comparison picker (UsageWindow "Compare two periods") -------------------------------

function session(overrides = {}) {
  return U.createCompareSession({
    a: { from: day("2026-09-29"), to: day("2026-10-28") },
    b: { from: day("2026-08-30"), to: day("2026-09-28") },
    bIsPrevious: true,
    today: day("2026-10-28"),
    ...overrides,
  });
}

test("compare: A then B in four clicks, with the calendar moving on to B", () => {
  const picker = session();
  assert.equal(picker.editA, true);
  assert.equal(picker.hint(), "Editing period A. Underlined days are period B.");
  assert.deepEqual(picker.reference(), { from: day("2026-08-30"), to: day("2026-09-28") });
  assert.equal(picker.canApply(), true, "the defaults can be applied as they are");

  assert.deepEqual(picker.click(day("2026-10-20")), { changed: true, switched: false });
  assert.equal(picker.hint(), "Now click the last day of period A.");
  assert.equal(picker.canApply(), false, "never half a selection");
  assert.deepEqual(picker.click(day("2026-10-26")), { changed: true, switched: true });
  assert.deepEqual(picker.a, { from: day("2026-10-20"), to: day("2026-10-26") });
  // An untouched B is "the period before A" and follows it.
  assert.deepEqual(picker.b, { from: day("2026-10-13"), to: day("2026-10-19") });
  assert.equal(picker.editA, false);
  assert.equal(picker.hint(), "Editing period B. Underlined days are period A.");

  picker.click(day("2026-09-01"));
  picker.click(day("2026-09-07"));
  assert.deepEqual(picker.result(), {
    current: { from: day("2026-10-20"), to: day("2026-10-26") },
    comparison: { from: day("2026-09-01"), to: day("2026-09-07") },
  });
});

test("compare: untouched defaults keep their meaning on Apply", () => {
  // Nothing edited: the range on screen and "the period before" both stay.
  assert.deepEqual(session().result(), { current: null, comparison: null });
  // A picked comparison is handed back even when untouched.
  const picked = session({ b: { from: day("2026-09-01"), to: day("2026-09-07") }, bIsPrevious: false });
  assert.deepEqual(picked.result(), { current: null, comparison: { from: day("2026-09-01"), to: day("2026-09-07") } });
  // Only B edited: A stays as it is.
  const onlyB = session();
  onlyB.select(false);
  onlyB.click(day("2026-10-01"));
  onlyB.click(day("2026-10-02"));
  assert.deepEqual(onlyB.result(), { current: null, comparison: { from: day("2026-10-01"), to: day("2026-10-02") } });
});

test("compare: a first click alone is dropped when switching period", () => {
  const picker = session();
  picker.click(day("2026-10-10"));
  picker.select(false);
  assert.equal(picker.selection.awaitingEnd, false);
  assert.deepEqual(picker.a, { from: day("2026-09-29"), to: day("2026-10-28") });
  assert.equal(picker.aEdited, false);
});

test("compare: the picker respects the desktop limits", () => {
  const picker = session();
  assert.deepEqual(picker.click(day("2026-10-29")), { changed: false, switched: false }, "no future day");
  assert.deepEqual(picker.click(day("2026-10-28") - 731), { changed: false, switched: false }, "lookback");
  picker.click(day("2025-01-01"));
  picker.click(day("2026-10-28"));
  assert.equal(periodDays(picker.a), 366, "pulled back to 366 days");
});

function periodDays(period) {
  return period.to - period.from + 1;
}

test("phones hide the B and token columns, and Share without a comparison", () => {
  assert.equal(U.hiddenOnPhone("tokens", true), true);
  assert.equal(U.hiddenOnPhone("previousValue", true), true);
  assert.equal(U.hiddenOnPhone("previousTokens", true), true);
  assert.equal(U.hiddenOnPhone("change", true), false);
  assert.equal(U.hiddenOnPhone("value", true), false);
  assert.equal(U.hiddenOnPhone("label", true), false);
  assert.equal(U.hiddenOnPhone("share", false), true);
  assert.equal(U.hiddenOnPhone("tokens", false), false);
});

// --- aggregation and coverage -----------------------------------------------------------

test("aggregation sums totals, models, providers and accounts and follows the filter", () => {
  const data = prepared({
    days: [
      [20, 0, 0, ...metrics({ uncached: 100, cacheRead: 1000, output: 50, cost: 1500000, savings: 270000, requests: 2 })],
      [20, 1, 1, ...metrics({ uncached: 10, output: 5, cost: 62500 })],
      [21, 1, 2, ...metrics({ uncached: 400, unpriced: 400 })],
    ],
  });
  const period = U.resolvePeriod(data, { kind: "days", from: day("2026-10-20"), to: day("2026-10-27") });
  const all = U.aggregate(data, period);
  assert.equal(all.totals[M.cost], 1562500);
  assert.equal(all.totals[M.requests], 4);
  assert.equal(U.processedTokens(all.totals), 100 + 1000 + 50 + 10 + 5 + 400);
  assert.equal(all.byProvider.codex[M.unpricedTokens], 400);
  assert.deepEqual(Object.keys(all.byModel).sort(), ["0", "1", "2"]);
  assert.equal(all.points[day("2026-10-21") - period.from].metrics[M.cost], 1562500);
  assert.equal(all.activeCount, 2);
  const codexOnly = U.aggregate(data, period, { accounts: [1] });
  assert.equal(codexOnly.totals[M.cost], 62500);
  assert.equal(codexOnly.byProvider.claude[M.rows], 0);
  const oneModel = U.aggregate(data, period, { model: 0 });
  assert.equal(oneModel.totals[M.cost], 1500000);
});

test("days outside the shared report are missing, never zero, and labelled partial", () => {
  const data = prepared({
    coverage: { dailyFrom: "2026-10-10", dailyTo: "2026-10-27", hourlyFrom: null, hourlyTo: null },
    days: [[0, 0, 0, ...metrics({ output: 7, cost: 105 })]],
  });
  const period = U.resolvePeriod(data, { kind: "days", from: day("2026-10-01"), to: day("2026-10-30") });
  assert.equal(period.coverage.partial, true);
  assert.equal(period.coverage.from, day("2026-10-10"));
  assert.equal(period.coverage.to, day("2026-10-27"));
  const result = U.aggregate(data, period);
  assert.equal(result.points[0].missing, true, "before dailyFrom");
  assert.equal(result.points[9].missing, false);
  assert.equal(result.points[29].missing, true, "after dailyTo");
  const view = U.computeView(data, { period: { kind: "days", from: day("2026-10-01"), to: day("2026-10-30") }, comparison: "previous", accounts: null }, Date.parse("2026-10-27T12:00:00Z"));
  const chart = U.buildChart(view, "tokens");
  assert.ok(Number.isNaN(chart.series[0].values[0]), "a missing day is a gap on the chart");
  assert.equal(chart.series[0].values[9], 7);
  assert.equal(chart.partialIndex, day("2026-10-27") - day("2026-10-01"), "the report's last day is marked so far");
  const captions = U.buildCaptions(view);
  assert.equal(captions.rangePartial, true);
  assert.equal(captions.comparisonPartial, true);
  const notes = U.buildNotes(view, chart);
  assert.ok(notes.some((note) => note.startsWith("Partial data: A is covered only from Oct 10 to Oct 27")), notes.join(" | "));
  assert.ok(notes.some((note) => note.startsWith("Partial data: no usage was shared for B")), notes.join(" | "));
});

test("hours after the report was made are missing, the current one is so far", () => {
  const data = prepared({ ...londonHourly(), generatedAt: "2026-10-27T12:20:00Z" });
  const period = U.resolvePeriod(data, { kind: "days", from: day("2026-10-27"), to: day("2026-10-27") });
  assert.equal(period.partialIndex, 12);
  const result = U.aggregate(data, period);
  assert.equal(result.points[12].missing, false);
  assert.equal(result.points[13].missing, true);
});

// --- the Lord Howe straddle (contract 1.4), built through hourDayStarts ----------------------

function lordHowe() {
  const late = metrics({ uncached: 5000, cost: 15000 });   // 23:45 local on Oct 4 = 12:45Z
  const early = metrics({ uncached: 7000, cost: 21000 });  // 00:15 local on Oct 5 = 13:15Z
  const oct4 = 3;
  const oct5 = 4;
  return prepared({
    generatedAt: "2026-10-05T12:00:00Z",
    timeZone: "Australia/Lord_Howe",
    coverage: { dailyFrom: "2026-10-01", dailyTo: "2026-10-05", hourlyFrom: "2026-10-04", hourlyTo: "2026-10-05" },
    accounts: [{ name: "Personal", provider: "claude" }],
    models: [{ id: "claude-sonnet-4-5", provider: "claude", priced: true, price: PRICE }],
    // Oct 4 starts 13:30Z on Oct 3 (+10:30); Oct 5 and Oct 6 start at 13:00Z (+11).
    hourDayStarts: [minutes("2026-10-03T13:30:00Z"), minutes("2026-10-04T13:00:00Z"), minutes("2026-10-05T13:00:00Z")],
    days: [[oct4, 0, 0, ...late], [oct5, 0, 0, ...early]],
    hours: [
      { phaseMinutes: 0, rows: [[oct4, minutes("2026-10-04T12:00:00Z"), 0, 0, ...late], [oct5, minutes("2026-10-04T13:00:00Z"), 0, 0, ...early]] },
      { phaseMinutes: 30, rows: [[oct4, minutes("2026-10-04T12:30:00Z"), 0, 0, ...late], [oct5, minutes("2026-10-04T12:30:00Z"), 0, 0, ...early]] },
    ],
  });
}

test("Lord Howe: Oct 4 alone counts only its own sample in the 12:30Z bucket", () => {
  const data = lordHowe();
  const period = U.resolvePeriod(data, { kind: "days", from: day("2026-10-04"), to: day("2026-10-04") });
  assert.equal(period.phase, 30);
  assert.equal(period.slots.length, 24, "23.5 hours give 24 buckets, the last one short");
  const result = U.aggregate(data, period);
  const last = result.points[result.points.length - 1];
  assert.equal(last.key, minutes("2026-10-04T12:30:00Z"));
  assert.equal(last.metrics[M.uncached], 5000);
  assert.equal(result.totals[M.uncached], 5000);
  assert.equal(U.longHourLabel(last.key * 60000, data.zone, day("2026-10-05")), "Sun, Oct 4, 23:30");
});

test("Lord Howe: Oct 4 to Oct 5 puts both samples in the same 12:30Z bucket", () => {
  const data = lordHowe();
  const period = U.resolvePeriod(data, { kind: "days", from: day("2026-10-04"), to: day("2026-10-05") });
  const result = U.aggregate(data, period);
  const bucket = result.points.find((point) => point.key === minutes("2026-10-04T12:30:00Z"));
  assert.equal(bucket.metrics[M.uncached], 12000);
  assert.equal(bucket.metrics[M.rows], 2);
  assert.equal(result.totals[M.uncached], 12000);
});

test("Lord Howe: Oct 5 alone uses the whole-hour grid", () => {
  const data = lordHowe();
  const period = U.resolvePeriod(data, { kind: "days", from: day("2026-10-05"), to: day("2026-10-05") });
  assert.equal(period.phase, 0);
  const result = U.aggregate(data, period);
  assert.equal(result.points[0].key, minutes("2026-10-04T13:00:00Z"));
  assert.equal(result.points[0].metrics[M.uncached], 7000);
  assert.equal(result.totals[M.uncached], 7000);
});

// --- comparison alignment ------------------------------------------------------------

test("two hourly days match the comparison by clock across a daylight-saving change", () => {
  // A = Oct 18 to 19 (24 h days), B = Oct 25 to 26 (Oct 25 has 25 hours).
  const starts = [];
  for (let d = 18; d <= 27; d++) {
    starts.push(minutes(d <= 25 ? `2026-10-${d - 1}T23:00:00Z` : `2026-10-${d}T00:00:00Z`));
  }
  const bMidnight = minutes("2026-10-26T00:00:00Z");
  const data = prepared({
    generatedAt: "2026-10-27T12:00:00Z",
    coverage: { dailyFrom: "2026-10-01", dailyTo: "2026-10-27", hourlyFrom: "2026-10-18", hourlyTo: "2026-10-26" },
    hourDayStarts: starts,
    days: [[25, 0, 0, ...metrics({ output: 7, cost: 7000000 })]],
    hours: [{ phaseMinutes: 0, rows: [[25, bMidnight, 0, 0, ...metrics({ output: 7, cost: 7000000 })]] }],
  });
  const view = U.computeView(data, {
    period: { kind: "days", from: day("2026-10-18"), to: day("2026-10-19") },
    comparison: { from: day("2026-10-25"), to: day("2026-10-26") },
    accounts: null,
  }, Date.parse("2026-10-27T12:00:00Z"));
  assert.equal(view.primary.slots.length, 48);
  const chart = U.buildChart(view, "cost");
  const values = chart.comparisonSeries[0].values;
  assert.equal(values[24], 7, "B's 26th at 00:00 faces A's day-two midnight");
  assert.equal(values.filter((value) => Number.isFinite(value)).reduce((sum, value) => sum + value, 0), 7);
  assert.equal(chart.comparisonLabels[24], "OCT 26 00:00");
});

test("the rolling window compares hour for hour with the 24 hours before it", () => {
  const generatedAt = "2026-10-27T12:00:00Z";
  const windows = rolling(generatedAt);
  windows.current.rows = [[23, 0, 0, ...metrics({ output: 10, cost: 2000000 })]];
  windows.previous.rows = [[23, 0, 0, ...metrics({ output: 10, cost: 1000000 })]];
  const data = prepared({ generatedAt, rolling: windows });
  const view = U.computeView(data, { period: { kind: "rolling" }, comparison: "previous", accounts: null }, Date.parse(generatedAt));
  const chart = U.buildChart(view, "cost");
  assert.equal(chart.series[0].values[23], 2);
  assert.equal(chart.comparisonSeries[0].values[23], 1);
  assert.equal(chart.partialIndex, null, "the rolling window has nothing in progress");
  const hero = U.buildHero(view, "cost");
  assert.equal(hero.value, "$2.00");
  assert.equal(hero.deltaText, "B → A ▲ 100%");
  assert.equal(U.buildCaptions(view).asOf, "Past 24 hours as of Oct 27, 12:00");
});

test("hero, tiles, breakdown and model details follow the desktop window", () => {
  const data = prepared({
    days: [
      [19, 0, 0, ...metrics({ uncached: 1000000, cacheRead: 9000000, output: 100000, reasoning: 20000, cost: 5700000, savings: 2430000 })],
      [26, 0, 0, ...metrics({ uncached: 2000000, cacheRead: 18000000, w5: 400000, output: 300000, reasoning: 50000, cost: 18900000, savings: 4860000, requests: 3 })],
      [26, 1, 2, ...metrics({ uncached: 500000, unpriced: 500000 })],
    ],
  });
  const now = Date.parse("2026-10-27T12:00:00Z");
  const selection = U.selectionFor({ range: "7", compare: "previous" }, data, now);
  const view = U.computeView(data, selection, now);
  const hero = U.buildHero(view, "cost");
  assert.equal(hero.value, "$18.90");
  assert.equal(hero.previousText, "B: $5.70");
  assert.equal(hero.unpricedNote, "excludes 1 unpriced model");
  assert.equal(hero.providers[0].name, "Claude Code");
  assert.equal(hero.providers[0].caption, "100.0% of estimate · 20.7M tokens");
  assert.equal(hero.providers[1].valueText, "unpriced");
  const tokensHero = U.buildHero(view, "tokens");
  assert.equal(tokensHero.label, "Processed tokens");
  assert.equal(tokensHero.note, "$18.90 estimated API value");

  const tiles = U.buildTiles(view);
  assert.deepEqual(tiles.map((tile) => tile.caption), ["Processed tokens", "Cached input", "Uncached input", "Output", "Cache savings"]);
  assert.equal(tiles[0].value, "21.2M");
  assert.equal(tiles[0].footnote, "21.2M per active day");
  assert.equal(tiles[3].footnote, "includes 50.0K reasoning");
  assert.equal(tiles[4].value, "$4.86");

  const table = U.buildBreakdown(view, "model", { column: "value", descending: true }, { column: "label", descending: true });
  assert.equal(table.showComparison, true);
  assert.equal(table.changeHeader, "Change B → A");
  assert.deepEqual(table.rows.map((row) => row.label), ["claude-sonnet-4-5", "mystery"]);
  assert.equal(table.rows[1].valueText, "unpriced");
  assert.equal(table.rows[1].changeText, "no prior data");

  const byDay = U.buildBreakdown(view, "time", { column: "value", descending: true }, { column: "label", descending: true });
  assert.deepEqual(byDay.rows.map((row) => row.label), ["Tue, Oct 27"]);
  assert.equal(byDay.rows[0].drillDay, null, "no hourly data shared, so the day cannot be opened");

  const detail = U.buildModelDetail(view, 0, "cost");
  assert.equal(detail.costText, "$18.90");
  assert.equal(detail.perMillionText, "$0.91");
  assert.equal(detail.cacheHitText, "88.2%");
  assert.equal(detail.costDeltaText, "B: $5.70 · ▲ 232%");
  assert.deepEqual(detail.costSplit.map((part) => part.text), ["$6.00", "$5.40", "$1.50", "$4.50"]);
  assert.deepEqual(detail.tokenSplit.map((part) => part.text), ["2.00M", "18.0M", "400K", "300K"]);
  const unpriced = U.buildModelDetail(view, 2, "cost");
  assert.equal(unpriced.costText, "unpriced");
  assert.equal(unpriced.metric, "tokens");
  assert.deepEqual(unpriced.costSplit, []);
});

test("an empty report says so instead of showing zeros as usage", () => {
  const data = prepared({ days: [[2, 0, 0, ...metrics({ output: 1 })]] });
  const view = U.computeView(data, { period: { kind: "days", from: day("2026-10-20"), to: day("2026-10-27") }, comparison: null, accounts: null }, Date.parse("2026-10-27T12:00:00Z"));
  assert.equal(U.buildEmptyStatus(view), "No local agent usage in this range. Recorded usage: Sat, Oct 3.");
});

// --- discovery and freshness (contract P12, P16) ----------------------------------------------

test("the usage flag drives fetching, retrying and clearing", () => {
  const idle = { generation: 0, displayedSeq: null, inFlightSeq: null };
  assert.equal(U.usageSyncAction(idle, undefined), "clear");
  assert.equal(U.usageSyncAction(idle, { shared: false }), "clear");
  assert.equal(U.usageSyncAction(idle, { shared: true, seq: null }), "none");
  assert.equal(U.usageSyncAction(idle, { shared: true, seq: 5 }), "fetch");
  assert.equal(U.usageSyncAction({ ...idle, inFlightSeq: 5 }, { shared: true, seq: 5 }), "none", "one request per seq at a time");
  assert.equal(U.usageSyncAction({ ...idle, inFlightSeq: 5 }, { shared: true, seq: 6 }), "fetch", "a newer seq starts a newer request");
  assert.equal(U.usageSyncAction({ ...idle, displayedSeq: 5 }, { shared: true, seq: 5 }), "none");
  assert.equal(U.usageSyncAction({ ...idle, displayedSeq: 5 }, { shared: true, seq: 6 }), "fetch");
  // A 404 or a failure clears inFlightSeq, so the next poll asks again.
  assert.equal(U.usageSyncAction(idle, { shared: true, seq: 5 }), "fetch");
});

test("stale or superseded responses never replace what is on screen", () => {
  const sync = { generation: 3, displayedSeq: 8, inFlightSeq: 9 };
  assert.equal(U.acceptUsageResponse(sync, 2, 9), false, "an older request");
  assert.equal(U.acceptUsageResponse(sync, 3, 7), false, "a lower seq than displayed");
  assert.equal(U.acceptUsageResponse(sync, 3, 8), true);
  assert.equal(U.acceptUsageResponse(sync, 3, 9), true);
  assert.equal(U.acceptUsageResponse({ generation: 1, displayedSeq: null }, 1, 1), true);
});

test("freshness comes from checkedAt, with a stale banner past 30 minutes", () => {
  const data = prepared({ generatedAt: "2026-10-27T08:00:00Z" });
  const now = Date.parse("2026-10-27T12:00:00Z");
  const fresh = U.usageFreshness({ shared: true, seq: 1, checkedAt: "2026-10-27T11:55:00Z" }, data, now);
  assert.equal(fresh.updated, "Usage report updated 5m ago");
  assert.equal(fresh.stale, false);
  const stale = U.usageFreshness({ shared: true, seq: 1, checkedAt: "2026-10-27T11:00:00Z" }, data, now);
  assert.equal(stale.stale, true);
  const fallback = U.usageFreshness({ shared: true, seq: 1, checkedAt: null }, data, now);
  assert.equal(fallback.updated, "Usage report updated 4h 0m ago");
});

test("R5: a new seq's checkedAt never refreshes the older report on screen", () => {
  const data = prepared({ seq: 10, generatedAt: "2026-10-27T08:00:00Z" });
  const now = Date.parse("2026-10-27T12:00:00Z");
  // Seq 10 on screen, confirmed at 11:00.
  let checkedAt = U.confirmedCheckedAt(null, { shared: true, seq: 10, checkedAt: "2026-10-27T11:00:00Z" }, 10);
  assert.equal(checkedAt, "2026-10-27T11:00:00Z");
  // A snapshot announces seq 11, checked at 11:59; its report has not loaded
  // (or its GET failed). Seq 10 keeps its own time and stays stale.
  checkedAt = U.confirmedCheckedAt(checkedAt, { shared: true, seq: 11, checkedAt: "2026-10-27T11:59:00Z" }, 10);
  assert.equal(checkedAt, "2026-10-27T11:00:00Z");
  const shown = U.usageFreshness({ checkedAt }, data, now);
  assert.equal(shown.updated, "Usage report updated 1h 0m ago");
  assert.equal(shown.stale, true, "the stale banner stays up");
  // Without any confirmation of its own, the report's generatedAt is used.
  assert.equal(U.confirmedCheckedAt(null, { shared: true, seq: 11, checkedAt: "2026-10-27T11:59:00Z" }, 10), null);
  assert.equal(U.usageFreshness({ checkedAt: null }, data, now).updated, "Usage report updated 4h 0m ago");
  // Once seq 11 is the one on screen, its checkedAt applies.
  assert.equal(U.confirmedCheckedAt(null, { shared: true, seq: 11, checkedAt: "2026-10-27T11:59:00Z" }, 11), "2026-10-27T11:59:00Z");
  // Nothing on screen, or an unreadable time: nothing is confirmed.
  assert.equal(U.confirmedCheckedAt(null, { shared: true, seq: 11, checkedAt: "2026-10-27T11:59:00Z" }, null), null);
  assert.equal(U.confirmedCheckedAt("2026-10-27T11:00:00Z", { shared: true, seq: 10, checkedAt: "soon" }, 10), "2026-10-27T11:00:00Z");
});

test("R7: reports with day starts or instants a Date cannot hold are refused", () => {
  // The valid London report is accepted.
  assert.ok(U.prepareUsage(payload(londonHourly())));
  // A start at 1e15 minutes with 1440-minute steps: increasing, but past the Date range.
  const huge = londonHourly();
  huge.hourDayStarts = huge.hourDayStarts.map((_, index) => 1e15 + index * 1440);
  assert.equal(U.prepareUsage(payload(huge)), null);
  // The first start must lie within 15 hours of the UTC midnight of hourlyFrom.
  const shifted = londonHourly();
  // London's first start is 23:00Z, an hour before UTC midnight: +17 h is 16 h past it.
  shifted.hourDayStarts = shifted.hourDayStarts.map((value) => value + 17 * 60);
  assert.equal(U.prepareUsage(payload(shifted)), null);
  const edge = londonHourly();
  edge.hourDayStarts = edge.hourDayStarts.map((value) => value - 14 * 60);
  assert.ok(U.prepareUsage(payload(edge)), "a +15 h zone's midnight is still accepted");
  // Not increasing, not whole minutes, or the wrong count.
  const flat = londonHourly();
  flat.hourDayStarts[3] = flat.hourDayStarts[2];
  assert.equal(U.prepareUsage(payload(flat)), null);
  const fractional = londonHourly();
  fractional.hourDayStarts[1] += 0.5;
  assert.equal(U.prepareUsage(payload(fractional)), null);
  const short = londonHourly();
  short.hourDayStarts.pop();
  assert.equal(U.prepareUsage(payload(short)), null);
  // Rolling or generatedAt instants outside the Date range.
  const windows = rolling("2026-10-27T12:00:00Z");
  windows.current.since = "+275760-09-13T00:00:01Z";
  assert.equal(U.prepareUsage(payload({ rolling: windows })), null);
  assert.equal(U.prepareUsage({ ...payload({}), generatedAt: "+275760-09-13T00:00:01Z" }), null);
  // A rolling window that is not exactly 24 hours.
  const longWindow = rolling("2026-10-27T12:00:00Z");
  longWindow.previous.since = "2026-10-25T11:00:00Z";
  assert.equal(U.prepareUsage(payload({ rolling: longWindow })), null);
});

test("R7: an accepted report renders every preset without throwing", () => {
  const data = prepared({
    ...londonHourly(),
    days: [[24, 0, 0, ...metrics({ output: 10, cost: 1000000 })]],
    hours: [{ phaseMinutes: 0, rows: [[24, minutes("2026-10-25T09:00:00Z"), 0, 0, ...metrics({ output: 10, cost: 1000000 })]] }],
  });
  const now = Date.parse("2026-10-27T12:00:00Z");
  for (const range of ["24h", "7", "30", "90"]) {
    for (const compare of ["off", "previous"]) {
      const view = U.computeView(data, U.selectionFor({ range, compare }, data, now), now);
      const chart = U.buildChart(view, "cost");
      U.buildHero(view, "tokens");
      U.buildTiles(view);
      U.buildBreakdown(view, "time", { column: "value", descending: true }, { column: "label", descending: true });
      U.buildNotes(view, chart);
      for (let index = 0; index < chart.count; index++) U.tooltipFor(chart, index);
    }
  }
});

test("unreadable payloads are refused", () => {
  assert.equal(U.prepareUsage(null), null);
  assert.equal(U.prepareUsage({ ...payload({}), version: 2 }), null);
  assert.equal(U.prepareUsage({ ...payload({}), timeZone: "Not/AZone" }), null);
  assert.equal(U.prepareUsage({ ...payload({}), seq: 0 }), null);
});

// --- cross-check with the desktop aggregator (contract section 5) ------------------------------

const TOTAL_KEYS = ["uncached", "cacheRead", "cacheWrite5m", "cacheWrite1h", "output", "reasoning", "costMicroUsd", "savingsMicroUsd", "requests", "unpricedTokens"];
const MONEY = new Set(["costMicroUsd", "savingsMicroUsd"]);

// Cost and savings may differ by at most the number of payload buckets
// summed: the desktop sums exact decimals, the payload rounded micro-dollars.
function assertTotals(actual, expected, where) {
  TOTAL_KEYS.forEach((key, index) => {
    if (MONEY.has(key)) {
      const slack = actual[M.rows];
      assert.ok(Math.abs(actual[index] - expected[key]) <= slack,
        `${where}: ${key} ${actual[index]} vs ${expected[key]} (slack ${slack})`);
    } else {
      assert.equal(actual[index], expected[key], `${where}: ${key}`);
    }
  });
}

function pointKey(start) {
  return /^\d{4}-\d{2}-\d{2}$/.test(start) ? U.parseDay(start) : Date.parse(start) / 60000;
}

function assertExpected(data, resolved, accounts, expected, where) {
  const result = U.aggregate(data, resolved, { accounts });
  assertTotals(result.totals, expected.totals, `${where} totals`);
  assert.equal(result.points.length, expected.points.length, `${where} point count`);
  expected.points.forEach((point, index) => {
    assert.equal(result.points[index].key, pointKey(point.start), `${where} point ${index} start`);
    assertTotals(result.points[index].metrics, point.totals, `${where} point ${point.start}`);
  });
  const models = Object.keys(result.byModel).map(Number).filter((index) => result.byModel[index][M.rows] > 0).sort((a, b) => a - b);
  assert.deepEqual(models, expected.byModel.map((entry) => entry.modelIdx), `${where} models`);
  expected.byModel.forEach((entry) => assertTotals(result.byModel[entry.modelIdx], entry.totals, `${where} model ${entry.modelIdx}`));
}

const fixtureMissing = !existsSync(FIXTURE);

test("cross-check: the viewer reproduces the desktop aggregator", { skip: fixtureMissing && "tests/fixtures/remote-usage/crosscheck.json is missing; the desktop part writes it" }, () => {
  const file = JSON.parse(readFileSync(FIXTURE, "utf8"));
  const fixtures = Array.isArray(file.fixtures) ? file.fixtures : [file];
  assert.ok(fixtures.length > 0, "the fixture holds at least one zone");
  let cases = 0;
  for (const fixture of fixtures) {
    const data = U.prepareUsage(fixture.payload);
    assert.ok(data, `${fixture.zone}: payload is readable`);
    for (const entry of fixture.cases) {
      const where = `${fixture.zone} / ${entry.name}`;
      const period = entry.period.kind === "rolling"
        ? { kind: "rolling" }
        : { kind: "days", from: U.parseDay(entry.period.from), to: U.parseDay(entry.period.to) };
      const comparison = entry.comparison === null || entry.comparison === undefined
        ? null
        : entry.comparison === "previous"
          ? "previous"
          : { from: U.parseDay(entry.comparison.from), to: U.parseDay(entry.comparison.to) };
      const view = U.computeView(data, { period, comparison, accounts: entry.accounts }, data.generatedAt);
      assertExpected(data, view.primary, entry.accounts, entry.expected, where);
      if (entry.expected.comparison) {
        assert.ok(view.comparison, `${where}: a comparison was resolved`);
        assertExpected(data, view.comparison, entry.accounts, entry.expected.comparison, `${where} (B)`);
      }
      cases++;
    }
  }
  assert.ok(cases > 0);
});

// --- chart curves stay inside the plot (review D1) ------------------------------

function pathYs(d) {
  // "M x y", "L x y" and "C x1 y1 x2 y2 x y": every second number is a y.
  const numbers = d.match(/-?\d+(?:\.\d+)?/g).map(Number);
  return numbers.filter((_, index) => index % 2 === 1);
}

test("D1: smooth chart curves never leave the plot, top or bottom", () => {
  const top = 14;
  const bottom = 200;
  const x = (index) => 40 + index * 100;
  const y = (value) => bottom - (value / 100) * (bottom - top);
  const shapes = [
    [0, 100, 100, 0],      // two points at the ceiling: no peak above it
    [100, 0, 0, 100],      // two points at zero: no dip below it
    [0, 100, 0, 100, 0],   // a zigzag between both bounds
    [50, 100, 100, 100, 50],
    [0, 0, 5, 100, 100, 0],
  ];
  for (const values of shapes) {
    const d = U.smoothLinePath(values.map((value, index) => [x(index), y(value)]), top, bottom);
    for (const value of pathYs(d)) {
      assert.ok(value >= top && value <= bottom, `${values.join(",")}: y ${value} outside [${top}, ${bottom}] in ${d}`);
    }
  }
  // The reviewer's case: the curve must not reach above the ceiling (it reached -18.47).
  const ceiling = U.smoothLinePath([0, 100, 100, 0].map((value, index) => [x(index), y(value)]), top, bottom);
  assert.equal(Math.min(...pathYs(ceiling)), top);
  // Two points are a straight segment.
  assert.equal(U.smoothLinePath([[0, 50], [10, 60]], top, bottom), "M0.0 50.0 L10.0 60.0");
});

test("phone sheets wrap Tab and Shift+Tab inside, and take focus in at the right end", () => {
  assert.equal(U.wrapFocusIndex(4, 5, false), 0, "Tab on the last control goes to the first");
  assert.equal(U.wrapFocusIndex(0, 5, true), 4, "Shift+Tab on the first goes to the last");
  assert.equal(U.wrapFocusIndex(2, 5, false), null, "in the middle the browser order is right");
  assert.equal(U.wrapFocusIndex(2, 5, true), null);
  assert.equal(U.wrapFocusIndex(-1, 5, false), 0, "from outside, Tab enters at the first");
  assert.equal(U.wrapFocusIndex(-1, 5, true), 4, "from outside, Shift+Tab enters at the last");
  assert.equal(U.wrapFocusIndex(0, 1, false), 0, "a single control keeps focus");
  assert.equal(U.wrapFocusIndex(0, 0, false), null);
});
