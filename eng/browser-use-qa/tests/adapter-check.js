"use strict";
// Synthetic DOM/transport interface fixture. No browser, URL access or model.
const assert = require("node:assert/strict");
const fs = require("node:fs"), vm = require("node:vm"), path = require("node:path");
const listeners = [], sent = [], received = [];
const body = { nodeType: 1, tagName: "BODY", childNodes: [], parentNode: null, parentElement: null,
  hasAttribute: () => false, getAttribute: () => null };
const document = { body, nodeType: 9, title: "Synthetic interface fixture", childNodes: [body],
  createRange: () => {
    let node;
    return { selectNodeContents(value) { node = value; }, getClientRects: () =>
      node.rects || (["SCRIPT", "STYLE"].includes(node.parentElement?.tagName) ? [] : [{ x: 0, y: 10, width: 100, height: 20 }]) };
  },
  addEventListener: type => listeners.push(type), removeEventListener() {} };
body.parentNode = document;
function text(tag, value) {
  const element = { nodeType: 1, tagName: tag, parentNode: body, parentElement: body, childNodes: [],
    hasAttribute: () => false, getAttribute: name => name === "role" && tag === "BUTTON" ? "button" : null,
    getRootNode: () => document };
  const node = { nodeType: 3, nodeValue: value, parentNode: element, parentElement: element, isConnected: true };
  element.childNodes.push(node); body.childNodes.push(element); return node;
}
const code = text("CODE", "const x = 42"), date = text("TIME", "2026-10-11"), button = text("BUTTON", "Submit");
const prose = text("P", "Synthetic foreign paragraph.");
const script = text("SCRIPT", "window.synthetic = true;"), style = text("STYLE", ".synthetic { color: red; }");
const hidden = text("P", "Initially hidden source."), neverVisible = text("P", "Never visible prediction source.");
hidden.parentElement.display = neverVisible.parentElement.display = "none";
const record = { node: prose, source: prose.nodeValue, lastWritten: prose.nodeValue, groupElement: prose.parentElement,
  pieces: [{ source: prose.nodeValue, complete: false, failed: false }] };
const state = { records: new Map([[prose, record]]), blocks: new Map(), jobs: 0, pending: 0, scanning: false,
  assessed: null, originalView: false, observing: true, active: true };
let environment, mutations = 0, layout = 0, routes = 0, observerCallback, extractProbe;
const mockSession = { inspect: () => state, configure: () => true, receive: value => { received.push(value); return true; },
  control: action => { state.originalView = action === "view-original"; }, mutated: () => { mutations++; },
  layoutChanged: () => { layout++; }, navigationChanged: () => { routes++; }, visibilityChanged() {},
  selectionChanged() {}, suspend() {}, resumeDocument() {}, discoverOpenRoots() {}, dispose() {} };
