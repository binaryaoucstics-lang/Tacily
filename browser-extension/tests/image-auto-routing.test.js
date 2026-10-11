"use strict";
const test = require("node:test"), assert = require("node:assert/strict");
const fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const tick = () => new Promise(resolve => setImmediate(resolve));
function event() { const listeners = []; return { listeners, addListener: listener => listeners.push(listener),
  emit: (...args) => listeners.map(listener => listener(...args)) }; }
function fixture(options = {}) {
  const nativeMessages = [], tabMessages = [], timers = new Set();
  const token = "01234567-89ab-4cde-8123-0123456789ab";
  const tab = { id: 11, windowId: 7, active: true, url: "https://reader.example/private?secret=123" };
  const documentState = { documentToken: token, devicePixelRatio: 1.25, viewportSize: { width: 1280, height: 720 } };
  const image = { found: true, imageId: "image-1", documentToken: token, natural: { width: 850, height: 1200 },
    autoImageAllowed: true, autoImageAdmitted: true,
    display: { left: 0, top: 0, width: 425, height: 600 }, clip: { left: 0, top: 0, width: 425, height: 600 },
    viewportSize: { width: 1280, height: 720 }, devicePixelRatio: 1.25 };
  const port = { onMessage: event(), onDisconnect: event(), postMessage: message => nativeMessages.push(message) };
  const chrome = { runtime: { id: "extension", lastError: null, connectNative: () => port, onMessage: event() },
    declarativeNetRequest: {}, tabs: { onActivated: event(), onRemoved: event(), onUpdated: event(),
      get: async id => { if (id !== tab.id) throw new Error("Tab closed"); return { ...tab }; }, query: async () => [tab], sendMessage: async (id, message, settings) => {
        if (id !== tab.id) throw new Error("Tab closed");
        tabMessages.push({ id, message, options: settings });
        await options.beforeReply?.(message);
        if (message.type === "queryDocumentState") return { ...documentState };
        if (message.type === "queryImagePreview") return { ...image, previewBase64: "QUJDRA==", url: "https://cdn.example/private.png" };
        if (message.type === "queryImageGeometry") return { ...image };
        if (message.type === "queryImage") return { ...image, base64: "QUJDRA==" };
        return { found: true };
      } }, windows: { WINDOW_ID_NONE: -1, onFocusChanged: event(),
        getLastFocused: async () => { throw new Error("No foreground fixture window"); }, get: async () => ({ left: 0, top: 0, width: 1280, height: 800 }) } };
  let context;
  context = vm.createContext({ chrome, navigator: { userAgent: "Chrome/154" }, URL, AbortSignal,
    fetch: async () => { throw new Error("Unexpected download"); },
    setTimeout: (fn, ms) => { const id = setTimeout(fn, ms); id.unref(); timers.add(id); return id; },
    clearTimeout: id => { clearTimeout(id); timers.delete(id); },
    importScripts: (...files) => files.forEach(file => vm.runInContext(fs.readFileSync(path.join(__dirname, "..", file), "utf8"), context)) });
  vm.runInContext(fs.readFileSync(path.join(__dirname, "..", "background.js"), "utf8"), context);
  const sender = { tab, frameId: 0 };
  const candidate = { type: "autoImageCandidate", requestId: "request-1", imageId: image.imageId,
    documentToken: token, devicePixelRatio: 1.25, generation: 1 };
  return { nativeMessages, tabMessages, port, chrome, sender, candidate, image, tab, documentState,
    configure: (enabled = true, generation = 1) => port.onMessage.emit({ type: "imageAutoConfig", enabled, generation, targetLanguage: "简体中文" }),
    send: (message, respond) => chrome.runtime.onMessage.emit(message, sender, respond),
    assess: (overrides = {}) => port.onMessage.emit({ ...candidate, tabId: tab.id, type: "autoImageAssessment", needsTranslation: true, reason: "foreign_text", ...overrides }),
    dispose: () => { for (const timer of timers) clearTimeout(timer); } };
}
test("automatic detection is opt-in and inactive top-frame config retains enabled with separate active admission", async t => {
  const f = fixture(); t.after(f.dispose); let reply;
  f.send(f.candidate, result => { reply = result; }); await tick();
  assert.equal(reply.needsTranslation, false);
  assert.equal(f.nativeMessages.some(m => m.type === "autoImageCandidate"), false);
  f.configure(); await tick();
  const config = f.tabMessages.find(m => m.message.type === "imageAutoConfig");
  assert.equal(config.message.enabled, true); assert.equal(config.options.frameId, 0);
  assert.equal(config.message.active, true);
  f.tab.active = false;
  f.send({ type: "queryImageAutoConfig" }, result => { reply = result; });
  assert.equal(reply.enabled, true); assert.equal(reply.active, false);
  f.sender.frameId = 2;
  f.send({ type: "queryImageAutoConfig" }, result => { reply = result; });
  assert.equal(reply.enabled, false);
});

