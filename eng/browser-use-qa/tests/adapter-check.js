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
      ["SCRIPT", "STYLE"].includes(node.parentElement?.tagName) ? [] : [{ x: 0, y: 10, width: 100, height: 20 }] };
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
  getComputedStyle: element => ({ display: element.display || "block", visibility: "visible", opacity: "1" }),
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
