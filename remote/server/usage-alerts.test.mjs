import assert from "node:assert/strict";
import { test } from "node:test";
import {
  findResetAlerts,
  findThresholdCrossings,
  normalizeAlertPreferences,
} from "../shared/usage-alerts.mjs";

function snapshot(session, weekly, threshold = 80, enabled = true) {
  return {
    version: 2,
    displayMode: "used",
    accounts: [{
      id: "claude:work",
      name: "Claude Work",
      alert: { enabled, thresholdPercent: threshold },
      windows: [
        { label: "Session", usedPercent: session, resetsAt: "2026-08-28T14:00:00Z" },
        { label: "Weekly", usedPercent: weekly, resetsAt: "2026-09-01T12:00:00Z" },
      ],
    }],
  };
}

function resetSnapshot(session, weekly, resetEnabled = true) {
  return {
    version: 2,
    displayMode: "used",
    accounts: [{
      id: "codex:personal",
      name: "Codex Personal",
      alert: { enabled: true, thresholdPercent: 80, resetEnabled },
      windows: [
        { label: "Session", usedPercent: session },
        { label: "Weekly", usedPercent: weekly },
      ],
    }],
  };
}

test("tracks each account window independently and emits only on crossings", () => {
  let previous = snapshot(79, 82);
  let current = snapshot(81, 83);

  const first = findThresholdCrossings(previous, current);
  assert.equal(first.length, 1);
  assert.equal(first[0].windowKey, "session");

  previous = current;
  current = snapshot(90, 84);
  assert.deepEqual(findThresholdCrossings(previous, current), []);

  previous = snapshot(4, 84);
  current = snapshot(80, 85);
  assert.equal(findThresholdCrossings(previous, current).length, 1);
});

test("does not retroactively alert on first upload, enable, or threshold change", () => {
  assert.deepEqual(findThresholdCrossings(null, snapshot(90, 90)), []);
  assert.deepEqual(findThresholdCrossings(snapshot(79, 79, 80, false), snapshot(90, 90)), []);
  assert.deepEqual(findThresholdCrossings(snapshot(85, 85, 90), snapshot(86, 86, 80)), []);
});

test("scoped windows are keyed separately", () => {
  const previous = snapshot(10, 10);
  const current = snapshot(10, 10);
  previous.accounts[0].windows.push({ label: "Weekly", scope: "Fable", usedPercent: 79 });
  previous.accounts[0].windows.push({ label: "Weekly", scope: "Sonnet", usedPercent: 90 });
  current.accounts[0].windows.push({ label: "Weekly", scope: "Fable", usedPercent: 81 });
  current.accounts[0].windows.push({ label: "Weekly", scope: "Sonnet", usedPercent: 91 });

  const alert = findThresholdCrossings(previous, current)[0];
  assert.equal(alert.windowKey, "weekly:fable");
  assert.equal(alert.scope, "Fable");
});

test("detects a reset only on a non-zero to zero transition", () => {
  assert.deepEqual(findResetAlerts(null, resetSnapshot(0, 0)), []);
  const resets = findResetAlerts(resetSnapshot(42, 42), resetSnapshot(0, 0));
  assert.equal(resets.length, 1);
  assert.equal(resets[0].windowKey, "weekly");
  assert.deepEqual(findResetAlerts(resetSnapshot(0, 0), resetSnapshot(0, 0)), []);
});

test("does not detect a session reset", () => {
  assert.deepEqual(findResetAlerts(resetSnapshot(42, 42), resetSnapshot(0, 42)), []);
});

test("collapses a scoped weekly reset into the account-wide reset", () => {
  const previous = resetSnapshot(10, 42);
  const current = resetSnapshot(10, 0);
  previous.accounts[0].windows.push({ label: "Weekly", scope: "Fable", usedPercent: 17 });
  current.accounts[0].windows.push({ label: "Weekly", scope: "Fable", usedPercent: 0 });

  const resets = findResetAlerts(previous, current);
  assert.equal(resets.length, 1);
  assert.equal(resets[0].windowKey, "weekly");
  assert.equal(resets[0].scope, null);
});

