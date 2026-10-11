"use strict";
const test = require("node:test"), assert = require("node:assert/strict");
const { AutoImageQueue, eligibleImage, normalizeConfig, pageAllowsImages, randomIdentifier, previewSize } = require("../image-auto-policy.js");
const tick = () => new Promise(resolve => setImmediate(resolve));
test("HTTP pages use secure random image identities without requiring the secure-context randomUUID API", () => {
  const { getRandomValues } = require("node:crypto");
  const first = randomIdentifier({ getRandomValues }), second = randomIdentifier({ getRandomValues });
  assert.match(first, /^[a-f\d]{8}-[a-f\d]{4}-4[a-f\d]{3}-[89ab][a-f\d]{3}-[a-f\d]{12}$/u);
  assert.notEqual(first, second);
});
function deferred() { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; }
function fixture(options = {}) {
  const sent = [], reads = [];
  const queue = new AutoImageQueue({ read: async (image, version) => { reads.push(image); return { image, version }; },
    send: async candidate => { sent.push(candidate); }, isCurrent: image => image.visible !== false, ...options });
  return { queue, sent, reads };
}
test("auto mode is disabled until an explicit valid desktop configuration", async () => {
  const f = fixture(), image = {};
  assert.equal(f.queue.offer(image, "a"), false);
  assert.equal(f.queue.configure({ enabled: true, generation: Number.MAX_SAFE_INTEGER + 1 }), false);
  assert.equal(f.queue.configure({ enabled: true, generation: 1, targetLanguage: "简体中文" }), true);
  assert.equal(f.queue.offer(image, "a"), true); await tick();
  assert.equal(f.sent.length, 1);
});
test("same source version is assessed once, source changes are assessed again", async () => {
  const f = fixture(), image = {};
  f.queue.configure({ enabled: true, generation: 1 });
  f.queue.offer(image, "src-a"); await tick();
  assert.equal(f.queue.offer(image, "src-a"), false);
  assert.equal(f.queue.offer(image, "src-b"), true); await tick();
  assert.equal(f.sent.length, 2);
});
test("same config keeps history; newer generation resets history; stale config is rejected", async () => {
  const f = fixture(), image = {};
  f.queue.configure({ enabled: true, generation: 2 }); f.queue.offer(image, "a"); await tick();
  assert.equal(f.queue.configure({ enabled: true, generation: 2 }), false);
  assert.equal(f.queue.offer(image, "a"), false);
  assert.equal(f.queue.configure({ enabled: false, generation: 1 }), false);
  f.queue.configure({ enabled: true, generation: 3 }); f.queue.offer(image, "a"); await tick();
  assert.deepEqual(f.sent.map(c => c.generation), [2, 3]);
});
test("read/assessment pipeline is serial and applies bounded queue backpressure", async () => {
  const wait = deferred(), f = fixture({ maximumPending: 2, send: async () => wait.promise });
  f.queue.configure({ enabled: true, generation: 1 });
  f.queue.offer({}, "one"); await tick();
  assert.equal(f.queue.offer({}, "two"), true);
  assert.equal(f.queue.offer({}, "three"), true);
  assert.equal(f.queue.offer({}, "overflow"), false);
  assert.equal(f.reads.length, 1);
  wait.resolve(); await tick();
  assert.equal(f.reads.length, 3);
});
test("turning off config during preview prevents sending and discards waiting images", async () => {
  const wait = deferred(), f = fixture({ read: async () => wait.promise });
  f.queue.configure({ enabled: true, generation: 1 });
  f.queue.offer({}, "a"); f.queue.offer({}, "b");
  f.queue.configure({ enabled: false, generation: 2 }); wait.resolve({}); await tick();
  assert.equal(f.sent.length, 0); assert.equal(f.queue.pending.length, 0);
});
test("offscreen or source-changed pending image is skipped and can return later", async () => {
  const wait = deferred(), first = {}, second = {}, f = fixture({ send: async () => wait.promise });
  f.queue.configure({ enabled: true, generation: 1 });
  f.queue.offer(first, "a"); await tick(); f.queue.offer(second, "b"); second.visible = false;
  wait.resolve(); await tick();
  assert.equal(f.reads.length, 1);
  second.visible = true; assert.equal(f.queue.offer(second, "b"), true); await tick();
  assert.equal(f.reads.length, 2);
});
test("unavailable pixels do not cause endless retries", async () => {
  let attempts = 0;
  const f = fixture({ read: async () => { attempts++; throw new Error("CORS unavailable"); } }), image = {};
  f.queue.configure({ enabled: true, generation: 1 }); f.queue.offer(image, "a"); await tick();
  assert.equal(f.queue.offer(image, "a"), false); assert.equal(attempts, 1);
});
test("desktop retry clears only the same source history and waits until it is visible", async () => {
  const f = fixture(), image = {};
  f.queue.configure({ enabled: true, generation: 1 }); f.queue.offer(image, "a"); await tick();
  assert.equal(f.queue.retry(image, "old-source"), false);
  image.visible = false; assert.equal(f.queue.retry(image, "a"), true);
  assert.equal(f.queue.offer(image, "a"), false); await tick(); assert.equal(f.sent.length, 1);
  image.visible = true; assert.equal(f.queue.offer(image, "a"), true); await tick(); assert.equal(f.sent.length, 2);
});
test("eligibility ignores icons, avatars, offscreen images, oversized originals and invalid dimensions", () => {
  const base = { natural: { width: 850, height: 1200 }, display: { width: 425, height: 600 }, clip: { width: 425, height: 400 }, viewportSize: { width: 1280, height: 720 } };
  assert.equal(eligibleImage(base), true);
  for (const change of [ { natural: { width: 64, height: 64 } }, { display: { width: 40, height: 60 } },
    { clip: { width: 0, height: 0 } }, { clip: { width: 40, height: 40 } }, { natural: { width: 6000, height: 6000 } },
    { natural: { width: Number.NaN, height: 1000 } }, { complete: false }, { connected: false } ]) {
    assert.equal(eligibleImage({ ...base, ...change }), false);
  }
});

