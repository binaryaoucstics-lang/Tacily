"use strict";
const test = require("node:test"), assert = require("node:assert/strict");
const fs = require("node:fs"), path = require("node:path"), vm = require("node:vm"), crypto = require("node:crypto");
const tick = () => new Promise(resolve => setImmediate(resolve));
test("X background-backed photo supports manual original picking and automatic enlarged preview",async()=>{
  let sourceImage;
  const f=fixture({getStyle:element=>element===sourceImage?{opacity:"0"}:{width:"425px",height:"600px",backgroundImage:'url("https://cdn.example/book.png")',backgroundSize:"contain",backgroundPosition:"50% 50%"}});sourceImage=f.image;
  const host={isConnected:true,parentElement:null,clientWidth:425,clientHeight:600,clientLeft:0,clientTop:0,
    getAttribute:name=>name==="role"?"dialog":null,getBoundingClientRect:()=>({left:20,top:40,width:425,height:600}),
    querySelectorAll:()=>[f.image],closest:selector=>selector.includes("tweetPhoto")?host:null,textContent:""};
  f.document.location={href:"https://x.com/example/status/1/photo/1"};f.image.parentElement=host;f.image.clientWidth=1;f.image.clientHeight=1;f.document.elementFromPoint=()=>host;
  const picked=f.context.ScreenTranslatorManualImages.pick(50,80);assert.ok(picked?.imageId);assert.equal(f.canvases.length,0);
  const original=await f.send({type:"queryImage",x:50*1.25,y:80*1.25});assert.equal(original.found,true);assert.equal(original.imageId,picked.imageId);assert.equal(original.natural.width,2150);
  await f.send({type:"imageAutoConfig",enabled:true,generation:1,requirePageJudgement:true});
  f.pageDecision({enabled:true,assessed:true,allowImages:false,generation:1});f.intersections[0].callback([{target:f.image,isIntersecting:true}]);await tick();
  assert.equal(f.sent.filter(m=>m.type==="autoImageCandidate").length,1);
  const preview=await f.send({type:"queryImagePreview",imageId:picked.imageId,documentToken:f.token});assert.equal(preview.found,true);
  assert.ok(Math.abs(preview.display.width-425)<1);assert.equal(preview.display.height,600);
});
test("manual image picking reads only identity, supports thumbnails when auto is off and checks clipped pixels",async()=>{
  const f=fixture();f.image.clientWidth=160;f.image.clientHeight=220;
  const pick=f.context.ScreenTranslatorManualImages.pick(50,60);assert.ok(pick?.imageId);assert.equal(pick.documentToken,f.token);assert.equal(f.canvases.length,0);
  assert.equal(f.context.ScreenTranslatorManualImages.pick(5,5),null);const original=await f.send({type:"queryImage",imageId:pick.imageId,documentToken:f.token});assert.equal(original.found,true);
  assert.equal(original.natural.width,2150);assert.equal(f.canvases.length,1);f.document.visibilityState="hidden";assert.equal(f.context.ScreenTranslatorManualImages.pick(50,60),null);
});
test("X lightbox is locally assessed even when its Chinese feed blocks page image admission, but thumbnails stay blocked",async()=>{
  const f=fixture();f.document.location={href:"https://x.com/example/status/123/photo/1"};
  f.image.parentElement={parentElement:null,getAttribute:name=>name==="role"?"dialog":null,getBoundingClientRect:()=>({left:0,top:0,width:1280,height:720})};
  await f.send({type:"imageAutoConfig",enabled:true,generation:1,requirePageJudgement:true});
  f.pageDecision({enabled:true,assessed:true,allowImages:false,generation:1});f.intersections[0].callback([{target:f.image,isIntersecting:true}]);await tick();
  const candidate=f.sent.find(m=>m.type==="autoImageCandidate");assert.ok(candidate);assert.equal(f.canvases.length,0);
  const preview=await f.send({type:"queryImagePreview",imageId:candidate.imageId,documentToken:f.token});assert.equal(preview.found,true);
  const geometry=await f.send({type:"queryImageGeometry",imageId:candidate.imageId,documentToken:f.token});assert.equal(geometry.autoImageAdmitted,true);
  f.image.parentElement=null;assert.equal((await f.send({type:"queryImageGeometry",imageId:candidate.imageId,documentToken:f.token})).autoImageAdmitted,false);
  f.image.parentElement={parentElement:null,getAttribute:name=>name==="role"?"dialog":null,getBoundingClientRect:()=>({left:0,top:0,width:1280,height:720})};
  f.image.clientWidth=160;f.image.clientHeight=220;assert.equal((await f.send({type:"queryImagePreview",imageId:candidate.imageId,documentToken:f.token})).found,false);
});
function fixture(options = {}) {
  const listeners = [], sent = [], documentEvents = new Map(), windowEvents = new Map(), canvases = [], intersections = [], resizes = [], mutationObservers = [];
  class Image {
    constructor() { Object.assign(this, { isConnected: true, complete: true, naturalWidth: 2150, naturalHeight: 3036,
      currentSrc: "https://cdn.example/book.png", src: "https://cdn.example/book.png", parentElement: null,
      offsetWidth: 425, offsetHeight: 600, clientWidth: 425, clientHeight: 600, clientLeft: 0, clientTop: 0 }); }
    getBoundingClientRect() { return { left: 20, top: 40, width: this.clientWidth, height: this.clientHeight }; }
  }
  const image = new Image(), windowObject = {}, token = "01234567-89ab-4cde-8123-0123456789ab";
  const document = { visibilityState: "visible", images: [image], elementFromPoint: () => image,
    addEventListener: (name, listener) => documentEvents.set(name, listener),
    createElement: () => {
      const canvas = { width: 0, height: 0, getContext: () => ({ fillRect() {}, drawImage() {} }),
        toDataURL: type => {
          if (canvas.securityError) throw Object.assign(new Error("tainted"), { name: "SecurityError" });
          return `data:${type};base64,QUJDRA==`;
        } };
      canvases.push(canvas); return canvas;
    } };
  const context = vm.createContext({ document, URL, window: windowObject, top: windowObject, innerWidth: 1280, innerHeight: 720,
    devicePixelRatio: 1.25, crypto, HTMLImageElement: Image, ScreenTranslatorDocumentToken: token,
    ScreenTranslatorImageAutoPolicy: require("../image-auto-policy.js"),
    ScreenTranslatorImageGeometry: require("../image-geometry.js"),
    getComputedStyle: element => ({ transform: "none", opacity: "1", visibility: "visible", paddingLeft: "0", paddingRight: "0",
      paddingTop: "0", paddingBottom: "0", objectFit: "fill", objectPosition: "50% 50%",...options.getStyle?.(element) }),
    createImageBitmap: options.createImageBitmap || (async input => ({ width: input.naturalWidth, height: input.naturalHeight, close() {} })),
    setTimeout, clearTimeout, requestIdleCallback: callback => { queueMicrotask(callback); },
    requestAnimationFrame: callback => { queueMicrotask(callback); return 1; }, cancelAnimationFrame() {},
    addEventListener: (name, listener) => windowEvents.set(name, listener),
    IntersectionObserver: class { constructor(callback) { this.callback = callback; intersections.push(this); } observe() {} disconnect() {} },
    ResizeObserver: class { constructor(callback) { this.callback = callback; resizes.push(this); } observe() {} disconnect() {} },
    MutationObserver: class { constructor(callback) { this.callback = callback; mutationObservers.push(this); } observe() {} disconnect() {} },
    chrome: { runtime: { lastError: null, onMessage: { addListener: listener => listeners.push(listener) },
      sendMessage: (message, callback) => {
        sent.push(message); if (message.type === "queryImageAutoConfig") callback(options.queryConfig?.() || { enabled: false, generation: 0 });
        else if (options.sendResult) options.sendResult(message, callback);
        else callback({ needsTranslation: false, reason: "test_local_negative" });
      } } } });
  vm.runInContext(fs.readFileSync(path.join(__dirname, "..", "image-content.js"), "utf8"), context);
  function send(message) {
    return new Promise(resolve => { for (const listener of listeners) listener(message, {}, resolve); });
  }
  function pageDecision(value, eventDetail) {
    context.ScreenTranslatorPageAutoDecision = value;
    documentEvents.get("ScreenTranslatorPageAssessmentChanged")?.({ detail: eventDetail });
  }
  return { image, token, sent, canvases, intersections, resizes, mutationObservers, documentEvents, windowEvents, send, document, pageDecision, context };
}

