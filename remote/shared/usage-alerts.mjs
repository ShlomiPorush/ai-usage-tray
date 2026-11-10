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

// Device preferences: a browser subscription's own alert rules, independent of
// the desktop's account.alert. An account the device has no entry for (one
// added after the device saved) is on at the default threshold.
export const DEFAULT_ALERT_THRESHOLD = 90;
export const MAX_PREFERENCE_ACCOUNTS = 32;
const MAX_ACCOUNT_ID_LENGTH = 128;

export function defaultAlertPreferences() {
  return { accounts: Object.create(null), resetAlerts: true };
}

function hasExactKeys(value, keys) {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const own = Object.keys(value);
  return own.length === keys.length && keys.every((key) => Object.hasOwn(value, key));
}

/**
 * Strict parse of device preferences: { accounts: { <id>: { enabled,
 * thresholdPercent } }, resetAlerts }. Returns a normalized copy (account ids
 * lowercased, keys sorted, null-prototype map) or null when anything is off.
 */
export function normalizeAlertPreferences(value) {
  if (!hasExactKeys(value, ["accounts", "resetAlerts"])) return null;
  if (typeof value.resetAlerts !== "boolean") return null;
  const accounts = value.accounts;
  if (!accounts || typeof accounts !== "object" || Array.isArray(accounts)) return null;
  const ids = Object.keys(accounts);
  if (ids.length > MAX_PREFERENCE_ACCOUNTS) return null;

  const normalized = Object.create(null);
  for (const id of ids.sort()) {
    if (id.length === 0 || id.length > MAX_ACCOUNT_ID_LENGTH) return null;
    const rule = accounts[id];
    if (!hasExactKeys(rule, ["enabled", "thresholdPercent"])) return null;
    if (typeof rule.enabled !== "boolean") return null;
    if (!Number.isInteger(rule.thresholdPercent) || rule.thresholdPercent < 1 || rule.thresholdPercent > 100) {
      return null;
    }
    const key = id.toLowerCase();
    if (Object.hasOwn(normalized, key)) return null;
    normalized[key] = { enabled: rule.enabled, thresholdPercent: rule.thresholdPercent };
  }
  return { accounts: normalized, resetAlerts: value.resetAlerts };
}

function deviceRule(account, preferences) {
  const id = account.id.toLowerCase();
  return Object.hasOwn(preferences.accounts, id)
    ? preferences.accounts[id]
    : { enabled: true, thresholdPercent: DEFAULT_ALERT_THRESHOLD };
}

// Without preferences the desktop's account.alert decides (a subscription made
// before device preferences existed); with them, account.alert is ignored.
function thresholdFor(account, preferences) {
  if (!preferences) return normalizedThreshold(account);
  const rule = deviceRule(account, preferences);
  return rule.enabled ? rule.thresholdPercent : null;
}

function resetFor(account, preferences) {
  if (!preferences) return resetAlertsEnabled(account);
  return preferences.resetAlerts && deviceRule(account, preferences).enabled;
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

/** preferences: normalized device preferences, or null/undefined for the desktop's rules. */
export function findThresholdCrossings(previousSnapshot, currentSnapshot, preferences = null) {
  if (!previousSnapshot || typeof previousSnapshot !== "object") return [];
  const previousAccounts = new Map(
    (Array.isArray(previousSnapshot.accounts) ? previousSnapshot.accounts : [])
      .filter((account) => account && typeof account.id === "string")
      .map((account) => [account.id.toLowerCase(), account]),
  );
  const crossings = [];

  for (const account of Array.isArray(currentSnapshot?.accounts) ? currentSnapshot.accounts : []) {
    if (!account || typeof account.id !== "string") continue;
    const threshold = thresholdFor(account, preferences);
    if (threshold === null) continue;

    const previousAccount = previousAccounts.get(account.id.toLowerCase());
    if (!previousAccount) continue;
    // A desktop threshold travels in the snapshot, so a change between two
    // uploads must not read as a crossing. Device thresholds never do.
    if (!preferences && normalizedThreshold(previousAccount) !== threshold) continue;
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

export function findResetAlerts(previousSnapshot, currentSnapshot, preferences = null) {
  if (!previousSnapshot || typeof previousSnapshot !== "object") return [];
  const previousAccounts = new Map(
    (Array.isArray(previousSnapshot.accounts) ? previousSnapshot.accounts : [])
      .filter((account) => account && typeof account.id === "string")
      .map((account) => [account.id.toLowerCase(), account]),
  );
  const resets = [];

  for (const account of Array.isArray(currentSnapshot?.accounts) ? currentSnapshot.accounts : []) {
    if (!account || typeof account.id !== "string" || !resetFor(account, preferences)) continue;
    const previousAccount = previousAccounts.get(account.id.toLowerCase());
    if (!previousAccount || (!preferences && !resetAlertsEnabled(previousAccount))) continue;
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
