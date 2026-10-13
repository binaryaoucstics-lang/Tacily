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
    for (const rect of row.rects) {
      for (const key of ["x", "y", "width", "height", "documentX", "documentY"])
        assert.ok(Number.isFinite(rect[key]), `${row.id} has non-finite ${key}`);
      assert.ok(rect.width > 0 && rect.height > 0, `${row.id} has nonpositive extent`);
    }
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
// Ordinary Range and element boxes may expose edges, dimensions, or both.
// All variants must supply the same independent evidence without a prediction.
const representations = [
  rect,
  (x, y, width, height) => ({ left: x, top: y, right: x + width, bottom: y + height }),
  (x, y, width, height) => ({ left: x, top: y, width, height }),
  (x, y, width, height) => ({ x, y, right: x + width, bottom: y + height }),
  (x, y, width, height) => ({ ...rect(x, y, width, height), left: x, top: y, right: x + width, bottom: y + height })
];
const represented = text("P", "Independent source with ordinary rectangle representations.");
let representedId;
for (const [index, representation] of representations.entries()) {
  represented.rects = [representation(-15, 1800, 120, 20)];
  const value = qa.sample(`range-representation-${index}`), row = find(value, represented);
  assert.ok(row, `ordinary range representation ${index} must be independently observed`);
  representedId ||= row.id;
  assert.equal(row.id, representedId);
  assert.equal(row.rendered, true); assert.equal(row.inViewport, false);
  assert.deepEqual(JSON.parse(JSON.stringify(row.rects)), [
    { ...rect(-15, 1800, 120, 20), documentX: -15, documentY: 1800 }]);
  assert.ok(!value.predictions.some(prediction => prediction.sourceId === row.id));
  assertCompleteSources(value);
}
const representedClip = wrap(represented, rect(0, 1700, 120, 300), { overflowX: "hidden", overflowY: "visible" });
Object.assign(representedClip, { offsetWidth: 60, offsetHeight: 150, clientLeft: 5, clientTop: 0,
  clientWidth: 40, clientHeight: 150 });
for (const [rangeIndex, rangeRepresentation] of representations.entries()) {
  for (const [boxIndex, boxRepresentation] of representations.entries()) {
    represented.rects = [rangeRepresentation(-20, 1800, 100, 20), rangeRepresentation(100, 1800, 20, 20)];
    representedClip.getBoundingClientRect = () => boxRepresentation(0, 1700, 120, 300);
    const value = qa.sample(`clip-representation-${rangeIndex}-${boxIndex}`), row = find(value, represented);
    assert.deepEqual(JSON.parse(JSON.stringify(row.rects)), [
      { ...rect(10, 1800, 70, 20), documentX: 10, documentY: 1800 }]);
    assert.equal(row.inViewport, false);
    assertCompleteSources(value);
  }
}
// Unknown or impossible ranges cannot be promoted by completed predictions.
const invalidRects = [
  {}, null, { width: 100, height: 20 },
  { x: NaN, y: 0, width: 100, height: 20 },
  { left: 0, top: 0, right: Infinity, bottom: 20 },
  { x: Infinity, y: 0, width: 100, height: 20 },
  { x: 0, y: 0, width: NaN, height: 20 },
  { x: 0, y: 0, width: 100, height: -1 },
  { left: 10, top: 0, right: 0, bottom: 20 },
  { x: 0, y: 0, width: 100, height: 20, left: 1 },
  { x: 0, y: 0, width: 100, height: 20, right: 90 },
  { x: 0, y: "0", width: 100, height: 20 },
  { x: 0, y: 0, width: null, height: 20 },
  { x: 1e308, y: 0, width: 1e308, height: 20 }
];
const invalidSource = text("P", "Prediction with no independently known geometry.");
predict("invalid-geometry", invalidSource);
for (const [index, invalid] of [...invalidRects, rect(0, 0, 0, 20)].entries()) {
  invalidSource.rects = [invalid];
  const value = qa.sample(`invalid-range-${index}`);
  assert.equal(find(value, invalidSource), undefined);
  assert.ok(!value.sourceNodes.some(row => row.sourceText === invalidSource.nodeValue));
  assert.equal(value.predictions.find(prediction => prediction.id === "invalid-geometry").sourceEvidence, "unmatched");
  assertCompleteSources(value);
}
// A good fragment can still be observed alongside an unknown fragment, but
// only its finite positive geometry enters the source row.
invalidSource.rects = [invalidRects[3], { left: 5, top: 10, right: 105, bottom: 30 }];
sample = qa.sample("mixed-known-ranges");
assert.equal(find(sample, invalidSource).rects.length, 1);
assertCompleteSources(sample);