const context = {
  document, location: { href: "https://synthetic.invalid/" }, innerWidth: 800, innerHeight: 600, scrollX: 0, scrollY: 0,
  getComputedStyle: element => ({ display: element.display || "block", visibility: "visible", opacity: "1", ...element.css }),
  ScreenTranslatorMotion: { dispose() {} }, ScreenTranslatorMotionFactory: {},
  ScreenTranslatorPageAutoContent: { randomIdentifier: () => "opaque", createRenderedTextTester: () => () => true,
    extractPageSnapshot: value => extractProbe(value),
    yieldToBrowser: async () => {}, createPageAutoSession: value => { environment = value; return mockSession; },
    createOpenRootObserver: value => { observerCallback = value.onMutations; return { configure() {}, found() {}, replace() {}, dispose() {} }; } },
  MutationObserver: function () {}, setInterval: () => 1, clearInterval() {},
  addEventListener: type => listeners.push(type), removeEventListener() {},
  navigation: { addEventListener: type => listeners.push(type), removeEventListener() {} },
  Date, console
};
vm.createContext(context);
vm.runInContext(fs.readFileSync(path.join(__dirname, "../page-adapter.js"), "utf8"), context);
const qa = context.__websiteQa;
let sample = qa.sample("before");
assert.deepEqual(Array.from(sample.nodes, row => row.tag), ["CODE", "TIME", "BUTTON", "P"]);
assert.deepEqual(Array.from(sample.sourceNodes, row => row.tag), ["CODE", "TIME", "BUTTON", "P"]);
function assertCompleteSources(value) {
  for (const row of value.sourceNodes) {
    for (const key of ["id", "sourceText", "locator", "firstSeen", "tag", "role", "rects",
        "rendered", "ancestorsVisible", "inViewport", "displayedText", "phase"])
      assert.ok(Object.hasOwn(row, key), `${row.id} missing ${key}`);
    assert.ok(row.rects.some(rect => rect.width > 0 && rect.height > 0), `${row.id} lacks observed geometry`);
    assert.ok(!["SCRIPT", "STYLE"].includes(row.tag));
  }
}
assertCompleteSources(sample);
assert.equal(sample.nodes[1].sourceText, "2026-10-11");
assert.equal(sample.nodes[2].role, "button");
assert.equal(sample.nodes[0].rects[0].documentY, 10);
assert.equal(sample.nodes[0].inViewport, true);
const sourceId = sample.nodes[3].id;
const firstSeen = sample.nodes[3].firstSeen;
// Mock host output is test-only, never a real case.
prose.nodeValue = record.lastWritten = "Synthetic translated display";
record.pieces[0].complete = true;
state.blocks.set("block-1", { id: "block-1", record, piece: record.pieces[0], text: record.source, groupId: "g1" });
sample = qa.sample("after");
assert.equal(sample.nodes[3].id, sourceId); assert.equal(sample.nodes[3].sourceText, record.source);
assert.equal(sample.predictions[0].completed, true); assert.equal(sample.predictions[0].sourceId, sourceId);
assert.equal(sample.predictions[0].sourceEvidence, "matched"); assert.equal(sample.predictions[0].auditIssue, null);
state.blocks.get("block-1").atomicRecords = [record, { node: button, source: button.nodeValue }];
sample = qa.sample("atomic-members");
assert.deepEqual(Array.from(sample.predictions[0].sourceIds), [sourceId, sample.nodes[2].id]);
assert.equal(sample.productionEligibility.find(row => row.sourceId === sample.nodes[2].id).eligible, true);
// Production atomic records may include empty separators. They must not allocate
// source membership that the independent visible collector can never observe.
const blank = text("P", " \t\n\u00a0");
const empty = text("P", "");
const atomic = state.blocks.get("block-1");
atomic.atomicRecords.push({ node: blank, source: blank.nodeValue }, { node: empty, source: "" });
sample = qa.sample("blank-atomic-members");
assert.deepEqual(Array.from(sample.predictions[0].sourceIds), [sourceId, sample.nodes[2].id]);
assert.equal(sample.predictions[0].sourceEvidence, "matched");
assert.ok(sample.sourceNodes.every(row => String(row.sourceText).trim()));
// A nonblank source remains a member even when hidden, detached, or currently
// displayed as blank. Membership cannot manufacture independent visibility.
atomic.atomicRecords.push({ node: neverVisible, source: neverVisible.nodeValue });
const retainedSource = neverVisible.nodeValue;
neverVisible.nodeValue = "";
sample = qa.sample("nonblank-unmatched-atomic-member");
assert.equal(sample.predictions[0].sourceIds.length, 3);
assert.equal(sample.predictions[0].sourceEvidence, "unmatched");
assert.equal(sample.predictions[0].auditIssue, "source_not_independently_visible");
assert.ok(!sample.sourceNodes.some(row => row.id === sample.predictions[0].sourceIds[2]));
neverVisible.nodeValue = retainedSource;
delete state.blocks.get("block-1").atomicRecords;
assert.equal(sample.nodes[3].firstSeen, firstSeen);
assert.equal(sample.productionEligibility.find(row => row.sourceId === sample.nodes[0].id).eligible, false);
assert.equal(sample.productionEligibility.find(row => row.sourceId === sourceId).eligible, true);
const rangeFactory = document.createRange;
prose.nodeValue = record.lastWritten = "";
document.createRange = () => ({ selectNodeContents() {}, getClientRects: () => [] });
sample = qa.sample("blank");
assert.equal(sample.events.filter(x => x.type === "retraction").length, 1);
assert.equal(sample.nodes[0].id, sourceId);
assertCompleteSources(sample);
assert.equal(sample.nodes[0].rendered, false);
assert.equal(sample.nodes[0].rects[0].documentY, 10);
// Zero-area ranges also reuse the last observed geometry of the disappearing translation.
prose.nodeValue = record.lastWritten = "Synthetic translated display";
document.createRange = rangeFactory; qa.sample("visible-again");
prose.nodeValue = record.lastWritten = "";
document.createRange = () => ({ selectNodeContents() {}, getClientRects: () => [{ x: 0, y: 0, width: 0, height: 0 }] });
sample = qa.sample("blank-zero-area");
assertCompleteSources(sample);
assert.equal(sample.nodes[0].rects[0].width, 100);
assert.equal(sample.events.filter(x => x.type === "retraction").length, 2);
document.createRange = rangeFactory;
prose.nodeValue = record.lastWritten = "Synthetic translated display"; qa.sample("again");
prose.nodeValue = record.lastWritten = record.source;
sample = qa.sample("stable");
assert.equal(sample.events.filter(x => x.type === "retraction").length, 3);
prose.nodeValue = record.lastWritten = "Synthetic translated display"; qa.sample("again");
qa.control("view-original"); prose.nodeValue = record.lastWritten = record.source; qa.sample("intentional-original");
assert.equal(qa.sample("check").events.filter(x => x.type === "retraction").length, 3);
qa.control("view-translation"); prose.nodeValue = record.lastWritten = "Synthetic translated display"; qa.sample("again");
record.source = "Changed authored source."; prose.nodeValue = record.lastWritten = record.source;
sample = qa.sample("source-change"); assert.notEqual(sample.nodes[3].id, sourceId);
assert.equal(sample.events.filter(x => x.type === "retraction").length, 3);
// Prediction identities cannot create source evidence, even for a detached node.
function predict(id, node) {
  const record = { node, source: node.nodeValue, lastWritten: node.nodeValue };
  state.blocks.set(id, { id, record, piece: { complete: true }, text: record.source });
}
const detached = text("P", "Detached before any observation.");
body.childNodes.splice(body.childNodes.indexOf(detached.parentElement), 1);
detached.isConnected = false;
predict("hidden-block", hidden); predict("never-visible-block", neverVisible); predict("detached-block", detached);
predict("script-block", script); predict("style-block", style);
sample = qa.sample("hidden-predictions");
const hiddenPredictionId = sample.predictions.find(row => row.id === "hidden-block").sourceId;
for (const row of sample.predictions.slice(1)) {
  assert.equal(row.sourceEvidence, "unmatched");
  assert.equal(row.auditIssue, "source_not_independently_visible");
  assert.ok(!sample.sourceNodes.some(source => source.id === row.sourceId));
}
assert.equal(sample.sourceNodes.length, 5); // Four original rows plus changed authored source.
assertCompleteSources(sample);
// A later independently rendered node gets its full row with the already allocated stable ID.
hidden.parentElement.display = "block";
sample = qa.sample("later-visible");
const visibleHistory = sample.sourceNodes.find(row => row.id === hiddenPredictionId);
assert.equal(visibleHistory.sourceText, hidden.nodeValue);
assert.equal(visibleHistory.phase, "later-visible"); assert.equal(visibleHistory.rendered, true);
assert.equal(sample.predictions.find(row => row.id === "hidden-block").sourceEvidence, "matched");
assertCompleteSources(sample);
hidden.parentElement.display = "none";
sample = qa.sample("later-hidden");
assert.ok(!sample.nodes.some(row => row.id === hiddenPredictionId));
visibleHistory.currentlyRendered = false;
assert.deepEqual(sample.sourceNodes.find(row => row.id === hiddenPredictionId), visibleHistory);
body.childNodes.splice(body.childNodes.indexOf(hidden.parentElement), 1); hidden.isConnected = false;
sample = qa.sample("later-detached");
assert.deepEqual(sample.sourceNodes.find(row => row.id === hiddenPredictionId), visibleHistory);
assert.equal(sample.predictions.find(row => row.id === "hidden-block").sourceEvidence, "matched");
assertCompleteSources(sample);
// Previously visible source versions and blank-translation geometry remain in history.
assert.equal(sample.sourceNodes.find(row => row.id === sourceId).sourceText, "Synthetic foreign paragraph.");
assert.equal(sample.sourceNodes.length, 6);
// An unchanged proper-name prefix completing is not a translation withdrawal.
const identityName = text("P", "FOIA");
const identityRecord = { node: identityName, source: "FOIA", lastWritten: "FOI", groupElement: identityName.parentElement };
state.records.set(identityName, identityRecord);
identityName.nodeValue = "FOI"; qa.sample("identity-prefix");
identityName.nodeValue = identityRecord.lastWritten = "FOIA";
const identityComplete = qa.sample("identity-complete");
assert.equal(identityComplete.events.filter(x => x.type === "retraction").length, 3);
// A genuine differing translation returning to source still counts.
identityName.nodeValue = identityRecord.lastWritten = "信息自由法"; qa.sample("different-translation");
identityName.nodeValue = identityRecord.lastWritten = "FOIA";
assert.equal(qa.sample("real-withdrawal").events.filter(x => x.type === "retraction").length, 4);
// Independent geometry fixtures. Production eligibility is deliberately wrong
// for clipped nodes; neither that tester nor predictions determine visibility.
const rect = (x, y, width, height) => ({ x, y, width, height });
function box(element, bounds, css) {
  element.getBoundingClientRect = () => bounds;
  element.css = css;
  return element;
}
function wrap(node, bounds, css) {
  const inner = node.parentElement, outer = { nodeType: 1, tagName: "DIV", childNodes: [inner],
    parentElement: inner.parentElement, parentNode: inner.parentNode, hasAttribute: () => false, getAttribute: () => null };
  const siblings = outer.parentNode.childNodes;
  siblings[siblings.indexOf(inner)] = outer;
  inner.parentElement = inner.parentNode = outer;
  return box(outer, bounds, css);
}
const find = (value, node) => value.nodes.find(row => row.sourceText === (state.records.get(node)?.source || node.nodeValue));
const offslide = text("P", "Clipped horizontal carousel slide.");
offslide.rects = [rect(-2439, 10, 200, 20)];
const carousel = wrap(offslide, rect(2060.5, 0, 500, 100), { overflowX: "hidden", overflowY: "visible" });
predict("offslide", offslide);
sample = qa.sample("carousel-clipped");
assert.equal(find(sample, offslide), undefined);
assert.equal(sample.predictions.find(p => p.id === "offslide").sourceEvidence, "unmatched");
assert.ok(!sample.sourceNodes.some(row => row.sourceText === offslide.nodeValue));
offslide.rects = [rect(2070, 10, 200, 20)];
sample = qa.sample("carousel-reveal");
const slideRow = find(sample, offslide);
assert.ok(slideRow.rendered); assert.equal(slideRow.inViewport, false);
offslide.rects = [rect(-2439, 10, 200, 20)];
sample = qa.sample("carousel-retire");
assert.equal(find(sample, offslide), undefined);
assert.equal(sample.sourceNodes.find(row => row.id === slideRow.id).currentlyRendered, false);
assert.equal(sample.sourceNodes.find(row => row.id === slideRow.id).phase, "carousel-reveal");
assert.equal(sample.predictions.find(p => p.id === "offslide").sourceEvidence, "matched");