test("thumbnail stays pixel-free until the same source is enlarged, then completed decisions survive resizing", async () => {
  const f = fixture(); f.image.clientWidth = 160; f.image.clientHeight = 220;
  await f.send({ type: "imageAutoConfig", enabled: true, generation: 1 });
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 0); assert.equal(f.canvases.length, 0);
  const refused = await f.send({ type: "queryImagePreview", x: 50, y: 80 });
  assert.equal(refused.errorCode, "automatic_image_gate_closed"); assert.equal(f.canvases.length, 0);
  const manual = await f.send({ type: "queryImage", x: 50, y: 80 }); assert.equal(manual.found, true);
  f.image.clientWidth = 425; f.image.clientHeight = 600; f.resizes[0].callback([]); await tick();
  const candidates = f.sent.filter(m => m.type === "autoImageCandidate"); assert.equal(candidates.length, 1);
  assert.equal(candidates[0].imageId, manual.imageId);
  f.image.clientWidth = 160; f.image.clientHeight = 220; f.resizes[0].callback([]); await tick();
  f.image.clientWidth = 425; f.image.clientHeight = 600; f.documentEvents.get("click")(); await tick();
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 1);
});

test("lightbox ancestor reflow can admit an image without a load or new intersection entry", async () => {
  const f = fixture(); f.image.clientWidth = 160; f.image.clientHeight = 220;
  await f.send({ type: "imageAutoConfig", enabled: true, generation: 1 });
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  f.image.clientWidth = 425; f.image.clientHeight = 600;
  f.mutationObservers[0].callback([{ type: "attributes", attributeName: "class", target: { closest: () => null } }]); await tick();
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 1);
});