// Edge-only retained blank evidence follows scroll and current clipping. Empty
// and known zero-area ranges retain geometry; unknown ranges do not retract.
const retained = text("P", "Edge-only source retained through blank translation.");
const edgeRect = representations[1];
const retainedBox = wrap(retained, edgeRect(0, 90, 60, 60), { overflow: "hidden" });
const retainedRecord = { node: retained, source: retained.nodeValue, lastWritten: "保留译文", groupElement: retained.parentElement };
state.records.set(retained, retainedRecord);
function restoreRetained() {
  context.scrollX = context.scrollY = 0;
  retainedBox.getBoundingClientRect = () => edgeRect(0, 90, 60, 60);
  delete retainedBox.clientWidth;
  retained.rects = [edgeRect(-20, 100, 100, 20)];
  retained.nodeValue = retainedRecord.lastWritten = "保留译文";
  return qa.sample("edge-translation-restored");
}
let retainedObservation = find(restoreRetained(), retained);
retained.nodeValue = retainedRecord.lastWritten = ""; retained.rects = [];
context.scrollY = 50;
retainedBox.getBoundingClientRect = () => edgeRect(10, 40, 30, 60);
sample = qa.sample("edge-blank-clipped-and-scrolled");
assert.equal(find(sample, retained).rendered, false);
assert.deepEqual(JSON.parse(JSON.stringify(find(sample, retained).rects)), [
  { ...rect(10, 50, 30, 20), documentX: 10, documentY: 100 }]);
assert.equal(find(sample, retained).firstSeen, retainedObservation.firstSeen);
assertCompleteSources(sample);
for (const [index, invalid] of invalidRects.entries()) {
  const before = restoreRetained(), count = before.events.filter(event => event.type === "retraction").length;
  const history = before.sourceNodes.find(row => row.id === retainedObservation.id);
  retained.nodeValue = retainedRecord.lastWritten = ""; retained.rects = [invalid];
  sample = qa.sample(`edge-blank-unknown-range-${index}`);
  assert.equal(find(sample, retained), undefined);
  assert.equal(sample.events.filter(event => event.type === "retraction").length, count);
  history.currentlyRendered = false;
  assert.deepEqual(sample.sourceNodes.find(row => row.id === retainedObservation.id), history);
  assertCompleteSources(sample);
}
const invalidBlankLayouts = [
  () => { retainedBox.getBoundingClientRect = () => undefined; },
  () => { retainedBox.getBoundingClientRect = () => ({ left: 0, top: 90, right: NaN, bottom: 150 }); },
  () => { retainedBox.getBoundingClientRect = () => ({ left: 60, top: 90, right: 0, bottom: 150 }); },
  () => { retainedBox.clientWidth = Infinity; },
  () => { context.scrollY = NaN; },
  () => { context.scrollX = Infinity; },
  () => { retainedBox.getBoundingClientRect = () => edgeRect(1000, 90, 60, 60); }
];
for (const [index, invalidate] of invalidBlankLayouts.entries()) {
  const before = restoreRetained(), count = before.events.filter(event => event.type === "retraction").length;
  const history = before.sourceNodes.find(row => row.id === retainedObservation.id);
  retained.nodeValue = retainedRecord.lastWritten = ""; retained.rects = [];
  invalidate();
  sample = qa.sample(`edge-blank-unknown-layout-${index}`);
  assert.equal(find(sample, retained), undefined);
  assert.equal(sample.events.filter(event => event.type === "retraction").length, count);
  history.currentlyRendered = false;
  assert.deepEqual(sample.sourceNodes.find(row => row.id === retainedObservation.id), history);
  assertCompleteSources(sample);
}
restoreRetained();
retained.nodeValue = retainedRecord.lastWritten = "";
retained.rects = [edgeRect(0, 0, 0, 0)];
sample = qa.sample("edge-blank-known-zero-area");
assert.equal(find(sample, retained).rects[0].width, 60);
assertCompleteSources(sample);
// Overflow in document coordinate addition cannot emit source positions.
const overflowPosition = text("P", "Finite viewport coordinates with impossible document position.");
overflowPosition.rects = [rect(1e308, 0, 1e307, 20)];
context.scrollX = 1e308;
assert.equal(find(qa.sample("document-position-overflow"), overflowPosition), undefined);
context.scrollX = 0;
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