const partial = text("P", "Partly rendered multiple text ranges.");
partial.rects = [rect(-20, -10, 80, 30), rect(130, 5, 50, 20), rect(90, 5, 40, 20)];
const xClip = wrap(partial, rect(0, 500, 100, 100), { overflowX: "hidden", overflowY: "visible" });
const yClip = wrap(partial.parentElement.childNodes[0], rect(-500, 0, 1000, 15), { overflowX: "visible", overflowY: "clip" });
sample = qa.sample("nested-axis-partial");
assert.deepEqual(JSON.parse(JSON.stringify(find(sample, partial).rects)), [
  { ...rect(0, 0, 60, 15), documentX: 0, documentY: 0 },
  { ...rect(90, 5, 10, 10), documentX: 90, documentY: 5 }]);
// Border and scrollbar area do not enlarge the real overflow aperture.
Object.assign(xClip, { clientLeft: 5, clientTop: 5, clientWidth: 80, clientHeight: 80, offsetWidth: 100, offsetHeight: 100 });
sample = qa.sample("client-aperture");
assert.equal(find(sample, partial).rects.length, 1);
assert.equal(find(sample, partial).rects[0].x, 5);
assert.equal(find(sample, partial).rects[0].width, 55);
yClip.css.overflowY = "hidden";
partial.rects = [rect(10, 50, 20, 20)];
assert.equal(find(qa.sample("nested-retire"), partial), undefined);

