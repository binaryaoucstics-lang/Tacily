"use strict";
const test = require("node:test"), assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const tick = () => new Promise(resolve => setImmediate(resolve));

function fixture() {
  const frames = new Map(), listeners = new Map(), hosts = [], decodes = [], log = [], sent = [], phases = [];
  let frameId = 0, lookupEnabled = true, savedPixels = null;
  class Node {
    constructor(tag = "root") { this.tag = tag; this.dataset = {}; this.children = []; this.isConnected = tag === "root";
      this.style = new Proxy({ setProperty: (key, value) => { log.push(`write:${key}`); this.style[key] = value; } },
        { set(target, key, value) { target[key] = value; if (key !== "cssText") log.push(`style:${key}`); return true; } }); }
    append(node) { this.children.push(node); node.parent = this; node.isConnected = true; }
    attachShadow() { this.shadow = new Node("shadow"); this.shadow.isConnected = true; return this.shadow; }
    setAttribute() {}
    remove() { this.isConnected = false; if (this.parent) this.parent.children = this.parent.children.filter(node => node !== this); }
    decode() { return new Promise((resolve, reject) => decodes.push({ node: this, resolve, reject })); }
  }
  const document = { hidden: false, documentElement: new Node(), createElement: tag => {
    const node = new Node(tag); if (tag === "div") hosts.push(node); return node;
  } };
  const context = vm.createContext({ document, performance,
    ScreenTranslatorMotion: { imageState: (_id, _image, phase) => phases.push(phase), cancelImage: () => phases.push("cancelled"), motionAllowed: () => false },
    requestAnimationFrame: fn => { const id = ++frameId; frames.set(id, fn); return id; }, cancelAnimationFrame: id => frames.delete(id),
    addEventListener: (name, fn) => listeners.set(name, fn), chrome: { runtime: { sendMessage: value => { sent.push(value); return Promise.resolve(); } } } });
  vm.runInContext(fs.readFileSync(path.join(__dirname, "..", "image-overlay.js"), "utf8"), context);
  const token = "document-1234567890", base = { imageId: "image-1", documentToken: token, sessionId: "session-1234567890" };
  const image = { isConnected: true, naturalWidth: 600, naturalHeight: 600, currentSrc: "source.png", src: "source.png" };
  let shape = { display: { left: 10, top: 20, width: 300, height: 300 }, clip: { left: 10, top: 20, width: 300, height: 300 } };
  const describe = () => { log.push("read"); return shape; };
  const lookup = () => lookupEnabled ? { image, src: "source.png", ...(savedPixels ? { pixelSize: savedPixels } : {}) } : null;
  const receive = value => context.ScreenTranslatorImageOverlay.receive({ ...base, ...value }, token, lookup, describe);
  const draw = () => { const first = frames.entries().next().value; if (first) { frames.delete(first[0]); first[1](); } };
  const begin = () => receive({ type: "imageOverlayBegin", generation: 1, sourceSize: { width: 600, height: 600 } });
  const commit = (revision = 1, bounds = { x: 10, y: 10, width: 32, height: 32 }) => {
    assert.equal(receive({ type: "imageOverlayChunk", id: "tile", revision, index: 0, data: "QUJDRA==" }).found, true);
    assert.equal(receive({ type: "imageOverlayCommit", id: "tile", revision, length: 8, bounds }).found, true);
  };
  const publish = async () => { begin(); commit(); draw(); decodes[0].resolve(); await tick(); draw(); };
  return { receive, draw, begin, commit, publish, hosts, decodes, context, image, listeners, sent, log, phases,
    setShape: value => { shape = value; }, setLookup: value => { lookupEnabled = value; }, setPixels: value => { savedPixels = value; }, token };
}