// Append-only transient fixtures. This separate VM cannot affect original checks.
async function transientProducerChecks() {
  const timers = new Map(); let timerSerial = 0, callback, environment;
  const body = { nodeType: 1, tagName: "BODY", childNodes: [], hasAttribute: () => false };
  const document = { nodeType: 9, childNodes: [body], body, visibilityState: "visible", title: "Offline transient",
    createRange: () => ({ selectNodeContents() {}, getClientRects: () => [{ x: 0, y: 10, width: 200, height: 24 }] }),
    addEventListener() {}, removeEventListener() {} };
  body.parentNode = document;
  function source(value) {
    const parent = { nodeType: 1, tagName: "P", childNodes: [], parentNode: body, parentElement: body,
      hasAttribute: () => false, getAttribute: () => null, getRootNode: () => document };
    const node = { nodeType: 3, nodeValue: value, parentNode: parent, parentElement: parent, isConnected: true };
    parent.childNodes.push(node); body.childNodes.push(parent); return node;
  }
  const node = source("Transient independently visible source.");
  const state = { records: new Map(), blocks: new Map(), jobs: 0, pending: 0, scanning: false,
    assessed: true, active: true, observing: true, paused: false, originalView: false, manualMode: null };
  const session = { inspect: () => state, configure: () => true, mutated() {}, receive: () => true,
    control() {}, layoutChanged() {}, discoverOpenRoots() {}, dispose() {} };
  const context = { document, location: { href: "https://offline.invalid/" }, innerWidth: 800, innerHeight: 600,
    scrollX: 0, scrollY: 0, Date, console,
    getComputedStyle: node => ({ display: node.display || "block", visibility: "visible", opacity: "1" }),
    ScreenTranslatorMotion: { dispose() {} }, ScreenTranslatorMotionFactory: {},
    ScreenTranslatorPageAutoContent: { randomIdentifier: () => "offline", createRenderedTextTester: () => () => true,
      createPageAutoSession: value => { environment = value; return session; },
      createOpenRootObserver: value => { callback = value.onMutations;
        return { configure() {}, found() {}, replace() {}, dispose() {} }; } },
    MutationObserver: function () {}, addEventListener() {}, removeEventListener() {},
    setInterval: () => 1, clearInterval() {},
    setTimeout: (fn, delay) => { assert.equal(delay, 0); const id = ++timerSerial; timers.set(id, fn); return id; },
    clearTimeout: id => timers.delete(id) };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../page-adapter.js"), "utf8"), context);
  const qa = context.__websiteQa;
  // Mutation is the only observation while the row is visible.
  callback([]);
  node.parentElement.display = "none";
  let snapshot = qa.sample("scroll-before");
  let retained = snapshot.producerObservations[0];
  assert.equal(snapshot.nodes.length, 0);
  assert.equal(retained.phase, "mutation");
  assert.match(retained.at, /^\d{4}-\d\d-\d\dT/);
  assert.equal(retained.source.firstSeen, retained.at);
  assert.equal(retained.rendered, true); assert.equal(retained.eligible, false);
  assert.equal(retained.work.scheduledScans, 0);
  const id = retained.sourceId;
  // Returned objects cannot corrupt adapter history.
  retained.eligible = true; retained.source.sourceText = "tampered";
  retained = qa.sample("hidden-again").producerObservations[0];
  assert.equal(retained.eligible, false);
  assert.equal(retained.sourceText, node.nodeValue);
  // A queued scan is not idle even while the inspect counters are all zero.
  node.parentElement.display = "block";
  const cancelled = environment.schedule(async () => {});
  snapshot = qa.sample("visible-scheduled");
  assert.equal(snapshot.producerObservations[0].eligible, null);
  assert.equal(snapshot.producerObservations[0].work.scheduledScans, 1);
  environment.clearSchedule(cancelled); assert.equal(timers.size, 0);
  // Actual asynchronous scan completion takes another independent view.
  let finish;
  const scheduled = environment.schedule(() => { state.scanning = true;
    return new Promise(resolve => { finish = () => { state.scanning = false; resolve(); }; }); });
  const run = timers.get(scheduled); timers.delete(scheduled); run();
  callback([]); assert.equal(qa.sample("while-scanning").producerObservations[0].eligible, null);
  finish(); await Promise.resolve();
  node.parentElement.display = "none";
  snapshot = qa.sample("hidden-after-idle");
  assert.equal(snapshot.producerObservations[0].phase, "producer-idle");
  assert.equal(snapshot.producerObservations[0].eligible, false);
  // Hiding before asynchronous completion never supplies negative evidence.
  node.parentElement.display = "block";
  const early = environment.schedule(() => { state.scanning = true;
    return new Promise(resolve => { finish = () => { state.scanning = false; resolve(); }; }); });
  const earlyRun = timers.get(early); timers.delete(early); earlyRun(); callback([]);
  node.parentElement.display = "none"; finish(); await Promise.resolve();
  assert.equal(qa.sample("hidden-before-idle").producerObservations[0].eligible, null);
  // Real producer flags and explicit atomic membership remain separate from truth.
  node.parentElement.display = "block";
  const peer = source("Independently observed atomic peer."); qa.sample("peer-before");
  peer.parentElement.display = "none";
  const record = { node, source: node.nodeValue, lastWritten: node.nodeValue };
  state.records.set(node, record);
  const piece = { complete: true, failed: false };
  state.blocks.set("atomic", { id: "atomic", record, text: record.source, piece,
    atomicRecords: [record, { node: peer, source: peer.nodeValue }] });
  snapshot = qa.sample("atomic-idle");
  const peerObservation = snapshot.producerObservations.find(row => row.sourceId !== id);
  assert.equal(peerObservation.rendered, false); assert.equal(peerObservation.representedBy, id);
  assert.equal(peerObservation.representedSource.rendered, true);
  assert.equal(peerObservation.pieces[0].completed, true);
  piece.failed = true; snapshot = qa.sample("atomic-failed");
  assert.equal(snapshot.producerObservations[0].pieces[0].failed, true);
  assert.equal(snapshot.producerObservations[0].pieces[0].completed, false);
  state.blocks.clear(); state.scanning = true;
  snapshot = qa.sample("new-visible-pending");
  assert.equal(snapshot.producerObservations.find(row => row.sourceId === id).eligible, null);
  // An authored source change cannot reuse stale record text as source authority.
  node.nodeValue = "New authored factual version.";
  snapshot = qa.sample("authored-change-before-production-scan");
  assert.notEqual(snapshot.nodes[0].id, id);
  assert.equal(snapshot.nodes[0].sourceText, node.nodeValue);
  assert.equal(snapshot.sourceNodes.find(row => row.id === id).sourceText, record.source);
  state.scanning = false; state.assessed = null;
  const count = snapshot.producerObservations.length;
  source("Before admission is unknown."); snapshot = qa.sample("unassessed");
  assert.equal(snapshot.producerObservations.length, count);
  // Bound additional capture. Overflow is explicit; Python refuses readiness.
  state.records.clear(); state.assessed = true;
  for (let i = 0; i < 2050; i++) source(`Bounded source ${i}`);
  snapshot = qa.sample("observation-capacity");
  assert.ok(snapshot.producerObservations.length <= 2048);
  assert.ok(snapshot.producerObservationDropped > 0);
  assert.ok(snapshot.sourceNodes.some(row => row.id === id));
  assert.ok(JSON.stringify(snapshot.producerObservations).length <= 2000000 + 4096);
  qa.dispose();
  console.log("adapter transient producer/scheduled scan/atomic/bounds checks passed");
}
transientProducerChecks().catch(error => { console.error(error); process.exitCode = 1; });