const below = text("P", "Loaded below-fold prose must remain source evidence.");
below.rects = [rect(10, 1800, 300, 20)];
document.documentElement = box({ nodeType: 1, tagName: "HTML", parentNode: document }, rect(0, 0, 800, 600),
  { overflowX: "hidden", overflowY: "auto" });
document.scrollingElement = document.documentElement;
body.parentElement = document.documentElement;
sample = qa.sample("below-fold");
assert.equal(find(sample, below).rendered, true); assert.equal(find(sample, below).inViewport, false);

const legacyLabel = text("SPAN", "Accessibility text with legacy clipping.");
legacyLabel.rects = [rect(0, 0, 180, 20)];
box(legacyLabel.parentElement, rect(0, 0, 1, 1), { position: "absolute", clip: "rect(0px, 0px, 0px, 0px)" });
const insetLabel = text("SPAN", "Accessibility text with inset clipping.");
insetLabel.rects = [rect(0, 0, 180, 20)];
box(insetLabel.parentElement, rect(0, 0, 1, 1), { clipPath: "inset(50%)" });
const tinyLabel = text("SPAN", "Accessibility text through a one pixel aperture.");
tinyLabel.rects = [rect(0, 0, 180, 20)];
wrap(tinyLabel, rect(0, 0, 1, 1), { overflow: "hidden" });
const ariaVisible = text("P", "Visibly rendered despite hidden and aria-hidden attributes.");
ariaVisible.parentElement.hidden = true;
ariaVisible.parentElement.getAttribute = name => name === "aria-hidden" ? "true" : null;
sample = qa.sample("accessibility-clipping");
for (const node of [legacyLabel, insetLabel, tinyLabel]) assert.equal(find(sample, node), undefined);
assert.ok(find(sample, ariaVisible).rendered);
legacyLabel.parentElement.css.clip = "auto";
assert.ok(find(qa.sample("label-reveal"), legacyLabel));
legacyLabel.parentElement.css.clip = "rect(0px 0px 0px 0px)";
assert.equal(find(qa.sample("label-retire"), legacyLabel), undefined);
const zeroOverflow = text("SPAN", "A nonrendering overflow aperture.");
wrap(zeroOverflow, rect(0, 0, 0, 100), { overflow: "hidden" });
assert.equal(find(qa.sample("zero-overflow"), zeroOverflow), undefined);
const corner = text("P", "Even a small real corner of a partial line is observed.");
corner.rects = [rect(-99, -19, 100, 20)];
wrap(corner, rect(0, 0, 200, 100), { overflow: "hidden" });
assert.equal(find(qa.sample("partial-corner"), corner).rects[0].width, 1);
assert.equal(find(qa.sample("partial-corner"), corner).rects[0].height, 1);
const cssReveal = text("P", "A descendant overrides inherited visibility.");
const invisibleAncestor = wrap(cssReveal, rect(0, 0, 300, 100), { visibility: "hidden" });
assert.ok(find(qa.sample("visibility-overridden"), cssReveal));
cssReveal.parentElement.css = { visibility: "hidden" };
assert.equal(find(qa.sample("visibility-hidden"), cssReveal), undefined);
cssReveal.parentElement.css = { visibility: "visible" };
invisibleAncestor.css.opacity = "0.0";
assert.equal(find(qa.sample("opacity-zero"), cssReveal), undefined);
invisibleAncestor.css.opacity = "1";
invisibleAncestor.css.contentVisibility = "hidden";
assert.equal(find(qa.sample("content-visibility-hidden"), cssReveal), undefined);
const noOverflowBox = text("P", "Display contents has no overflow box.");
wrap(noOverflowBox, rect(0, 0, 0, 0), { display: "contents", overflow: "hidden" });
assert.ok(find(qa.sample("display-contents"), noOverflowBox));

