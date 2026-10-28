// AI Usage Tray - remote view, Usage tab.
//
// Reads the usage report the desktop app shares (GET /u/{readId}/usage, payload
// version 1) and shows it like the desktop Usage window: range presets, account
// filter, comparison, cost or tokens, chart, tiles, breakdown and model details.
//
// The top half is pure logic and is exported for Node tests through
// module.exports. The bottom half (createController) is the DOM layer; app.js
// creates it and feeds it every quota snapshot so it can discover the report.
//
// Time-zone rule: the viewer never works out a local midnight on its own. Day
// starts come from the payload's hourDayStarts; Intl is used only to read the
// local clock of an instant (labels, "today", clock-hour matching).

var UsageView = (function () {
  "use strict";

  // --- constants ---------------------------------------------------------

  // Metric columns of every payload row, after its key columns.
  var UNCACHED = 0;
  var CACHE_READ = 1;
  var WRITE_5M = 2;
  var WRITE_1H = 3;
  var OUTPUT = 4;
  var REASONING = 5;
  var COST = 6;       // micro-USD
  var SAVINGS = 7;    // micro-USD, signed
  var REQUESTS = 8;
  var UNPRICED = 9;
  var METRIC_COUNT = 10;
  // Not a payload column: how many payload rows were summed into a bucket.
  // Lets a test bound the rounding drift and tells "no data" from "zero".
  var ROWS = 10;

  var DAY_MS = 86400000;
  var MINUTE_MS = 60000;
  var MAX_HOURLY_DAYS = 3;
  var MAX_CUSTOM_DAYS = 366;
  var STALE_MS = 30 * 60000;
  var PROVIDERS = ["claude", "codex"];
  var PROVIDER_NAMES = { claude: "Claude Code", codex: "Codex" };
  var MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  var WEEKDAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  var SPLIT_LABELS = ["Input", "Cache read", "Cache write", "Output"];
  var ARROW = "\u2192";

  var DELTA_NONE = { kind: "none", fraction: 0, basis: "cost" };
  var DEFAULT_DIRECTION = { currentIsEarlier: false, scale: 1, label: "B " + ARROW + " A" };

  // --- metrics -------------------------------------------------------------

  function emptyMetrics() {
    return [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
  }

  function addRow(target, row, offset) {
    for (var i = 0; i < METRIC_COUNT; i++) target[i] += row[offset + i];
    target[ROWS] += 1;
  }

  function addMetrics(target, source) {
    for (var i = 0; i <= ROWS; i++) target[i] += source[i];
  }

  function inputTokens(m) {
    return m[UNCACHED] + m[CACHE_READ] + m[WRITE_5M] + m[WRITE_1H];
  }

  function processedTokens(m) {
    return inputTokens(m) + m[OUTPUT];
  }

  function cacheWriteTokens(m) {
    return m[WRITE_5M] + m[WRITE_1H];
  }

  // The figure a chart or a share follows: dollars, or processed tokens.
  function metricValue(m, metric) {
    return metric === "tokens" ? processedTokens(m) : m[COST] / 1e6;
  }

  function hasData(m) {
    return m[ROWS] > 0;
  }

  // --- calendar days (pure arithmetic, no time zone) ------------------------

  // "2026-10-04" -> days since 1970-01-01, or NaN.
  function parseDay(text) {
    var match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(typeof text === "string" ? text : "");
    if (!match) return NaN;
    var year = Number(match[1]);
    var month = Number(match[2]) - 1;
    var date = Number(match[3]);
    var value = new Date(Date.UTC(year, month, date));
    if (value.getUTCFullYear() !== year || value.getUTCMonth() !== month || value.getUTCDate() !== date) {
      return NaN;
    }
    return value.getTime() / DAY_MS;
  }

  function dayToIso(day) {
    return new Date(day * DAY_MS).toISOString().slice(0, 10);
  }

  function dayParts(day) {
    var value = new Date(day * DAY_MS);
    return {
      year: value.getUTCFullYear(),
      month: value.getUTCMonth(),
      date: value.getUTCDate(),
      weekday: value.getUTCDay()
    };
  }

  function pad2(value) {
    return value < 10 ? "0" + value : String(value);
  }

  // "Jul 25"
  function dayLabel(day) {
    var parts = dayParts(day);
    return MONTHS[parts.month] + " " + parts.date;
  }

  // "JUL 25"
  function axisDayLabel(day) {
    return dayLabel(day).toUpperCase();
  }

  // "Sat, Aug 23", with the year when it is not today's: "Wed, Oct 1, 2025".
  function longDayLabel(day, today) {
    var parts = dayParts(day);
    var label = WEEKDAYS[parts.weekday] + ", " + MONTHS[parts.month] + " " + parts.date;
    if (today !== undefined && today !== null && dayParts(today).year !== parts.year) {
      label += ", " + parts.year;
    }
    return label;
  }

  // "Jul 25 to Aug 23"; "Dec 20, 2025 to Jan 5, 2026" outside today's year;
  // "Sat, Aug 23" for one day.
  function rangeLabel(from, to, today) {
    if (today === undefined || today === null) return dayLabel(from) + " to " + dayLabel(to);
    var year = dayParts(today).year;
    var a = dayParts(from);
    var b = dayParts(to);
    var withYear = a.year !== year || b.year !== year;
    if (from === to) {
      return WEEKDAYS[a.weekday] + ", " + MONTHS[a.month] + " " + a.date + (withYear ? ", " + a.year : "");
    }
    return withYear
      ? dayLabel(from) + ", " + a.year + " to " + dayLabel(to) + ", " + b.year
      : dayLabel(from) + " to " + dayLabel(to);
  }

  // --- the desktop's zone (Intl reads the clock; it never builds a midnight) --

  var clockCache = {};

  // A function that turns an instant into the desktop's local wall clock:
  // { day, hour, minute }.
  function zoneClock(zone) {
    if (clockCache[zone]) return clockCache[zone];
    var format = new Intl.DateTimeFormat("en-US", {
      timeZone: zone,
      hourCycle: "h23",
      year: "numeric",
      month: "numeric",
      day: "numeric",
      hour: "numeric",
      minute: "numeric"
    });
    var clock = function (ms) {
      var parts = format.formatToParts(new Date(ms));
      var values = {};
      for (var i = 0; i < parts.length; i++) values[parts[i].type] = parts[i].value;
      return {
        day: Date.UTC(Number(values.year), Number(values.month) - 1, Number(values.day)) / DAY_MS,
        hour: Number(values.hour) % 24,
        minute: Number(values.minute)
      };
    };
    clockCache[zone] = clock;
    return clock;
  }

  function isValidZone(zone) {
    if (typeof zone !== "string" || !zone) return false;
    try {
      zoneClock(zone)(0);
      return true;
    } catch (error) {
      return false;
    }
  }

  // The local calendar day of an instant in the desktop's zone.
  function todayIn(zone, nowMs) {
    return zoneClock(zone)(nowMs).day;
  }

  function sameClock(a, b) {
    return a.day === b.day && a.hour === b.hour && a.minute === b.minute;
  }

  // The zone's UTC offset at an instant, in minutes.
  function offsetMinutes(zone, ms) {
    var local = zoneClock(zone)(ms);
    var wall = local.day * DAY_MS + local.hour * 3600000 + local.minute * MINUTE_MS;
    return Math.round((wall - Math.floor(ms / MINUTE_MS) * MINUTE_MS) / MINUTE_MS);
  }

  // True when the wall-clock time of this instant happens twice (a
  // daylight-saving fall back), so a label needs its offset to be unique.
  function isRepeatedLocalTime(zone, ms) {
    var clock = zoneClock(zone);
    var local = clock(ms);
    var shifts = [-60, -30, 30, 60];
    for (var i = 0; i < shifts.length; i++) {
      if (sameClock(local, clock(ms + shifts[i] * MINUTE_MS))) return true;
    }
    return false;
  }

  function clockText(local) {
    return pad2(local.hour) + ":" + pad2(local.minute);
  }

  // "14:37"
  function axisHourLabel(ms, zone) {
    return clockText(zoneClock(zone)(ms));
  }

  // "OCT 18 14:00"
  function axisDayHourLabel(ms, zone) {
    var local = zoneClock(zone)(ms);
    return axisDayLabel(local.day) + " " + clockText(local);
  }

  // "Tue, Sep 23, 14:37"; the year outside today's; "(UTC+3)" on a repeated hour.
  function longHourLabel(ms, zone, today) {
    var local = zoneClock(zone)(ms);
    var parts = dayParts(local.day);
    var label = WEEKDAYS[parts.weekday] + ", " + MONTHS[parts.month] + " " + parts.date;
    if (today !== undefined && today !== null && dayParts(today).year !== parts.year) {
      label += ", " + parts.year;
    }
    label += ", " + clockText(local);
    if (isRepeatedLocalTime(zone, ms)) {
      var offset = offsetMinutes(zone, ms);
      var sign = offset < 0 ? "-" : "+";
      var hours = Math.floor(Math.abs(offset) / 60);
      var minutes = Math.abs(offset) % 60;
      label += " (UTC" + sign + hours + (minutes ? ":" + pad2(minutes) : "") + ")";
    }
    return label;
  }

  // "Oct 27, 10:00"
  function shortInstantLabel(ms, zone) {
    var local = zoneClock(zone)(ms);
    return dayLabel(local.day) + ", " + clockText(local);
  }

  // "Sep 22, 14:37 to Sep 23, 14:37"
  function rollingRangeLabel(sinceMs, untilMs, zone) {
    return shortInstantLabel(sinceMs, zone) + " to " + shortInstantLabel(untilMs, zone);
  }

  // --- number formatting (identical to UsageNumberFormat) -------------------

  function group(integer) {
    return String(integer).replace(/\B(?=(\d{3})+(?!\d))/g, ",");
  }

  function tokenDecimals(scaled) {
    return scaled >= 100 ? 0 : scaled >= 10 ? 1 : 2;
  }

  // "0", "348", "16.5K", "106M", "7.90B", "13.3B".
  function formatTokens(tokens) {
    if (!(tokens > 0)) return "0";
    if (tokens < 1000) return String(Math.round(tokens));
    var scaled;
    var suffix;
    if (tokens >= 1e9) {
      scaled = tokens / 1e9;
      suffix = "B";
    } else if (tokens >= 1e6) {
      scaled = tokens / 1e6;
      suffix = "M";
    } else {
      scaled = tokens / 1e3;
      suffix = "K";
    }
    // Rounding can push a value over its own unit (999,600 -> "1000K").
    var decimals = tokenDecimals(scaled);
    var factor = Math.pow(10, decimals);
    if (suffix !== "B" && Math.round(scaled * factor) / factor >= 1000) {
      scaled /= 1000;
      suffix = suffix === "K" ? "M" : "B";
    }
    return scaled.toFixed(tokenDecimals(scaled)) + suffix;
  }

  // An exact amount from micro-USD: "$0.00", "$1,152.15", "-$3.10".
  // Cents round half away from zero, like the desktop's decimal formatting.
  function formatMoneyMicro(micro) {
    var value = Math.round(Number(micro) || 0);
    var negative = value < 0;
    var abs = Math.abs(value);
    var cents = Math.floor(abs / 10000);
    if (abs - cents * 10000 >= 5000) cents += 1;
    var dollars = Math.floor(cents / 100);
    return (negative && cents > 0 ? "-" : "") + "$" + group(dollars) + "." + pad2(cents % 100);
  }

  function formatMoney(usd) {
    return formatMoneyMicro(Math.round(Number(usd) * 1e6));
  }

  // Nothing that cost money is written as $0.00: all-unpriced usage says so.
  function costOrUnpriced(micro, unpricedTokens) {
    return micro === 0 && unpricedTokens > 0 ? "unpriced" : formatMoneyMicro(micro);
  }

  function axisCost(usd) {
    return usd === 0 ? "0" : formatMoney(usd);
  }

  function axisTokens(tokens) {
    return tokens <= 0 ? "0" : formatTokens(tokens);
  }

  function formatFraction(fraction) {
    if (typeof fraction !== "number" || !isFinite(fraction)) return "0.0%";
    return (fraction * 100).toFixed(1) + "%";
  }

  // "48.8%"; nothing to divide into is "0.0%".
  function formatPercent(part, whole) {
    return !(whole > 0) ? "0.0%" : formatFraction(part / whole);
  }

  // "6.7x"
  function formatMultiplier(part, whole) {
    return !(whole > 0) ? "0.0x" : (part / whole).toFixed(1) + "x";
  }

  // "▲ 12.4%", "▼ 3.0%", "0.0%", "▲ 1,200% tokens", "no prior data", "".
  function formatDelta(delta, showBasis) {
    if (!delta) return "";
    var suffix = showBasis !== false && delta.basis === "tokens" ? " tokens" : "";
    if (delta.kind === "noPriorData") return "no prior data";
    if (delta.kind !== "change") return "";
    var percent = Math.abs(delta.fraction) * 100;
    var magnitude = (percent >= 100 ? group(Math.round(percent)) : percent.toFixed(1)) + "%";
    if (magnitude === "0.0%" || magnitude === "0%") return "0.0%" + suffix;
    return (delta.fraction > 0 ? "\u25B2 " : "\u25BC ") + magnitude + suffix;
  }

  // "B: $258.60 · ▼ 75.6%"
  function withPrevious(previous, delta) {
    return delta ? "B: " + previous + " \u00B7 " + delta : "B: " + previous;
  }

  // "Updated 5m ago" style durations.
  function formatAge(ms) {
    var minutes = Math.floor(Math.max(0, ms) / MINUTE_MS);
    if (minutes < 1) return "just now";
    var days = Math.floor(minutes / 1440);
    var hours = Math.floor((minutes % 1440) / 60);
    if (days > 0) return days + "d " + hours + "h ago";
    if (hours > 0) return hours + "h " + (minutes % 60) + "m ago";
    return minutes + "m ago";
  }

  // --- deltas and comparison direction (UsageDelta, UsageChangeDirection) ----

  function deltaOf(current, previous, basis) {
    current = Math.max(0, current);
    previous = Math.max(0, previous);
    if (previous <= 0) {
      return current <= 0 ? DELTA_NONE : { kind: "noPriorData", fraction: 0, basis: basis };
    }
    return { kind: "change", fraction: (current - previous) / previous, basis: basis };
  }

  // Cost when both sides are fully priced, processed tokens otherwise.
  function deltaBetween(current, previous, previousScale) {
    var scale = previousScale === undefined ? 1 : previousScale;
    var useTokens = current[UNPRICED] > 0 || previous[UNPRICED] > 0 ||
      (current[COST] === 0 && previous[COST] === 0);
    return useTokens
      ? deltaOf(processedTokens(current), processedTokens(previous) * scale, "tokens")
      : deltaOf(current[COST], previous[COST] * scale, "cost");
  }

  function compareKeys(a, b) {
    return a[0] !== b[0] ? a[0] - b[0] : a[1] - b[1];
  }

  // Changes always run from the earlier period to the later one, scaled to an
  // average per day when the lengths differ.
  function changeDirection(current, comparison) {
    var start = compareKeys(current.startKey, comparison.startKey);
    var earlier = start !== 0 ? start < 0 : compareKeys(current.endKey, comparison.endKey) < 0;
    var scale = comparison.dayCount > 0 ? current.dayCount / comparison.dayCount : 1;
    return { currentIsEarlier: earlier, scale: scale, label: earlier ? "A " + ARROW + " B" : "B " + ARROW + " A" };
  }

  function inverseScale(direction) {
    return direction.scale > 0 ? 1 / direction.scale : 1;
  }

  function directionBetween(direction, current, previous) {
    return direction.currentIsEarlier
      ? deltaBetween(previous, current, inverseScale(direction))
      : deltaBetween(current, previous, direction.scale);
  }

  function directionOfTokens(direction, current, previous) {
    return direction.currentIsEarlier
      ? deltaOf(previous, current * inverseScale(direction), "tokens")
      : deltaOf(current, previous * direction.scale, "tokens");
  }

  function directionOfCost(direction, current, previous) {
    return direction.currentIsEarlier
      ? deltaOf(previous, current * inverseScale(direction), "cost")
      : deltaOf(current, previous * direction.scale, "cost");
  }

  // --- payload ----------------------------------------------------------------

  function isSafeCount(value) {
    return typeof value === "number" && Number.isSafeInteger(value);
  }

  function readWindow(source) {
    if (!source || typeof source !== "object") return null;
    var since = Date.parse(source.since);
    var until = Date.parse(source.until);
    // Date.parse already refuses instants a Date cannot hold.
    if (!isFinite(since) || !isFinite(until) || until - since !== DAY_MS) return null;
    return { since: since, until: until, rows: Array.isArray(source.rows) ? source.rows : [] };
  }

  // Largest instant a Date can hold, in milliseconds either side of 1970.
  var MAX_DATE_MS = 8.64e15;
  // A local midnight is never further than this from the UTC one.
  var MAX_ZONE_OFFSET_MIN = 15 * 60;

  // Day starts the viewer can turn into dates: whole minutes, strictly
  // increasing, every one a valid Date, and the first within 15 hours of the
  // UTC midnight of the first hourly day (the relay applies the same rule).
  function validDayStarts(starts, hourlyFrom, count) {
    if (starts.length !== count) return false;
    for (var i = 0; i < starts.length; i++) {
      var value = starts[i];
      if (!Number.isSafeInteger(value) || Math.abs(value * MINUTE_MS) > MAX_DATE_MS) return false;
      if (i > 0 && value <= starts[i - 1]) return false;
    }
    return Math.abs(starts[0] - hourlyFrom * 1440) <= MAX_ZONE_OFFSET_MIN;
  }

  // Checks the shape the viewer relies on and indexes it. Returns null for a
  // payload it cannot show; the relay validates the details.
  function prepareUsage(payload) {
    if (!payload || typeof payload !== "object" || payload.version !== 1) return null;
    if (!isSafeCount(payload.seq) || payload.seq <= 0) return null;
    var generatedAt = Date.parse(payload.generatedAt);
    if (!isFinite(generatedAt) || !isValidZone(payload.timeZone)) return null;

    var coverage = payload.coverage || {};
    var dailyFrom = parseDay(coverage.dailyFrom);
    var dailyTo = parseDay(coverage.dailyTo);
    if (!isFinite(dailyFrom) || !isFinite(dailyTo) || dailyFrom > dailyTo) return null;

    var hourlyFrom = null;
    var hourlyTo = null;
    var hourDayStarts = [];
    if (coverage.hourlyFrom !== null && coverage.hourlyFrom !== undefined) {
      hourlyFrom = parseDay(coverage.hourlyFrom);
      hourlyTo = parseDay(coverage.hourlyTo);
      hourDayStarts = Array.isArray(payload.hourDayStarts) ? payload.hourDayStarts : [];
      if (!isFinite(hourlyFrom) || !isFinite(hourlyTo) || hourlyFrom > hourlyTo ||
          !validDayStarts(hourDayStarts, hourlyFrom, hourlyTo - hourlyFrom + 2)) {
        return null;
      }
    }

    var accounts = (Array.isArray(payload.accounts) ? payload.accounts : []).map(function (account) {
      return {
        name: account && typeof account.name === "string" && account.name ? account.name : "Account",
        provider: account && account.provider === "codex" ? "codex" : "claude"
      };
    });
    var models = (Array.isArray(payload.models) ? payload.models : []).map(function (model) {
      var price = model && model.price && typeof model.price === "object" ? model.price : null;
      return {
        id: model && typeof model.id === "string" && model.id ? model.id : "unknown",
        provider: model && model.provider === "codex" ? "codex" : "claude",
        priced: Boolean(model && model.priced === true && price),
        price: price
      };
    });

    var grids = {};
    (Array.isArray(payload.hours) ? payload.hours : []).forEach(function (grid) {
      if (grid && isSafeCount(grid.phaseMinutes) && Array.isArray(grid.rows)) {
        grids[grid.phaseMinutes] = grid.rows;
      }
    });

    var rolling = payload.rolling || {};
    var current = readWindow(rolling.current);
    var previous = readWindow(rolling.previous);
    if (!current || !previous) return null;

    var days = Array.isArray(payload.days) ? payload.days : [];
    var firstDataDay = null;
    var lastDataDay = null;
    for (var i = 0; i < days.length; i++) {
      var row = days[i];
      if (!Array.isArray(row) || row.length < 3 + METRIC_COUNT) continue;
      var day = dailyFrom + row[0];
      if (firstDataDay === null || day < firstDataDay) firstDataDay = day;
      if (lastDataDay === null || day > lastDataDay) lastDataDay = day;
    }

    return {
      seq: payload.seq,
      generatedAt: generatedAt,
      zone: payload.timeZone,
      dailyFrom: dailyFrom,
      dailyTo: dailyTo,
      hourlyFrom: hourlyFrom,
      hourlyTo: hourlyTo,
      hourDayStarts: hourDayStarts,
      accounts: accounts,
      models: models,
      days: days,
      grids: grids,
      rolling: { current: current, previous: previous },
      firstDataDay: firstDataDay,
      lastDataDay: lastDataDay
    };
  }

  // --- periods (UsagePeriods.Resolve / ResolveComparison) ----------------------

  function hourlyCovers(data, from, to) {
    return data.hourlyFrom !== null && from >= data.hourlyFrom && to <= data.hourlyTo;
  }

  function slotsBetween(startMin, endMin) {
    var slots = [];
    for (var cursor = startMin; cursor < endMin; cursor += 60) slots.push(cursor);
    return slots;
  }

  // A period on screen: { kind: "rolling" | "rollingPrevious" | "days", from, to }.
  // `wantsHourly` is set for a comparison: it is shown by hour exactly when
  // the current period is.
  function resolvePeriod(data, period, wantsHourly) {
    var clock = zoneClock(data.zone);
    var generatedMin = Math.floor(data.generatedAt / MINUTE_MS);

    if (period.kind === "rolling" || period.kind === "rollingPrevious") {
      var window = period.kind === "rolling" ? data.rolling.current : data.rolling.previous;
      var sinceLocal = clock(window.since);
      var untilLocal = clock(window.until);
      var startMin = Math.round(window.since / MINUTE_MS);
      return {
        kind: period.kind,
        rolling: true,
        from: sinceLocal.day,
        to: untilLocal.day,
        dayCount: 1,
        wantsHourly: true,
        hourly: true,
        hourlyFallback: false,
        startMin: startMin,
        endMin: Math.round(window.until / MINUTE_MS),
        sinceMs: window.since,
        untilMs: window.until,
        slots: slotsBetween(startMin, startMin + 24 * 60),
        phase: null,
        coverage: { partial: false, empty: false, from: sinceLocal.day, to: untilLocal.day },
        startKey: [sinceLocal.day, sinceLocal.hour * 60 + sinceLocal.minute],
        endKey: [untilLocal.day, untilLocal.hour * 60 + untilLocal.minute],
        partialIndex: null
      };
    }

    var from = Math.min(period.from, period.to);
    var to = Math.max(period.from, period.to);
    var dayCount = to - from + 1;
    var wants = wantsHourly === undefined ? dayCount <= MAX_HOURLY_DAYS : Boolean(wantsHourly);
    var hourly = wants && hourlyCovers(data, from, to);
    var coveredFrom = Math.max(from, data.dailyFrom);
    var coveredTo = Math.min(to, data.dailyTo);
    var resolved = {
      kind: "days",
      rolling: false,
      from: from,
      to: to,
      dayCount: dayCount,
      wantsHourly: wants,
      hourly: hourly,
      hourlyFallback: wants && !hourly,
      startMin: null,
      endMin: null,
      slots: null,
      phase: null,
      coverage: {
        partial: from < data.dailyFrom || to > data.dailyTo,
        empty: coveredFrom > coveredTo,
        from: coveredFrom,
        to: coveredTo
      },
      startKey: [from, 0],
      endKey: [to + 1, 0],
      partialIndex: null
    };

    if (hourly) {
      var start = data.hourDayStarts[from - data.hourlyFrom];
      var end = data.hourDayStarts[to - data.hourlyFrom + 1];
      resolved.startMin = start;
      resolved.endMin = end;
      resolved.slots = slotsBetween(start, end);
      resolved.phase = ((start % 60) + 60) % 60;
      if (generatedMin >= start && generatedMin < end) {
        resolved.partialIndex = Math.floor((generatedMin - start) / 60);
      }
    } else if (data.dailyTo >= from && data.dailyTo <= to) {
      // The report's last day holds usage only up to generatedAt.
      resolved.partialIndex = data.dailyTo - from;
    }
    return resolved;
  }

  // The period the current one is compared with by default: the same length
  // right before it, or the exact 24 hours before the rolling window.
  function previousPeriod(period) {
    if (period.kind === "rolling") return { kind: "rollingPrevious" };
    var count = period.to - period.from + 1;
    return { kind: "days", from: period.from - count, to: period.to - count };
  }

  // The preset and custom choices of the screen, turned into periods.
  // state: { range: "24h"|"7"|"30"|"90"|"custom", customFrom, customTo,
  //          compare: "off"|"previous"|"custom", compareFrom, compareTo, account }
  function selectionFor(state, data, nowMs) {
    var today = todayIn(data.zone, nowMs);
    var period;
    if (state.range === "24h") {
      period = { kind: "rolling" };
    } else if (state.range === "custom" && isFinite(state.customFrom) && isFinite(state.customTo)) {
      period = { kind: "days", from: Math.min(state.customFrom, state.customTo), to: Math.max(state.customFrom, state.customTo) };
    } else {
      var count = Number(state.range) || 30;
      period = { kind: "days", from: today - (count - 1), to: today };
    }

    var comparison = null;
    if (state.compare === "previous") {
      comparison = "previous";
    } else if (state.compare === "custom" && isFinite(state.compareFrom) && isFinite(state.compareTo)) {
      comparison = { from: Math.min(state.compareFrom, state.compareTo), to: Math.max(state.compareFrom, state.compareTo) };
    }

    var index = resolveAccount(state.account, data);
    return { today: today, period: period, comparison: comparison, accounts: index === null ? null : [index] };
  }

  // The picked account is its index in the report plus the provider and name
  // it had when picked. Names can repeat (and collide once cut to 64
  // characters), so the index is the identity; after a refresh the pick holds
  // only while that index still shows the same account. Null means every
  // account. Internal account ids are never sent, so none are used.
  function accountSelection(data, index) {
    var account = data.accounts[index];
    return account ? { index: index, provider: account.provider, name: account.name } : null;
  }

  function resolveAccount(selection, data) {
    if (!selection || typeof selection !== "object") return null;
    var account = data.accounts[selection.index];
    return account && account.provider === selection.provider && account.name === selection.name
      ? selection.index
      : null;
  }

  // Picker entries: the value is the index; a name shown twice gets " (2)",
  // " (3)" on its later copies so they can be told apart.
  function accountOptions(data) {
    var seen = {};
    return data.accounts.map(function (account, index) {
      var count = (seen[account.name] || 0) + 1;
      seen[account.name] = count;
      return { value: String(index), label: count === 1 ? account.name : account.name + " (" + count + ")" };
    });
  }

  // --- date pickers (DateRangeSelection and the UsagePeriods limits) -----------

  // How far back a custom range may start, in days before today.
  var MAX_LOOKBACK_DAYS = 730;
  var FULL_MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August",
    "September", "October", "November", "December"];

  // What the custom range and comparison pickers offer, exactly as the
  // desktop: no future day, no earlier than 730 days back, at most 366 days.
  function customRangeLimits(today) {
    return { min: today - MAX_LOOKBACK_DAYS, max: today, maxSpan: MAX_CUSTOM_DAYS };
  }

  // UsagePeriods.IsValidCustomRange.
  function isValidCustomRange(from, to, today) {
    return isFinite(from) && isFinite(to) && from <= to && to <= today &&
      from >= today - MAX_LOOKBACK_DAYS && to - from + 1 <= MAX_CUSTOM_DAYS;
  }

  // The first day of the month a day falls in.
  function monthStart(day) {
    var parts = dayParts(day);
    return Date.UTC(parts.year, parts.month, 1) / DAY_MS;
  }

  function addMonths(month, count) {
    var parts = dayParts(month);
    return Date.UTC(parts.year, parts.month + count, 1) / DAY_MS;
  }

  // "October 2026"
  function monthTitle(month) {
    var parts = dayParts(month);
    return FULL_MONTHS[parts.month] + " " + parts.year;
  }

  // DateRangeSelection.MonthGrid: the 42 days a six-week grid shows for a
  // month (1 to 12), starting on firstDayOfWeek (0 is Sunday).
  function monthGrid(year, month, firstDayOfWeek) {
    var first = Date.UTC(year, month - 1, 1) / DAY_MS;
    var lead = (dayParts(first).weekday - firstDayOfWeek + 7) % 7;
    var days = [];
    for (var index = 0; index < 42; index++) days.push(first - lead + index);
    return days;
  }

  // DateRangeSelection: the pending selection inside a date picker. Range
  // mode picks with two plain clicks (the first sets the anchor and selects
  // that day, the second closes the range on either side, a third starts
  // over); a shift-click closes against the anchor; single mode picks one
  // day per click. Days isAllowed refuses change nothing, and a range never
  // grows past maxSpanDays: the far end is pulled back towards the anchor.
  function createRangeSelection(singleDate, maxSpanDays, isAllowed, from, to) {
    var selection = {
      singleDate: Boolean(singleDate),
      maxSpanDays: Math.max(1, maxSpanDays),
      isAllowed: isAllowed,
      from: null,
      to: null,
      anchor: null,
      awaitingEnd: false
    };
    if (from !== null && from !== undefined && isFinite(from)) {
      var end = selection.singleDate ? from : (to !== null && to !== undefined && isFinite(to) ? to : from);
      selection.from = Math.min(from, end);
      selection.to = Math.max(from, end);
      selection.anchor = selection.from;
    }

    selection.contains = function (day) {
      return selection.from !== null && day >= selection.from && day <= selection.to;
    };

    // Applies a click; false when the day cannot be picked and nothing changed.
    selection.click = function (day, extend) {
      if (!selection.isAllowed(day)) return false;
      if (selection.singleDate) {
        selection.from = selection.to = selection.anchor = day;
        return true;
      }
      if ((extend || selection.awaitingEnd) && selection.anchor !== null) {
        var anchor = selection.anchor;
        var limit = selection.maxSpanDays - 1;
        if (Math.abs(day - anchor) > limit) day = day > anchor ? anchor + limit : anchor - limit;
        selection.from = Math.min(day, anchor);
        selection.to = Math.max(day, anchor);
        selection.awaitingEnd = false;
        return true;
      }
      selection.from = selection.to = selection.anchor = day;
      selection.awaitingEnd = true;
      return true;
    };

    return selection;
  }

  function periodLength(period) {
    return period ? period.to - period.from + 1 : 0;
  }

  // The desktop "Compare two periods" picker. A starts on the range on
  // screen; B on the picked comparison, or the period just before A. One
  // calendar edits whichever period is selected. A period changes only once
  // both of its days are picked; once A is complete the calendar moves on to
  // B. An untouched B that stands for "the period before" follows A.
  // options: { a: {from, to}, b: {from, to}, bIsPrevious, today }.
  function createCompareSession(options) {
    var limits = customRangeLimits(options.today);
    var session = {
      a: options.a ? { from: options.a.from, to: options.a.to } : null,
      b: options.b ? { from: options.b.from, to: options.b.to } : null,
      aEdited: false,
      bEdited: false,
      bIsPrevious: Boolean(options.bIsPrevious),
      editA: true,
      limits: limits,
      selection: null
    };

    function allowed(day) {
      return day >= limits.min && day <= limits.max;
    }

    // Points the calendar at one period; the other one is the reference.
    session.select = function (editA) {
      session.editA = Boolean(editA);
      var editing = session.editA ? session.a : session.b;
      session.selection = createRangeSelection(false, limits.maxSpan, allowed,
        editing ? editing.from : null, editing ? editing.to : null);
    };

    session.reference = function () {
      return session.editA ? session.b : session.a;
    };

    // A click on the calendar: { changed, switched }.
    session.click = function (day, extend) {
      if (!session.selection.click(day, extend)) return { changed: false, switched: false };
      if (session.selection.awaitingEnd) return { changed: true, switched: false };
      var range = { from: session.selection.from, to: session.selection.to };
      if (session.editA) {
        session.a = range;
        session.aEdited = true;
        if (session.bIsPrevious && !session.bEdited) {
          var length = periodLength(range);
          session.b = { from: range.from - length, to: range.from - 1 };
        }
        session.select(false);
        return { changed: true, switched: true };
      }
      session.b = range;
      session.bEdited = true;
      return { changed: true, switched: false };
    };

    session.bFollowsA = function () {
      return session.bIsPrevious && !session.bEdited;
    };

    session.canApply = function () {
      return Boolean(session.a && session.b) && !session.selection.awaitingEnd;
    };

    // What Apply hands over. Untouched defaults keep their meaning: the range
    // on screen stays (so the past 24 hours stay rolling), and an untouched B
    // stays "the period before", which follows the range.
    session.result = function () {
      return {
        current: session.aEdited ? session.a : null,
        comparison: session.bEdited || !session.bIsPrevious ? session.b : null
      };
    };

    session.hint = function () {
      var editing = session.editA ? "A" : "B";
      return session.selection.awaitingEnd
        ? "Now click the last day of period " + editing + "."
        : "Editing period " + editing + ". Underlined days are period " + (session.editA ? "B" : "A") + ".";
    };

    session.select(true);
    return session;
  }

  // The Custom picker's line under the calendar.
  function rangeSelectionText(selection, today) {
    if (selection.from === null || selection.to === null) return "No days picked";
    var label = rangeLabel(selection.from, selection.to, today);
    if (selection.awaitingEnd) return label + ", now pick the last day";
    var days = selection.to - selection.from + 1;
    return label + " (" + (days === 1 ? "by hour" : days + " days") + ")";
  }

  // Where Tab goes inside an overlay that keeps focus: wraps from the last
  // control to the first (and back with Shift+Tab), comes in from outside at
  // the matching end, and is null where the browser's own order is right.
  function wrapFocusIndex(index, count, backwards) {
    if (count <= 0) return null;
    if (index < 0) return backwards ? count - 1 : 0;
    if (backwards && index === 0) return count - 1;
    if (!backwards && index === count - 1) return 0;
    return null;
  }

  // Columns a phone leaves out of the breakdown. A sort on one of them falls
  // back to the default there, since its header cannot be reached.
  function hiddenOnPhone(column, comparing) {
    return comparing
      ? column === "previousValue" || column === "tokens" || column === "previousTokens"
      : column === "share";
  }

  // --- aggregation (UsageAggregator over payload rows) --------------------------

  function rowProvider(data, row, accountColumn, modelColumn) {
    var model = data.models[row[modelColumn]];
    if (model) return model.provider;
    var account = data.accounts[row[accountColumn]];
    return account ? account.provider : "claude";
  }

  // Sums a resolved period: totals, per model, per provider, per account and
  // one point per day or per hour. Points outside the shared data are marked
  // missing, never zero. options: { accounts: [idx] | null, model: idx }.
  function aggregate(data, resolved, options) {
    var opts = options || {};
    var accountFilter = null;
    if (Array.isArray(opts.accounts) && opts.accounts.length > 0) {
      accountFilter = {};
      opts.accounts.forEach(function (index) { accountFilter[index] = true; });
    }
    var modelFilter = opts.model === undefined || opts.model === null ? null : opts.model;

    var totals = emptyMetrics();
    var byModel = {};
    var byAccount = {};
    var byProvider = { claude: emptyMetrics(), codex: emptyMetrics() };
    var count = resolved.hourly ? resolved.slots.length : resolved.dayCount;
    var generatedMin = Math.floor(data.generatedAt / MINUTE_MS);
    var points = [];
    for (var p = 0; p < count; p++) {
      var missing;
      if (resolved.hourly) {
        // Hours after the report was made are unknown, not empty.
        missing = !resolved.rolling && resolved.slots[p] > generatedMin;
      } else {
        var day = resolved.from + p;
        missing = day < data.dailyFrom || day > data.dailyTo;
      }
      points.push({
        key: resolved.hourly ? resolved.slots[p] : resolved.from + p,
        metrics: emptyMetrics(),
        byProvider: { claude: emptyMetrics(), codex: emptyMetrics() },
        missing: missing
      });
    }

    function included(accountIndex, modelIndex) {
      if (!isSafeCount(accountIndex) || accountIndex < 0 || accountIndex >= data.accounts.length) return false;
      if (!isSafeCount(modelIndex) || modelIndex < 0 || modelIndex >= data.models.length) return false;
      if (accountFilter && !accountFilter[accountIndex]) return false;
      return modelFilter === null || modelFilter === modelIndex;
    }

    function addTotals(row, accountColumn, modelColumn, offset) {
      var provider = rowProvider(data, row, accountColumn, modelColumn);
      addRow(totals, row, offset);
      addRow(byProvider[provider], row, offset);
      var model = row[modelColumn];
      if (!byModel[model]) byModel[model] = emptyMetrics();
      addRow(byModel[model], row, offset);
      var account = row[accountColumn];
      if (!byAccount[account]) byAccount[account] = emptyMetrics();
      addRow(byAccount[account], row, offset);
    }

    function addPoint(index, row, accountColumn, modelColumn, offset) {
      if (index < 0 || index >= points.length || points[index].missing) return;
      var point = points[index];
      addRow(point.metrics, row, offset);
      addRow(point.byProvider[rowProvider(data, row, accountColumn, modelColumn)], row, offset);
    }

    var i;
    var row;
    if (resolved.rolling) {
      var window = resolved.kind === "rolling" ? data.rolling.current : data.rolling.previous;
      for (i = 0; i < window.rows.length; i++) {
        row = window.rows[i];
        if (!Array.isArray(row) || row.length < 3 + METRIC_COUNT || !included(row[1], row[2])) continue;
        addTotals(row, 1, 2, 3);
        addPoint(row[0], row, 1, 2, 3);
      }
    } else {
      var firstNumber = resolved.from - data.dailyFrom;
      var lastNumber = resolved.to - data.dailyFrom;
      for (i = 0; i < data.days.length; i++) {
        row = data.days[i];
        if (!Array.isArray(row) || row.length < 3 + METRIC_COUNT) continue;
        if (row[0] < firstNumber || row[0] > lastNumber || !included(row[1], row[2])) continue;
        addTotals(row, 1, 2, 3);
        if (!resolved.hourly) addPoint(row[0] - firstNumber, row, 1, 2, 3);
      }
      if (resolved.hourly) {
        // The grid aligned to this range's first midnight, rows of its days only.
        var grid = data.grids[resolved.phase] || [];
        for (i = 0; i < grid.length; i++) {
          row = grid[i];
          if (!Array.isArray(row) || row.length < 4 + METRIC_COUNT) continue;
          if (row[0] < firstNumber || row[0] > lastNumber || !included(row[2], row[3])) continue;
          var offset = row[1] - resolved.startMin;
          if (offset < 0 || offset % 60 !== 0) continue;
          addPoint(offset / 60, row, 2, 3, 4);
        }
      }
    }

    var active = 0;
    points.forEach(function (point) { if (hasData(point.metrics)) active++; });
    return {
      totals: totals,
      byModel: byModel,
      byAccount: byAccount,
      byProvider: byProvider,
      points: points,
      activeCount: active
    };
  }

  // --- the screen's numbers (UsageWindowViewModel, pure) -----------------------

  // Everything one render needs, from one clock reading.
  function computeView(data, selection, nowMs) {
    var primary = resolvePeriod(data, selection.period);
    var comparison = null;
    if (selection.comparison) {
      var compared = selection.comparison === "previous"
        ? previousPeriod(selection.period)
        : { kind: "days", from: selection.comparison.from, to: selection.comparison.to };
      comparison = resolvePeriod(data, compared, primary.hourly);
    }
    var filter = { accounts: selection.accounts };
    return {
      data: data,
      nowMs: nowMs,
      today: selection.today !== undefined ? selection.today : todayIn(data.zone, nowMs),
      selection: selection,
      primary: primary,
      comparison: comparison,
      picked: Boolean(comparison) && selection.comparison !== "previous",
      current: aggregate(data, primary, filter),
      previous: comparison ? aggregate(data, comparison, filter) : null,
      direction: comparison ? changeDirection(primary, comparison) : DEFAULT_DIRECTION
    };
  }

  function costText(m) {
    return costOrUnpriced(m[COST], m[UNPRICED]);
  }

  function unpricedModelCount(view) {
    var seen = {};
    [view.current, view.previous].forEach(function (aggregateResult) {
      if (!aggregateResult) return;
      Object.keys(aggregateResult.byModel).forEach(function (index) {
        var model = view.data.models[index];
        if (model && !model.priced && aggregateResult.byModel[index][UNPRICED] > 0) seen[model.id.toUpperCase()] = true;
      });
    });
    return Object.keys(seen).length;
  }

  function buildProviderRows(view, metric) {
    var current = view.current;
    var previous = view.previous;
    var totalCost = current.totals[COST];
    var totalTokens = processedTokens(current.totals);
    var entries = PROVIDERS.filter(function (provider) {
      return hasData(current.byProvider[provider]) || (previous && hasData(previous.byProvider[provider]));
    }).map(function (provider) {
      return {
        provider: provider,
        totals: current.byProvider[provider],
        previous: previous ? previous.byProvider[provider] : emptyMetrics()
      };
    });

    if (metric === "tokens") {
      entries.sort(function (a, b) {
        return (processedTokens(b.totals) - processedTokens(a.totals)) ||
          (processedTokens(b.previous) - processedTokens(a.previous));
      });
      return entries.map(function (entry) {
        var tokens = processedTokens(entry.totals);
        return {
          provider: entry.provider,
          name: PROVIDER_NAMES[entry.provider],
          valueText: formatTokens(tokens),
          share: totalTokens > 0 ? tokens / totalTokens : 0,
          caption: formatPercent(tokens, totalTokens) + " of tokens \u00B7 " + costText(entry.totals),
          deltaText: previous
            ? withPrevious(formatTokens(processedTokens(entry.previous)),
              formatDelta(directionOfTokens(view.direction, tokens, processedTokens(entry.previous))))
            : ""
        };
      });
    }

    entries.sort(function (a, b) {
      return (b.totals[COST] - a.totals[COST]) ||
        (processedTokens(b.totals) - processedTokens(a.totals)) ||
        (b.previous[COST] - a.previous[COST]);
    });
    return entries.map(function (entry) {
      var tokens = processedTokens(entry.totals);
      return {
        provider: entry.provider,
        name: PROVIDER_NAMES[entry.provider],
        valueText: costText(entry.totals),
        share: totalCost > 0 ? entry.totals[COST] / totalCost : (totalTokens > 0 ? tokens / totalTokens : 0),
        caption: totalCost > 0
          ? formatPercent(entry.totals[COST], totalCost) + " of estimate \u00B7 " + formatTokens(tokens) + " tokens"
          : formatPercent(tokens, totalTokens) + " of tokens \u00B7 " + formatTokens(tokens) + " tokens",
        deltaText: previous
          ? withPrevious(costText(entry.previous), formatDelta(directionBetween(view.direction, entry.totals, entry.previous)))
          : ""
      };
    });
  }

  // The hero block in the selected metric: "cost" or "tokens".
  function buildHero(view, metric) {
    var totals = view.current.totals;
    var previous = view.previous ? view.previous.totals : null;
    var tokens = metric === "tokens";
    var unpriced = unpricedModelCount(view);
    var hasUnpriced = totals[UNPRICED] > 0 || (previous !== null && previous[UNPRICED] > 0);
    return {
      label: tokens ? "Processed tokens" : "Estimated API value",
      value: tokens ? formatTokens(processedTokens(totals)) : formatMoneyMicro(totals[COST]),
      isCost: !tokens,
      previousText: previous
        ? "B: " + (tokens ? formatTokens(processedTokens(previous)) : costText(previous))
        : "",
      deltaText: previous
        ? view.direction.label + " " + formatDelta(tokens
          ? directionOfTokens(view.direction, processedTokens(totals), processedTokens(previous))
          : directionBetween(view.direction, totals, previous))
        : "",
      note: tokens
        ? costText(totals) + " estimated API value"
        : "* API list-price estimate, not your subscription bill",
      unpricedNote: hasUnpriced && unpriced > 0
        ? "excludes " + unpriced + " unpriced model" + (unpriced === 1 ? "" : "s")
        : "",
      providers: buildProviderRows(view, metric)
    };
  }

  // The five headline tiles.
  function buildTiles(view) {
    var totals = view.current.totals;
    var previous = view.previous ? view.previous.totals : null;
    var hourly = view.primary.hourly;
    var active = Math.max(1, view.current.activeCount);
    var perActive = Math.floor(processedTokens(totals) / active);

    function tokenDelta(pick) {
      return previous
        ? withPrevious(formatTokens(pick(previous)),
          formatDelta(directionOfTokens(view.direction, pick(totals), pick(previous)), false))
        : "";
    }

    var savingsDelta = "";
    if (previous) {
      var fullyPriced = totals[UNPRICED] === 0 && previous[UNPRICED] === 0;
      savingsDelta = withPrevious(formatMoneyMicro(previous[SAVINGS]), formatDelta(fullyPriced
        ? directionOfCost(view.direction, totals[SAVINGS], previous[SAVINGS])
        : directionOfTokens(view.direction, totals[CACHE_READ], previous[CACHE_READ])));
    }

    return [
      {
        caption: "Processed tokens",
        value: formatTokens(processedTokens(totals)),
        footnote: formatTokens(perActive) + " per active " + (hourly ? "hour" : "day"),
        deltaText: tokenDelta(processedTokens)
      },
      {
        caption: "Cached input",
        value: formatTokens(totals[CACHE_READ]),
        footnote: formatPercent(totals[CACHE_READ], inputTokens(totals)) + " of observed input",
        deltaText: tokenDelta(function (m) { return m[CACHE_READ]; })
      },
      {
        caption: "Uncached input",
        value: formatTokens(totals[UNCACHED]),
        footnote: formatTokens(cacheWriteTokens(totals)) + " cache writes",
        deltaText: tokenDelta(function (m) { return m[UNCACHED]; })
      },
      {
        caption: "Output",
        value: formatTokens(totals[OUTPUT]),
        footnote: "includes " + formatTokens(totals[REASONING]) + " reasoning",
        deltaText: tokenDelta(function (m) { return m[OUTPUT]; })
      },
      {
        caption: "Cache savings",
        value: formatMoneyMicro(totals[SAVINGS]),
        footnote: formatMultiplier(totals[SAVINGS], totals[COST]) + " the API list-price estimate",
        deltaText: savingsDelta
      }
    ];
  }

  // --- chart data (UsageSeriesAlignment) ---------------------------------------

  function clockKey(local) {
    return local.day + ":" + local.hour;
  }

  // True when picked days lie under an hourly range as an average day.
  function usesAverageDay(view) {
    return view.picked && view.primary.hourly &&
      (view.primary.rolling || view.comparison.dayCount > MAX_HOURLY_DAYS);
  }

  // The comparison laid under the current period's points. Each entry is
  // null (a gap) or { byProvider, divisor, axisLabel, tooltipLabel }.
  function alignComparison(view, current, previous) {
    var primary = view.primary;
    var comparison = view.comparison;
    var data = view.data;
    var count = current.points.length;
    var result = { entries: [], title: "B", available: true, partialIndex: null, averageDay: false };
    var i;

    if (!primary.hourly) {
      for (i = 0; i < count; i++) {
        var point = i < comparison.dayCount ? previous.points[i] : null;
        result.entries.push(point && !point.missing ? {
          byProvider: point.byProvider,
          divisor: 1,
          axisLabel: axisDayLabel(comparison.from + i),
          tooltipLabel: longDayLabel(comparison.from + i, view.today)
        } : null);
      }
      result.partialIndex = comparison.partialIndex !== null && comparison.partialIndex < count
        ? comparison.partialIndex : null;
      return result;
    }

    if (primary.rolling && comparison.rolling) {
      for (i = 0; i < count; i++) {
        var hour = previous.points[i];
        result.entries.push(hour ? {
          byProvider: hour.byProvider,
          divisor: 1,
          axisLabel: axisHourLabel(hour.key * MINUTE_MS, data.zone),
          tooltipLabel: longHourLabel(hour.key * MINUTE_MS, data.zone, view.today)
        } : null);
      }
      return result;
    }

    // Everything below needs the comparison by hour.
    if (!comparison.hourly) {
      result.available = false;
      for (i = 0; i < count; i++) result.entries.push(null);
      return result;
    }

    var clock = zoneClock(data.zone);
    var primaryKeys = current.points.map(function (point) { return clock(point.key * MINUTE_MS); });

    if (usesAverageDay(view)) {
      result.averageDay = true;
      result.title = "B: average day";
      var byHour = [];
      for (i = 0; i < 24; i++) byHour.push({ claude: emptyMetrics(), codex: emptyMetrics() });
      previous.points.forEach(function (point) {
        if (point.missing) return;
        var local = clock(point.key * MINUTE_MS);
        PROVIDERS.forEach(function (provider) { addMetrics(byHour[local.hour][provider], point.byProvider[provider]); });
      });
      var occurrences = {};
      primaryKeys.forEach(function (local) {
        var key = clockKey(local);
        occurrences[key] = (occurrences[key] || 0) + 1;
      });
      primaryKeys.forEach(function (local) {
        result.entries.push({
          byProvider: byHour[local.hour],
          divisor: Math.max(1, comparison.dayCount) * occurrences[clockKey(local)],
          axisLabel: "",
          tooltipLabel: ""
        });
      });
      return result;
    }

    // Whole days by clock: day N at 14:00 in A against day N at 14:00 in B.
    var comparisonByKey = {};
    var progressKey = null;
    previous.points.forEach(function (point, index) {
      var local = clock(point.key * MINUTE_MS);
      var key = clockKey(local);
      var entry = comparisonByKey[key];
      if (!entry) {
        entry = comparisonByKey[key] = {
          byProvider: { claude: emptyMetrics(), codex: emptyMetrics() },
          missing: false,
          local: local
        };
      }
      if (point.missing) entry.missing = true;
      PROVIDERS.forEach(function (provider) { addMetrics(entry.byProvider[provider], point.byProvider[provider]); });
      if (index === comparison.partialIndex) progressKey = key;
    });

    var map = primaryKeys.map(function (local) {
      var offset = local.day - primary.from;
      if (offset < 0 || offset >= comparison.dayCount) return null;
      var key = (comparison.from + offset) + ":" + local.hour;
      return comparisonByKey[key] && !comparisonByKey[key].missing ? key : null;
    });
    var mapped = {};
    map.forEach(function (key) { if (key !== null) mapped[key] = (mapped[key] || 0) + 1; });
    map.forEach(function (key, index) {
      if (key === null) {
        result.entries.push(null);
        return;
      }
      var entry = comparisonByKey[key];
      var axisTime = pad2(entry.local.hour) + ":00";
      result.entries.push({
        byProvider: entry.byProvider,
        divisor: mapped[key],
        axisLabel: primary.dayCount <= 1 ? axisTime : axisDayLabel(entry.local.day) + " " + axisTime,
        tooltipLabel: longDayLabel(entry.local.day, view.today) + ", " + axisTime
      });
      if (key === progressKey) result.partialIndex = index;
    });
    return result;
  }

  function providersWithData(current, previous) {
    return PROVIDERS.filter(function (provider) {
      return hasData(current.byProvider[provider]) || (previous && hasData(previous.byProvider[provider]));
    });
  }

  // Everything the chart draws. modelIndex cuts both periods to one model.
  function buildChart(view, metric, modelIndex) {
    var current = view.current;
    var previous = view.previous;
    if (modelIndex !== undefined && modelIndex !== null) {
      var filter = { accounts: view.selection.accounts, model: modelIndex };
      current = aggregate(view.data, view.primary, filter);
      previous = view.comparison ? aggregate(view.data, view.comparison, filter) : null;
    }
    var primary = view.primary;
    var zone = view.data.zone;
    var providers = providersWithData(current, previous);
    var count = current.points.length;

    var labels = current.points.map(function (point) {
      if (!primary.hourly) return axisDayLabel(point.key);
      return primary.rolling || primary.dayCount <= 1
        ? axisHourLabel(point.key * MINUTE_MS, zone)
        : axisDayHourLabel(point.key * MINUTE_MS, zone);
    });
    var tooltipLabels = current.points.map(function (point) {
      return primary.hourly
        ? longHourLabel(point.key * MINUTE_MS, zone, view.today)
        : longDayLabel(point.key, view.today);
    });

    var series = providers.map(function (provider) {
      return {
        provider: provider,
        name: PROVIDER_NAMES[provider],
        values: current.points.map(function (point) {
          return point.missing ? NaN : metricValue(point.byProvider[provider], metric);
        })
      };
    });

    var aligned = previous ? alignComparison(view, current, previous) : null;
    var comparisonSeries = aligned && aligned.available ? providers.map(function (provider) {
      return {
        provider: provider,
        name: PROVIDER_NAMES[provider],
        values: aligned.entries.map(function (entry) {
          return entry ? metricValue(entry.byProvider[provider], metric) / entry.divisor : NaN;
        })
      };
    }) : [];

    return {
      metric: metric,
      count: count,
      hourly: primary.hourly,
      labels: labels,
      tooltipLabels: tooltipLabels,
      series: series,
      comparisonSeries: comparisonSeries,
      comparisonLabels: aligned && aligned.available && !aligned.averageDay
        ? aligned.entries.map(function (entry) { return entry ? entry.axisLabel : ""; })
        : [],
      comparisonTooltipLabels: aligned && aligned.available
        ? aligned.entries.map(function (entry) { return entry ? entry.tooltipLabel : ""; })
        : [],
      comparisonTitle: aligned ? aligned.title : "B",
      comparisonUnavailable: Boolean(aligned && !aligned.available),
      averageDay: Boolean(aligned && aligned.averageDay),
      partialIndex: primary.partialIndex,
      comparisonPartialIndex: aligned && aligned.available ? aligned.partialIndex : null
    };
  }

  function chartValueText(value, metric) {
    return metric === "tokens" ? formatTokens(Math.round(value)) : formatMoney(value);
  }

  function chartAxisText(value, metric) {
    return metric === "tokens" ? axisTokens(Math.round(value)) : axisCost(value);
  }

  function tooltipLines(series, index, metric) {
    var rows = [];
    var total = 0;
    series.forEach(function (line) {
      var value = line.values[index];
      if (typeof value === "number" && isFinite(value)) {
        rows.push({ provider: line.provider, name: line.name, value: chartValueText(value, metric) });
        total += value;
      }
    });
    if (rows.length > 1) rows.push({ provider: null, name: "Total", value: chartValueText(total, metric) });
    return rows;
  }

  // What the hover tooltip shows at a point, or null where nothing has a value.
  function tooltipFor(chart, index) {
    if (index < 0 || index >= chart.count) return null;
    var rows = tooltipLines(chart.series, index, chart.metric);
    var comparisonRows = tooltipLines(chart.comparisonSeries, index, chart.metric);
    if (rows.length === 0 && comparisonRows.length === 0) return null;
    var title = chart.tooltipLabels[index] || chart.labels[index];
    if (chart.partialIndex === index) title += " so far";
    var comparisonTitle = "";
    if (comparisonRows.length > 0) {
      comparisonTitle = chart.comparisonTooltipLabels[index]
        ? "B: " + chart.comparisonTooltipLabels[index]
        : chart.comparisonTitle;
      if (chart.comparisonPartialIndex === index) comparisonTitle += " so far";
    }
    return { title: title, rows: rows, comparisonTitle: comparisonTitle, comparisonRows: comparisonRows };
  }

  // A smooth SVG path through chart points (Catmull-Rom as cubic Bezier).
  // Every control point is clamped to the plot's [top, bottom], so the curve,
  // which stays inside the hull of its points, never rises above the axis
  // ceiling or dips below zero: no peak or trough the data does not have.
  function smoothLinePath(points, top, bottom) {
    function f(value) {
      return value.toFixed(1);
    }
    function clampY(value) {
      return Math.max(top, Math.min(bottom, value));
    }
    var d = "M" + f(points[0][0]) + " " + f(points[0][1]);
    if (points.length === 2) return d + " L" + f(points[1][0]) + " " + f(points[1][1]);
    for (var n = 0; n < points.length - 1; n++) {
      var p0 = points[n - 1] || points[n];
      var p1 = points[n];
      var p2 = points[n + 1];
      var p3 = points[n + 2] || p2;
      var c1x = p1[0] + (p2[0] - p0[0]) / 6;
      var c1y = clampY(p1[1] + (p2[1] - p0[1]) / 6);
      var c2x = p2[0] - (p3[0] - p1[0]) / 6;
      var c2y = clampY(p2[1] - (p3[1] - p1[1]) / 6);
      d += " C" + f(c1x) + " " + f(c1y) + " " + f(c2x) + " " + f(c2y) + " " + f(p2[0]) + " " + f(p2[1]);
    }
    return d;
  }

  // --- breakdown (UsageBreakdown) ------------------------------------------------

  var MODEL_DEFAULT_SORT = { column: "value", descending: true };
  var TIME_DEFAULT_SORT = { column: "label", descending: true };

  function toggleSort(sort, column, labelIsTime) {
    return column === sort.column
      ? { column: column, descending: !sort.descending }
      : { column: column, descending: column !== "label" || Boolean(labelIsTime) };
  }

  function compareText(a, b) {
    var left = a.toUpperCase();
    var right = b.toUpperCase();
    return left < right ? -1 : left > right ? 1 : 0;
  }

  function providerRank(provider) {
    return provider === null || provider === undefined ? -1 : PROVIDERS.indexOf(provider);
  }

  function changeRank(delta) {
    return delta.kind === "noPriorData" ? Infinity : delta.kind === "change" ? delta.fraction : -Infinity;
  }

  function lastGroup(item, column) {
    if (column === "value" || column === "share") return item.isUnpriced ? 1 : 0;
    if (column === "previousValue") return item.isPreviousUnpriced ? 1 : 0;
    if (column === "change") return item.delta.kind === "none" ? 1 : 0;
    return 0;
  }

  function sign(value) {
    return value > 0 ? 1 : value < 0 ? -1 : 0;
  }

  // Orders rows by raw values. Unpriced rows go last on value and share, rows
  // without a change go last on change, ties fall back to label then provider.
  function sortBreakdown(items, sort) {
    var list = items.slice();
    list.sort(function (left, right) {
      var group = lastGroup(left, sort.column) - lastGroup(right, sort.column);
      if (group !== 0) return group;
      var primary = 0;
      switch (sort.column) {
        case "label":
          primary = left.time !== null && right.time !== null
            ? sign(left.time - right.time)
            : compareText(left.label, right.label);
          break;
        case "value":
        case "share":
          primary = sign(left.totals[COST] - right.totals[COST]);
          break;
        case "tokens":
          primary = sign(processedTokens(left.totals) - processedTokens(right.totals));
          break;
        case "change":
          var a = changeRank(left.delta);
          var b = changeRank(right.delta);
          primary = a === b ? 0 : a < b ? -1 : 1;
          break;
        case "previousValue":
          primary = sign((left.previousTotals ? left.previousTotals[COST] : 0) -
            (right.previousTotals ? right.previousTotals[COST] : 0));
          break;
        case "previousTokens":
          primary = sign((left.previousTotals ? processedTokens(left.previousTotals) : 0) -
            (right.previousTotals ? processedTokens(right.previousTotals) : 0));
          break;
      }
      if (primary !== 0) return sort.descending ? -primary : primary;
      var label = compareText(left.label, right.label);
      if (label !== 0) return label;
      return providerRank(left.provider) - providerRank(right.provider);
    });
    return list;
  }

  // One row per model in either period; a model only B used stays as a drop.
  function modelItems(view) {
    var keys = {};
    Object.keys(view.current.byModel).forEach(function (index) { keys[index] = true; });
    if (view.previous) Object.keys(view.previous.byModel).forEach(function (index) { keys[index] = true; });
    return Object.keys(keys).map(function (key) {
      var index = Number(key);
      var model = view.data.models[index];
      var now = view.current.byModel[index] || null;
      var before = view.previous ? view.previous.byModel[index] || null : null;
      var totals = now || emptyMetrics();
      return {
        label: model.id,
        modelIndex: index,
        provider: model.provider,
        time: null,
        day: null,
        totals: totals,
        isUnpriced: Boolean(now) && !model.priced,
        previousTotals: view.previous ? before || emptyMetrics() : null,
        isPreviousUnpriced: Boolean(before) && !model.priced,
        delta: view.previous ? directionBetween(view.direction, totals, before || emptyMetrics()) : DELTA_NONE
      };
    });
  }

  // One row per day or hour that has usage.
  function timeItems(view) {
    var zone = view.data.zone;
    var hourly = view.primary.hourly;
    return view.current.points.filter(function (point) {
      return !point.missing && hasData(point.metrics);
    }).map(function (point) {
      return {
        label: hourly ? longHourLabel(point.key * MINUTE_MS, zone, view.today) : longDayLabel(point.key, view.today),
        modelIndex: null,
        provider: null,
        time: point.key,
        day: hourly ? null : point.key,
        totals: point.metrics,
        isUnpriced: point.metrics[COST] === 0 && point.metrics[UNPRICED] > 0,
        previousTotals: null,
        isPreviousUnpriced: false,
        delta: DELTA_NONE
      };
    });
  }

  // The model sort in force: a comparison column without a comparison, or
  // Share while the comparison hides it, falls back to the default.
  function effectiveModelSort(sort, comparing) {
    var comparisonColumn = sort.column === "change" || sort.column === "previousValue" || sort.column === "previousTokens";
    return (comparisonColumn && !comparing) || (sort.column === "share" && comparing) ? MODEL_DEFAULT_SORT : sort;
  }

  function canDrillDay(view, day) {
    return !view.primary.hourly && day !== null && hourlyCovers(view.data, day, day);
  }

  // The breakdown table for "model" or "time" mode.
  function buildBreakdown(view, mode, modelSort, timeSort) {
    var byTime = mode === "time";
    var comparing = Boolean(view.previous);
    var sort = byTime ? timeSort : effectiveModelSort(modelSort, comparing);
    var items = sortBreakdown(byTime ? timeItems(view) : modelItems(view), sort);
    var totalCost = view.current.totals[COST];
    var rows = items.map(function (item) {
      var previous = item.previousTotals;
      return {
        label: item.label,
        provider: item.provider,
        modelIndex: item.modelIndex,
        valueText: item.isUnpriced ? "unpriced" : formatMoneyMicro(item.totals[COST]),
        shareText: formatPercent(item.totals[COST], totalCost),
        tokensText: formatTokens(processedTokens(item.totals)),
        changeText: byTime ? "" : formatDelta(item.delta),
        previousValueText: previous ? (item.isPreviousUnpriced ? "unpriced" : formatMoneyMicro(previous[COST])) : "",
        previousTokensText: previous ? formatTokens(processedTokens(previous)) : "",
        drillDay: byTime && canDrillDay(view, item.day) ? item.day : null
      };
    });
    var showComparison = !byTime && comparing;
    var hint = "";
    if (rows.length > 0) {
      if (!byTime) hint = "Select a model for its details";
      else if (rows.some(function (row) { return row.drillDay !== null; })) hint = "Select a day to see it by hour";
    }
    return {
      mode: byTime ? "time" : "model",
      sort: sort,
      rows: rows,
      showComparison: showComparison,
      labelHeader: byTime ? (view.primary.hourly ? "Hour" : "Day") : "Model",
      valueHeader: showComparison ? "API value A" : "API value",
      tokensHeader: showComparison ? "Tokens A" : "Tokens",
      changeHeader: comparing ? "Change " + view.direction.label : "Change",
      hint: hint
    };
  }

  // --- model details ---------------------------------------------------------------

  function priceRate(price, name) {
    var value = price ? Number(price[name]) : 0;
    return isFinite(value) ? value : 0;
  }

  function buildModelDetail(view, modelIndex, metric) {
    var model = view.data.models[modelIndex];
    if (!model) return null;
    var now = view.current.byModel[modelIndex] || null;
    var before = view.previous ? view.previous.byModel[modelIndex] || null : null;
    if (!now && !before) return null;

    var totals = now || emptyMetrics();
    var report = view.current.totals;
    var unpricedNow = Boolean(now) && !model.priced;
    var processed = processedTokens(totals);
    var share = model.priced && report[COST] > 0
      ? formatPercent(totals[COST], report[COST]) + " of cost"
      : formatPercent(processed, processedTokens(report)) + " of tokens";
    var detailMetric = model.priced ? metric : "tokens";
    var period = view.primary.hourly ? "Hourly" : "Daily";

    var costDelta = "";
    var tokensDelta = "";
    if (view.previous) {
      var previous = before || emptyMetrics();
      costDelta = withPrevious(costText(previous), formatDelta(directionBetween(view.direction, totals, previous)));
      tokensDelta = withPrevious(formatTokens(processedTokens(previous)),
        formatDelta(directionOfTokens(view.direction, processed, processedTokens(previous)), false));
    }

    var tokenParts = [totals[UNCACHED], totals[CACHE_READ], cacheWriteTokens(totals), totals[OUTPUT]];
    var costParts = model.priced ? [
      totals[UNCACHED] * priceRate(model.price, "input"),
      totals[CACHE_READ] * priceRate(model.price, "cacheRead"),
      totals[WRITE_5M] * priceRate(model.price, "cacheWrite5m") + totals[WRITE_1H] * priceRate(model.price, "cacheWrite1h"),
      totals[OUTPUT] * priceRate(model.price, "output")
    ] : [];
    // Rates are USD per million tokens, so tokens x rate is already micro-USD.

    return {
      modelIndex: modelIndex,
      model: model.id,
      provider: model.provider,
      subtitle: PROVIDER_NAMES[model.provider] + " \u00B7 " + share,
      costText: unpricedNow ? "unpriced" : formatMoneyMicro(totals[COST]),
      tokensText: formatTokens(processed),
      perMillionText: unpricedNow
        ? "unpriced"
        : processed > 0 ? formatMoney(totals[COST] / processed) : formatMoneyMicro(0),
      cacheHitText: formatPercent(totals[CACHE_READ], inputTokens(totals)),
      costDeltaText: costDelta,
      tokensDeltaText: tokensDelta,
      isPriced: model.priced,
      metric: detailMetric,
      chartTitle: (detailMetric === "tokens" ? period + " tokens" : period + " API value"),
      chart: buildChart(view, detailMetric, modelIndex),
      costSplit: costParts.map(function (micro, index) {
        return { label: SPLIT_LABELS[index], value: micro, text: formatMoneyMicro(micro) };
      }),
      tokenSplit: tokenParts.map(function (tokens, index) {
        return { label: SPLIT_LABELS[index], value: tokens, text: formatTokens(tokens) };
      })
    };
  }

  // --- labels, notes and status ----------------------------------------------------

  function describePeriod(view, resolved) {
    if (resolved.rolling) return rollingRangeLabel(resolved.sinceMs, resolved.untilMs, view.data.zone);
    return rangeLabel(resolved.from, resolved.to, view.today);
  }

  function dayCountText(count) {
    return count === 1 ? "1 day" : count + " days";
  }

  // Captions above the chart: the range, the comparison, "as of" for the
  // rolling window and whether each side is only partly covered.
  function buildCaptions(view) {
    var comparing = Boolean(view.comparison);
    return {
      range: (comparing ? "A: " : "") + describePeriod(view, view.primary),
      rangePartial: view.primary.coverage.partial,
      comparison: comparing ? "vs B: " + describePeriod(view, view.comparison) : "",
      comparisonPartial: comparing && view.comparison.coverage.partial,
      asOf: view.primary.rolling
        ? "Past 24 hours as of " + shortInstantLabel(view.data.generatedAt, view.data.zone)
        : ""
    };
  }

  function coverageNote(view, resolved, name) {
    if (!resolved.coverage.partial) return null;
    var data = view.data;
    if (resolved.coverage.empty) {
      return "Partial data: no usage was shared for " + name + " (" + rangeLabel(resolved.from, resolved.to, view.today) +
        "). The shared report covers " + rangeLabel(data.dailyFrom, data.dailyTo, view.today) + ".";
    }
    return "Partial data: " + name + " is covered only from " +
      rangeLabel(resolved.coverage.from, resolved.coverage.to, view.today) +
      ". Days outside the shared report are left blank, not counted as zero.";
  }

  // Coverage and comparison caveats, in reading order.
  function buildNotes(view, chart) {
    var notes = [];
    var data = view.data;
    var primaryNote = coverageNote(view, view.primary, view.comparison ? "A" : "this range");
    if (primaryNote) notes.push(primaryNote);
    if (view.primary.hourlyFallback) {
      notes.push(data.hourlyFrom === null
        ? "Hourly data was not shared, so this range is shown by day."
        : "Hourly data is shared only for " + rangeLabel(data.hourlyFrom, data.hourlyTo, view.today) +
          ", so this range is shown by day.");
    }
    if (view.comparison) {
      var comparisonNote = coverageNote(view, view.comparison, "B");
      if (comparisonNote) notes.push(comparisonNote);
      if (chart && chart.comparisonUnavailable) {
        notes.push("B has no hourly data in the shared report, so its line is not drawn. Its totals still count.");
      }
      if (view.comparison.dayCount !== view.primary.dayCount) {
        notes.push("The comparison is " + dayCountText(view.comparison.dayCount) + ", this range " +
          dayCountText(view.primary.dayCount) + ": changes compare the average per day.");
      }
      if (chart && chart.averageDay && view.comparison.dayCount > 1) {
        notes.push("The dashed line is an average day of the comparison.");
      }
      var comparisonIsRolling = view.comparison.rolling;
      // Only a covered today counts: a day outside the report is already a partial-data note.
      var aIncludesToday = !view.primary.rolling && !view.primary.coverage.empty && view.primary.coverage.to >= view.today;
      var bIncludesToday = !comparisonIsRolling && !view.comparison.coverage.empty && view.comparison.coverage.to >= view.today;
      if (aIncludesToday !== bIncludesToday) {
        notes.push(aIncludesToday ? "A includes today so far." : "B includes today so far.");
      }
      var earlierFirst = Math.min(view.primary.from, view.comparison.from);
      if (data.firstDataDay !== null && earlierFirst < data.firstDataDay &&
          earlierFirst >= data.dailyFrom) {
        notes.push("The earlier period starts before the earliest recorded usage (" + dayLabel(data.firstDataDay) +
          "); older logs may have been deleted.");
      }
    }
    return notes;
  }

  // A single line for an empty report, or "".
  function buildEmptyStatus(view) {
    var any = hasData(view.current.totals) || (view.previous && hasData(view.previous.totals));
    if (any) return "";
    var data = view.data;
    if (data.firstDataDay === null) {
      return "The shared report holds no Claude Code or Codex usage yet.";
    }
    var empty = view.comparison ? "No local agent usage in either range." : "No local agent usage in this range.";
    return empty + " Recorded usage: " + rangeLabel(data.firstDataDay, data.lastDataDay, view.today) + ".";
  }

  // --- report discovery (contract P12, P16) ------------------------------------------

  // What to do with the snapshot's usage flag: "clear", "fetch" or "none".
  // sync: { displayedSeq, inFlightSeq }.
  function usageSyncAction(sync, flag) {
    if (!flag || typeof flag !== "object" || flag.shared !== true) return "clear";
    var seq = flag.seq;
    if (!isSafeCount(seq) || seq <= 0) return "none";
    if (sync.displayedSeq !== null && sync.displayedSeq !== undefined && seq <= sync.displayedSeq) return "none";
    if (sync.inFlightSeq === seq) return "none";
    return "fetch";
  }

  // True when a fetched report may replace what is on screen.
  function acceptUsageResponse(sync, generation, payloadSeq) {
    if (generation !== sync.generation) return false;
    return sync.displayedSeq === null || sync.displayedSeq === undefined || payloadSeq >= sync.displayedSeq;
  }

  // The checkedAt that belongs to the report on screen. A snapshot's checkedAt
  // confirms only its own seq: while a newer report has not loaded (or failed
  // to), the old numbers keep the freshness they had, never the new one.
  function confirmedCheckedAt(previous, flag, displayedSeq) {
    if (flag && flag.shared === true && displayedSeq !== null && displayedSeq !== undefined &&
        flag.seq === displayedSeq && typeof flag.checkedAt === "string" && isFinite(Date.parse(flag.checkedAt))) {
      return flag.checkedAt;
    }
    return previous === undefined ? null : previous;
  }

  // "Updated" line and stale banner for the usage report. `confirmed` carries
  // the checkedAt of the displayed seq (see confirmedCheckedAt), or nothing.
  function usageFreshness(confirmed, data, nowMs) {
    var flag = confirmed;
    var checked = flag && typeof flag.checkedAt === "string" ? Date.parse(flag.checkedAt) : NaN;
    var at = isFinite(checked) ? checked : data ? data.generatedAt : NaN;
    if (!isFinite(at)) return { updated: "", stale: false, at: null };
    var age = nowMs - at;
    return {
      updated: "Usage report updated " + formatAge(age),
      stale: age > STALE_MS,
      at: at
    };
  }

  // =================================================================================
  // DOM layer
  // =================================================================================

  var SVG_NS = "http://www.w3.org/2000/svg";

  function createController(options) {
    var doc = document;
    var apiBase = String(options.apiBase || "").replace(/\/+$/, "");
    var readId = options.readId;
    var panel = options.panel;
    var quotaPanel = options.quotaPanel;
    var tabList = options.tabList;
    var quotaTab = options.quotaTab;
    var usageTab = options.usageTab;
    var providerIcon = typeof options.providerIcon === "function" ? options.providerIcon : null;

    var sync = { generation: 0, displayedSeq: null, inFlightSeq: null };
    var data = null;
    var flag = null;
    var activeView = "quotas";
    var wantUsage = window.location.hash === "#usage";
    var state = defaultState();
    var view = null;
    var charts = [];
    var dom = null;
    var resizeObserver = null;
    var resizeTimer = 0;
    var idCounter = 0;
    // checkedAt of the report on screen, never of a seq that has not loaded.
    var checkedAt = null;

    function defaultState() {
      return {
        range: "30",
        customFrom: NaN,
        customTo: NaN,
        compare: "off",
        compareFrom: NaN,
        compareTo: NaN,
        account: null,
        metric: "cost",
        breakdown: "model",
        modelSort: MODEL_DEFAULT_SORT,
        timeSort: TIME_DEFAULT_SORT,
        openModel: null,
        back: null
      };
    }

    // --- tiny DOM helpers ---

    function el(tag, className, text) {
      var node = doc.createElement(tag);
      if (className) node.className = className;
      if (text !== undefined && text !== null) node.textContent = text;
      return node;
    }

    function svg(tag, attributes) {
      var node = doc.createElementNS(SVG_NS, tag);
      if (attributes) {
        Object.keys(attributes).forEach(function (name) { node.setAttribute(name, attributes[name]); });
      }
      return node;
    }

    function clear(node) {
      while (node.firstChild) node.removeChild(node.firstChild);
    }

    function setText(node, text) {
      if (node.textContent !== text) node.textContent = text;
    }

    function nextId(prefix) {
      idCounter += 1;
      return prefix + "-" + idCounter;
    }

    function button(className, text, onClick) {
      var node = el("button", className, text);
      node.type = "button";
      if (onClick) node.addEventListener("click", onClick);
      return node;
    }

    function focusKey(node, key) {
      node.setAttribute("data-focus-key", key);
      return node;
    }

    function providerMark(provider) {
      if (providerIcon) {
        var icon = providerIcon(provider);
        icon.classList.add("usage-provider-icon", "is-" + provider);
        return icon;
      }
      var dot = el("span", "usage-dot is-" + provider);
      dot.setAttribute("aria-hidden", "true");
      return dot;
    }

    // --- tabs and hash ---

    function setHash(usage) {
      var url = window.location.pathname + window.location.search + (usage ? "#usage" : "");
      try {
        window.history.replaceState(null, "", url);
      } catch (error) {
        if (usage) window.location.hash = "usage";
      }
    }

    function selectView(name, fromUser) {
      var target = name === "usage" && data ? "usage" : "quotas";
      if (target !== "usage") releaseOverlays();
      activeView = target;
      quotaPanel.hidden = target !== "quotas";
      panel.hidden = target !== "usage";
      doc.body.setAttribute("data-view", target);
      [quotaTab, usageTab].forEach(function (tab) {
        var selected = (tab === usageTab) === (target === "usage");
        tab.setAttribute("aria-selected", selected ? "true" : "false");
        tab.tabIndex = selected ? 0 : -1;
      });
      if (fromUser) {
        wantUsage = target === "usage";
        setHash(wantUsage);
      }
      if (target === "usage") redrawCharts();
    }

    // The panels are tab panels only while the switch is on screen.
    function setTabsVisible(visible) {
      tabList.hidden = !visible;
      if (visible) {
        quotaPanel.setAttribute("role", "tabpanel");
        quotaPanel.setAttribute("aria-labelledby", quotaTab.id);
      } else {
        quotaPanel.removeAttribute("role");
        quotaPanel.removeAttribute("aria-labelledby");
      }
    }

    function setupTabs() {
      quotaTab.addEventListener("click", function () { selectView("quotas", true); });
      usageTab.addEventListener("click", function () { selectView("usage", true); });
      tabList.addEventListener("keydown", function (event) {
        var keys = ["ArrowLeft", "ArrowRight", "Home", "End"];
        if (keys.indexOf(event.key) === -1) return;
        event.preventDefault();
        var next = event.key === "Home" ? "quotas" : event.key === "End" ? "usage"
          : activeView === "usage" ? "quotas" : "usage";
        selectView(next, true);
        (next === "usage" && data ? usageTab : quotaTab).focus();
      });
      window.addEventListener("hashchange", function () {
        wantUsage = window.location.hash === "#usage";
        selectView(wantUsage ? "usage" : "quotas", false);
      });
    }

    // --- discovery ---

    function onSnapshot(usageFlag) {
      var action = usageSyncAction(sync, usageFlag);
      if (action === "clear") {
        flag = null;
        clearUsage();
        return;
      }
      flag = usageFlag;
      checkedAt = confirmedCheckedAt(checkedAt, usageFlag, sync.displayedSeq);
      renderFreshness();
      if (action === "fetch") fetchUsage(usageFlag.seq);
    }

    function clearUsage() {
      sync.generation += 1;
      sync.displayedSeq = null;
      sync.inFlightSeq = null;
      data = null;
      checkedAt = null;
      view = null;
      charts = [];
      if (dom && dom.dialog.open) dom.dialog.close();
      state.openModel = null;
      closeAllPops();
      if (dom) {
        clear(panel);
        dom = null;
      }
      setTabsVisible(false);
      if (activeView === "usage") selectView("quotas", false);
      if (window.location.hash === "#usage") setHash(false);
      wantUsage = false;
    }

    // Shows a fetched report. One that cannot be drawn is treated like an
    // invalid response: the previous report stays, or the tab stays hidden.
    function adopt(prepared) {
      var previous = { data: data, seq: sync.displayedSeq, checkedAt: checkedAt };
      data = prepared;
      sync.displayedSeq = prepared.seq;
      // A new seq starts without a confirmation of its own.
      checkedAt = confirmedCheckedAt(previous.seq === prepared.seq ? previous.checkedAt : null, flag, prepared.seq);
      if (render()) {
        setTabsVisible(true);
        if (wantUsage && activeView !== "usage") selectView("usage", false);
        return;
      }
      data = previous.data;
      sync.displayedSeq = previous.seq;
      checkedAt = previous.checkedAt;
      if (data && render()) return;
      data = null;
      sync.displayedSeq = null;
      checkedAt = null;
      if (dom) {
        clear(panel);
        dom = null;
      }
      setTabsVisible(false);
      if (activeView === "usage") selectView("quotas", false);
    }

    function fetchUsage(seq) {
      sync.generation += 1;
      var generation = sync.generation;
      sync.inFlightSeq = seq;
      fetch(apiBase + "/u/" + readId + "/usage", {
        cache: "no-store",
        headers: { Accept: "application/json" }
      })
        .then(function (response) {
          if (!response.ok) throw new Error(response.status === 404 ? "not_found" : "http_" + response.status);
          return response.json();
        })
        .then(function (json) {
          var prepared = prepareUsage(json);
          if (!prepared) throw new Error("bad_payload");
          if (!acceptUsageResponse(sync, generation, prepared.seq)) return;
          adopt(prepared);
        })
        .catch(function () {
          // 404 while the flag is set, a network error or an unreadable
          // report: try again on the next snapshot poll.
        })
        .then(function () {
          if (generation === sync.generation) sync.inFlightSeq = null;
        });
    }

    // --- skeleton (built once per report session) ---

    function segment(group, value, text, onClick) {
      var node = button("usage-segment", text, onClick);
      node.setAttribute("data-value", value);
      node.setAttribute("aria-pressed", "false");
      group.appendChild(node);
      return node;
    }

    // A range segment: a long label on wide screens, a short one on phones.
    function rangeSegment(group, value, long, short, name, onClick) {
      var node = segment(group, value, null, onClick);
      node.appendChild(el("span", "usage-long", long));
      node.appendChild(el("span", "usage-short", short));
      node.setAttribute("aria-label", name);
      return node;
    }

    function chevron() {
      var node = el("span", "usage-chevron", "▾");
      node.setAttribute("aria-hidden", "true");
      return node;
    }

    // The dashed marker that stands for period B.
    function dashMark() {
      var dash = svg("svg", { "class": "usage-dash", viewBox: "0 0 16 4", width: "16", height: "4", "aria-hidden": "true" });
      dash.appendChild(svg("line", { x1: "0", y1: "2", x2: "16", y2: "2" }));
      return dash;
    }

    // --- the month calendar (the desktop DateRangePicker) -------------------
    // A six-week grid of day buttons with one tab stop. Arrow keys move by a
    // day or a week, Page Up and Page Down by a month, Home and End to the
    // ends of the week; Enter or Space picks, and Escape is the popover's.

    function weekStart() {
      try {
        var locale = new Intl.Locale(navigator.language || "en-US");
        var info = typeof locale.getWeekInfo === "function" ? locale.getWeekInfo() : locale.weekInfo;
        if (info && info.firstDay) return info.firstDay % 7;
      } catch (error) { /* older engines: Sunday, as en-US */ }
      return 0;
    }

    function buildCalendar(onPick) {
      var root = el("div", "usage-cal");
      var head = el("div", "usage-cal-head");
      var cal = {
        root: root,
        month: 0,
        today: 0,
        min: 0,
        max: 0,
        selection: null,
        reference: null,
        focusDay: null,
        days: [],
        firstDay: weekStart()
      };
      var previous = button("usage-cal-nav", "‹", function () { cal.show(addMonths(cal.month, -1)); });
      previous.setAttribute("aria-label", "Previous month");
      var title = el("p", "usage-cal-title");
      title.id = nextId("usage-cal-title");
      title.setAttribute("aria-live", "polite");
      var next = button("usage-cal-nav", "›", function () { cal.show(addMonths(cal.month, 1)); });
      next.setAttribute("aria-label", "Next month");
      head.appendChild(previous);
      head.appendChild(title);
      head.appendChild(next);
      root.appendChild(head);
      var grid = el("div", "usage-cal-grid");
      grid.setAttribute("role", "grid");
      grid.setAttribute("aria-labelledby", title.id);
      root.appendChild(grid);

      function allowed(day) {
        return day >= cal.min && day <= cal.max;
      }

      function clampDay(day) {
        return Math.max(cal.min, Math.min(cal.max, day));
      }

      function render() {
        var parts = dayParts(cal.month);
        setText(title, monthTitle(cal.month));
        previous.disabled = cal.month <= monthStart(cal.min);
        next.disabled = cal.month >= monthStart(cal.max);
        var days = monthGrid(parts.year, parts.month + 1, cal.firstDay);
        cal.days = days;
        if (cal.focusDay === null || days.indexOf(cal.focusDay) === -1 || !allowed(cal.focusDay)) {
          cal.focusDay = days.filter(function (day) { return allowed(day) && monthStart(day) === cal.month; })[0];
          if (cal.focusDay === undefined) cal.focusDay = days[0];
        }
        clear(grid);
        var header = el("div", "usage-cal-row");
        header.setAttribute("role", "row");
        for (var column = 0; column < 7; column++) {
          var weekday = (cal.firstDay + column) % 7;
          var name = el("span", "usage-cal-weekday", WEEKDAYS[weekday].charAt(0));
          name.setAttribute("role", "columnheader");
          name.setAttribute("aria-label", WEEKDAYS[weekday]);
          header.appendChild(name);
        }
        grid.appendChild(header);
        for (var week = 0; week < 6; week++) {
          var row = el("div", "usage-cal-row");
          row.setAttribute("role", "row");
          for (var index = 0; index < 7; index++) row.appendChild(dayCell(days[week * 7 + index]));
          grid.appendChild(row);
        }
      }

      function dayCell(day) {
        var cell = el("span", "usage-cal-cell");
        cell.setAttribute("role", "gridcell");
        var classes = ["usage-cal-day"];
        if (monthStart(day) !== cal.month) classes.push("is-outside");
        if (day === cal.today) classes.push("is-today");
        if (!allowed(day)) classes.push("is-disabled");
        var inRange = cal.selection && cal.selection.contains(day);
        if (inRange) classes.push("is-range");
        if (cal.selection && (day === cal.selection.from || day === cal.selection.to)) classes.push("is-end");
        if (cal.reference && day >= cal.reference.from && day <= cal.reference.to) classes.push("is-reference");
        var node = button(classes.join(" "), String(dayParts(day).date), function (event) { pick(day, event.shiftKey); });
        node.setAttribute("data-day", String(day));
        node.tabIndex = day === cal.focusDay ? 0 : -1;
        var label = longDayLabel(day, -1);
        if (cal.reference && classes.indexOf("is-reference") !== -1) label += ", in the other period";
        node.setAttribute("aria-label", label);
        node.setAttribute("aria-pressed", inRange ? "true" : "false");
        if (!allowed(day)) node.setAttribute("aria-disabled", "true");
        node.addEventListener("keydown", function (event) { onKey(event, day); });
        cell.appendChild(node);
        return cell;
      }

      function focusCurrent() {
        var node = grid.querySelector('[data-day="' + cal.focusDay + '"]');
        if (node) node.focus();
      }

      function moveTo(day) {
        cal.focusDay = clampDay(day);
        if (monthStart(cal.focusDay) !== cal.month) cal.month = monthStart(cal.focusDay);
        render();
        focusCurrent();
      }

      function onKey(event, day) {
        var offset = (dayParts(day).weekday - cal.firstDay + 7) % 7;
        var target;
        switch (event.key) {
          case "ArrowLeft": target = day - 1; break;
          case "ArrowRight": target = day + 1; break;
          case "ArrowUp": target = day - 7; break;
          case "ArrowDown": target = day + 7; break;
          case "Home": target = day - offset; break;
          case "End": target = day + (6 - offset); break;
          case "PageUp":
          case "PageDown":
            var month = addMonths(monthStart(day), event.key === "PageUp" ? -1 : 1);
            var last = addMonths(month, 1) - 1;
            target = Math.min(month + dayParts(day).date - 1, last);
            break;
          default: return;
        }
        event.preventDefault();
        moveTo(target);
      }

      function pick(day, extend) {
        if (!allowed(day)) return;
        cal.focusDay = day;
        var result = onPick(day, Boolean(extend));
        if (!result || !result.changed) return;
        if (result.reset) return;
        if (monthStart(day) !== cal.month) cal.month = monthStart(day);
        render();
        focusCurrent();
      }

      // Starts over on a selection; the month shown holds its end.
      cal.reset = function (options) {
        cal.today = options.today;
        cal.min = options.min;
        cal.max = options.max;
        cal.selection = options.selection;
        cal.reference = options.reference || null;
        var shown = cal.selection.to !== null ? cal.selection.to
          : cal.selection.from !== null ? cal.selection.from : options.today;
        cal.focusDay = null;
        cal.month = monthStart(clampDay(shown));
        render();
      };

      cal.show = function (month) {
        var first = monthStart(cal.min);
        var last = monthStart(cal.max);
        cal.month = Math.max(first, Math.min(last, month));
        render();
      };

      // Focus the selected day, or today, when the month shown has it;
      // otherwise the first day of the month that can be picked.
      cal.focusSelection = function () {
        var target = cal.selection && cal.selection.from !== null ? cal.selection.from : cal.today;
        cal.focusDay = cal.days.indexOf(target) !== -1 && allowed(target) ? target : null;
        render();
        focusCurrent();
      };

      return cal;
    }

    // The Custom popover: "Show a day or a range".
    function buildRangePopover() {
      var box = el("div", "usage-pop usage-cal-pop");
      box.hidden = true;
      box.id = nextId("usage-custom");
      box.setAttribute("role", "dialog");
      var heading = el("p", "usage-picker-title", "Show a day or a range");
      heading.id = nextId("usage-custom-title");
      box.setAttribute("aria-labelledby", heading.id);
      box.appendChild(heading);
      box.appendChild(el("p", "usage-picker-help", "Click the first day, then the last. A single day is shown by hour."));
      var calendar = buildCalendar(function (day, extend) {
        var changed = calendar.selection.click(day, extend);
        updateRangeText();
        return { changed: changed, reset: false };
      });
      box.appendChild(calendar.root);
      var text = el("p", "usage-cal-selection");
      text.setAttribute("aria-live", "polite");
      box.appendChild(text);
      var actions = el("div", "usage-picker-actions");
      actions.appendChild(button("usage-button", "Cancel", function () { closePop(dom.popCustom, true); }));
      var apply = button("usage-button usage-button-primary", "Apply", function () { applyCustomPicker(); });
      actions.appendChild(apply);
      box.appendChild(actions);
      return { box: box, calendar: calendar, text: text, apply: apply };
    }

    function periodCard(label, onSelect) {
      var card = button("usage-period-card", null, onSelect);
      card.setAttribute("aria-pressed", "false");
      card.appendChild(el("span", "usage-period-label", label));
      var range = el("span", "usage-period-range");
      var days = el("span", "usage-period-days");
      card.appendChild(range);
      card.appendChild(days);
      return { node: card, range: range, days: days };
    }

    // The Compare popover: "Compare two periods".
    function buildComparePopover() {
      var box = el("div", "usage-pop usage-cal-pop usage-compare-pop");
      box.hidden = true;
      box.id = nextId("usage-compare");
      box.setAttribute("role", "dialog");
      var heading = el("p", "usage-picker-title", "Compare two periods");
      heading.id = nextId("usage-compare-title");
      box.setAttribute("aria-labelledby", heading.id);
      box.appendChild(heading);
      box.appendChild(el("p", "usage-picker-help", "Pick a period, then click the first day and the last."));
      var cards = el("div", "usage-period-cards");
      cards.setAttribute("role", "group");
      cards.setAttribute("aria-label", "Period to edit");
      var cardA = periodCard("PERIOD A", function () { selectComparePeriod(true); });
      var cardB = periodCard("PERIOD B", function () { selectComparePeriod(false); });
      cardA.node.setAttribute("aria-label", "Period A");
      cardB.node.setAttribute("aria-label", "Period B");
      cards.appendChild(cardA.node);
      cards.appendChild(cardB.node);
      box.appendChild(cards);
      var calendar = buildCalendar(function (day, extend) { return pickComparisonDay(day, extend); });
      box.appendChild(calendar.root);
      var hint = el("p", "usage-picker-help usage-compare-hint");
      hint.setAttribute("aria-live", "polite");
      box.appendChild(hint);
      var lengthNote = el("p", "usage-picker-help", "Different lengths: changes compare the average per day.");
      lengthNote.hidden = true;
      box.appendChild(lengthNote);
      var footer = el("div", "usage-picker-footer");
      var off = button("usage-link usage-compare-off", "Turn off", function () { turnComparisonOff(); });
      footer.appendChild(off);
      var actions = el("div", "usage-picker-actions");
      actions.appendChild(button("usage-button", "Cancel", function () { closePop(dom.popCompare, true); }));
      var apply = button("usage-button usage-button-primary", "Apply", function () { applyComparison(); });
      actions.appendChild(apply);
      footer.appendChild(actions);
      box.appendChild(footer);
      return { box: box, calendar: calendar, cardA: cardA, cardB: cardB, hint: hint, lengthNote: lengthNote, off: off, apply: apply };
    }

    // --- popovers -------------------------------------------------------------
    // One stack for the range picker, the comparison menu, its date picker and
    // the phone Filters sheet. Escape closes the top one and returns focus to
    // the button that opened it; a press outside closes whatever it is outside.

    var pops = [];

    // --- background scroll lock ---------------------------------------------
    // While any popover, the sheet, a picker or the model dialog is open the
    // page behind stays put, on mobile Safari and Chrome too: the body is
    // fixed at the current offset and the root cannot scroll. A counter keeps
    // nested opens (a picker inside the sheet) from unlocking early.

    var lockCount = 0;
    // Each open of the model dialog has a generation; its cleanup (lock,
    // state, charts, focus) runs once, from whichever close signal comes first.
    var dialogGeneration = 0;
    var dialogCleaned = 0;
    var lockedY = 0;

    function lockScroll() {
      lockCount += 1;
      if (lockCount !== 1) return;
      lockedY = window.scrollY || window.pageYOffset || 0;
      doc.body.style.top = -lockedY + "px";
      doc.documentElement.classList.add("usage-scroll-locked");
    }

    function unlockScroll() {
      if (lockCount === 0) return;
      lockCount -= 1;
      if (lockCount !== 0) return;
      doc.documentElement.classList.remove("usage-scroll-locked");
      doc.body.style.top = "";
      window.scrollTo(0, lockedY);
    }

    function isShown(node) {
      return Boolean(node && (node.offsetWidth || node.offsetHeight || node.getClientRects().length));
    }

    // A box shown by the is-open class (the sheet).
    function popEntry(box, trigger) {
      return {
        box: box,
        trigger: trigger,
        show: function () { box.classList.add("is-open"); },
        hide: function () { box.classList.remove("is-open"); }
      };
    }

    // A box shown by the hidden attribute (the calendar popovers).
    function hiddenEntry(box, trigger) {
      return {
        box: box,
        trigger: trigger,
        show: function () { box.hidden = false; },
        hide: function () { box.hidden = true; }
      };
    }

    function isOpen(entry) {
      return Boolean(entry) && pops.indexOf(entry) !== -1;
    }

    function openPop(entry) {
      pops.slice().reverse().forEach(function (open) {
        if (open !== entry && !open.box.contains(entry.box)) closePop(open, false);
      });
      if (pops.indexOf(entry) === -1) {
        pops.push(entry);
        lockScroll();
      }
      entry.show();
      entry.trigger.setAttribute("aria-expanded", "true");
    }

    function closePop(entry, focusTrigger) {
      if (!isOpen(entry)) return;
      // Anything opened inside it closes with it.
      pops.slice().reverse().forEach(function (inner) {
        if (inner !== entry && entry.box.contains(inner.box)) closePop(inner, false);
      });
      pops.splice(pops.indexOf(entry), 1);
      entry.hide();
      entry.trigger.setAttribute("aria-expanded", "false");
      unlockScroll();
      // Without scrolling: the page goes back to where the user left it.
      if (focusTrigger && isShown(entry.trigger)) entry.trigger.focus({ preventScroll: true });
    }

    function closeAllPops() {
      pops.slice().reverse().forEach(function (entry) { closePop(entry, false); });
    }

    // Closes every popover, the sheet and the model dialog, and with them the
    // scroll lock: used whenever the Usage panel is hidden.
    function releaseOverlays() {
      closeAllPops();
      if (dom && dom.dialog.open) dom.dialog.close();
    }

    // The keyboard-reachable controls inside an overlay, in order.
    function focusablesIn(box) {
      return Array.prototype.slice.call(box.querySelectorAll("button, select, input, textarea, a[href], [tabindex]"))
        .filter(function (node) {
          return node.tabIndex >= 0 && !node.disabled && isShown(node) &&
            window.getComputedStyle(node).visibility !== "hidden";
        });
    }

    function onDocumentKey(event) {
      if (pops.length === 0 || (dom && dom.dialog.open)) return;
      var top = pops[pops.length - 1];
      if (event.key === "Escape") {
        event.preventDefault();
        closePop(top, true);
        return;
      }
      // Phone sheets keep Tab and Shift+Tab inside: they cover the page.
      if (event.key === "Tab" && isPhone()) {
        var items = focusablesIn(top.box);
        if (items.length === 0) return;
        var next = wrapFocusIndex(items.indexOf(doc.activeElement), items.length, event.shiftKey);
        if (next !== null) {
          event.preventDefault();
          items[next].focus();
        }
      }
    }

    // Closes the overlays a target lies outside of, top first.
    function closeOutside(target) {
      for (var i = pops.length - 1; i >= 0; i--) {
        var entry = pops[i];
        if (!entry) continue;
        if (entry.box.contains(target) || entry.trigger.contains(target)) break;
        closePop(entry, false);
      }
    }

    function onDocumentPointer(event) {
      closeOutside(event.target);
    }

    // Focus never reaches the page behind an overlay: a desktop popover
    // closes when focus leaves it (and frees the page), and a phone sheet
    // takes focus back.
    function onDocumentFocusIn(event) {
      if (pops.length === 0) return;
      var target = event.target;
      var top = pops[pops.length - 1];
      if (top.box.contains(target) || top.trigger.contains(target)) return;
      if (isPhone()) {
        var items = focusablesIn(top.box);
        if (items.length) items[0].focus();
        return;
      }
      closeOutside(target);
    }

    function isPhone() {
      return Boolean(phoneQuery && phoneQuery.matches);
    }

    // Crossing the phone breakpoint closes whatever is open (the controls
    // change place), which also releases the scroll lock, and focus goes to
    // the visible control that stands for it. A breakdown sorted on a column
    // the phone leaves out falls back to the default there.
    function onLayoutChange() {
      if (!dom) return;
      if (pops.length) {
        var root = pops[0];
        var active = doc.activeElement;
        var inside = pops.some(function (entry) { return entry.box.contains(active) || entry.trigger === active; });
        closeAllPops();
        if (inside || !isShown(active)) {
          var target = isShown(root.trigger) ? root.trigger
            : isShown(dom.compareButton) ? dom.compareButton : dom.filtersButton;
          if (isShown(target)) target.focus({ preventScroll: true });
        }
      }
      if (data && view && isPhone() && phoneSortFallback()) renderResults();
    }

    // Resets a sort the phone cannot show; true when it changed anything.
    function phoneSortFallback() {
      var changed = false;
      if (hiddenOnPhone(state.modelSort.column, Boolean(view && view.previous))) {
        state.modelSort = MODEL_DEFAULT_SORT;
        changed = true;
      }
      if (hiddenOnPhone(state.timeSort.column, false)) {
        state.timeSort = TIME_DEFAULT_SORT;
        changed = true;
      }
      return changed;
    }

    function buildSkeleton() {
      clear(panel);
      closeAllPops();
      var root = el("div", "usage");

      var stale = el("p", "notice notice-stale usage-stale");
      stale.setAttribute("role", "status");
      stale.hidden = true;
      root.appendChild(stale);

      var toolbar = el("div", "usage-toolbar");

      // Left: Clear filters, the period lines, and the freshness line.
      var period = el("div", "usage-period");
      var clearLink = button("usage-link usage-clear", "Clear filters", clearFilters);
      period.appendChild(clearLink);
      var periodLines = el("div", "usage-period-lines");
      period.appendChild(periodLines);
      var fresh = el("p", "usage-fresh");
      var updated = el("span", "usage-updated");
      var zoneNote = el("span", "usage-zone");
      fresh.appendChild(updated);
      fresh.appendChild(zoneNote);
      period.appendChild(fresh);
      toolbar.appendChild(period);

      // Right: account, range and comparison; on phones, range and Filters.
      var controls = el("div", "usage-controls");

      var rangeWrap = el("div", "usage-anchor usage-range-wrap");
      var rangeGroup = el("div", "usage-segments usage-range");
      rangeGroup.setAttribute("role", "group");
      rangeGroup.setAttribute("aria-label", "Range");
      var rangeButtons = [
        ["24h", "Past 24h", "24h", "Past 24 hours"],
        ["7", "7 days", "7d", "7 days"],
        ["30", "30 days", "30d", "30 days"],
        ["90", "90 days", "90d", "90 days"]
      ].map(function (entry) {
        return rangeSegment(rangeGroup, entry[0], entry[1], entry[2], entry[3], function () { applyRange(entry[0]); });
      });
      var customButton = rangeSegment(rangeGroup, "custom", "Custom", "⋯", "Custom dates", function () { toggleCustomPicker(); });
      customButton.querySelector(".usage-long").appendChild(chevron());
      customButton.setAttribute("aria-haspopup", "dialog");
      customButton.setAttribute("aria-expanded", "false");
      rangeWrap.appendChild(rangeGroup);
      var custom = buildRangePopover();
      customButton.setAttribute("aria-controls", custom.box.id);
      rangeWrap.appendChild(custom.box);
      controls.appendChild(rangeWrap);

      var filtersButton = button("usage-filters-button", null, function () { toggleFilters(); });
      filtersButton.appendChild(el("span", null, "Filters"));
      var badge = el("span", "usage-badge");
      badge.setAttribute("aria-hidden", "true");
      badge.hidden = true;
      filtersButton.appendChild(badge);
      filtersButton.setAttribute("aria-expanded", "false");
      controls.appendChild(filtersButton);

      // On wide screens these sit in the row; on phones they form the sheet.
      var filterPanel = el("div", "usage-filter-panel");
      filterPanel.id = nextId("usage-filters");
      filterPanel.setAttribute("role", "group");
      filterPanel.setAttribute("aria-label", "Filters");
      filtersButton.setAttribute("aria-controls", filterPanel.id);
      filterPanel.appendChild(el("p", "usage-sheet-title", "Filters"));

      var accountLabel = el("label", "usage-field usage-account");
      accountLabel.appendChild(el("span", "usage-field-label", "Account"));
      var accountSelect = el("select", "usage-select");
      accountSelect.addEventListener("change", function () {
        state.account = accountSelect.value === "" ? null : accountSelection(data, Number(accountSelect.value));
        state.back = null;
        render();
      });
      accountLabel.appendChild(accountSelect);
      filterPanel.appendChild(accountLabel);

      // Compare opens the two-period picker straight away, as on the desktop.
      var compareWrap = el("div", "usage-anchor usage-compare");
      var compareButton = button("usage-compare-button", null, function () { toggleComparePicker(); });
      var compareText = el("span", null, "Compare");
      compareButton.appendChild(compareText);
      compareButton.appendChild(chevron());
      compareButton.setAttribute("aria-haspopup", "dialog");
      compareButton.setAttribute("aria-expanded", "false");
      compareWrap.appendChild(compareButton);
      var compare = buildComparePopover();
      compareButton.setAttribute("aria-controls", compare.box.id);
      compareWrap.appendChild(compare.box);
      filterPanel.appendChild(compareWrap);

      var sheetClear = button("usage-button usage-sheet-clear", "Clear filters", clearFilters);
      filterPanel.appendChild(sheetClear);
      controls.appendChild(filterPanel);
      toolbar.appendChild(controls);
      root.appendChild(toolbar);

      var results = el("div", "usage-results");
      root.appendChild(results);

      var live = el("p", "sr-only");
      live.setAttribute("aria-live", "polite");
      root.appendChild(live);

      var dialog = el("dialog", "usage-dialog");
      var dialogTitleId = nextId("usage-dialog-title");
      dialog.setAttribute("aria-labelledby", dialogTitleId);
      var dialogHeader = el("div", "usage-dialog-header");
      var dialogTitle = el("h2", "usage-dialog-title");
      dialogTitle.id = dialogTitleId;
      dialogHeader.appendChild(dialogTitle);
      var dialogClose = button("usage-button usage-dialog-close", "Close", function () { dialog.close(); });
      dialogClose.setAttribute("aria-label", "Close details");
      dialogHeader.appendChild(dialogClose);
      dialog.appendChild(dialogHeader);
      var dialogBody = el("div", "usage-dialog-body");
      dialog.appendChild(dialogBody);
      dialog.addEventListener("click", function (event) {
        if (event.target !== dialog) return;
        var bounds = dialog.getBoundingClientRect();
        if (event.clientX < bounds.left || event.clientX > bounds.right ||
            event.clientY < bounds.top || event.clientY > bounds.bottom) dialog.close();
      });
      // The dialog closing is seen first by the open attribute going away;
      // its close event can arrive a while later, even after the dialog was
      // opened again. Both call the same cleanup, which ignores a closing
      // that is not the current open's or that already ran.
      function dialogClosed() {
        if (dialog.open || dialogCleaned === dialogGeneration) return;
        dialogCleaned = dialogGeneration;
        unlockScroll();
        var open = state.openModel;
        state.openModel = null;
        charts = charts.filter(function (chart) { return !chart.inDialog; });
        if (open) focusByKey("model:" + open);
      }
      if (typeof MutationObserver === "function") {
        new MutationObserver(dialogClosed).observe(dialog, { attributes: true, attributeFilter: ["open"] });
      }
      dialog.addEventListener("close", dialogClosed);
      root.appendChild(dialog);

      panel.appendChild(root);
      dom = {
        updated: updated,
        zoneNote: zoneNote,
        stale: stale,
        clearLink: clearLink,
        periodLines: periodLines,
        rangeButtons: rangeButtons,
        customButton: customButton,
        filtersButton: filtersButton,
        badge: badge,
        accountSelect: accountSelect,
        accountSignature: "",
        compareButton: compareButton,
        compareText: compareText,
        sheetClear: sheetClear,
        custom: custom,
        compare: compare,
        popCustom: hiddenEntry(custom.box, customButton),
        popCompare: hiddenEntry(compare.box, compareButton),
        popCompareInline: hiddenEntry(compare.box, compareButton),
        popFilters: popEntry(filterPanel, filtersButton),
        results: results,
        live: live,
        dialog: dialog,
        dialogTitle: dialogTitle,
        dialogBody: dialogBody
      };

      if (!resizeObserver && typeof ResizeObserver === "function") {
        resizeObserver = new ResizeObserver(function () {
          window.clearTimeout(resizeTimer);
          resizeTimer = window.setTimeout(redrawCharts, 80);
        });
        resizeObserver.observe(panel);
        resizeObserver.observe(dialog);
      }
    }

    // --- control actions ---

    function today() {
      return todayIn(data.zone, Date.now());
    }

    function applyRange(range) {
      state.range = range;
      state.back = null;
      closeAllPops();
      render();
    }

    function setMetric(metric) {
      state.metric = metric;
      render();
    }

    function clearFilters() {
      state.range = "30";
      state.account = null;
      state.compare = "off";
      state.back = null;
      closeAllPops();
      render();
      dom.rangeButtons[2].focus();
    }

    function toggleFilters() {
      if (isOpen(dom.popFilters)) {
        closePop(dom.popFilters, false);
        return;
      }
      openPop(dom.popFilters);
      dom.accountSelect.focus();
    }

    // --- Custom: one day or a range of days ---

    function toggleCustomPicker() {
      if (isOpen(dom.popCustom)) {
        closePop(dom.popCustom, false);
        return;
      }
      var now = today();
      var limits = customRangeLimits(now);
      var shown = view ? view.primary : null;
      var from = state.range === "custom" ? state.customFrom : shown ? shown.from : now;
      var to = state.range === "custom" ? state.customTo : shown ? shown.to : now;
      dom.custom.calendar.reset({
        today: now,
        min: limits.min,
        max: limits.max,
        selection: createRangeSelection(false, limits.maxSpan, function (day) {
          return day >= limits.min && day <= limits.max;
        }, from, to)
      });
      updateRangeText();
      openPop(dom.popCustom);
      dom.custom.calendar.focusSelection();
    }

    function updateRangeText() {
      var selection = dom.custom.calendar.selection;
      setText(dom.custom.text, rangeSelectionText(selection, today()));
      dom.custom.apply.disabled = selection.from === null || selection.to === null;
    }

    function applyCustomPicker() {
      var selection = dom.custom.calendar.selection;
      if (selection.from === null || selection.to === null) return;
      if (!isValidCustomRange(selection.from, selection.to, today())) return;
      state.range = "custom";
      state.customFrom = selection.from;
      state.customTo = selection.to;
      state.back = null;
      closePop(dom.popCustom, true);
      render();
    }

    // --- Compare: two periods, A against B ---

    var compareSession = null;

    // The days of the period just before the range on screen.
    function previousDays() {
      var resolved = resolvePeriod(data, previousPeriod(view.selection.period));
      return { from: resolved.from, to: resolved.to };
    }

    function toggleComparePicker() {
      if (isOpen(dom.popCompare)) {
        closePop(dom.popCompare, false);
        return;
      }
      var now = today();
      var picked = state.compare === "custom" && isFinite(state.compareFrom)
        ? { from: state.compareFrom, to: state.compareTo }
        : null;
      compareSession = createCompareSession({
        a: view ? { from: view.primary.from, to: view.primary.to } : { from: now, to: now },
        b: picked || (view ? previousDays() : { from: now - 1, to: now - 1 }),
        bIsPrevious: picked === null,
        today: now
      });
      dom.compare.off.hidden = state.compare === "off";
      showComparePeriod();
      if (isPhone() && isOpen(dom.popFilters)) {
        handOffSheet();
      } else {
        dom.popCompare = dom.popCompareInline;
        openPop(dom.popCompare);
      }
      dom.compare.calendar.focusSelection();
    }

    // On a phone the picker replaces the Filters sheet instead of stacking on
    // it. The picker's element lives inside the sheet, so the sheet stays laid
    // out but invisible until the picker closes. Its scroll lock passes to the
    // picker without ever being released, so the page cannot jump. The
    // Filters button is the visible trigger: focus returns there.
    function handOffSheet() {
      var sheet = dom.popFilters.box;
      var box = dom.compare.box;
      pops.splice(pops.indexOf(dom.popFilters), 1);
      dom.popFilters.trigger.setAttribute("aria-expanded", "false");
      sheet.classList.add("is-handoff");
      sheet.classList.remove("is-open");
      dom.popCompare = {
        box: box,
        trigger: dom.filtersButton,
        show: function () { box.hidden = false; },
        hide: function () {
          box.hidden = true;
          sheet.classList.remove("is-handoff");
        }
      };
      openPop(dom.popCompare);
      // The sheet's lock is the picker's now: drop the extra one openPop took.
      unlockScroll();
    }

    function showComparePeriod() {
      var limits = compareSession.limits;
      dom.compare.calendar.reset({
        today: today(),
        min: limits.min,
        max: limits.max,
        selection: compareSession.selection,
        reference: compareSession.reference()
      });
      updateCompareText();
    }

    function selectComparePeriod(editA) {
      if (!compareSession) return;
      compareSession.select(editA);
      showComparePeriod();
    }

    function pickComparisonDay(day, extend) {
      var result = compareSession.click(day, extend);
      if (!result.changed) return { changed: false };
      if (result.switched) {
        // Period A is complete: the calendar moves on to period B.
        showComparePeriod();
        dom.compare.calendar.focusSelection();
        return { changed: true, reset: true };
      }
      updateCompareText();
      return { changed: true, reset: false };
    }

    function setCard(card, text) {
      var lines = text.split("\n");
      setText(card.range, lines[0]);
      setText(card.days, lines[1] || "");
    }

    function describeDays(period) {
      return period
        ? rangeLabel(period.from, period.to, today()) + "\n" + dayCountText(periodLength(period))
        : "Not picked";
    }

    // The two cards, the hint and Apply, as the desktop describes them.
    function updateCompareText() {
      var session = compareSession;
      var selectedCount = view ? view.primary.dayCount : 1;
      var bFollowsA = session.bFollowsA();
      var textA = session.aEdited || !view
        ? describeDays(session.a)
        : describePeriod(view, view.primary) + "\n" + dayCountText(selectedCount);
      var textB = bFollowsA && !session.aEdited && view
        ? describePeriod(view, resolvePeriod(data, previousPeriod(view.selection.period))) + "\n" + dayCountText(selectedCount)
        : describeDays(session.b);
      if (session.selection.awaitingEnd && session.selection.from !== null) {
        var pending = dayLabel(session.selection.from) + " to …\npick the last day";
        if (session.editA) textA = pending;
        else textB = pending;
      }
      setCard(dom.compare.cardA, textA);
      setCard(dom.compare.cardB, textB);
      dom.compare.cardA.node.setAttribute("aria-pressed", session.editA ? "true" : "false");
      dom.compare.cardB.node.setAttribute("aria-pressed", session.editA ? "false" : "true");
      setText(dom.compare.hint, session.hint());
      var lengthA = session.aEdited ? periodLength(session.a) : selectedCount;
      var lengthB = bFollowsA ? lengthA : periodLength(session.b);
      dom.compare.lengthNote.hidden = !(lengthA > 0 && lengthB > 0 && lengthA !== lengthB);
      dom.compare.apply.disabled = !session.canApply();
    }

    function applyComparison() {
      if (!compareSession || !compareSession.canApply()) return;
      var result = compareSession.result();
      var now = today();
      if ((result.current && !isValidCustomRange(result.current.from, result.current.to, now)) ||
          (result.comparison && !isValidCustomRange(result.comparison.from, result.comparison.to, now))) {
        return;
      }
      if (result.current) {
        state.back = null;
        state.range = "custom";
        state.customFrom = result.current.from;
        state.customTo = result.current.to;
      }
      if (result.comparison) {
        state.compare = "custom";
        state.compareFrom = result.comparison.from;
        state.compareTo = result.comparison.to;
      } else {
        state.compare = "previous";
      }
      closePop(dom.popCompare, true);
      render();
    }

    function turnComparisonOff() {
      state.compare = "off";
      closePop(dom.popCompare, true);
      render();
    }

    function drillDay(day) {
      if (!view) return;
      var shift = view.comparison ? view.comparison.from - view.primary.from : null;
      state.back = {
        range: state.range,
        customFrom: state.customFrom,
        customTo: state.customTo,
        compare: state.compare,
        compareFrom: state.compareFrom,
        compareTo: state.compareTo,
        breakdown: state.breakdown,
        modelSort: state.modelSort,
        timeSort: state.timeSort,
        label: backLabel()
      };
      state.range = "custom";
      state.customFrom = day;
      state.customTo = day;
      if (shift !== null && state.compare !== "custom") {
        state.compare = "custom";
        state.compareFrom = day + shift;
        state.compareTo = day + shift;
      }
      render();
      focusByKey("back");
    }

    function backLabel() {
      if (state.range === "24h") return "Back to past 24 hours";
      if (state.range === "custom" && view) return "Back to " + rangeLabel(view.primary.from, view.primary.to, view.today);
      return "Back to " + state.range + " days";
    }

    function goBack() {
      var back = state.back;
      if (!back) return;
      state.back = null;
      ["range", "customFrom", "customTo", "compare", "compareFrom", "compareTo", "breakdown", "modelSort", "timeSort"]
        .forEach(function (name) { state[name] = back[name]; });
      render();
      focusByKey("breakdown-time");
    }

    function sortBy(column) {
      if (state.breakdown === "time") {
        state.timeSort = toggleSort(state.timeSort, column, true);
      } else {
        state.modelSort = toggleSort(effectiveModelSort(state.modelSort, Boolean(view && view.previous)), column, false);
      }
      renderResults();
      focusByKey("sort:" + column);
    }

    function focusByKey(key) {
      var node = panel.querySelector('[data-focus-key="' + key.replace(/["\\]/g, "\\$&") + '"]');
      if (node) node.focus();
    }

    // --- rendering ---

    // True when the report was drawn. Never throws: a value the page cannot
    // draw leaves a short message instead of a broken tab.
    function render() {
      if (!data) return false;
      try {
        if (!dom) buildSkeleton();
        syncControls();
      } catch (error) {
        return false;
      }
      var drawn = renderResults();
      renderFreshness();
      return drawn;
    }

    function hasFilters() {
      return state.account !== null || state.range !== "30" || state.compare !== "off";
    }

    function syncControls() {
      dom.rangeButtons.forEach(function (node) {
        node.setAttribute("aria-pressed", node.getAttribute("data-value") === state.range ? "true" : "false");
      });
      dom.customButton.setAttribute("aria-pressed", state.range === "custom" ? "true" : "false");

      var signature = JSON.stringify(data.accounts);
      if (signature !== dom.accountSignature) {
        dom.accountSignature = signature;
        clear(dom.accountSelect);
        var all = el("option", null, "All accounts");
        all.value = "";
        dom.accountSelect.appendChild(all);
        accountOptions(data).forEach(function (entry) {
          var option = el("option", null, entry.label);
          option.value = entry.value;
          dom.accountSelect.appendChild(option);
        });
      }
      // A pick holds only while its index still shows the same account.
      var picked = resolveAccount(state.account, data);
      if (picked === null) state.account = null;
      dom.accountSelect.value = picked === null ? "" : String(picked);

      var comparing = state.compare !== "off";
      setText(dom.compareText, comparing ? "Comparing" : "Compare");
      dom.compareButton.classList.toggle("is-active", comparing);

      // The phone Filters badge counts the account and the comparison.
      var active = (picked === null ? 0 : 1) + (comparing ? 1 : 0);
      setText(dom.badge, active ? String(active) : "");
      dom.badge.hidden = active === 0;
      dom.filtersButton.setAttribute("aria-label", active ? "Filters, " + active + " active" : "Filters");
      dom.clearLink.hidden = !hasFilters();
      dom.sheetClear.hidden = !hasFilters();
    }

    function renderFreshness() {
      try {
        showFreshness();
      } catch (error) { /* the next tick or report tries again */ }
    }

    function showFreshness() {
      if (!dom || !data) return;
      var fresh = usageFreshness({ checkedAt: checkedAt }, data, Date.now());
      setText(dom.updated, fresh.updated);
      if (fresh.stale && fresh.at !== null) {
        setText(dom.stale, "The desktop app hasn't confirmed this usage report since " +
          shortInstantLabel(fresh.at, data.zone) + ". The figures may be out of date.");
        dom.stale.hidden = false;
      } else {
        dom.stale.hidden = true;
      }
      setText(dom.zoneNote, " · times in " + data.zone);
    }

    function captureFocus() {
      var active = doc.activeElement;
      return active && panel.contains(active) ? active.getAttribute("data-focus-key") : null;
    }

    function renderResults() {
      try {
        drawResults();
        return true;
      } catch (error) {
        if (dom) {
          clear(dom.results);
          dom.results.appendChild(el("p", "usage-empty",
            "This usage report could not be shown. It is read again when the desktop app shares a new one."));
        }
        return false;
      }
    }

    function drawResults() {
      var focus = captureFocus();
      var selection = selectionFor(state, data, Date.now());
      view = computeView(data, selection, Date.now());
      var chart = buildChart(view, state.metric);
      var results = dom.results;
      clear(results);
      charts = charts.filter(function (entry) { return entry.inDialog; });

      renderPeriodLines();
      var notes = buildNotes(view, chart);
      if (notes.length) {
        var list = el("div", "usage-notes");
        notes.forEach(function (text) { list.appendChild(el("p", "usage-note", text)); });
        results.appendChild(list);
      }
      var empty = buildEmptyStatus(view);
      if (empty) results.appendChild(el("p", "usage-empty", empty));

      var overview = el("div", "usage-overview");
      overview.appendChild(renderHero());
      overview.appendChild(renderChartCard(chart));
      results.appendChild(overview);
      results.appendChild(renderTiles());
      results.appendChild(renderBreakdown());

      if (state.openModel !== null) {
        if (!fillDialog()) dom.dialog.close();
      }
      if (focus) focusByKey(focus);
      redrawCharts();
    }

    function chip(text) {
      return el("span", "usage-chip", text);
    }

    // The toolbar's period lines: A (or the range), vs B, and Back.
    function renderPeriodLines() {
      var captions = buildCaptions(view);
      var lines = dom.periodLines;
      clear(lines);
      var title = el("h2", "usage-range-title");
      title.appendChild(el("span", null, captions.range));
      if (captions.rangePartial) title.appendChild(chip("Partial data"));
      lines.appendChild(title);
      if (captions.comparison) {
        var versus = el("p", "usage-versus");
        versus.appendChild(dashMark());
        versus.appendChild(el("span", null, captions.comparison));
        if (captions.comparisonPartial) versus.appendChild(chip("Partial data"));
        lines.appendChild(versus);
      }
      if (captions.asOf) lines.appendChild(el("p", "usage-asof", captions.asOf));
      if (state.back) {
        lines.appendChild(focusKey(button("usage-link usage-back", "‹ " + state.back.label, goBack), "back"));
      }
    }

    function shareBar(provider, share) {
      var bar = svg("svg", { "class": "usage-share", viewBox: "0 0 100 6", preserveAspectRatio: "none", "aria-hidden": "true" });
      bar.appendChild(svg("rect", { "class": "usage-share-track", x: "0", y: "0", width: "100", height: "6", rx: "3" }));
      var width = Math.max(0, Math.min(1, share)) * 100;
      if (width > 0) {
        bar.appendChild(svg("rect", { "class": "usage-share-fill is-" + provider, x: "0", y: "0", width: String(width), height: "6", rx: "3" }));
      }
      return bar;
    }

    function renderHero() {
      var hero = buildHero(view, state.metric);
      var box = el("section", "usage-hero");
      box.setAttribute("aria-label", "Total");
      box.appendChild(el("p", "usage-hero-label", hero.label));
      var value = el("p", "usage-hero-value", hero.value);
      if (hero.isCost) {
        var star = el("span", "usage-hero-star", "*");
        star.setAttribute("aria-hidden", "true");
        value.appendChild(star);
      }
      box.appendChild(value);
      if (hero.previousText || hero.deltaText) {
        var delta = el("p", "usage-hero-delta");
        if (hero.previousText) delta.appendChild(el("span", null, hero.previousText));
        if (hero.deltaText) delta.appendChild(el("span", "usage-delta-value", hero.deltaText));
        box.appendChild(delta);
      }
      box.appendChild(el("p", "usage-hero-note", hero.note));
      if (hero.unpricedNote) box.appendChild(el("p", "usage-hero-note", hero.unpricedNote));

      var list = el("div", "usage-providers");
      hero.providers.forEach(function (row) {
        var item = el("div", "usage-provider");
        var top = el("div", "usage-provider-top");
        top.appendChild(providerMark(row.provider));
        top.appendChild(el("span", "usage-provider-name", row.name));
        top.appendChild(el("span", "usage-provider-value", row.valueText));
        item.appendChild(top);
        item.appendChild(shareBar(row.provider, row.share));
        var bottom = el("div", "usage-provider-bottom");
        bottom.appendChild(el("span", "usage-provider-caption", row.caption));
        if (row.deltaText) bottom.appendChild(el("span", "usage-delta", row.deltaText));
        item.appendChild(bottom);
        list.appendChild(item);
      });
      box.appendChild(list);
      return box;
    }

    // A small segmented control whose buttons keep focus across re-renders.
    function smallSegments(label, entries, current, keyPrefix, onPick) {
      var group = el("div", "usage-segments is-small");
      group.setAttribute("role", "group");
      group.setAttribute("aria-label", label);
      entries.forEach(function (entry) {
        var node = segment(group, entry[0], entry[1], function () { onPick(entry[0]); });
        focusKey(node, keyPrefix + entry[0]);
        node.setAttribute("aria-pressed", current === entry[0] ? "true" : "false");
      });
      return group;
    }

    function renderChartCard(chart) {
      var card = el("section", "usage-chart-card");
      var head = el("div", "usage-chart-head");
      var period = view.primary.hourly ? "Hourly" : "Daily";
      var titleText = state.metric === "tokens" ? period + " tokens" : period + " API value";
      var titles = el("div", "usage-chart-titles");
      titles.appendChild(el("h3", "usage-chart-title", titleText));
      var captions = buildCaptions(view);
      if (captions.comparison && (chart.comparisonSeries.length || chart.comparisonUnavailable)) {
        var sub = el("p", "usage-chart-sub");
        sub.appendChild(dashMark());
        sub.appendChild(el("span", null, captions.comparison + (chart.averageDay ? ", average day" : "")));
        titles.appendChild(sub);
      }
      head.appendChild(titles);
      var legend = el("div", "usage-legend");
      chart.series.forEach(function (line) {
        var item = el("span", "usage-legend-item");
        item.appendChild(providerMark(line.provider));
        item.appendChild(el("span", null, line.name));
        legend.appendChild(item);
      });
      head.appendChild(legend);
      head.appendChild(smallSegments("Metric", [["cost", "API value"], ["tokens", "Tokens"]], state.metric, "metric-", setMetric));
      card.appendChild(head);
      var host = el("div", "usage-chart");
      card.appendChild(host);
      mountChart(host, chart, titleText, false);
      return card;
    }

    function renderTiles() {
      var grid = el("div", "usage-tiles");
      buildTiles(view).forEach(function (tile) {
        var card = el("div", "usage-tile");
        card.appendChild(el("p", "usage-tile-caption", tile.caption));
        card.appendChild(el("p", "usage-tile-value", tile.value));
        card.appendChild(el("p", "usage-tile-note", tile.footnote));
        if (tile.deltaText) card.appendChild(el("p", "usage-delta", tile.deltaText));
        grid.appendChild(card);
      });
      return grid;
    }

    // extra: "is-secondary" marks a column phones leave out (hiddenOnPhone).
    function sortHeader(text, column, sort, numeric, title, extra) {
      var th = el("th", [numeric ? "is-numeric" : "", extra || ""].join(" ").trim() || null);
      th.scope = "col";
      var active = sort.column === column;
      th.setAttribute("aria-sort", active ? (sort.descending ? "descending" : "ascending") : "none");
      var node = focusKey(button("usage-sort", null, function () { sortBy(column); }), "sort:" + column);
      node.appendChild(el("span", null, text));
      var glyph = el("span", "usage-sort-glyph", active ? (sort.descending ? "▼" : "▲") : "");
      glyph.setAttribute("aria-hidden", "true");
      node.appendChild(glyph);
      if (title) node.title = title;
      th.appendChild(node);
      return th;
    }

    function renderBreakdown() {
      if (isPhone()) phoneSortFallback();
      var table = buildBreakdown(view, state.breakdown, state.modelSort, state.timeSort);
      var section = el("section", "usage-breakdown");
      var head = el("div", "usage-breakdown-head");
      head.appendChild(el("h3", "usage-breakdown-title", "Breakdown"));
      if (table.hint) head.appendChild(el("p", "usage-breakdown-hint", table.hint));
      head.appendChild(smallSegments("Breakdown by", [["model", "Model"], ["time", view.primary.hourly ? "Hour" : "Day"]],
        state.breakdown, "breakdown-", function (mode) {
          state.breakdown = mode;
          renderResults();
          focusByKey("breakdown-" + mode);
        }));
      section.appendChild(head);

      if (table.rows.length === 0) {
        section.appendChild(el("p", "usage-breakdown-empty", "No usage to break down in this range."));
        return section;
      }

      var scroller = el("div", "usage-table-wrap");
      var grid = el("table", "usage-table" + (table.showComparison ? " is-comparing" : ""));
      var caption = el("caption", "sr-only", "Usage by " + table.labelHeader.toLowerCase());
      grid.appendChild(caption);
      var thead = el("thead");
      var headRow = el("tr");
      headRow.appendChild(sortHeader(table.labelHeader, "label", table.sort, false));
      headRow.appendChild(sortHeader(table.valueHeader, "value", table.sort, true));
      if (table.showComparison) {
        headRow.appendChild(sortHeader("API value B", "previousValue", table.sort, true, null, "is-secondary"));
        headRow.appendChild(sortHeader(table.tokensHeader, "tokens", table.sort, true, null, "is-secondary"));
        headRow.appendChild(sortHeader("Tokens B", "previousTokens", table.sort, true, null, "is-secondary"));
        headRow.appendChild(sortHeader(table.changeHeader, "change", table.sort, true,
          "Change in API value from the earlier period to the later one, as an average per day when the periods differ in length. Models without a price, or periods with unpriced usage, compare tokens instead."));
      } else {
        headRow.appendChild(sortHeader("Share", "share", table.sort, true, null, "is-secondary"));
        headRow.appendChild(sortHeader(table.tokensHeader, "tokens", table.sort, true));
      }
      thead.appendChild(headRow);
      grid.appendChild(thead);

      var tbody = el("tbody");
      table.rows.forEach(function (row) {
        var tr = el("tr");
        var label = el("th", "usage-row-label");
        label.scope = "row";
        var labelInner;
        if (row.modelIndex !== null) {
          var key = data.models[row.modelIndex].provider + ":" + data.models[row.modelIndex].id;
          labelInner = focusKey(button("usage-row-button", null, function () { openModel(key); }), "model:" + key);
          labelInner.setAttribute("aria-haspopup", "dialog");
          labelInner.setAttribute("aria-label", "Show details for " + row.label);
          labelInner.appendChild(providerMark(row.provider));
          labelInner.appendChild(el("span", "usage-row-text", row.label));
        } else if (row.drillDay !== null) {
          labelInner = focusKey(button("usage-row-button", null, function () { drillDay(row.drillDay); }), "day:" + row.drillDay);
          labelInner.setAttribute("aria-label", "Show " + row.label + " by hour");
          labelInner.appendChild(el("span", "usage-row-text", row.label));
          var more = el("span", "usage-row-more", "›");
          more.setAttribute("aria-hidden", "true");
          labelInner.appendChild(more);
        } else {
          labelInner = el("span", "usage-row-text", row.label);
        }
        label.appendChild(labelInner);
        tr.appendChild(label);
        tr.appendChild(el("td", "is-numeric", row.valueText));
        if (table.showComparison) {
          tr.appendChild(el("td", "is-numeric is-muted is-secondary", row.previousValueText));
          tr.appendChild(el("td", "is-numeric is-secondary", row.tokensText));
          tr.appendChild(el("td", "is-numeric is-muted is-secondary", row.previousTokensText));
          tr.appendChild(el("td", "is-numeric usage-change", row.changeText));
        } else {
          tr.appendChild(el("td", "is-numeric is-muted is-secondary", row.shareText));
          tr.appendChild(el("td", "is-numeric", row.tokensText));
        }
        tbody.appendChild(tr);
      });
      grid.appendChild(tbody);
      scroller.appendChild(grid);
      section.appendChild(scroller);
      return section;
    }

    // --- model details dialog ---

    function findModelIndex(key) {
      for (var i = 0; i < data.models.length; i++) {
        if (data.models[i].provider + ":" + data.models[i].id === key) return i;
      }
      return -1;
    }

    function openModel(key) {
      closeAllPops();
      state.openModel = key;
      var filled = false;
      try {
        filled = fillDialog();
      } catch (error) {
        filled = false;
      }
      if (!filled) {
        state.openModel = null;
        return;
      }
      if (!dom.dialog.open) {
        dom.dialog.showModal();
        dialogGeneration += 1;
        lockScroll();
      }
      redrawCharts();
      dom.dialog.querySelector(".usage-dialog-close").focus();
    }

    function splitBar(provider, parts) {
      var total = parts.reduce(function (sum, part) { return sum + Math.max(0, part.value); }, 0);
      var bar = svg("svg", { "class": "usage-split", viewBox: "0 0 100 10", preserveAspectRatio: "none", "aria-hidden": "true" });
      bar.appendChild(svg("rect", { "class": "usage-share-track", x: "0", y: "0", width: "100", height: "10", rx: "3" }));
      var x = 0;
      parts.forEach(function (part, index) {
        var width = total > 0 ? Math.max(0, part.value) / total * 100 : 0;
        if (width <= 0) return;
        bar.appendChild(svg("rect", {
          "class": "usage-split-part is-" + provider + " is-part-" + index,
          x: String(x), y: "0", width: String(width), height: "10"
        }));
        x += width;
      });
      return bar;
    }

    function splitLegend(provider, parts) {
      var list = el("ul", "usage-split-legend");
      parts.forEach(function (part, index) {
        var item = el("li");
        var swatch = svg("svg", { "class": "usage-swatch", viewBox: "0 0 10 10", width: "10", height: "10", "aria-hidden": "true" });
        swatch.appendChild(svg("rect", { "class": "usage-split-part is-" + provider + " is-part-" + index, x: "0", y: "0", width: "10", height: "10", rx: "2" }));
        item.appendChild(swatch);
        item.appendChild(el("span", "usage-split-name", part.label));
        item.appendChild(el("span", "usage-split-value", part.text));
        list.appendChild(item);
      });
      return list;
    }

    function stat(label, value, delta) {
      var box = el("div", "usage-stat");
      box.appendChild(el("p", "usage-stat-label", label));
      box.appendChild(el("p", "usage-stat-value", value));
      if (delta) box.appendChild(el("p", "usage-delta", delta));
      return box;
    }

    // Fills the open dialog from the view on screen; false when the model is
    // in neither period any more.
    function fillDialog() {
      var index = findModelIndex(state.openModel);
      var detail = index === -1 ? null : buildModelDetail(view, index, state.metric);
      if (!detail) return false;
      charts = charts.filter(function (chart) { return !chart.inDialog; });
      setText(dom.dialogTitle, detail.model);
      var body = dom.dialogBody;
      clear(body);

      var subtitle = el("p", "usage-dialog-subtitle");
      subtitle.appendChild(providerMark(detail.provider));
      subtitle.appendChild(el("span", null, detail.subtitle));
      body.appendChild(subtitle);

      var stats = el("div", "usage-stats");
      stats.appendChild(stat("API value", detail.costText, detail.costDeltaText));
      stats.appendChild(stat("Tokens", detail.tokensText, detail.tokensDeltaText));
      stats.appendChild(stat("Per 1M tokens", detail.perMillionText));
      stats.appendChild(stat("Cache hit", detail.cacheHitText));
      body.appendChild(stats);

      var chartHead = el("div", "usage-chart-head");
      chartHead.appendChild(el("h3", "usage-chart-title", detail.chartTitle));
      if (detail.isPriced) {
        var toggle = el("div", "usage-segments");
        toggle.setAttribute("role", "group");
        toggle.setAttribute("aria-label", "Metric");
        [["cost", "Cost"], ["tokens", "Tokens"]].forEach(function (entry) {
          var node = segment(toggle, entry[0], entry[1], function () {
            state.metric = entry[0];
            render();
            var target = dom.dialogBody.querySelector('[data-focus-key="dialog-metric-' + entry[0] + '"]');
            if (target) target.focus();
          });
          focusKey(node, "dialog-metric-" + entry[0]);
          node.setAttribute("aria-pressed", state.metric === entry[0] ? "true" : "false");
        });
        chartHead.appendChild(toggle);
      }
      body.appendChild(chartHead);
      var host = el("div", "usage-chart usage-chart-small");
      body.appendChild(host);
      mountChart(host, detail.chart, detail.chartTitle + " for " + detail.model, true);

      var split = el("div", "usage-splits");
      var costBox = el("div", "usage-split-box");
      costBox.appendChild(el("h3", "usage-split-title", "API value by type"));
      if (detail.isPriced) {
        costBox.appendChild(splitBar(detail.provider, detail.costSplit));
        costBox.appendChild(splitLegend(detail.provider, detail.costSplit));
      } else {
        costBox.appendChild(el("p", "usage-hero-note", "This model has no known price, so its value cannot be split."));
      }
      split.appendChild(costBox);
      var tokenBox = el("div", "usage-split-box");
      tokenBox.appendChild(el("h3", "usage-split-title", "Tokens by type"));
      tokenBox.appendChild(splitBar(detail.provider, detail.tokenSplit));
      tokenBox.appendChild(splitLegend(detail.provider, detail.tokenSplit));
      split.appendChild(tokenBox);
      body.appendChild(split);
      return true;
    }

    // --- chart (inline SVG) ---

    function mountChart(host, chart, label, inDialog) {
      host.tabIndex = 0;
      host.setAttribute("role", "group");
      host.setAttribute("aria-label", label + ". Use the left and right arrow keys to read each point.");
      var instance = { host: host, chart: chart, inDialog: inDialog, active: null, width: 0 };
      var announcer = el("p", "sr-only");
      announcer.setAttribute("aria-live", "polite");
      instance.announcer = announcer;
      host.addEventListener("keydown", function (event) {
        var count = chart.count;
        if (!count) return;
        var index = instance.active === null ? defaultIndex(chart) : instance.active;
        if (event.key === "ArrowRight") index = Math.min(count - 1, index + 1);
        else if (event.key === "ArrowLeft") index = Math.max(0, index - 1);
        else if (event.key === "Home") index = 0;
        else if (event.key === "End") index = count - 1;
        else return;
        event.preventDefault();
        setActive(instance, index, true);
      });
      host.addEventListener("focus", function () {
        if (chart.count) setActive(instance, instance.active === null ? defaultIndex(chart) : instance.active, true);
      });
      host.addEventListener("blur", function () { setActive(instance, null, false); });
      charts.push(instance);
      drawChart(instance);
    }

    function defaultIndex(chart) {
      for (var i = chart.count - 1; i >= 0; i--) {
        if (tooltipFor(chart, i)) return i;
      }
      return chart.count - 1;
    }

    function redrawCharts() {
      charts.forEach(function (instance) {
        try {
          var width = Math.round(instance.host.clientWidth);
          if (width > 0 && width !== instance.width) drawChart(instance);
        } catch (error) {
          clear(instance.host);
        }
      });
    }

    function niceStep(max, ticks) {
      var raw = max / ticks;
      var power = Math.pow(10, Math.floor(Math.log10(raw)));
      var fraction = raw / power;
      var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 5 ? 5 : 10;
      return nice * power;
    }

    function drawChart(instance) {
      var host = instance.host;
      var chart = instance.chart;
      var measured = Math.round(host.clientWidth);
      instance.width = measured;
      var width = Math.max(280, measured || 640);
      var small = width < 520;
      var height = instance.inDialog ? (small ? 180 : 200) : (small ? 230 : 300);
      clear(host);
      host.appendChild(instance.announcer);

      if (!chart.count) {
        host.appendChild(el("p", "usage-chart-empty", "Nothing to chart."));
        return;
      }

      var max = 0;
      chart.series.concat(chart.comparisonSeries).forEach(function (line) {
        line.values.forEach(function (value) { if (isFinite(value) && value > max) max = value; });
      });
      var ticks = 4;
      var step = max > 0 ? niceStep(max, ticks) : (chart.metric === "tokens" ? 250 : 0.25);
      if (chart.metric === "tokens") step = Math.max(1, step);
      else step = Math.max(0.01, step);
      var axisMax = step * Math.max(1, Math.ceil(max / step - 1e-9));
      var tickCount = Math.round(axisMax / step);

      var tickLabels = [];
      for (var t = 0; t <= tickCount; t++) tickLabels.push(chartAxisText(step * t, chart.metric));
      var longest = tickLabels.reduce(function (length, text) { return Math.max(length, text.length); }, 1);
      var left = Math.round(longest * 6.6 + 12);
      if (chart.comparisonLabels.some(function (text) { return text; })) left = Math.max(left, 44);
      var right = 14;
      var top = 14;
      var hasComparisonLabels = chart.comparisonLabels.some(function (text) { return text; });
      var bottom = height - (hasComparisonLabels ? 40 : 26);
      var plotWidth = width - left - right;
      var plotHeight = bottom - top;

      function x(index) {
        return chart.count === 1 ? left + plotWidth / 2 : left + index * plotWidth / (chart.count - 1);
      }
      function y(value) {
        return bottom - (value / axisMax) * plotHeight;
      }

      var root = svg("svg", {
        "class": "usage-chart-svg",
        viewBox: "0 0 " + width + " " + height,
        width: "100%",
        height: String(height),
        "aria-hidden": "true",
        focusable: "false"
      });

      for (t = 0; t <= tickCount; t++) {
        var gy = y(step * t);
        root.appendChild(svg("line", { "class": "usage-grid" + (t === 0 ? " is-base" : ""), x1: String(left), x2: String(width - right), y1: String(gy), y2: String(gy) }));
        var tick = svg("text", { "class": "usage-axis", x: String(left - 8), y: String(gy + 4), "text-anchor": "end" });
        tick.textContent = tickLabels[t];
        root.appendChild(tick);
      }

      // X labels, thinned so they never overlap; the first and last sit flush
      // with the plot edges, and the last point is always labelled.
      var labelChars = chart.labels.reduce(function (length, text) { return Math.max(length, text.length); }, 1);
      var labelWidth = labelChars * 6.4 + 14;
      var stride = Math.max(1, Math.ceil(chart.count / Math.max(1, Math.floor(plotWidth / labelWidth))));
      var shown = [];
      for (var i = 0; i < chart.count; i += stride) shown.push(i);
      var lastIndex = chart.count - 1;
      if (shown[shown.length - 1] !== lastIndex) {
        var gap = x(lastIndex) - x(shown[shown.length - 1]);
        // The last label hangs left of its point, so it needs one and a half widths.
        if (gap < labelWidth * 1.5 && shown.length > 1) shown.pop();
        shown.push(lastIndex);
      }
      shown.forEach(function (index) {
        var anchor = chart.count === 1 ? "middle" : index === 0 ? "start" : index === lastIndex ? "end" : "middle";
        var label = svg("text", { "class": "usage-axis", x: String(x(index)), y: String(bottom + 17), "text-anchor": anchor });
        label.textContent = chart.labels[index];
        root.appendChild(label);
        if (hasComparisonLabels && chart.comparisonLabels[index]) {
          var second = svg("text", { "class": "usage-axis is-comparison", x: String(x(index)), y: String(bottom + 32), "text-anchor": anchor });
          second.textContent = chart.comparisonLabels[index];
          root.appendChild(second);
        }
      });
      if (hasComparisonLabels) {
        // Which date row is which: A on top, B below.
        var keyX = String(Math.max(2, left - 30));
        var keyA = svg("text", { "class": "usage-axis is-key", x: keyX, y: String(bottom + 17) });
        keyA.textContent = "A";
        root.appendChild(keyA);
        var keyB = svg("text", { "class": "usage-axis is-key", x: keyX, y: String(bottom + 32) });
        keyB.textContent = "B";
        root.appendChild(keyB);
      }

      function runs(values) {
        var list = [];
        var current = [];
        values.forEach(function (value, index) {
          if (typeof value === "number" && isFinite(value)) {
            current.push([x(index), y(value)]);
          } else if (current.length) {
            list.push(current);
            current = [];
          }
        });
        if (current.length) list.push(current);
        return list;
      }

      function f(value) {
        return value.toFixed(1);
      }

      // A smooth line through the points (Catmull-Rom as cubic Bezier). The
      // control points are clamped to the baseline, so a curve between two
      // values never dips below zero.
      function smoothPath(points) {
        return smoothLinePath(points, top, bottom);
      }

      // Soft areas first, then the dashed comparison, then the lines on top.
      var mainRuns = chart.series.map(function (line) { return { line: line, runs: runs(line.values) }; });
      mainRuns.forEach(function (entry) {
        entry.runs.forEach(function (points) {
          if (points.length < 2) return;
          var area = smoothPath(points) + " L" + f(points[points.length - 1][0]) + " " + bottom +
            " L" + f(points[0][0]) + " " + bottom + " Z";
          root.appendChild(svg("path", { "class": "usage-area is-" + entry.line.provider, d: area }));
        });
      });

      chart.comparisonSeries.forEach(function (line) {
        runs(line.values).forEach(function (points) {
          if (points.length === 1) {
            root.appendChild(svg("circle", { "class": "usage-point-cmp is-" + line.provider, cx: String(points[0][0]), cy: String(points[0][1]), r: "1.8" }));
          } else {
            root.appendChild(svg("path", { "class": "usage-line-cmp is-" + line.provider, d: smoothPath(points) }));
          }
        });
      });

      mainRuns.forEach(function (entry) {
        entry.runs.forEach(function (points) {
          if (points.length === 1) {
            root.appendChild(svg("circle", { "class": "usage-point is-" + entry.line.provider, cx: String(points[0][0]), cy: String(points[0][1]), r: "3" }));
            return;
          }
          root.appendChild(svg("path", { "class": "usage-line is-" + entry.line.provider, d: smoothPath(points) }));
        });
      });

      // A point still in progress: a hollow ring, so a low value never reads as a drop.
      [[chart.series, chart.partialIndex], [chart.comparisonSeries, chart.comparisonPartialIndex]].forEach(function (pair) {
        var index = pair[1];
        if (index === null || index === undefined) return;
        pair[0].forEach(function (line) {
          var value = line.values[index];
          if (!isFinite(value)) return;
          root.appendChild(svg("circle", { "class": "usage-ring is-" + line.provider, cx: String(x(index)), cy: String(y(value)), r: "5" }));
        });
      });

      var hover = svg("g", { "class": "usage-hover" });
      root.appendChild(hover);

      var capture = svg("rect", { "class": "usage-capture", x: String(left - 6), y: String(top), width: String(plotWidth + 12), height: String(plotHeight) });
      function pointAt(event) {
        var bounds = root.getBoundingClientRect();
        var scale = bounds.width > 0 ? width / bounds.width : 1;
        var px = (event.clientX - bounds.left) * scale;
        var index = chart.count === 1 ? 0 : Math.round((px - left) / (plotWidth / (chart.count - 1)));
        setActive(instance, Math.max(0, Math.min(chart.count - 1, index)), false);
      }
      capture.addEventListener("pointermove", pointAt);
      capture.addEventListener("pointerdown", pointAt);
      capture.addEventListener("pointerleave", function () {
        if (doc.activeElement !== host) setActive(instance, null, false);
      });
      root.appendChild(capture);
      host.appendChild(root);

      instance.geometry = { x: x, y: y, top: top, bottom: bottom, left: left, right: width - right, width: width, hover: hover };
      if (instance.active !== null) setActive(instance, instance.active, false);
    }

    function setActive(instance, index, announce) {
      try {
        showActive(instance, index, announce);
      } catch (error) {
        if (instance.geometry) clear(instance.geometry.hover);
      }
    }

    function showActive(instance, index, announce) {
      instance.active = index;
      var geometry = instance.geometry;
      if (!geometry) return;
      var hover = geometry.hover;
      clear(hover);
      if (index === null) return;
      var chart = instance.chart;
      var tip = tooltipFor(chart, index);
      if (!tip) {
        if (announce) setText(instance.announcer, (chart.tooltipLabels[index] || chart.labels[index]) + ": no data.");
        return;
      }
      var cx = geometry.x(index);
      hover.appendChild(svg("line", { "class": "usage-guide", x1: String(cx), x2: String(cx), y1: String(geometry.top), y2: String(geometry.bottom) }));
      chart.series.concat(chart.comparisonSeries).forEach(function (line) {
        var value = line.values[index];
        if (!isFinite(value)) return;
        var dashed = chart.comparisonSeries.indexOf(line) !== -1;
        hover.appendChild(svg("circle", { "class": (dashed ? "usage-dot-cmp" : "usage-dot-active") + " is-" + line.provider, cx: String(cx), cy: String(geometry.y(value)), r: dashed ? "3" : "4" }));
      });

      // Tooltip box, measured after its text is in the document.
      var box = svg("g", { "class": "usage-tip" });
      var background = svg("rect", { "class": "usage-tip-bg", rx: "8", ry: "8" });
      box.appendChild(background);
      var lines = [{ text: tip.title, kind: "title" }];
      tip.rows.forEach(function (row) { lines.push({ row: row, kind: "row" }); });
      if (tip.comparisonRows.length) {
        lines.push({ text: tip.comparisonTitle, kind: "title" });
        tip.comparisonRows.forEach(function (row) { lines.push({ row: row, kind: "row" }); });
      }
      var lineHeight = 17;
      var padding = 10;
      var texts = [];
      lines.forEach(function (line, n) {
        var baseline = padding + 12 + n * lineHeight;
        if (line.kind === "title") {
          var title = svg("text", { "class": "usage-tip-title", x: String(padding), y: String(baseline) });
          title.textContent = line.text;
          box.appendChild(title);
          texts.push({ node: title, value: null });
        } else {
          if (line.row.provider) {
            box.appendChild(svg("circle", { "class": "usage-tip-dot is-" + line.row.provider, cx: String(padding + 4), cy: String(baseline - 4), r: "3.5" }));
          }
          var name = svg("text", { "class": "usage-tip-name", x: String(padding + (line.row.provider ? 13 : 0)), y: String(baseline) });
          name.textContent = line.row.name;
          box.appendChild(name);
          var value = svg("text", { "class": "usage-tip-value", y: String(baseline), "text-anchor": "end" });
          value.textContent = line.row.value;
          box.appendChild(value);
          texts.push({ node: name, value: value, indent: line.row.provider ? 13 : 0 });
        }
      });
      hover.appendChild(box);
      var widest = 0;
      texts.forEach(function (entry) {
        var length = measure(entry.node);
        if (entry.value) length += (entry.indent || 0) + 18 + measure(entry.value);
        widest = Math.max(widest, length);
      });
      var boxWidth = Math.ceil(widest + padding * 2);
      var boxHeight = lines.length * lineHeight + padding * 2 - 2;
      texts.forEach(function (entry) {
        if (entry.value) entry.value.setAttribute("x", String(boxWidth - padding));
      });
      background.setAttribute("width", String(boxWidth));
      background.setAttribute("height", String(boxHeight));
      var bx = cx + 12;
      if (bx + boxWidth > geometry.width - 4) bx = cx - 12 - boxWidth;
      if (bx < 4) bx = Math.max(4, Math.min(geometry.width - boxWidth - 4, cx - boxWidth / 2));
      box.setAttribute("transform", "translate(" + bx.toFixed(1) + " " + (geometry.top + 2) + ")");

      if (announce) {
        var spoken = [tip.title].concat(tip.rows.map(function (row) { return row.name + " " + row.value; }));
        if (tip.comparisonRows.length) {
          spoken.push(tip.comparisonTitle);
          tip.comparisonRows.forEach(function (row) { spoken.push(row.name + " " + row.value); });
        }
        setText(instance.announcer, spoken.join(". ") + ".");
      }
    }

    function measure(node) {
      try {
        return node.getComputedTextLength();
      } catch (error) {
        return (node.textContent || "").length * 7;
      }
    }

    // --- start ---

    setupTabs();
    setTabsVisible(false);
    doc.addEventListener("keydown", onDocumentKey);
    doc.addEventListener("pointerdown", onDocumentPointer, true);
    doc.addEventListener("focusin", onDocumentFocusIn, true);
    var phoneQuery = typeof window.matchMedia === "function" ? window.matchMedia("(max-width: 719px)") : null;
    if (phoneQuery) {
      if (typeof phoneQuery.addEventListener === "function") phoneQuery.addEventListener("change", onLayoutChange);
      else if (typeof phoneQuery.addListener === "function") phoneQuery.addListener(onLayoutChange);
    }
    doc.body.setAttribute("data-view", "quotas");
    window.setInterval(renderFreshness, 30000);

    return {
      onSnapshot: onSnapshot,
      clear: function () {
        flag = null;
        clearUsage();
      }
    };
  }

  return {
    // constants
    METRIC: {
      uncached: UNCACHED, cacheRead: CACHE_READ, cacheWrite5m: WRITE_5M, cacheWrite1h: WRITE_1H,
      output: OUTPUT, reasoning: REASONING, cost: COST, savings: SAVINGS, requests: REQUESTS,
      unpricedTokens: UNPRICED, rows: ROWS
    },
    MAX_HOURLY_DAYS: MAX_HOURLY_DAYS,
    MAX_CUSTOM_DAYS: MAX_CUSTOM_DAYS,
    // calendar and zone
    parseDay: parseDay,
    dayToIso: dayToIso,
    todayIn: todayIn,
    zoneClock: zoneClock,
    dayLabel: dayLabel,
    axisDayLabel: axisDayLabel,
    longDayLabel: longDayLabel,
    rangeLabel: rangeLabel,
    longHourLabel: longHourLabel,
    axisHourLabel: axisHourLabel,
    axisDayHourLabel: axisDayHourLabel,
    rollingRangeLabel: rollingRangeLabel,
    // numbers
    formatTokens: formatTokens,
    formatMoney: formatMoney,
    formatMoneyMicro: formatMoneyMicro,
    costOrUnpriced: costOrUnpriced,
    axisCost: axisCost,
    axisTokens: axisTokens,
    formatPercent: formatPercent,
    formatFraction: formatFraction,
    formatMultiplier: formatMultiplier,
    formatDelta: formatDelta,
    // metrics, deltas, direction
    emptyMetrics: emptyMetrics,
    processedTokens: processedTokens,
    inputTokens: inputTokens,
    deltaOf: deltaOf,
    deltaBetween: deltaBetween,
    changeDirection: changeDirection,
    directionBetween: directionBetween,
    directionOfTokens: directionOfTokens,
    directionOfCost: directionOfCost,
    // payload, periods, aggregation
    prepareUsage: prepareUsage,
    resolvePeriod: resolvePeriod,
    previousPeriod: previousPeriod,
    selectionFor: selectionFor,
    customRangeLimits: customRangeLimits,
    isValidCustomRange: isValidCustomRange,
    monthGrid: monthGrid,
    monthTitle: monthTitle,
    createRangeSelection: createRangeSelection,
    createCompareSession: createCompareSession,
    rangeSelectionText: rangeSelectionText,
    hiddenOnPhone: hiddenOnPhone,
    wrapFocusIndex: wrapFocusIndex,
    accountSelection: accountSelection,
    resolveAccount: resolveAccount,
    accountOptions: accountOptions,
    aggregate: aggregate,
    computeView: computeView,
    // view models
    buildHero: buildHero,
    buildTiles: buildTiles,
    buildChart: buildChart,
    tooltipFor: tooltipFor,
    smoothLinePath: smoothLinePath,
    buildBreakdown: buildBreakdown,
    sortBreakdown: sortBreakdown,
    toggleSort: toggleSort,
    effectiveModelSort: effectiveModelSort,
    buildModelDetail: buildModelDetail,
    buildCaptions: buildCaptions,
    buildNotes: buildNotes,
    buildEmptyStatus: buildEmptyStatus,
    // discovery
    usageSyncAction: usageSyncAction,
    acceptUsageResponse: acceptUsageResponse,
    usageFreshness: usageFreshness,
    confirmedCheckedAt: confirmedCheckedAt,
    // DOM
    createController: createController
  };
})();

if (typeof module !== "undefined" && module.exports) {
  module.exports = UsageView;
}