// An actual Text moves while admission is reset. The independent source row
// advances, while the last factual producer view and its timestamp stay older.
function movedProducerFixture(atomic) {
  let selected;
  const body = { nodeType: 1, tagName: "BODY", childNodes: [], hasAttribute: () => false };
  const document = { nodeType: 9, childNodes: [body], body, visibilityState: "visible", title: "Offline movement",
    createRange: () => ({ selectNodeContents(node) { selected = node; },
      getClientRects: () => [{ x: selected.x, y: 10, width: 200, height: 24 }] }),
    addEventListener() {}, removeEventListener() {} };
  body.parentNode = document;
  function parent() {
    const value = { nodeType: 1, tagName: "P", childNodes: [], parentNode: body, parentElement: body,
      hasAttribute: () => false, getAttribute: () => null, getRootNode: () => document };
    body.childNodes.push(value); return value;
  }
  function text(value, container) {
    const node = { nodeType: 3, nodeValue: value, parentNode: container, parentElement: container,
      isConnected: true, x: 0 };
    container.childNodes.push(node); return node;
  }
  const peer = atomic ? text("Independent atomic member.", parent()) : null;
  if (!atomic) parent(); // Historical 0/1/0, current 0/2/0 as in the review probe.
  const oldParent = parent(), node = text("Independent moving institution.", oldParent), newParent = parent();
  const state = { records: new Map(), blocks: new Map(), jobs: 0, pending: 0, scanning: false,
    assessed: true, active: true, observing: true, paused: false, originalView: false, manualMode: null };
  const session = { inspect: () => state, configure: () => true, mutated() {}, receive: () => true,
    control() {}, layoutChanged() {}, discoverOpenRoots() {}, dispose() {} };
  const context = { document, location: { href: "https://offline.invalid/" }, innerWidth: 800, innerHeight: 600,
    scrollX: 0, scrollY: 0, Date, console,
    getComputedStyle: node => ({ display: node.display || "block", visibility: "visible", opacity: "1" }),
    ScreenTranslatorMotion: { dispose() {} }, ScreenTranslatorMotionFactory: {},
    ScreenTranslatorPageAutoContent: { randomIdentifier: () => "offline", createRenderedTextTester: () => () => true,
      createPageAutoSession: () => session, createOpenRootObserver: () => ({ configure() {}, found() {}, replace() {}, dispose() {} }) },
    MutationObserver: function () {}, addEventListener() {}, removeEventListener() {},
    setInterval: () => 1, clearInterval() {}, setTimeout: () => 1, clearTimeout() {} };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../page-adapter.js"), "utf8"), context);
  const qa = context.__websiteQa;
  if (atomic) {
    qa.sample("independent-members");
    peer.parentElement.display = "none";
    const record = { node, source: node.nodeValue, lastWritten: node.nodeValue };
    state.records.set(node, record);
    state.blocks.set("moving-piece", { id: "moving-piece", record, text: node.nodeValue,
      piece: { complete: true, failed: false }, atomicRecords: [record, { node: peer, source: peer.nodeValue }] });
  }
  const before = qa.sample("mutation"), historic = before.producerObservations.find(row => row.rendered);
  assert.equal(historic.source.locator, "0/1/0");
  state.assessed = null;
  oldParent.childNodes.splice(oldParent.childNodes.indexOf(node), 1);
  newParent.childNodes.push(node); node.parentElement = node.parentNode = newParent; node.x = 80;
  const moved = qa.sample("unassessed-movement");
  assert.equal(moved.nodes.find(row => row.id === historic.sourceId).locator, "0/2/0");
  assert.equal(moved.nodes.find(row => row.id === historic.sourceId).rects[0].x, 80);
  assert.equal(moved.nodes.find(row => row.id === historic.sourceId).firstSeen, historic.source.firstSeen);
  newParent.display = "none";
  const value = qa.sample("hidden-after-movement");
  assert.deepEqual(value.producerObservations, before.producerObservations);
  assert.equal(value.independentSourceObservationDropped, 0);
  assert.ok(value.independentSourceObservations.some(version => version.source.id === historic.sourceId
    && version.source.locator === "0/1/0" && version.at === historic.at && version.sequence === historic.sequence));
  assert.ok(value.independentSourceObservations.some(version => version.source.id === historic.sourceId
    && version.source.locator === "0/2/0" && version.source.phase === "unassessed-movement"));
  qa.dispose(); return JSON.parse(JSON.stringify(value));
}
const movementFixtures = { rejected: movedProducerFixture(false), atomic: movedProducerFixture(true) };
if (process.argv.includes("--movement-fixture")) console.log("MOVEMENT_FIXTURE=" + JSON.stringify(movementFixtures));
console.log("adapter independent Text reparent/geometry/admission reset/atomic owner movement checks passed");