// A slot's composed ancestry, including its shadow host, supplies clipping.
const slotted = text("P", "Assigned source in the composed slot tree.");
slotted.rects = [rect(-10, 40, 60, 20)];
const light = box(slotted.parentElement, rect(2000, 2000, 1, 1), { overflow: "hidden" });
const host = { nodeType: 1, tagName: "SECTION", childNodes: [light], parentNode: body, parentElement: body,
  hasAttribute: () => false, getAttribute: () => null };
const shadow = { nodeType: 11, host, childNodes: [] };
host.shadowRoot = shadow;
const slot = box({ nodeType: 1, tagName: "SLOT", childNodes: [], parentNode: shadow,
  assignedNodes: () => [slotted], hasAttribute: () => false, getAttribute: () => null },
rect(0, 30, 100, 100), { overflow: "hidden" });
shadow.childNodes.push(slot);
slotted.assignedSlot = slot;
body.childNodes[body.childNodes.indexOf(light)] = host;
light.parentElement = light.parentNode = host;
box(host, rect(5, 0, 30, 100), { overflow: "hidden" });
sample = qa.sample("slot-composition");
assert.equal(sample.nodes.filter(row => row.sourceText === slotted.nodeValue).length, 1);
assert.equal(find(sample, slotted).rects[0].x, 5); assert.equal(find(sample, slotted).rects[0].width, 30);
host.display = "none";
assert.equal(find(qa.sample("host-hidden"), slotted), undefined);
host.display = "block";
slot.css.overflow = "visible";
assert.ok(find(qa.sample("host-reveal"), slotted));