test("collapse during a preview decode closes the bitmap and admits no encoded pixels", async () => {
  let resolveBitmap, closed = 0;
  const f = fixture({ createImageBitmap: () => new Promise(resolve => { resolveBitmap = resolve; }) });
  await f.send({ type: "imageAutoConfig", enabled: true, generation: 1 });
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  const candidate = f.sent.find(m => m.type === "autoImageCandidate");
  const response = f.send({ type: "queryImagePreview", imageId: candidate.imageId, documentToken: f.token });
  f.image.clientWidth = 160; f.image.clientHeight = 220;
  resolveBitmap({ width: 2150, height: 3036, close: () => closed++ });
  assert.equal((await response).errorCode, "automatic_image_gate_closed"); assert.equal(closed, 1); assert.equal(f.canvases.length, 0);
  const geometry = await f.send({ type: "queryImageGeometry", imageId: candidate.imageId, documentToken: f.token });
  assert.equal(geometry.autoImageAllowed, false); assert.equal(geometry.autoImageAdmitted, true);
});
test("visible image observer starts only after desktop opt-in and keeps the scroll document identity", async () => {
  const f = fixture(); f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 0);
  await f.send({ type: "imageAutoConfig", enabled: true, generation: 1 }); await tick();
  const candidates = f.sent.filter(m => m.type === "autoImageCandidate");
  assert.equal(candidates.length, 1); assert.equal(candidates[0].documentToken, f.token);
  assert.equal(candidates[0].url, undefined); assert.equal(candidates[0].previewBase64, undefined);
  // Worker creates the actual preview; page observer creates no pixel canvas by itself.
  assert.equal(f.canvases.length, 0);
});
test("auto candidate identity can read bounded preview and full image without a coordinate", async () => {
  const f = fixture(); await f.send({ type: "imageAutoConfig", enabled: true, generation: 1 });
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  const candidate = f.sent.find(m => m.type === "autoImageCandidate");
  const preview = await f.send({ type: "queryImagePreview", imageId: candidate.imageId, documentToken: f.token });
  assert.equal(preview.found, true); assert.equal(preview.previewBase64, "QUJDRA==");
  assert.equal(Math.max(f.canvases[0].width, f.canvases[0].height), 1280);
  const full = await f.send({ type: "queryImage", imageId: candidate.imageId, documentToken: f.token });
  assert.equal(full.found, true); assert.equal(full.base64, "QUJDRA==");
  assert.equal(full.imageId, candidate.imageId); assert.equal(f.canvases[1].width, 2150); assert.equal(f.canvases[1].height, 3036);
});
test("source change rejects old identity and a new source is independently assessed", async () => {
  const f = fixture(); await f.send({ type: "imageAutoConfig", enabled: true, generation: 1 });
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  const old = f.sent.find(m => m.type === "autoImageCandidate");
  f.image.currentSrc = "https://cdn.example/new-page.png";
  const geometry = await f.send({ type: "queryImageGeometry", imageId: old.imageId, documentToken: f.token });
  assert.equal(geometry.found, false); assert.equal(geometry.errorCode, "image_changed");
  f.documentEvents.get("load")({ target: f.image }); await tick();
  const candidates = f.sent.filter(m => m.type === "autoImageCandidate");
  assert.equal(candidates.length, 2); assert.notEqual(candidates[1].imageId, old.imageId);
});
test("scroll reentry does not reassess completed same source, disabling stops subsequent loads", async () => {
  const f = fixture(); await f.send({ type: "imageAutoConfig", enabled: true, generation: 1 });
  const enter = flag => f.intersections[0].callback([{ target: f.image, isIntersecting: flag }]);
  enter(true); await tick(); enter(false); enter(true); f.documentEvents.get("scroll")(); await tick();
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 1);
  await f.send({ type: "imageAutoConfig", enabled: false, generation: 2 });
  f.image.currentSrc = "https://cdn.example/new-page.png"; f.documentEvents.get("load")({ target: f.image }); await tick();
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 1);
});
test("desktop retry is bound to current source/document/generation and does not spin offscreen", async () => {
  const f = fixture(); await f.send({ type: "imageAutoConfig", enabled: true, generation: 2 });
  const enter = flag => f.intersections[0].callback([{ target: f.image, isIntersecting: flag }]);
  enter(true); await tick(); const candidate = f.sent.find(m => m.type === "autoImageCandidate");
  const retry = { type: "autoImageRetry", imageId: candidate.imageId, documentToken: f.token, generation: 2 };
  assert.equal((await f.send({ ...retry, generation: 1 })).found, false);
  enter(false); assert.equal((await f.send(retry)).found, true); await tick();
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 1);
  enter(true); await tick(); assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 2);
});
test("whole-page mixed-language rejection permits neither an image candidate nor preview pixels", async () => {
  let reads = 0;
  const f = fixture({ createImageBitmap: async () => { reads++; throw new Error("Should never decode"); } });
  f.pageDecision({ enabled: true, assessed: true, allowImages: false, generation: 1, revision: 1 });
  await f.send({ type: "imageAutoConfig", enabled: true, generation: 1, requirePageJudgement: true });
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  const preview = await f.send({ type: "queryImagePreview", x: 100, y: 100 });
  assert.equal(preview.found, false); assert.equal(preview.errorCode, "automatic_image_gate_closed");
  assert.equal(reads, 0); assert.equal(f.canvases.length, 0);
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 0);
});
test("Chinese-UI/no-prose page decision can permit visible image assessment", async () => {
  const f = fixture();
  f.pageDecision({ enabled: true, assessed: true, allowImages: true, generation: 2, revision: 1 });
  await f.send({ type: "imageAutoConfig", enabled: true, generation: 2, requirePageJudgement: true });
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  const candidate = f.sent.find(m => m.type === "autoImageCandidate"); assert.ok(candidate);
  const preview = await f.send({ type: "queryImagePreview", imageId: candidate.imageId, documentToken: f.token });
  assert.equal(preview.found, true); assert.equal(preview.previewBase64, "QUJDRA==");
});
test("old page generation and forged event.detail cannot authorize image translation", async () => {
  const f = fixture();
  f.pageDecision({ enabled: true, assessed: true, allowImages: true, generation: 1, revision: 1 });
  await f.send({ type: "imageAutoConfig", enabled: true, generation: 2, requirePageJudgement: true });
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  f.documentEvents.get("ScreenTranslatorPageAssessmentChanged")({ detail: { enabled: true, assessed: true, allowImages: true, generation: 2, revision: 99 } });
  await tick(); assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 0);
  f.pageDecision({ enabled: true, assessed: true, allowImages: true, generation: 2, revision: 2 });
  await tick(); assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 1);
});
test("page gate turning negative before deferred candidate send prevents a late image candidate", async () => {
  const f = fixture();
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]);
  f.pageDecision({ enabled: true, assessed: true, allowImages: true, generation: 3, revision: 1 });
  const config = f.send({ type: "imageAutoConfig", enabled: true, generation: 3, requirePageJudgement: true });
  f.pageDecision({ enabled: true, assessed: true, allowImages: false, generation: 3, revision: 2 });
  await config; await tick();
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 0); assert.equal(f.canvases.length, 0);
});
test("page gate closing during async bitmap creation refuses encoding and reports false on geometry", async () => {
  let resolveBitmap, closed = 0;
  const f = fixture({ createImageBitmap: () => new Promise(resolve => { resolveBitmap = resolve; }) });
  f.pageDecision({ enabled: true, assessed: true, allowImages: true, generation: 3, revision: 1 });
  await f.send({ type: "imageAutoConfig", enabled: true, generation: 3, requirePageJudgement: true });
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  const candidate = f.sent.find(m => m.type === "autoImageCandidate");
  const preview = f.send({ type: "queryImagePreview", imageId: candidate.imageId, documentToken: f.token });
  f.pageDecision({ enabled: true, assessed: true, allowImages: false, generation: 3, revision: 2 });
  resolveBitmap({ width: 2150, height: 3036, close: () => closed++ });
  const response = await preview;
  assert.equal(response.found, false); assert.equal(response.errorCode, "automatic_image_gate_closed");
  assert.equal(closed, 1); assert.equal(f.canvases.length, 0);
  const geometry = await f.send({ type: "queryImageGeometry", imageId: candidate.imageId, documentToken: f.token });
  assert.equal(geometry.found, true); assert.equal(geometry.autoImageAllowed, false);
});
test("turning off full-page master restores independently enabled legacy image mode", async () => {
  const f = fixture();
  f.pageDecision({ enabled: false, assessed: false, allowImages: false, generation: 4, revision: 2 });
  await f.send({ type: "imageAutoConfig", enabled: true, generation: 4, requirePageJudgement: true });
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 0);
  await f.send({ type: "imageAutoConfig", enabled: true, generation: 4, requirePageJudgement: false }); await tick();
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 1);
});

