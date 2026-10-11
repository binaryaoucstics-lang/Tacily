"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const {
  hasCompleteViewportState,
  recoverCompleteViewportState
} = require("../document-state.js");

test("rejects SPA state restored from scroll without viewport geometry", () => {
  assert.equal(hasCompleteViewportState({
    documentToken: "document-1",
    viewportSize: null
  }), false);
});

test("accepts state after viewport geometry is restored", () => {
  assert.equal(hasCompleteViewportState({
    documentToken: "document-1",
    viewportSize: { width: 1280, height: 720 }
  }), true);
});

test("rejects invalid viewport geometry", () => {
  assert.equal(hasCompleteViewportState({
    viewportSize: { width: 0, height: 720 }
  }), false);
  assert.equal(hasCompleteViewportState({
    viewportSize: { width: Number.NaN, height: 720 }
  }), false);
});

test("recovers complete viewport after SPA navigation leaves partial state", async () => {
  let queryCount = 0;
  const recovered = await recoverCompleteViewportState(
    {
      documentToken: "document-1",
      viewportSize: null
    },
    async () => {
      queryCount += 1;
      return {
        documentToken: "document-1",
        viewportSize: { width: 1280, height: 720 }
      };
    });

  assert.equal(queryCount, 1);
  assert.deepEqual(recovered.viewportSize, {
    width: 1280,
    height: 720
  });
});

test("does not query content script when viewport state is already complete", async () => {
  let queryCount = 0;
  const current = {
    documentToken: "document-1",
    viewportSize: { width: 1280, height: 720 }
  };

  const recovered = await recoverCompleteViewportState(
    current,
    async () => {
      queryCount += 1;
      return null;
    });

  assert.equal(queryCount, 0);
  assert.equal(recovered, current);
});