test("worker rejects a thumbnail before asking for preview pixels, even if a stale candidate is offered", async t => {
  const f = fixture(); t.after(f.dispose); f.configure(); let reply;
  f.image.display = { left: 0, top: 0, width: 160, height: 220 };
  f.image.clip = { ...f.image.display };
  f.send(f.candidate, result => { reply = result; }); await tick(); await tick();
  assert.equal(reply.needsTranslation, false);
  assert.equal(f.tabMessages.filter(item => item.message.type === "queryImagePreview").length, 0);
  assert.equal(f.nativeMessages.filter(item => item.type === "autoImageCandidate").length, 0);
});

test("website blacklist blocks automatic preview reads while manual image access remains available", async t => {
  const f = fixture(); t.after(f.dispose); let reply;
  f.port.onMessage.emit({ type: "imageAutoConfig", enabled: true, generation: 1, siteBlacklist: ["reader.example"] }); await tick();
  f.send({ type: "queryImageAutoConfig" }, value => { reply = value; }); assert.equal(reply.enabled, false);
  f.send(f.candidate, value => { reply = value; }); await tick();
  assert.equal(reply.needsTranslation, false); assert.equal(f.tabMessages.some(item => item.message.type === "queryImagePreview"), false);
  f.port.onMessage.emit({ type: "queryImage", requestId: "manual-blacklisted", tabId: 11, browserWindowId: 7, imageId: f.image.imageId, documentToken: f.image.documentToken });
  await tick(); await tick(); assert.equal(f.nativeMessages.some(message => message.type === "browserImageReply" && message.requestId === "manual-blacklisted" && message.found), true);
});

test("motion preference is broadcast independently and cannot cancel pending image judgement", async t => {
  const f = fixture(); t.after(f.dispose); f.configure(); let reply;
  f.send(f.candidate, result => { reply = result; }); await tick(); await tick();
  f.port.onMessage.emit({ type: "translationMotionConfig", enabled: false }); await tick();
  assert.ok(f.tabMessages.some(item => item.message.type === "translationMotionConfig" && item.message.enabled === false));
  let preference; f.send({ type: "queryTranslationMotionConfig" }, result => { preference = result; }); assert.equal(preference.enabled, false);
  assert.equal(reply, undefined); f.assess(); await tick(); assert.equal(reply.needsTranslation, true);
  assert.equal(f.nativeMessages.filter(item => item.type === "autoImageCandidate").length, 1);
});
test("candidate contains bounded preview and geometry, never source URL; waits for matching local assessment", async t => {
  const f = fixture(); t.after(f.dispose); f.configure(); let reply;
  assert.equal(f.send(f.candidate, result => { reply = result; })[0], true); await tick(); await tick();
  const candidate = f.nativeMessages.find(m => m.type === "autoImageCandidate");
  assert.ok(candidate); assert.equal(candidate.previewBase64, "QUJDRA=="); assert.equal(candidate.tabId, 11);
  assert.equal(candidate.browserWindowId, 7); assert.equal(candidate.frameId, 0);
  assert.equal(JSON.stringify(candidate).includes("https://"), false); assert.equal(reply, undefined);
  f.assess({ imageId: "old-source" }); await tick(); assert.equal(reply, undefined);
  f.assess(); await tick(); assert.equal(reply.needsTranslation, true); assert.equal(reply.reason, "foreign_text");
});
test("document identity, generation and active-tab guards prevent stale or background candidates", async t => {
  const f = fixture(); t.after(f.dispose); f.configure(); const results = [];
  f.send({ ...f.candidate, generation: 0 }, result => results.push(result));
  f.send({ ...f.candidate, requestId: "old-doc", documentToken: "00000000-0000-4000-8000-000000000000" }, result => results.push(result));
  await tick(); await tick();
  f.tab.active = false; f.send({ ...f.candidate, requestId: "background" }, result => results.push(result)); await tick();
  assert.equal(results.length, 3); assert.equal(results.every(r => r.needsTranslation === false), true);
  assert.equal(f.nativeMessages.some(m => m.type === "autoImageCandidate"), false);
});
test("disconnect immediately disables observer and releases pending local assessment", async t => {
  const f = fixture(); t.after(f.dispose); f.configure(); let reply;
  f.send(f.candidate, result => { reply = result; }); await tick(); await tick();
  f.port.onDisconnect.emit(); await tick();
  assert.equal(reply.needsTranslation, false); assert.equal(reply.reason, "bridge_disconnected");
  assert.equal(f.tabMessages.at(-1).message.type, "imageAutoConfig"); assert.equal(f.tabMessages.at(-1).message.enabled, false);
});
test("accepted same-source assessment is retained after scrolling offscreen", async t => {
  const f = fixture(); t.after(f.dispose); f.configure(); let reply;
  f.send(f.candidate, result => { reply = result; }); await tick(); await tick();
  f.image.clip = { left: 0, top: 0, width: 0, height: 0 };
  f.assess(); await tick();
  assert.equal(reply.needsTranslation, true); assert.equal(reply.reason, "foreign_text");
});