test("large-image admission follows actual display and viewport share, never original megapixels or a narrow visible sliver", () => {
  for (const [width, height, vw, vh, expected] of [[160, 220, 1280, 720, false], [320, 240, 1280, 720, false],
    [400, 300, 800, 600, true], [400, 300, 1920, 1080, false], [425, 600, 3840, 2160, true],
    [600, 1800, 1280, 720, true], [200, 1000, 800, 600, false], [600, 800, 0, 720, false]]) {
    assert.equal(eligibleImage({ natural: { width: 850, height: 1200 }, display: { width, height },
      clip: { width: Math.min(width, vw), height: Math.min(height, vh) }, viewportSize: { width: vw, height: vh } }), expected);
  }
  const large = { natural: { width: 850, height: 1200 }, display: { width: 425, height: 600 },
    clip: { width: 425, height: 80 }, viewportSize: { width: 1280, height: 720 } };
  assert.equal(eligibleImage(large), false);
  assert.equal(eligibleImage({ ...large, viewportSize: undefined }), false);
});
test("preview dimensions preserve aspect ratio, never upscale and never exceed1280", () => {
  assert.deepEqual(previewSize(2150, 3036), { width: 906, height: 1280 });
  assert.deepEqual(previewSize(600, 400), { width: 600, height: 400 });
  assert.equal(normalizeConfig({ enabled: true, generation: "1" }), null);
});
test("full-page gate requires a finished positive decision of the exact image config generation", () => {
  const config = { enabled: true, generation: 2, requirePageJudgement: true };
  const decision = { enabled: true, assessed: true, allowImages: true, generation: 2 };
  assert.equal(normalizeConfig(config).requirePageJudgement, true);
  assert.equal(pageAllowsImages(config, decision), true);
  for (const change of [ { enabled: false }, { assessed: false }, { allowImages: false }, { generation: 1 }, { generation: "2" } ])
    assert.equal(pageAllowsImages(config, { ...decision, ...change }), false);
  assert.equal(pageAllowsImages(config, undefined), false);
  assert.equal(pageAllowsImages({ ...config, requirePageJudgement: false }, undefined), true);
});
test("page admission suspension drops deferred work but retains completed source history", async () => {
  const first = {}, pending = {}, deferredRead = deferred(), f = fixture({ read: async image => image === pending ? deferredRead.promise : ({ image }) });
  f.queue.configure({ enabled: true, generation: 2, requirePageJudgement: true });
  f.queue.offer(first, "a"); await tick(); f.queue.offer(pending, "b");
  f.queue.suspendPending(); deferredRead.resolve({}); await tick();
  assert.equal(f.sent.length, 1); assert.equal(f.queue.offer(first, "a"), false);
  assert.equal(f.queue.offer(pending, "b"), true); await tick(); assert.equal(f.sent.length, 2);
  assert.equal(f.queue.retry(first, "a"), true); assert.equal(f.queue.offer(first, "a"), true); await tick(); assert.equal(f.sent.length, 3);
});