// Blank withdrawal keeps prior independently clipped geometry; clipping the
// entire old aperture retires it without manufacturing a retraction event.
const retract = text("P", "Original source for geometry retraction.");
retract.rects = [rect(-20, 100, 100, 20)];
const retractBox = wrap(retract, rect(0, 90, 60, 60), { overflow: "hidden" });
const retractRecord = { node: retract, source: retract.nodeValue, lastWritten: "真实译文", groupElement: retract.parentElement };
state.records.set(retract, retractRecord); retract.nodeValue = retractRecord.lastWritten;
const observed = find(qa.sample("clipped-translated"), retract);
retract.nodeValue = retractRecord.lastWritten = ""; retract.rects = [];
context.scrollY = 50;
retractBox.getBoundingClientRect = () => rect(0, 40, 60, 60);
sample = qa.sample("clipped-blank-withdrawal");
const blankRow = find(sample, retract);
assert.equal(blankRow.rendered, false); assert.equal(blankRow.rects[0].width, 60);
assert.equal(blankRow.rects[0].documentY, observed.rects[0].documentY);
assert.equal(blankRow.rects[0].y, 50);
const retractionCount = sample.events.filter(event => event.type === "retraction").length;
assert.equal(retractionCount, 5);
context.scrollY = 0; retractBox.getBoundingClientRect = () => rect(0, 90, 60, 60);
retract.nodeValue = retractRecord.lastWritten = "真实译文"; retract.rects = [rect(-20, 100, 100, 20)];
qa.sample("translation-restored");
retract.nodeValue = retractRecord.lastWritten = ""; retract.rects = [];
retractBox.getBoundingClientRect = () => rect(1000, 90, 60, 60);
sample = qa.sample("blank-aperture-retired");
assert.equal(find(sample, retract), undefined);
assert.equal(sample.events.filter(event => event.type === "retraction").length, retractionCount);
assert.equal(sample.sourceNodes.find(row => row.id === observed.id).currentlyRendered, false);
assertCompleteSources(sample);
const packet = { type: "pageAutoCandidate", blocks: [{ id: "b", text: record.source }] };
const promise = environment.send(packet);
const outgoing = qa.drain(); assert.equal(outgoing[0].message, packet); assert.equal(qa.drain().length, 0);
qa.acknowledge(outgoing[0].rpcId, { accepted: true });
qa.deliver(outgoing[0].rpcId, JSON.stringify({ type: "pageAutoTranslations", updates: [] }));
assert.equal(received[0].type, "pageAutoTranslations");
observerCallback([{ type: "characterData", target: code }]); assert.equal(mutations, 1);
for (const type of ["scroll", "resize", "currententrychange", "popstate", "hashchange", "visibilitychange", "pagehide", "pageshow"])
  assert.ok(listeners.includes(type), type);