test("a streaming PNG revision keeps the previous decoded tile visible while scrolling", async () => {
  const f = fixture(); await f.publish(); const host = f.hosts[0], old = host.shadow.children[0];
  f.commit(2, { x: 20, y: 20, width: 32, height: 32 });
  f.setShape({ display: { left: 10.25, top: -30.5, width: 300.5, height: 300.5 }, clip: { left: 10.25, top: 0, width: 300.5, height: 270 } });
  for (let i = 0; i < 20; i++) { f.draw(); assert.equal(host.shadow.children[0], old); assert.equal(host.style.display, "block"); }
  // Old pixel revision also retains its old source-space bounds until the swap.
  assert.equal(old.style.left, `${10 * 300.5 / 600}px`);
  f.decodes[1].resolve(); await tick();
  assert.notEqual(host.shadow.children[0], old); assert.equal(old.isConnected, false);
  assert.equal(host.shadow.children[0].style.left, `${20 * 300.5 / 600}px`);
});

test("image waiting signal begins before any tile and completion waits for the final PNG decode", async () => {
  const f = fixture();
  f.receive({ type: "imageOverlayBegin", generation: 1, phase: "translating", sourceSize: { width: 600, height: 600 } });
  assert.equal(f.phases.at(-1), "translating"); assert.equal(f.decodes.length, 0);
  f.commit(); f.receive({ type: "imageOverlayState", enabled: true, phase: "complete", ids: ["tile"] });
  assert.equal(f.phases.includes("complete"), false); f.draw();
  assert.equal(f.phases.includes("complete"), false);
  f.decodes[0].resolve(); await tick(); assert.equal(f.phases.at(-1), "complete");
});

test("floating dot activity follows image phases and waits for the final tile to decode", async () => {
  const f = fixture();
  f.receive({ type: "imageOverlayBegin", generation: 1, phase: "preparing", sourceSize: { width: 600, height: 600 } });
  assert.equal(f.context.ScreenTranslatorImageTranslationActivity.preparing, 1);
  f.receive({ type: "imageOverlayState", enabled: true, phase: "translating" });
  assert.equal(f.context.ScreenTranslatorImageTranslationActivity.translating, 1);
  f.commit(); f.draw();
  f.receive({ type: "imageOverlayState", enabled: true, phase: "complete" });
  assert.equal(f.context.ScreenTranslatorImageTranslationActivity.translating, 1);
  assert.equal(f.context.ScreenTranslatorImageTranslationActivity.completed, 0);
  f.decodes[0].resolve(); await tick();
  assert.equal(f.context.ScreenTranslatorImageTranslationActivity.translating, 0);
  assert.equal(f.context.ScreenTranslatorImageTranslationActivity.completed, 1);
  f.listeners.get("keydown")({ key: "Escape" });
  assert.equal(f.context.ScreenTranslatorImageTranslationActivity.completed, 0);
});

test("cached image sessions carry their instant-display phase and Escape removes the waiting signal", () => {
  const f = fixture(); f.receive({ type: "imageOverlayBegin", generation: 1, phase: "cached", sourceSize: { width: 600, height: 600 } });
  assert.equal(f.phases.at(-1), "cached");
  f.listeners.get("keydown")({ key: "Escape" }); assert.equal(f.phases.at(-1), "cancelled");
  f.receive({ type: "imageOverlayState", enabled: true, phase: "translating" });
  assert.equal(f.phases.at(-1), "cancelled");
});

test("failed or out-of-order decodes cannot erase or resurrect a rendered revision", async () => {
  const f = fixture(); await f.publish(); const host = f.hosts[0], old = host.shadow.children[0];
  f.commit(2); f.commit(3); f.decodes[1].resolve(); await tick(); assert.equal(host.shadow.children[0], old);
  f.decodes[2].reject(new Error("invalid bitmap")); await tick(); f.draw(); assert.equal(host.shadow.children[0], old);
  f.commit(4); f.listeners.get("keydown")({ key: "Escape" }); f.draw();
  f.decodes[3].resolve(); await tick(); assert.equal(host.style.display, "none"); assert.equal(host.shadow.children[0], old);
});