test("a scoped weekly reset alone still alerts", () => {
  const previous = resetSnapshot(10, 0);
  const current = resetSnapshot(10, 0);
  previous.accounts[0].windows.push({ label: "Weekly", scope: "Fable", usedPercent: 17 });
  current.accounts[0].windows.push({ label: "Weekly", scope: "Fable", usedPercent: 0 });

  const resets = findResetAlerts(previous, current);
  assert.equal(resets.length, 1);
  assert.equal(resets[0].windowKey, "weekly:fable");
  assert.equal(resets[0].scope, "Fable");
});

test("does not detect resets when the account is not opted in", () => {
  assert.deepEqual(findResetAlerts(resetSnapshot(42, 42, false), resetSnapshot(0, 0, false)), []);
  assert.deepEqual(findResetAlerts(resetSnapshot(42, 42, false), resetSnapshot(0, 0, true)), []);
});

test("device preferences replace the desktop's rules for every account", () => {
  const devicePreferences = normalizeAlertPreferences({
    accounts: { "Claude:Work": { enabled: true, thresholdPercent: 50 } },
    resetAlerts: false,
  });
  // The desktop has this account's alerts off; the device decides alone.
  const previous = snapshot(40, 40, 80, false);
  const current = snapshot(60, 45, 80, false);
  assert.deepEqual(findThresholdCrossings(previous, current), []);
  const crossings = findThresholdCrossings(previous, current, devicePreferences);
  assert.deepEqual(crossings.map((crossing) => [crossing.windowKey, crossing.thresholdPercent]), [["session", 50]]);

  // A desktop threshold change between uploads is not a device crossing guard.
  assert.equal(findThresholdCrossings(snapshot(40, 40, 90), snapshot(60, 45, 10), devicePreferences).length, 1);
});

test("accounts without a device entry alert at 90% and resets follow the device switch", () => {
  const defaults = normalizeAlertPreferences({ accounts: {}, resetAlerts: true });
  assert.equal(findThresholdCrossings(snapshot(85, 85), snapshot(89, 89), defaults).length, 0);
  assert.equal(findThresholdCrossings(snapshot(85, 85), snapshot(91, 89), defaults).length, 1);

  assert.equal(findResetAlerts(resetSnapshot(10, 40, false), resetSnapshot(10, 0, false), defaults).length, 1);
  const noResets = normalizeAlertPreferences({ accounts: {}, resetAlerts: false });
  assert.deepEqual(findResetAlerts(resetSnapshot(10, 40), resetSnapshot(10, 0), noResets), []);
  const disabled = normalizeAlertPreferences({
    accounts: { "codex:personal": { enabled: false, thresholdPercent: 90 } },
    resetAlerts: true,
  });
  assert.deepEqual(findResetAlerts(resetSnapshot(10, 40), resetSnapshot(10, 0), disabled), []);
});

test("device preferences parse strictly", () => {
  const rule = { enabled: true, thresholdPercent: 70 };
  assert.deepEqual(
    JSON.parse(JSON.stringify(normalizeAlertPreferences({ accounts: { "B:x": rule, a: rule }, resetAlerts: true }))),
    { accounts: { a: rule, "b:x": rule }, resetAlerts: true },
  );
  for (const invalid of [
    null,
    [],
    { accounts: {} },
    { accounts: [], resetAlerts: true },
    { accounts: {}, resetAlerts: "yes" },
    { accounts: { a: { enabled: true } }, resetAlerts: true },
    { accounts: { a: { ...rule, note: 1 } }, resetAlerts: true },
    { accounts: { a: { enabled: true, thresholdPercent: 101 } }, resetAlerts: true },
    { accounts: { ["x".repeat(129)]: rule }, resetAlerts: true },
  ]) {
    assert.equal(normalizeAlertPreferences(invalid), null, JSON.stringify(invalid));
  }
});