test("switching tabs preserves image assessment and separates logical admission from visibility", async () => {
  let finish;
  const f = fixture({ sendResult: (_message, callback) => { finish = callback; } });
  f.pageDecision({ enabled: true, assessed: true, allowImages: true, generation: 2, revision: 1 });
  await f.send({ type: "imageAutoConfig", enabled: true, active: true, generation: 2, requirePageJudgement: true });
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  const candidate = f.sent.find(m => m.type === "autoImageCandidate"); assert.ok(candidate);
  f.document.visibilityState = "hidden";
  await f.send({ type: "imageAutoConfig", enabled: true, active: false, generation: 2, requirePageJudgement: true });
  const hidden = await f.send({ type: "queryImageGeometry", imageId: candidate.imageId, documentToken: f.token });
  assert.equal(hidden.found, true); assert.equal(hidden.autoImageAllowed, false); assert.equal(hidden.autoImageAdmitted, true);
  finish({ needsTranslation: false, reason: "no_foreign_text" }); await tick();
  f.document.visibilityState = "visible";
  await f.send({ type: "imageAutoConfig", enabled: true, active: true, generation: 2, requirePageJudgement: true });
  f.documentEvents.get("scroll")(); await tick();
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 1);
  assert.equal(f.canvases.length, 0);
});