test("local image decision finishing while a tab is inactive is retained without another preview or candidate", async t => {
  const f = fixture(); t.after(f.dispose); f.configure(); let reply;
  f.send(f.candidate, result => { reply = result; }); await tick(); await tick();
  f.tab.active = false; f.image.autoImageAllowed = false; f.image.autoImageAdmitted = true;
  f.assess({ needsTranslation: false, reason: "no_foreign_text" }); await tick();
  assert.equal(reply.needsTranslation, false); assert.equal(reply.reason, "no_foreign_text");
  f.tab.active = true; f.image.autoImageAllowed = true;
  f.chrome.tabs.onActivated.emit({ tabId: 11, windowId: 7 }); await tick(); await tick();
  assert.equal(f.nativeMessages.filter(message => message.type === "autoImageCandidate").length, 1);
  assert.equal(f.tabMessages.filter(entry => entry.message.type === "queryImagePreview").length, 1);
});

test("logical image/page gate closing still rejects an accepted source after asynchronous local assessment", async t => {
  const f = fixture(); t.after(f.dispose); f.configure(); let reply;
  f.send(f.candidate, result => { reply = result; }); await tick(); await tick();
  f.image.autoImageAllowed = false; f.image.autoImageAdmitted = false;
  f.assess(); await tick();
  assert.equal(reply.needsTranslation, false); assert.equal(reply.reason, "image_changed");
});
test("queryImage supports registered image identity without a screen point", async t => {
  const f = fixture(); t.after(f.dispose);
  f.port.onMessage.emit({ type: "queryImage", requestId: "full-image", tabId: 11, browserWindowId: 7,
    imageId: f.image.imageId, documentToken: f.image.documentToken }); await tick(); await tick();
  const query = f.tabMessages.find(m => m.message.type === "queryImage").message;
  assert.equal(query.imageId, "image-1"); assert.equal(query.x, undefined);
  assert.equal(f.nativeMessages.find(m => m.type === "browserImageChunk").data, "QUJDRA==");
  assert.equal(f.nativeMessages.find(m => m.type === "browserImageReply").found, true);
});

test("exact document query returns the requested inactive document without selecting the foreground tab", async t => {
  const f = fixture(); t.after(f.dispose); f.tab.active = false;
  f.port.onMessage.emit({ type: "queryBrowserDocument", requestId: "inactive-document", tabId: 11,
    browserWindowId: 7, documentToken: f.documentState.documentToken }); await tick(); await tick();
  const reply = f.nativeMessages.find(message => message.type === "browserDocumentReply");
  assert.equal(reply.found, true); assert.equal(reply.active, false); assert.equal(reply.tabId, 11);
  assert.equal(reply.browserWindowId, 7); assert.equal(reply.documentToken, f.documentState.documentToken);
  assert.equal(f.tabMessages.filter(entry => entry.message.type === "queryDocumentState").length, 1);
  assert.equal(f.nativeMessages.some(message => message.type === "activeTabReply"), false);
});