// Offline article-scale transport. 1,900 independently traversed Text sources,
// inline atomic groups and four pieces per group reproduce the redundant rows
// of a long reference article without importing real site labels or captures.
function boundedProducerFixture(count = 1900, oversized = false) {
  let selected;
  const body = { nodeType: 1, tagName: "BODY", childNodes: [], hasAttribute: () => false };
  const document = { nodeType: 9, childNodes: [body], body, visibilityState: "visible", title: "Offline long article",
    createRange: () => ({ selectNodeContents(node) { selected = node; },
      getClientRects: () => [{ x: selected.x, y: selected.y, width: 220, height: 24 }] }),
    addEventListener() {}, removeEventListener() {} };
  body.parentNode = document;
  const nodes = [], records = [];
  for (let i = 0; i < count; i++) {
    const parent = { nodeType: 1, tagName: i % 10 ? "SPAN" : "P", childNodes: [], parentNode: body,
      parentElement: body, hasAttribute: () => false, getAttribute: () => null, getRootNode: () => document };
    const text = oversized === true ? "Long independently authored source. ".repeat(61000) :
      `Article section ${i}: Independent history of scientific institutions and public reference material, ` +
      "including dates, inline citations, navigation entries and explanatory prose.";
    const node = { nodeType: 3, nodeValue: text, parentNode: parent, parentElement: parent,
      isConnected: true, x: i % 10 * 12, y: Math.floor(i / 10) * 28 };
    parent.childNodes.push(node); body.childNodes.push(parent); nodes.push(node);
    records.push({ node, source: text, lastWritten: text });
  }
  const state = { records: new Map(), blocks: new Map(), jobs: 0, pending: 0, scanning: false,
    assessed: null, active: true, observing: true, paused: false, originalView: false, manualMode: null };
  const session = { inspect: () => state, configure: () => true, mutated() {}, receive: () => true,
    control() {}, layoutChanged() {}, discoverOpenRoots() {}, dispose() {} };
  const context = { document, location: { href: "https://offline.invalid/" }, innerWidth: 800, innerHeight: 600,
    scrollX: 0, scrollY: 0, Date, console,
    getComputedStyle: node => ({ display: node.display || "block", visibility: "visible", opacity: "1" }),
    ScreenTranslatorMotion: { dispose() {} }, ScreenTranslatorMotionFactory: {},
    ScreenTranslatorPageAutoContent: { randomIdentifier: () => "offline", createRenderedTextTester: () => () => true,
      createPageAutoSession: () => session, createOpenRootObserver: () => ({ configure() {}, found() {}, replace() {}, dispose() {} }) },
    MutationObserver: function () {}, addEventListener() {}, removeEventListener() {},
    setInterval: () => 1, clearInterval() {}, setTimeout: () => 1, clearTimeout() {} };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../page-adapter.js"), "utf8"), context);
  const qa = context.__websiteQa;
  qa.sample("independent-article");
  state.assessed = true; state.jobs = 2; state.pending = 1; state.scanning = true;
  for (let i = 0; i < count; i += 10) {
    const members = records.slice(i, i + 10), record = members[0];
    for (const member of members) {
      state.records.set(member.node, member);
      if (member !== record) member.node.parentElement.display = "none";
    }
    for (let j = 0; j < (oversized === "pieces" ? 240 : 4); j++) {
      const id = `article-piece-${i}-${j}`;
      state.blocks.set(id, { id, record, atomicRecords: members,
        text: `Translated section ${i}, piece ${j}.`.repeat(oversized === "pieces" ? 500 : 1),
        piece: { complete: false, failed: false } });
    }
  }
  const partial = qa.sample("article-in-progress");
  state.jobs = state.pending = 0; state.scanning = false;
  for (const block of state.blocks.values()) block.piece.complete = true;
  const complete = qa.sample("article-idle");
  if (count === 1900 && !oversized) {
    assert.equal(complete.producerObservations.length, count);
    assert.equal(complete.producerObservationDropped, 0);
    assert.equal(complete.independentSourceObservationDropped, 0);
    assert.equal(complete.producerObservationTransport.schemaVersion, 1);
    assert.equal(Object.keys(complete.producerObservationTransport.values).length, count / 10 * 4 + 1);
    const wireCharacters = JSON.stringify(complete.producerObservations).length +
      JSON.stringify(complete.producerObservationTransport).length;
    assert.ok(wireCharacters <= 2000000);
    assert.ok(JSON.stringify(complete.independentSourceObservations).length <= 2000000);
    assert.ok(complete.independentSourceObservations.length <= 2048);
    // Only one owner moves during admission reset; historic atomic ownership
    // must keep its original locator, geometry, source version and timestamp.
    state.assessed = null;
    for (const node of nodes) node.parentElement.display = "none";
    const node = nodes[0], old = node.parentElement, moved = { ...old, childNodes: [] };
    body.childNodes.push(moved);
    moved.display = "block";
    old.childNodes.splice(old.childNodes.indexOf(node), 1); moved.childNodes.push(node);
    node.parentElement = node.parentNode = moved; node.x = 77; node.y = 111;
    qa.sample("article-unassessed-movement"); moved.display = "none";
    const historical = qa.sample("article-hidden");
    assert.equal(historical.producerObservationDropped, 0);
    assert.equal(historical.independentSourceObservationDropped, 0);
    assert.equal(historical.independentSourceObservations.length, count + 1);
    // Caller mutation must not change retained interned definitions or sources.
    const original = JSON.stringify(historical.producerObservationTransport);
    complete.producerObservationTransport.values[Object.keys(complete.producerObservationTransport.values)[0]].text = "tamper";
    assert.equal(JSON.stringify(qa.sample("article-hidden-again").producerObservationTransport), original);
    qa.dispose();
    return JSON.parse(JSON.stringify({ partial, historical, wireCharacters }));
  }
  assert.ok(complete.producerObservationDropped > 0 || complete.independentSourceObservationDropped > 0);
  assert.ok(complete.producerObservations.length <= 2048);
  assert.ok(complete.independentSourceObservations.length <= 2048);
  qa.dispose(); return JSON.parse(JSON.stringify(complete));
}
const boundedFixtures = boundedProducerFixture();
const rowOverflowFixture = boundedProducerFixture(2100);
const characterOverflowFixture = boundedProducerFixture(1, true);
const pieceOverflowFixture = boundedProducerFixture(1, "pieces");
assert.equal(pieceOverflowFixture.independentSourceObservationDropped, 0);
assert.ok(pieceOverflowFixture.producerObservationDropped > 0);
if (process.argv.includes("--bounded-fixture")) console.log("BOUNDED_FIXTURE=" + JSON.stringify({
  ...boundedFixtures, rowOverflow: rowOverflowFixture,
  pieceOverflow: { at: pieceOverflowFixture.at, producerObservations: pieceOverflowFixture.producerObservations,
    producerObservationDropped: pieceOverflowFixture.producerObservationDropped,
    independentSourceObservations: pieceOverflowFixture.independentSourceObservations,
    independentSourceObservationDropped: pieceOverflowFixture.independentSourceObservationDropped,
    sourceNodes: pieceOverflowFixture.sourceNodes },
  characterOverflow: { at: characterOverflowFixture.at, producerObservations: characterOverflowFixture.producerObservations,
    producerObservationDropped: characterOverflowFixture.producerObservationDropped,
    independentSourceObservations: characterOverflowFixture.independentSourceObservations,
    independentSourceObservationDropped: characterOverflowFixture.independentSourceObservationDropped } }));
