"use strict";
const test = require("node:test"), assert = require("node:assert/strict");
const { createStreamWriter } = require("../translation-motion.js");
function fixture(initial = "初始") {
  let clock = 0, value = initial, current = true, reduced = false, id = 0;
  const frames = new Map(), writes = [];
  const writer = createStreamWriter({ requestFrame: fn => { frames.set(++id, fn); return id; }, cancelFrame: id => frames.delete(id),
    now: () => clock, allowMotion: () => !reduced });
  const options = { read: () => value, current: () => current, commit: text => { if (!current) return false; value = text; writes.push(text); return true; } };
  return { writer, options, frames, writes, value: () => value, setCurrent: next => { current = next; }, setReduced: next => { reduced = next; },
    write: (target, extra) => writer.write("node", target, { ...options, ...extra }),
    draw: time => { clock = time; const batch = [...frames.values()]; frames.clear(); for (const fn of batch) fn(time); } };
}

test("short append streams reveal intermediate text and do not drop any final character", () => {
  const f = fixture(); f.write("初始译文逐渐出现"); assert.equal(f.value(), "初始");
  f.draw(50); assert.ok(f.value().length > 2 && f.value().length < "初始译文逐渐出现".length);
  f.draw(100); assert.equal(f.value(), "初始译文逐渐出现"); assert.equal(f.writer.count(), 0); assert.equal(f.frames.size, 0);
});
test("fast repeated provider chunks coalesce without restarting the reveal deadline", () => {
  const f = fixture(); f.write("初始译文"); f.draw(40); f.write("初始译文以及后续内容");
  assert.equal(f.frames.size, 1); f.draw(100); assert.equal(f.value(), "初始译文以及后续内容"); assert.equal(f.writer.count(), 0);
});
test("verified finals and failures replace a scheduled prefix synchronously", () => {
  const f = fixture(); f.write("初始未完译文"); f.draw(20);
  f.write("最终译文", { immediate: true }); assert.equal(f.value(), "最终译文"); assert.equal(f.frames.size, 0);
  f.draw(150); assert.equal(f.value(), "最终译文");
  f.write("原文保留", { immediate: true }); assert.equal(f.value(), "原文保留");
});
test("source replacement or detached identity prevents queued animation from overwriting new page content", () => {
  const f = fixture(); f.write("初始旧译文"); f.setCurrent(false); f.draw(50);
  assert.equal(f.value(), "初始"); assert.equal(f.writes.length, 0); assert.equal(f.writer.count(), 0); assert.equal(f.frames.size, 0);
});
test("cache and reduced-motion writes never queue artificial typing or delay final display", () => {
  const f = fixture(); f.setReduced(true); f.write("初始缓存结果");
  assert.equal(f.value(), "初始缓存结果"); assert.equal(f.frames.size, 0);
  f.setReduced(false); f.write("初始缓存结果直接完整显示", { immediate: true }); assert.equal(f.frames.size, 0);
});
test("paced emoji tails never expose half of a UTF16 surrogate pair", () => {
  const f = fixture(); const target = "初始🙂中文🚀译文"; f.write(target);
  for (const at of [10, 25, 45, 70, 100]) { f.draw(at); assert.equal(/[\uD800-\uDBFF]$/u.test(f.value()), false); }
  assert.equal(f.value(), target);
});
test("pause cleanup can flush the latest admitted prefix or discard it without late writes", () => {
  const f = fixture(); f.write("初始可恢复前缀"); f.writer.clear(true); assert.equal(f.value(), "初始可恢复前缀"); assert.equal(f.frames.size, 0);
  f.write("初始可恢复前缀新的部分"); f.writer.clear(); f.draw(200); assert.equal(f.value(), "初始可恢复前缀");
});
test("large chunks and rewritten translations are shown immediately rather than prolonging the queue", () => {
  const f = fixture(); const large = "初始" + "译".repeat(190); f.write(large); assert.equal(f.value(), large); assert.equal(f.frames.size, 0);
  f.write("修订后的译文"); assert.equal(f.value(), "修订后的译文"); assert.equal(f.frames.size, 0);
});

test("authored trailing whitespace stays intact while short translated tails are paced", () => {
  const f = fixture("初始 \n"); f.write("初始译文🙂 \n", { padding: " \n" }); f.draw(50);
  assert.ok(f.value().endsWith(" \n")); assert.notEqual(f.value(), "初始译文🙂 \n");
  f.draw(100); assert.equal(f.value(), "初始译文🙂 \n");
});
