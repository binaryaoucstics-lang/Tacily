"use strict";
const test = require("node:test"), assert = require("node:assert/strict");
const { extractPageSnapshot, createPageAutoSession, createOpenRootObserver, createRenderedTextTester } = require("../page-auto-content.js");
const fixtures = new Set();
async function tick() {
  await new Promise(resolve => setTimeout(resolve, 5));
  for (let at = 0; [...fixtures].some(f => f.session.inspect().scanning); at++) {
    if (at > 1500) throw new Error("source snapshot did not settle");
    await new Promise(resolve => setTimeout(resolve, 1));
  }
}
class Text {
  constructor(value) { this.nodeType = 3; this.nodeValue = value; this.isConnected = true; this.parentElement = null; }
  get textContent() { return this.nodeValue; }
  getRootNode() { return this.parentElement?.getRootNode() || this.parentRoot; }
}
class Element {
  constructor(tag, children = [], attributes = {}) { this.nodeType = 1; this.tagName = tag; this.attributes = attributes;
    this.childNodes = []; this.parentElement = null; this.style = {}; for (const child of children) this.append(child); }
  append(...children) { for (const child of children) { child.parentElement = this; child.parentRoot = null; this.childNodes.push(child); } return children[0]; }
  setAttribute(name, value) { this.attributes[name] = value; }
  remove() { if (this.parentElement) this.parentElement.childNodes = this.parentElement.childNodes.filter(child => child !== this); this.parentElement = null; }
  attachShadow(mode = "open") { const shadow = new Shadow(this, mode); if (mode === "open") this.shadowRoot = shadow; return shadow; }
  getRootNode() { return this.parentElement?.getRootNode() || this.parentRoot; }
  hasAttribute(name) { return Object.hasOwn(this.attributes, name); }
  getAttribute(name) { return this.attributes[name] ?? null; }
  get textContent() { return this.childNodes.map(child => child.textContent).join(""); }
  set textContent(value) { this.childNodes = []; this.append(new Text(value)); }
  getBoundingClientRect() { return { top: 10, bottom: 70 }; }
}
class Shadow {
  constructor(host, mode) { this.nodeType = 11; this.host = host; this.mode = mode; this.childNodes = []; }
  append(child) { child.parentElement = null; child.parentRoot = this; this.childNodes.push(child); return child; }
  getRootNode() { return this; }
}
function p(text) { const node = new Text(text), element = new Element("P", [node]); return { node, element }; }
function fixture(children) {
  const body = new Element("BODY", children), document = { body, visibilityState: "visible", documentElement: { lang: "en" }, createElement: tag => new Element(tag.toUpperCase()) };
  const sent = [], statuses = [], rootLists = [], found = []; let id = 0;
  const environment = { document, token: "shadow-document", makeId: () => `s-${++id}`, getStyle: element => element.style,
    send: async packet => { sent.push(packet); return { accepted: true }; }, onStatus: status => statuses.push(status),
    onRootFound: tree => found.push(tree), onOpenRoots: trees => rootLists.push(new Set(trees)) };
  const session = createPageAutoSession(environment);
  const candidate = () => sent.filter(packet => packet.type === "pageAutoCandidate").at(-1);
  function reply(type, extra = {}, source = candidate()) { return session.receive({ type, requestId: source.requestId, documentToken: source.documentToken,
    generation: source.generation, revision: source.revision, ...extra }); }
  const result = { body, document, environment, session, sent, statuses, rootLists, found, candidate, reply };
  fixtures.add(result); return result;
}
async function enable(f) { f.session.configure({ generation: 1, enabled: true, active: true, targetLanguage: "简体中文" }); await tick(); }
function finish(f) { const candidate = f.candidate(); f.reply("pageAutoAssessment", { needsTranslation: true, allowImages: true });
  f.reply("pageAutoTranslations", { updates: candidate.blocks.map(block => ({ id: block.id, text: "中文译文", complete: true })), finished: true }); }