test("unchanged positive page gate revisions do not reset a pending image assessment", async () => {
  let finish;
  const f = fixture({ sendResult: (_message, callback) => { finish = callback; } });
  f.pageDecision({ enabled: true, assessed: true, allowImages: true, generation: 2, revision: 1 });
  await f.send({ type: "imageAutoConfig", enabled: true, generation: 2, requirePageJudgement: true });
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  f.pageDecision({ enabled: true, assessed: true, allowImages: true, generation: 2, revision: 2 }); await tick();
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 1);
  finish({ needsTranslation: true, reason: "foreign_text" }); await tick();
  f.pageDecision({ enabled: true, assessed: true, allowImages: true, generation: 2, revision: 3 }); await tick();
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 1);
});

test("BFCache return retains completed image decisions for the same document and reassesses changed source", async () => {
  const config = { enabled: true, active: true, generation: 2 };
  const f = fixture({ queryConfig: () => config });
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  const before = f.sent.find(m => m.type === "autoImageCandidate"); assert.ok(before);
  f.windowEvents.get("pagehide")({ persisted: true });
  f.windowEvents.get("pageshow")({ persisted: true }); await tick();
  f.intersections[0].callback([{ target: f.image, isIntersecting: true }]); await tick();
  assert.equal(f.sent.filter(m => m.type === "autoImageCandidate").length, 1);
  f.image.currentSrc = "https://cdn.example/new-book.png";
  f.documentEvents.get("load")({ target: f.image }); await tick();
  const candidates = f.sent.filter(m => m.type === "autoImageCandidate");
  assert.equal(candidates.length, 2); assert.equal(candidates[1].documentToken, before.documentToken);
  assert.notEqual(candidates[1].imageId, before.imageId);
});

