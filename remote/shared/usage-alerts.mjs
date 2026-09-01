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

export function findThresholdCrossings(previousSnapshot, currentSnapshot) {
  if (!previousSnapshot || typeof previousSnapshot !== "object") return [];
  const previousAccounts = new Map(
    (Array.isArray(previousSnapshot.accounts) ? previousSnapshot.accounts : [])
      .filter((account) => account && typeof account.id === "string")
      .map((account) => [account.id.toLowerCase(), account]),
  );
  const crossings = [];

  for (const account of Array.isArray(currentSnapshot?.accounts) ? currentSnapshot.accounts : []) {
    if (!account || typeof account.id !== "string") continue;
    const threshold = normalizedThreshold(account);
    if (threshold === null) continue;

    const previousAccount = previousAccounts.get(account.id.toLowerCase());
    if (!previousAccount || normalizedThreshold(previousAccount) !== threshold) continue;
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

export function findResetAlerts(previousSnapshot, currentSnapshot) {
  if (!previousSnapshot || typeof previousSnapshot !== "object") return [];
  const previousAccounts = new Map(
    (Array.isArray(previousSnapshot.accounts) ? previousSnapshot.accounts : [])
      .filter((account) => account && typeof account.id === "string")
      .map((account) => [account.id.toLowerCase(), account]),
  );
  const resets = [];

  for (const account of Array.isArray(currentSnapshot?.accounts) ? currentSnapshot.accounts : []) {
    if (!account || typeof account.id !== "string" || !resetAlertsEnabled(account)) continue;
    const previousAccount = previousAccounts.get(account.id.toLowerCase());
    if (!previousAccount || !resetAlertsEnabled(previousAccount)) continue;
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