test("nested open shadow paragraphs and inline links/strong preserve actual text nodes during streaming and restore", async () => {
  const host = new Element("X-ARTICLE"), outer = host.attachShadow(), innerHost = new Element("X-PART"), inner = innerHost.attachShadow(); outer.append(innerHost);
  const prefix = new Text("Read the "), label = new Text("complete guide"), strong = new Element("STRONG", [label]), anchor = new Element("A", [strong], { href: "/guide" });
  const suffix = new Text(" for detailed browser translation instructions."), paragraph = new Element("P", [prefix, anchor, suffix]); inner.append(paragraph);
  const f = fixture([host]); await enable(f); const candidate = f.candidate(); assert.equal(candidate.blocks.length, 3);
  assert.equal(new Set(candidate.blocks.map(block => block.groupId)).size, 1); assert.equal(f.rootLists.at(-1).size, 2);
  finish(f); assert.equal(label.nodeValue, "中文译文"); assert.equal(paragraph.childNodes[1], anchor); assert.equal(anchor.childNodes[0], strong);
  assert.equal(anchor.getAttribute("href"), "/guide"); f.session.control("original"); assert.equal(label.nodeValue, "complete guide");
  f.session.control("original"); assert.equal(label.nodeValue, "中文译文"); f.session.control("pause"); assert.equal(label.nodeValue, "complete guide"); f.session.dispose();
});
test("shadow host hidden editable input code own UI and closed roots stay out of samples and blocks", async () => {
  const good = p("The ordinary light DOM English body remains available for automatic translation.");
  const hosts = [new Element("X-HIDDEN"), new Element("X-EDITOR"), new Element("X-FORM"), new Element("X-CODE"),
    new Element("X-OWN", [], { "data-screen-translator": "status" }), new Element("X-CLOSED")];
  const privateText = "Private foreign paragraph that must never leave a protected shadow component.";
  hosts[0].hidden = true; hosts[1].isContentEditable = true;
  for (const [index, host] of hosts.entries()) host.attachShadow(index === 5 ? "closed" : "open").append(p(privateText).element);
  const form = new Element("FORM", [new Element("TEXTAREA", [hosts[2]])]), code = new Element("PRE", [hosts[3]]);
  const f = fixture([good.element, hosts[0], hosts[1], form, code, hosts[4], hosts[5]]); const snapshot = await extractPageSnapshot(f.environment);
  assert.deepEqual(snapshot.blocks.map(block => block.text), [good.node.nodeValue]);
  assert.ok(snapshot.samples.every(sample => !sample.text.includes("Private"))); assert.ok(!snapshot.openRoots.has(hosts[4].shadowRoot));
  assert.equal(snapshot.openRoots.size, 2); f.session.dispose();
});
test("slots retain composed paragraph reading order and deduplicate assigned light text while hiding fallback and unslotted text", async () => {
  const host = new Element("X-SLOTTED"), shadow = host.attachShadow(), assigned = new Text("the complete guide"), unused = new Text("Unslotted private foreign prose must not be translated.");
  host.append(assigned); host.append(unused); const slot = new Element("SLOT", [new Text("Hidden fallback private prose should not be sent.")]);
  slot.assignedNodes = () => [assigned]; assigned.assignedSlot = slot;
  shadow.append(new Element("P", [new Text("Please read "), slot, new Text(" carefully before using browser translation.")]));
  const f = fixture([host]), snapshot = await extractPageSnapshot(f.environment);
  assert.equal(snapshot.blocks.length, 3); assert.equal(snapshot.blocks.filter(block => block.record.node === assigned).length, 1);
  assert.ok(snapshot.blocks.every(block => block.context === "Please read the complete guide carefully before using browser translation."));
  assert.ok(snapshot.blocks.every(block => !/fallback|Unslotted/u.test(block.text))); f.session.dispose();
});
test("a visible unassigned slot fallback remains eligible despite flatten:true returning fallback children", async () => {
  const host = new Element("X-FALLBACK"), shadow = host.attachShadow(), fallback = new Text("The visible default fallback paragraph still needs accurate automatic translation.");
  const slot = new Element("SLOT", [fallback]); slot.assignedNodes = options => options?.flatten ? [fallback] : [];
  shadow.append(new Element("P", [slot])); const f = fixture([host]), snapshot = await extractPageSnapshot(f.environment);
  assert.deepEqual(snapshot.blocks.map(block => block.text), [fallback.nodeValue]); f.session.dispose();
});
test("slotchange retires previously readable fallback when its new assignment is numeric", async () => {
  const host = new Element("X-ASSIGN"), shadow = host.attachShadow(), fallback = new Text("The original fallback English paragraph is visible until the assigned live value replaces it.");
  const live = new Text("42"), slot = new Element("SLOT", [fallback]); host.append(live); let assigned = [];
  slot.assignedNodes = options => assigned.length ? assigned : options?.flatten ? [fallback] : [];
  shadow.append(new Element("P", [slot])); const f = fixture([host]); await enable(f); const old = f.candidate(); finish(f);
  const revision = f.session.inspect().revision; assigned = [live]; live.assignedSlot = slot;
  f.session.mutated([{ type: "slotchange", target: slot }]); await tick();
  assert.ok(f.session.inspect().revision > revision); assert.equal(fallback.nodeValue, "The original fallback English paragraph is visible until the assigned live value replaces it.");
  assert.equal(live.nodeValue, "42"); assert.equal(f.reply("pageAutoTranslations", { updates: old.blocks.map(block => ({ id: block.id, text: "旧译文", complete: true })), finished: true }, old), false); f.session.dispose();
});
test("shadow ancestry correctly excludes navigation from page language statistics", async () => {
  const host = new Element("X-NAV"), shadow = host.attachShadow(); shadow.append(p("首页 登录 消息 帮助 中文导航不应该阻止正文翻译。").element);
  const nav = new Element("NAV", [host]), good = p("An ordinary foreign English article has sufficiently descriptive body prose to translate.");
  const f = fixture([nav, good.element]), snapshot = await extractPageSnapshot(f.environment);
  assert.equal(snapshot.pageSummary.hanProseCharacters, 0); assert.ok(snapshot.samples.some(sample => sample.kind === "navigation")); f.session.dispose();
});
test("open-root text mutations cancel old identity and dynamic attachShadow discovery finds new loaded paragraphs", async () => {
  const light = p("An existing English body paragraph provides reliable original language evidence.");
  const host = new Element("X-LATE"), f = fixture([light.element, host]); await enable(f); const old = f.candidate(); finish(f);
  const shadow = host.attachShadow(), dynamic = p("A dynamically attached open shadow paragraph is loaded after the page has already translated."); shadow.append(dynamic.element);
  await f.session.discoverOpenRoots(); await tick(); assert.equal(f.rootLists.at(-1).size, 1);
  assert.ok(f.candidate().blocks.some(block => block.text === dynamic.node.nodeValue)); assert.notEqual(f.candidate().revision, old.revision);
  finish(f); dynamic.node.nodeValue = "The component replaced its original English text and requires a fresh safe translation.";
  f.session.mutated([{ type: "characterData", target: dynamic.node }]); await tick();
  assert.ok(f.candidate().blocks.some(block => block.text.startsWith("The component replaced")));
  assert.equal(f.reply("pageAutoTranslations", { updates: old.blocks.map(block => ({ id: block.id, text: "晚到旧译文", complete: true })), finished: true }, old), false); f.session.dispose();
});
test("open root observer detaches removed roots and releases all root references when paused disabled or disposed", () => {
  const document = {}, watched = new Set(); let disconnects = 0;
  const manager = createOpenRootObserver({ document, onMutations() {}, createObserver: () => ({ observe: tree => watched.add(tree), disconnect: () => { disconnects++; watched.clear(); } }) });
  const first = { mode: "open" }, second = { mode: "open" }, closed = { mode: "closed" };
  manager.configure(true); manager.found(first); manager.found(second); manager.found(closed);
  assert.equal(manager.inspect().rootCount, 2); assert.equal(watched.size, 3);
  manager.replace(new Set([second])); assert.equal(watched.has(first), false); assert.equal(watched.has(second), true);
  manager.configure(false); assert.equal(watched.size, 0); assert.equal(manager.inspect().rootCount, 0);
  manager.found(first); assert.equal(watched.size, 0); manager.configure(true); manager.found(first); manager.dispose();
  assert.equal(watched.size, 0); assert.ok(disconnects >= 4);
});
test("shadow scan yields when its main-thread time budget expires and respects cancellation", async () => {
  const host = new Element("X-LARGE"), shadow = host.attachShadow();
  for (let index = 0; index < 130; index++) shadow.append(p(`A meaningful English shadow paragraph ${index} explains the safe browser translation process.`).element);
  const f = fixture([host]); let yields = 0, clock = 0;
  f.environment.now = () => clock += 20;
  f.environment.yieldWork = async () => { yields++; };
  const snapshot = await extractPageSnapshot(f.environment); assert.equal(snapshot.blocks.length, 130); assert.ok(yields >= 1);
  let current = true;
  await assert.rejects(extractPageSnapshot({ ...f.environment, isCurrent: () => current,
    yieldWork: async () => { current = false; } }), /scan_cancelled/u);
  await assert.rejects(extractPageSnapshot({ ...f.environment, isCurrent: () => false }), /scan_cancelled/u); f.session.dispose();
});
test("rendered text proof rejects unrendered closed-host light children without rejecting public below-fold text", async () => {
  const closedHost = new Element("X-CLOSED"), closed = closedHost.attachShadow("closed"); closed.append(p("Private closed shadow contents are never inspected.").element);
  const hiddenLight = p("Private unassigned light text inside a closed host must never be uploaded."); hiddenLight.node.rendered = false; closedHost.append(hiddenLight.element);
  const visibleLight = p("An actually rendered public light DOM paragraph is readable even under a closed host."); visibleLight.node.rendered = true; closedHost.append(visibleLight.element);
  const bottom = p("A normal below-fold public English paragraph is still eligible for automatic translation."); bottom.node.rendered = true;
  const f = fixture([closedHost, bottom.element]); let selected;
  f.document.createRange = () => ({ selectNodeContents: node => { selected = node; }, getClientRects: () => selected.rendered ? [{ top: 9000, width: 600, height: 32 }] : [] });
  f.environment.isRenderedText = createRenderedTextTester(f.document);
  const snapshot = await extractPageSnapshot(f.environment);
  assert.deepEqual(snapshot.blocks.map(block => block.text), [visibleLight.node.nodeValue, bottom.node.nodeValue]);
  assert.equal(snapshot.openRoots.size, 0); assert.ok(snapshot.samples.every(sample => !sample.text.includes("Private"))); f.session.dispose();
});
test("bilingual shadow paragraphs preserve the public link and exclude their translated annotations from resampling", async () => {
  const host = new Element("X-BILINGUAL"), shadow = host.attachShadow(), prefix = new Text("Read the "), label = new Text("complete guide");
  const anchor = new Element("A", [new Element("STRONG", [label])], { href: "/guide" });
  const paragraph = new Element("P", [prefix, anchor, new Text(" carefully before translating a browser document.")]); shadow.append(paragraph);
  const f = fixture([host]); await enable(f); const candidate = f.candidate(); f.reply("pageAutoAssessment", { needsTranslation: true, allowImages: true });
  f.session.control("view-bilingual"); f.reply("pageAutoTranslations", { updates: candidate.blocks.map(block => ({ id: block.id, text: "中文片段", complete: true })), finished: true });
  assert.equal(paragraph.childNodes[1], anchor); assert.equal(label.nodeValue, "complete guide"); assert.equal(f.session.inspect().annotations.size, 1);
  const annotation = paragraph.childNodes.at(-1); assert.equal(annotation.childNodes[1].textContent, "中文片段 中文片段 中文片段");
  const snapshot = await extractPageSnapshot(f.environment, f.session.inspect().records);
  assert.equal(snapshot.pageSummary.hanCharacters, 0); assert.equal(snapshot.blocks.length, 3); assert.ok(snapshot.blocks.every(block => block.piece.complete));
  f.session.control("view-original"); assert.equal(paragraph.childNodes.length, 3); f.session.control("view-translation"); assert.equal(label.nodeValue, "中文片段");
  f.session.dispose(); assert.equal(label.nodeValue, "complete guide"); assert.equal(paragraph.childNodes.length, 3);
});
test("dynamic attachShadow keeps the previously translated light paragraph and queues only the newly loaded source", async () => {
  const light = p("An existing English paragraph already has an accepted translation before the custom element becomes visible.");
  const host = new Element("X-LATE-BILINGUAL"), f = fixture([light.element, host]); await enable(f); finish(f);
  const shadow = host.attachShadow(), fresh = p("A new public English shadow paragraph appears below the article during scrolling."); shadow.append(fresh.element);
  await f.session.discoverOpenRoots(); assert.equal(light.node.nodeValue, "中文译文"); await tick(); assert.equal(light.node.nodeValue, "中文译文");
  assert.deepEqual(f.candidate().blocks.map(block => block.text), [fresh.node.nodeValue]);
  finish(f); f.session.control("view-bilingual"); assert.equal(f.session.inspect().annotations.size, 2); assert.ok(light.node.nodeValue.startsWith("An existing"));
  f.session.control("pause"); assert.equal(f.session.inspect().annotations.size, 0); assert.ok(fresh.node.nodeValue.startsWith("A new public")); f.session.dispose();
});