test("2x source pixels stay in local overlay identity and preview does not discard a completed full read", async () => {
  const f = fixture({ createImageBitmap: async input => ({ width: input.naturalWidth * 2, height: input.naturalHeight * 2, close() {} }) });
  f.image.naturalWidth = 600; f.image.naturalHeight = 800;
  const original = await f.send({ type: "queryImage", x: 50, y: 80 });
  assert.equal(original.found, true); assert.equal(original.natural.width, 600); assert.equal(f.canvases[0].width, 1200);
  assert.equal(original.pixelSize, undefined);
  let saved;
  f.context.ScreenTranslatorImageOverlay = { receive(request, token, lookup) { saved = lookup(request.imageId); return { found: true }; } };
  await f.send({ type: "imageOverlayBegin", imageId: original.imageId, documentToken: f.token });
  assert.equal(saved.pixelSize.width, 1200); assert.equal(saved.pixelSize.height, 1600); assert.equal(saved.naturalSize.width, 600);
  await f.send({ type: "imageAutoConfig", enabled: true, generation: 2 });
  await f.send({ type: "queryImagePreview", imageId: original.imageId, documentToken: f.token });
  await f.send({ type: "imageOverlayBegin", imageId: original.imageId, documentToken: f.token });
  assert.equal(saved.pixelSize.width, 1200); assert.equal(saved.naturalSize.width, 600);
  assert.equal(f.sent.some(packet => Object.hasOwn(packet, "pixelSize")), false);
});

test("same-URL natural dimension changes during bitmap decode reject the stale read and old saved identity", async () => {
  let resolveBitmap;
  const f = fixture({ createImageBitmap: () => new Promise(resolve => { resolveBitmap = resolve; }) });
  const read = f.send({ type: "queryImage", x: 50, y: 80 });
  f.image.naturalWidth = 2000; resolveBitmap({ width: 2150, height: 3036, close() {} });
  const reply = await read; assert.equal(reply.found, false); assert.equal(reply.errorCode, "image_changed");
});
