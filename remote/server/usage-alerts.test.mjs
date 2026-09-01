import assert from "node:assert/strict";
import { test } from "node:test";
import { findResetAlerts, findThresholdCrossings } from "../shared/usage-alerts.mjs";

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

test("does not detect resets when the account is not opted in", () => {
  assert.deepEqual(findResetAlerts(resetSnapshot(42, 42, false), resetSnapshot(0, 0, false)), []);
  assert.deepEqual(findResetAlerts(resetSnapshot(42, 42, false), resetSnapshot(0, 0, true)), []);
});
