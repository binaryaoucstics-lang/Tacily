"use strict";
const test = require("node:test"), assert = require("node:assert/strict");
const { statusIndicator } = require("../page-auto-content.js");

test("the dot distinguishes idle, preparation, streaming, completion, attention, failure and click selection", () => {
  const cases = [["manual_ready", "idle"], ["skipped", "idle"], ["paused", "idle"], ["assessing", "preparing"],
    ["retrying", "preparing"], ["translating", "translating"], ["completed", "complete"], ["partial", "attention"],
    ["waiting_for_page", "attention"], ["failed", "failed"], ["paragraph_pending", "picking"]];
  for (const [state, phase] of cases) assert.equal(statusIndicator({ state }).phase, phase);
  const colors = new Set(["manual_ready", "assessing", "translating", "completed", "partial", "failed"].map(state => statusIndicator({ state }).color));
  assert.equal(colors.size, 6);
});

test("image work keeps the dot busy even when no page text is translating", () => {
  assert.equal(statusIndicator({ state: "manual_ready" }, { translating: 1 }).phase, "translating");
  assert.equal(statusIndicator({ state: "completed" }, { preparing: 1 }).phase, "preparing");
  assert.equal(statusIndicator({ state: "skipped" }, { completed: 1 }).phase, "complete");
  assert.equal(statusIndicator({ state: "translating" }, { completed: 3 }).phase, "translating");
  assert.equal(statusIndicator({ state: "manual_ready" }, { preparing: 0, translating: 0, completed: 0 }).busy, false);
});