test("offscreen and original mode retain decoded nodes and returning reveals without another decode", async () => {
  const f = fixture(); await f.publish(); const host = f.hosts[0], tile = host.shadow.children[0];
  f.setShape({ display: { left: 10, top: -1000, width: 300, height: 300 }, clip: { left: 10, top: 0, width: 300, height: 0 } });
  f.draw(); assert.equal(host.style.display, "none"); assert.equal(tile.isConnected, true);
  f.setShape({ display: { left: 10, top: 80, width: 300, height: 300 }, clip: { left: 10, top: 80, width: 300, height: 300 } });
  f.draw(); assert.equal(host.style.display, "block");
  f.context.ScreenTranslatorPageViewState = Object.freeze({ documentToken: f.token, enabled: true, mode: "original" });
  f.listeners.get("ScreenTranslatorPageViewChanged")({ detail: { mode: "translation" } }); f.draw(); assert.equal(host.style.display, "none");
  f.context.ScreenTranslatorPageViewState = Object.freeze({ documentToken: f.token, enabled: true, mode: "bilingual" });
  f.listeners.get("ScreenTranslatorPageViewChanged")({ detail: { mode: "original" } }); f.draw(); assert.equal(host.style.display, "block");
  assert.equal(host.shadow.children[0], tile); assert.equal(f.decodes.length, 1); assert.equal(f.sent.length, 0);
});

test("forged event detail or an old document original view cannot hide the current image", async () => {
  const f = fixture(); await f.publish(); const host = f.hosts[0];
  f.context.ScreenTranslatorPageViewState = { documentToken: "old-document", enabled: true, mode: "original" };
  f.listeners.get("ScreenTranslatorPageViewChanged")({ detail: { documentToken: f.token, enabled: true, mode: "original" } });
  f.draw(); assert.equal(host.style.display, "block");
});

test("stable frames perform no overlay style writes while still checking source geometry", async () => {
  const f = fixture(); await f.publish(); f.log.length = 0;
  for (let i = 0; i < 10; i++) f.draw();
  assert.equal(f.log.filter(value => value === "read").length, 10);
  assert.equal(f.log.filter(value => value.startsWith("write:") || value.startsWith("style:")).length, 0);
});

test("lookup LRU eviction retains the same DOM source but a real source change removes it", async () => {
  const f = fixture(); await f.publish(); const host = f.hosts[0];
  f.setLookup(false); f.draw(); assert.equal(host.isConnected, true); assert.equal(host.style.display, "block");
  f.commit(2); f.decodes[1].resolve(); await tick(); f.draw(); assert.equal(host.shadow.children.length, 1);
  f.image.currentSrc = "new-source.png"; f.draw(); assert.equal(host.isConnected, false);
});

test("removed tiles cannot be reinserted by an already pending PNG decode", async () => {
  const f = fixture(); await f.publish(); const host = f.hosts[0]; f.commit(2);
  f.receive({ type: "imageOverlayState", enabled: true, ids: [] });
  f.decodes[1].resolve(); await tick(); f.draw(); assert.equal(host.shadow.children.length, 0);
});

test("2x decoded pixels are validated independently of density-corrected natural CSS dimensions", async () => {
  const f = fixture(); f.setPixels({ width: 1200, height: 1200 });
  assert.equal(f.begin().found, false);
  assert.equal(f.receive({ type: "imageOverlayBegin", generation: 1, sourceSize: { width: 1200, height: 1200 } }).found, true);
  f.commit(); f.draw(); f.decodes[0].resolve(); await tick(); f.draw();
  const host = f.hosts[0]; assert.equal(host.style.display, "block");
  assert.equal(host.shadow.children[0].style.width, "8px");
  f.image.naturalWidth = 601;
  assert.equal(f.receive({ type: "imageOverlayState", enabled: true }).found, false);
  f.draw(); assert.equal(host.isConnected, false);
});