test("inactive tab configuration retains completed positive and negative decisions without more reads", async () => {
  const f = fixture(), first = {}, second = {};
  f.queue.configure({ enabled: true, active: true, generation: 2 });
  f.queue.offer(first, "one"); f.queue.offer(second, "two"); await tick();
  const epoch = f.queue.epoch;
  assert.equal(f.queue.configure({ enabled: true, active: false, generation: 2 }), true);
  assert.equal(f.queue.offer({}, "not-admitted"), false);
  f.queue.configure({ enabled: true, active: true, generation: 2 });
  assert.equal(f.queue.epoch, epoch);
  assert.equal(f.queue.offer(first, "one"), false); assert.equal(f.queue.offer(second, "two"), false);
  await tick(); assert.equal(f.reads.length, 2); assert.equal(f.sent.length, 2);
});

test("assessment admitted before switching tabs is reserved until its same-source result finishes", async () => {
  const wait = deferred(), image = {}, sent = [], f = fixture({
    send: async message => { sent.push(message); return wait.promise; },
    isCurrent: () => f.queue.config.active, isSameSource: () => true
  });
  f.queue.configure({ enabled: true, active: true, generation: 2 }); f.queue.offer(image, "a"); await tick();
  f.queue.configure({ enabled: true, active: false, generation: 2 });
  wait.resolve({ needsTranslation: false, reason: "no_foreign_text" }); await tick();
  f.queue.configure({ enabled: true, active: true, generation: 2 });
  assert.equal(f.queue.offer(image, "a"), false); await tick();
  assert.equal(sent.length, 1); assert.equal(f.reads.length, 1);
});

test("switching tabs during preview discards unadmitted read and permits one fresh admission after return", async () => {
  const wait = deferred(), image = {}, f = fixture({ read: async () => wait.promise,
    isCurrent: () => f.queue.config.active, isSameSource: () => true });
  f.queue.configure({ enabled: true, active: true, generation: 2 }); f.queue.offer(image, "a");
  f.queue.configure({ enabled: true, active: false, generation: 2 }); wait.resolve({ image }); await tick();
  assert.equal(f.sent.length, 0);
  f.queue.configure({ enabled: true, active: true, generation: 2 });
  assert.equal(f.queue.offer(image, "a"), true); await tick(); assert.equal(f.sent.length, 1);
});

test("retryable unavailable assessment never enters completed cache or an automatic retry loop", async () => {
  const f = fixture({ send: async () => ({ needsTranslation: false, reason: "image_unavailable" }) }), image = {};
  f.queue.configure({ enabled: true, generation: 2 }); f.queue.offer(image, "a"); await tick();
  assert.equal(f.queue.completed.has(image), false); assert.equal(f.queue.offer(image, "a"), false);
  assert.equal(f.queue.retry(image, "a"), true); assert.equal(f.queue.offer(image, "a"), true);
});

test("finishing an old image source cannot release the new source reservation or cause duplicate assessment", async () => {
  const wait = deferred(), image = { version: "old" }, sent = [], f = fixture({
    send: async candidate => { sent.push(candidate); if (candidate.version === "old") await wait.promise; },
    isCurrent: (target, version) => target.version === version,
    isSameSource: (target, version) => target.version === version,
    onSettled: () => { f.queue.offer(image, image.version); }
  });
  f.queue.configure({ enabled: true, generation: 2 }); f.queue.offer(image, "old"); await tick();
  image.version = "new"; f.queue.offer(image, "new"); wait.resolve(); await tick();
  assert.deepEqual(sent.map(candidate => candidate.version), ["old", "new"]);
  assert.equal(f.queue.offer(image, "new"), false);
});
