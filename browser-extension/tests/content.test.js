"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { ScrollAccumulator } = require("../scroll-accumulator.js");
const fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");

function observerFixture() {
  const messages = [], documentEvents = new Map(), windowEvents = new Map(), frames = new Map();
  let frameId = 0;
  class Element {
    constructor() { Object.assign(this, { scrollLeft: 0, scrollTop: 0, clientHeight: 100, scrollHeight: 1000,
      clientWidth: 100, scrollWidth: 100 }); }
    getBoundingClientRect() { return { left: 20, top: 30, width: 100, height: 100 }; }
  }
  const root = new Element(), body = new Element(), nested = new Element();
  const document = { documentElement: root, scrollingElement: root, body,
    addEventListener: (name, callback) => documentEvents.set(name, callback) };
  const context = vm.createContext({ ScrollAccumulator, document, Element, scrollX: 0, scrollY: 100,
    innerWidth: 1280, innerHeight: 720, devicePixelRatio: 1.25,
    crypto: { randomUUID: () => "01234567-89ab-4cde-8123-0123456789ab" },
    requestAnimationFrame: callback => { const id = ++frameId; frames.set(id, callback); return id; },
    cancelAnimationFrame: id => frames.delete(id),
    addEventListener: (name, callback) => windowEvents.set(name, callback),
    chrome: { runtime: { sendMessage: message => { messages.push(message); }, onMessage: { addListener() {} } } }
  });
  vm.runInContext(fs.readFileSync(path.join(__dirname, "..", "content.js"), "utf8"), context);
  function flush() { const ready = [...frames.values()]; frames.clear(); for (const frame of ready) frame(); }
  return { messages, document, nested, context, frames, flush,
    scroll: y => { context.scrollY = y; documentEvents.get("scroll")({ target: document }); },
    hide: persisted => windowEvents.get("pagehide")({ persisted }),
    show: persisted => windowEvents.get("pageshow")({ persisted }),
    prime: () => documentEvents.get("wheel")({ target: nested, composedPath: () => [nested, body, root, document] }),
    nestedScroll: y => { nested.scrollTop = y; documentEvents.get("scroll")({ target: nested }); }
  };
}

test("BFCache pauses pending movement and resumes the same token without document invalidation", () => {
  const f = observerFixture(), before = f.messages[0];
  f.scroll(125); assert.equal(f.frames.size, 1);
  f.hide(true); assert.equal(f.frames.size, 0); f.flush();
  f.scroll(350); f.flush();
  assert.equal(f.messages.length, 1);
  f.context.devicePixelRatio = 1.5; f.context.innerWidth = 1400;
  f.show(true);
  const restored = f.messages.at(-1);
  assert.equal(restored.type, "contentReady"); assert.equal(restored.documentToken, before.documentToken);
  assert.equal(restored.devicePixelRatio, 1.5); assert.equal(restored.viewportSize.width, 1400);
  f.scroll(365); f.flush();
  assert.equal(f.messages.at(-1).type, "scrollBatch");
  assert.equal(f.messages.at(-1).scrolls[0].deltaYCss, 15);
  assert.equal(f.messages.some(message => message.type === "contentInvalidated"), false);
});

test("repeated BFCache returns preserve identity but a later real unload invalidates once and cannot resume", () => {
  const f = observerFixture();
  for (let i = 0; i < 2; i++) { f.hide(true); f.show(true); }
  assert.equal(f.messages.filter(message => message.type === "contentReady").length, 3);
  f.hide(false); f.hide(false); f.show(true); f.scroll(400); f.flush();
  assert.equal(f.messages.filter(message => message.type === "contentInvalidated").length, 1);
  assert.equal(f.messages.at(-1).reason, "documentUnloaded");
  assert.equal(f.messages.filter(message => message.type === "contentReady").length, 3);
});

test("nested scrolling after BFCache reprimes its current position before emitting a fresh delta", () => {
  const f = observerFixture();
  f.prime(); f.nestedScroll(25); f.flush();
  f.hide(true); f.nested.scrollTop = 350; f.show(true);
  f.prime(); f.nestedScroll(360); f.flush();
  const scroll = f.messages.at(-1).scrolls[0];
  assert.equal(scroll.target, "element-1"); assert.equal(scroll.deltaYCss, 10);
  assert.equal(scroll.scrollContainer.left, 20);
});

test("coalesces multiple root scroll positions into one delta", () => {
  const accumulator = new ScrollAccumulator();
  accumulator.observe("root", 0, 100);
  accumulator.observe("root", 0, 125);

  assert.deepEqual(accumulator.flush(), [{
    target: "root",
    deltaXCss: 0,
    deltaYCss: 25
  }]);
});

test("keeps nested scroll targets separate", () => {
  const accumulator = new ScrollAccumulator();
  accumulator.observe("element-1", 10, 20);
  accumulator.observe("element-2", 30, 40);
  accumulator.observe("element-1", 13, 28);
  accumulator.observe("element-2", 27, 45);

  assert.deepEqual(accumulator.flush(), [
    {
      target: "element-1",
      deltaXCss: 3,
      deltaYCss: 8
    },
    {
      target: "element-2",
      deltaXCss: -3,
      deltaYCss: 5
    }
  ]);
});

test("does not emit unchanged positions", () => {
  const accumulator = new ScrollAccumulator();
  accumulator.observe("root", 4, 8);

  assert.equal(accumulator.observe("root", 4, 8), false);
  assert.deepEqual(accumulator.flush(), []);
});

test("ignores non-finite positions without poisoning the baseline", () => {
  const accumulator = new ScrollAccumulator();
  accumulator.observe("root", 0, 10);

  assert.equal(accumulator.observe("root", 0, Number.NaN), false);
  assert.equal(accumulator.observe("root", Number.POSITIVE_INFINITY, 20), false);
  accumulator.observe("root", 0, 15);

  assert.deepEqual(accumulator.flush(), [{
    target: "root",
    deltaXCss: 0,
    deltaYCss: 5
  }]);
});

test("invalidation advances generation and discards pending movement", () => {
  const accumulator = new ScrollAccumulator();
  accumulator.observe("root", 0, 10);
  accumulator.observe("root", 0, 30);

  assert.equal(accumulator.invalidate(), 1);
  assert.equal(accumulator.generation, 1);
  assert.deepEqual(accumulator.flush(), []);

  accumulator.observe("root", 0, 50);
  accumulator.observe("root", 0, 55);
  assert.deepEqual(accumulator.flush(), [{
    target: "root",
    deltaXCss: 0,
    deltaYCss: 5
  }]);
});

test("uses the latest nested-container geometry in a coalesced update", () => {
  const accumulator = new ScrollAccumulator();
  accumulator.observe("element-1", 0, 0);
  accumulator.observe("element-1", 0, 10, {
    scrollContainer: { left: 1, top: 2, width: 100, height: 80 }
  });
  accumulator.observe("element-1", 0, 15, {
    scrollContainer: { left: 3, top: 4, width: 100, height: 80 }
  });

  assert.deepEqual(accumulator.flush(), [{
    target: "element-1",
    deltaXCss: 0,
    deltaYCss: 15,
    scrollContainer: { left: 3, top: 4, width: 100, height: 80 }
  }]);
});