test("shadow textContent replacement keeps an in-flight block bound to its true source and final output", async () => {
  const host = new Element("X-READER"), shadow = host.attachShadow(), item = p("An open shadow reader recreates the same English paragraph while its translation is streaming.");
  shadow.append(item.element); const f = fixture([host]); await enable(f); const old = f.candidate();
  f.reply("pageAutoAssessment", { needsTranslation: true, allowImages: true });
  f.reply("pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "流式前缀" }] });
  const revision = f.session.inspect().revision; item.node.isConnected = false; item.element.textContent = old.blocks[0].text;
  const next = item.element.childNodes[0]; f.session.mutated([{ type: "childList", target: item.element, removedNodes: [item.node], addedNodes: [next] }]);
  assert.equal(next.nodeValue, "流式前缀"); assert.equal(f.session.inspect().revision, revision);
  f.reply("pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "完整结果", complete: true }], finished: true }, old);
  assert.equal(next.nodeValue, "完整结果"); assert.equal(f.sent.filter(packet => packet.type === "pageAutoCandidate").length, 1);
  f.session.control("view-original"); assert.equal(next.nodeValue, old.blocks[0].text); f.session.dispose();
});

test("new shadow paragraph nodes replay a completed translation immediately without rejudgement", async () => {
  const host = new Element("X-VIRTUAL"), shadow = host.attachShadow(), source = "An open shadow virtual list restores an identical English paragraph after returning to it.", item = p(source);
  shadow.append(item.element); const f = fixture([host]); await enable(f); finish(f); item.node.isConnected = false;
  shadow.childNodes = []; f.session.mutated([{ type: "childList", target: shadow, removedNodes: [item.element] }]); await tick();
  assert.equal(f.session.inspect().records.size, 0);
  const replacement = p(source); shadow.append(replacement.element);
  f.session.mutated([{ type: "childList", target: shadow, addedNodes: [replacement.element] }]);
  assert.equal(replacement.node.nodeValue, "中文译文"); await tick(); assert.equal(f.sent.filter(packet => packet.type === "pageAutoCandidate").length, 1);
  f.session.control("view-bilingual"); assert.equal(replacement.node.nodeValue, source); assert.equal(f.session.inspect().annotations.size, 1);
  assert.equal(replacement.element.childNodes.at(-1).childNodes[1].textContent, "中文译文"); f.session.dispose();
});
