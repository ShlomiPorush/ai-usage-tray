import path from "node:path";
import os from "node:os";

export function defaultReportPath() {
  return path.join(os.homedir(), ".claude", "usage-data", "report.html");
}

export function defaultOutputPath() {
  return path.join(os.homedir(), ".costats", "images", "costats-insights.png");
}

export function getPlaywrightCacheDir() {
  const envPath = process.env.PLAYWRIGHT_BROWSERS_PATH;
  if (envPath && envPath !== "0") {
    return envPath;
  }
  const localAppData = process.env.LOCALAPPDATA || process.env.APPDATA;
  if (process.platform === "win32" && localAppData) {
    return path.join(localAppData, "costats", "playwright");
  }
  return path.join(os.homedir(), ".cache", "costats-playwright");
}

export function parseNumber(value) {
  if (value === null || value === undefined) {
    return null;
  }
  const cleaned = String(value).replace(/[^0-9.-]/g, "");
  if (!cleaned || cleaned === "-" || cleaned === ".") {
    return null;
  }
  const num = Number(cleaned);
  return Number.isFinite(num) ? num : null;
}

export function parseInteger(value) {
  const num = parseNumber(value);
  return num === null ? null : Math.round(num);
}

export function formatCompact(value) {
  if (value === null || value === undefined) {
    return "0";
  }
  const abs = Math.abs(value);
  if (abs >= 1_000_000_000) {
    return trimTrailingZero((abs / 1_000_000_000).toFixed(1)) + "B";
  }
  if (abs >= 1_000_000) {
    return trimTrailingZero((abs / 1_000_000).toFixed(1)) + "M";
  }
  if (abs >= 1_000) {
    return trimTrailingZero((abs / 1_000).toFixed(1)) + "K";
  }
  return String(Math.round(abs));
}

function trimTrailingZero(value) {
  return value.replace(/\.0$/, "");
}

export function escapeHtml(value) {
  return String(value)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/\"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

export function formatDateRange(start, end) {
  if (!start || !end) {
    return "";
  }
  const startDate = parseIsoDate(start);
  const endDate = parseIsoDate(end);
  if (!startDate || !endDate) {
    return start + " to " + end;
  }
  const sameYear = startDate.getUTCFullYear() === endDate.getUTCFullYear();
  const fmtMonthDay = new Intl.DateTimeFormat("en-US", {
    month: "short",
    day: "numeric",
    timeZone: "UTC"
  });
  const fmtYear = new Intl.DateTimeFormat("en-US", {
    year: "numeric",
    timeZone: "UTC"
  });
  const startText = fmtMonthDay.format(startDate);
  const endText = fmtMonthDay.format(endDate);
  const yearText = fmtYear.format(endDate);
  return sameYear ? `${startText} - ${endText}, ${yearText}` : `${startText}, ${fmtYear.format(startDate)} - ${endText}, ${yearText}`;
}

function parseIsoDate(value) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) {
    return null;
  }
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  return new Date(Date.UTC(year, month - 1, day));
}

export function computePeakPeriod(hourCounts) {
  if (!hourCounts || Object.keys(hourCounts).length === 0) {
    return null;
  }
  const periods = [
    { label: "Morning", hours: [6, 7, 8, 9, 10, 11] },
    { label: "Afternoon", hours: [12, 13, 14, 15, 16, 17] },
    { label: "Evening", hours: [18, 19, 20, 21, 22, 23] },
    { label: "Night", hours: [0, 1, 2, 3, 4, 5] }
  ];
  let best = null;
  for (const period of periods) {
    const total = period.hours.reduce((sum, hour) => {
      return sum + (hourCounts[String(hour)] || 0);
    }, 0);
    if (!best || total > best.total) {
      best = { label: period.label, total };
    }
  }
  return best ? best.label : null;
}

export function deriveBadge({ totalHours, linesChanged, toolCount, peakPeriod, multiFileChanges }) {
  if (totalHours && totalHours >= 500) {
    return { label: "Power User", reason: "500+ hours logged" };
  }
  if (linesChanged && linesChanged >= 1_000_000) {
    return { label: "Code Machine", reason: "1M+ lines changed" };
  }
  if (multiFileChanges && multiFileChanges >= 300) {
    return { label: "Refactor Lead", reason: "300+ multi-file changes" };
  }
  if (toolCount && toolCount >= 6) {
    return { label: "Explorer", reason: "Broad tool usage" };
  }
  if (peakPeriod === "Evening" || peakPeriod === "Night") {
    return { label: "Night Owl", reason: "Most active in the evening" };
  }
  if (peakPeriod === "Morning") {
    return { label: "Early Bird", reason: "Most active in the morning" };
  }
  return { label: "Focused Builder", reason: "Consistent momentum" };
}