async function probeChecks() {
  assert.equal((await promise).accepted, true);
  const tick = () => new Promise(resolve => setImmediate(resolve));
  const snapshot = { samples: [{ text: "Synthetic sample" }], pageSummary: { textCharacters: 16 } };
  let finish, extractionEnvironment;
  extractProbe = value => { extractionEnvironment = value; return new Promise(resolve => { finish = resolve; }); };
  const job = qa.startProbe("zh-CN");
  assert.equal(job.stage, "running"); assert.equal(typeof job.then, "undefined");
  assert.equal(qa.probeStatus(job.id).result, null);
  assert.throws(() => qa.startProbe("zh-CN"), /already_running/);
  await tick();
  assert.equal(extractionEnvironment.document, document);
  assert.equal(extractionEnvironment.targetLanguage, "zh-CN");
  assert.equal(typeof extractionEnvironment.isRenderedText, "function");
  assert.equal(qa.probeStatus(job.id).stage, "running");
  finish(snapshot); await tick();
  const completed = qa.probeStatus(job.id);
  assert.equal(completed.stage, "completed");
  assert.equal(completed.result.samples, snapshot.samples);
  assert.equal(completed.result.statistics, snapshot.pageSummary);
  assert.equal(qa.probeStatus("wrong-id"), null);
  for (const extraction of [() => Promise.reject(new Error("raw error must not escape")),
      () => { throw new Error("sync extraction error"); },
      () => Promise.resolve({}), () => Promise.resolve({ samples: [], pageSummary: [] })]) {
    extractProbe = extraction;
    const failed = qa.startProbe("zh-CN"); await tick();
    assert.equal(qa.probeStatus(failed.id).stage, "failed");
    assert.equal(qa.probeStatus(failed.id).result, null);
    assert.equal(qa.probeStatus(failed.id).error, "production_probe_failed");
    assert.ok(!JSON.stringify(qa.probeStatus(failed.id)).includes("raw error"));
  }
  extractProbe = () => new Promise(resolve => { finish = resolve; });
  const disposed = qa.startProbe("zh-CN"); await tick();
  qa.dispose(); finish(snapshot); await tick();
  assert.equal(qa.probeStatus(disposed.id), null);
  assert.equal(qa.sample("probe-final").events.filter(event => event.type === "probe"
    && event.id === disposed.id && event.stage === "completed").length, 0);
  console.log("adapter synthetic DOM/RPC/async probe checks passed");
}
probeChecks().catch(error => { console.error(error); process.exitCode = 1; });