test("exact document query rejects a replaced document, moved window and missing tab", async t => {
  const f = fixture(); t.after(f.dispose);
  const request = { type: "queryBrowserDocument", tabId: 11, browserWindowId: 7, documentToken: f.documentState.documentToken };
  for (const [index, changed] of [{ documentToken: "old-document-token" }, { browserWindowId: 99 }, { tabId: 99 }].entries()) {
    f.port.onMessage.emit({ ...request, ...changed, requestId: `invalid-${index}` }); await tick(); await tick();
    const reply = f.nativeMessages.find(message => message.requestId === `invalid-${index}`);
    assert.equal(reply.found, false);
  }
  assert.equal(f.tabMessages.filter(entry => entry.message.type === "queryDocumentState").length, 1);
});

test("exact document query revalidates its window after the content reply await", async t => {
  let moved = false;
  const f = fixture({ beforeReply: message => { if (message.type === "queryDocumentState" && !moved) { moved = true; f.tab.windowId = 8; } } });
  t.after(f.dispose);
  f.port.onMessage.emit({ type: "queryBrowserDocument", requestId: "moved-during-query", tabId: 11,
    browserWindowId: 7, documentToken: f.documentState.documentToken }); await tick(); await tick();
  assert.equal(f.nativeMessages.find(message => message.requestId === "moved-during-query").found, false);
});

test("automatic continuation reads a registered inactive offscreen image without screen visibility", async t => {
  const f = fixture(); t.after(f.dispose); f.tab.active = false;
  f.image.autoImageAllowed = false; f.image.autoImageAdmitted = true; f.image.clip = { left: 0, top: 0, width: 0, height: 0 };
  f.port.onMessage.emit({ type: "queryImage", requestId: "automatic-inactive-image", tabId: 11, browserWindowId: 7,
    imageId: f.image.imageId, documentToken: f.image.documentToken, allowInactive: true }); await tick(); await tick();
  const reply = f.nativeMessages.find(message => message.type === "browserImageReply" && message.requestId === "automatic-inactive-image");
  assert.equal(reply.found, true); assert.equal(reply.autoImageAllowed, false); assert.equal(reply.autoImageAdmitted, true);
  assert.equal(reply.clip.height, 0); assert.equal(f.nativeMessages.filter(message => message.type === "browserImageChunk").length, 1);
});

test("manual image read without an explicit inactive continuation flag refuses an inactive tab before reading pixels", async t => {
  const f = fixture(); t.after(f.dispose); f.tab.active = false;
  for (const [index, value] of [undefined, false, "true"].entries()) {
    f.port.onMessage.emit({ type: "queryImage", requestId: `manual-inactive-${index}`, tabId: 11, browserWindowId: 7,
      imageId: f.image.imageId, documentToken: f.image.documentToken, ...(value !== undefined ? { allowInactive: value } : {}) });
    await tick(); await tick();
    assert.equal(f.nativeMessages.find(message => message.requestId === `manual-inactive-${index}`).found, false);
  }
  assert.equal(f.tabMessages.filter(entry => entry.message.type === "queryImage").length, 0);
  assert.equal(f.nativeMessages.some(message => message.type === "browserImageChunk"), false);
});

test("automatic inactive image read cannot deliver pixels for a stale document, source or window", async t => {
  const f = fixture(); t.after(f.dispose); f.tab.active = false;
  const request = { type: "queryImage", tabId: 11, browserWindowId: 7,
    imageId: f.image.imageId, documentToken: f.image.documentToken, allowInactive: true };
  for (const [index, changed] of [{ documentToken: "old-document-token" }, { imageId: "old-image-source" }, { browserWindowId: 99 }].entries()) {
    f.port.onMessage.emit({ ...request, ...changed, requestId: `stale-image-${index}` }); await tick(); await tick();
    assert.equal(f.nativeMessages.find(message => message.requestId === `stale-image-${index}`).found, false);
  }
  assert.equal(f.nativeMessages.some(message => message.type === "browserImageChunk"), false);
});

test("manual image request switched to inactive during the geometry await is rejected after revalidation", async t => {
  const f = fixture({ beforeReply: message => { if (message.type === "queryImageGeometry") f.tab.active = false; } });
  t.after(f.dispose);
  f.port.onMessage.emit({ type: "queryImage", requestId: "manual-switch-during-read", tabId: 11, browserWindowId: 7,
    imageId: f.image.imageId, documentToken: f.image.documentToken }); await tick(); await tick();
  assert.equal(f.nativeMessages.find(message => message.requestId === "manual-switch-during-read").found, false);
  assert.equal(f.nativeMessages.some(message => message.type === "browserImageChunk"), false);
});