console.log("adapter 1900-source interned historical/atomic transport and true row/character overflow checks passed");

// >1800 real independent DOM rows, with old atomic views followed by a full
// admission-reset reflow. These fixtures exercise the adapter, not fabricated
// references or a reconstruction of the interrupted Wiki failure sample.
function referenceExportFixture(mode = "reflow") {
  let selected;
  const count = mode === "characterPressure" ? 1000 : 1844;
  const normal = ["reflow", "membership", "characterPressure"].includes(mode);
  const body = { nodeType: 1, tagName: "BODY", childNodes: [], hasAttribute: () => false };
  const document = { nodeType: 9, childNodes: [body], body, visibilityState: "visible", title: "Offline reflow article",
    createRange: () => ({ selectNodeContents(node) { selected = node; },
      getClientRects: () => [{ x: selected.x, y: selected.y, width: 220, height: 24 }] }),
    addEventListener() {}, removeEventListener() {} };
  body.parentNode = document;
  const records = [];
  for (let i = 0; i < count; i++) {
    const parent = { nodeType: 1, tagName: "SPAN", childNodes: [], parentNode: body, parentElement: body,
      hasAttribute: () => false, getAttribute: () => null, getRootNode: () => document };
    const source = `Public article row ${i}: Independent source and reference material.` +
      " Extra independently authored explanatory prose.".repeat(mode === "characters" ? 6 : mode === "characterPressure" ? 10 : 0);
    const node = { nodeType: 3, nodeValue: source, parentNode: parent, parentElement: parent,
      isConnected: true, x: i % 10 * 12, y: Math.floor(i / 10) * 28 };
    parent.childNodes.push(node); body.childNodes.push(parent);
    records.push({ node, source, lastWritten: source });
  }
  const state = { records: new Map(), blocks: new Map(), jobs: 0, pending: 0, scanning: false,
    assessed: null, active: true, observing: true, paused: false, originalView: false, manualMode: null };
  const session = { inspect: () => state, configure: () => true, mutated() {}, receive: () => true,
    control() {}, layoutChanged() {}, discoverOpenRoots() {}, dispose() {} };
  const context = { document, location: { href: "https://offline.invalid/" }, innerWidth: 800, innerHeight: 600,
    scrollX: 0, scrollY: 0, Date, console,
    getComputedStyle: node => ({ display: node.display || "block", visibility: "visible", opacity: "1" }),
    ScreenTranslatorMotion: { dispose() {} }, ScreenTranslatorMotionFactory: {},
    ScreenTranslatorPageAutoContent: { randomIdentifier: () => "offline", createRenderedTextTester: () => () => true,
      createPageAutoSession: () => session, createOpenRootObserver: () => ({ configure() {}, found() {}, replace() {}, dispose() {} }) },
    MutationObserver: function () {}, addEventListener() {}, removeEventListener() {},
    setInterval: () => 1, clearInterval() {}, setTimeout: () => 1, clearTimeout() {} };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../page-adapter.js"), "utf8"), context);
  const qa = context.__websiteQa;
  qa.sample("independent-reflow");
  state.assessed = true; state.jobs = 1; state.scanning = true;
  const width = normal ? 10 : 2;
  for (let i = 0; i < count; i += width) {
    const members = records.slice(i, i + width), record = members[0];
    for (const member of members) {
      state.records.set(member.node, member);
      if (mode === "membership" && i === 0) member.node.parentElement.display = member === members[1] ? "block" : "none";
      else if (member !== record) member.node.parentElement.display = "none";
    }
    for (let j = 0; j < (normal ? 4 : 1); j++) {
      const id = `reflow-piece-${i}-${j}`;
      state.blocks.set(id, { id, record, atomicRecords: members, text: `Public section ${i}, part ${j}`,
        piece: { complete: false, failed: false } });
    }
  }
  const partial = qa.sample("reflow-partial");
  state.jobs = 0; state.scanning = false;
  for (const block of state.blocks.values()) block.piece.complete = true;
  const complete = qa.sample("reflow-complete");
  if (!normal) {
    // Peers retain old owner references; owners now have separate actual views.
    state.blocks.clear();
    for (let i = 0; i < count; i += width) {
      const record = records[i], id = `new-owner-${i}`;
      state.blocks.set(id, { id, record, atomicRecords: [record], text: "New independent owner piece",
        piece: { complete: true, failed: false } });
    }
    const overflow = qa.sample("new-owner-views");
    assert.equal(overflow.producerObservationDropped, 0);
    assert.equal(overflow.independentSourceObservationExport.scope, "references");
    assert.ok(overflow.independentSourceObservationDropped > 0);
    assert.ok(overflow.independentSourceObservations.length <= 2048);
    if (mode === "characters") assert.ok(overflow.independentSourceObservations.length < 2048);
    qa.dispose(); return { overflow };
  }
  state.assessed = null;
  for (const [i, { node }] of records.entries()) {
    if (mode === "membership" && i < 10 && i !== 1) continue;
    const old = node.parentElement, moved = { ...old, childNodes: [], display: "block" };
    body.childNodes.push(moved); old.childNodes.length = 0; moved.childNodes.push(node);
    node.parentElement = node.parentNode = moved; node.x += 77; node.y += 111;
  }
  const reflow = qa.sample("unassessed-full-reflow");
  {
    assert.equal(reflow.independentSourceObservationExport.schemaVersion, 1);
    assert.equal(reflow.independentSourceObservationExport.scope, "references");
    assert.equal(reflow.independentSourceObservations.length, count);
    assert.equal(reflow.independentSourceObservationDropped, 0);
    assert.equal(reflow.producerObservationDropped, 0);
    assert.equal(reflow.sourceNodes.length, count);
    assert.ok(JSON.stringify(reflow.independentSourceObservations).length <= 2000000);
    // Exact old geometry/owner witnesses are preserved, while latest facts move.
    const sortedVersions = value => JSON.stringify([...value.independentSourceObservations]
      .sort((a, b) => a.source.id.localeCompare(b.source.id)));
    assert.equal(sortedVersions(reflow), sortedVersions(complete));
    const movedIndex = mode === "membership" ? 1 : 0;
    assert.notEqual(reflow.sourceNodes[movedIndex].locator, complete.sourceNodes[movedIndex].locator);
    assert.notEqual(reflow.sourceNodes[movedIndex].rects[0].x, complete.sourceNodes[movedIndex].rects[0].x);
    const visibleReflow = JSON.parse(JSON.stringify(reflow));
    reflow.independentSourceObservations[0].source.rects[0].x = -999;
    for (const { node } of records) node.parentElement.display = "none";
    const fresh = qa.sample("unassessed-reflow-again");
    assert.notEqual(fresh.independentSourceObservations[0].source.rects[0].x, -999);
    qa.dispose(); return { partial, complete, visibleReflow, reflow: fresh };
  }
  qa.dispose(); return { overflow: reflow };
}
if (process.argv.includes("--version-export-fixture")) {
  const fixture = referenceExportFixture();
  const requiredOverflow = referenceExportFixture("requiredOverflow").overflow;
  const characterOverflow = referenceExportFixture("characters").overflow;
  const characterPressure = referenceExportFixture("characterPressure").reflow;
  const membership = referenceExportFixture("membership").reflow;
  console.log("VERSION_EXPORT_FIXTURE=" + JSON.stringify({ ...fixture, requiredOverflow, characterOverflow,
    characterPressure, membership }));
  console.log("adapter reference-only reflow and necessary witness overflow checks passed");
}
