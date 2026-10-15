"use strict";
const test = require("node:test"), assert = require("node:assert/strict");
const { createPageAutoSession, canonicalSource } = require("../page-auto-content.js");
const { createHash } = require("node:crypto");

// Independent offline DOM and scheduler. Writes are recorded, including any
// transient restoration; a final Chinese value alone cannot prove retention.
class Text {
  constructor(source) { this.nodeType = 3; this.value = source; this.writes = []; this.parentElement = null; this.isConnected = true; }
  get nodeValue() { return this.value; }
  set nodeValue(value) { this.writes.push(value); this.value = value; }
  get textContent() { return this.nodeValue; }
}
class Element {
  constructor(tagName, children = [], attributes = {}) {
    Object.assign(this, { nodeType: 1, tagName, childNodes: [], attributes, style: {}, parentElement: null, top: 0 });
    this.append(...children);
  }
  append(...children) { for (const node of children) { node.parentElement = this; this.childNodes.push(node); } }
  getAttribute(name) { return this.attributes[name] ?? null; }
  hasAttribute(name) { return Object.hasOwn(this.attributes, name); }
  setAttribute(name, value) { this.attributes[name] = value; }
  get textContent() { return this.childNodes.map(node => node.textContent).join(""); }
  set textContent(value) { this.childNodes = []; this.append(new Text(value)); }
  remove() { this.parentElement.childNodes = this.parentElement.childNodes.filter(node => node !== this); this.parentElement = null; }
  getBoundingClientRect() { return { top: this.top, bottom: this.top + 40 }; }
}
const t = value => new Text(value), e = (tag, ...children) => new Element(tag, children);
function fixture(children, options = {}) {
  const sent = [], scheduled = new Map(), body = new Element("BODY", children); let sequence = 0;
  const document = { body, title: "The authored document title", visibilityState: "visible", documentElement: { lang: "en" },
    createElement: tag => new Element(tag.toUpperCase()) };
  const environment = { document, token: "retention-document", makeId: () => `retention-${++sequence}`,
    getStyle: element => element.style, isRenderedText: node => node.rendered !== false,
    send: async packet => { sent.push(packet); return { accepted: true }; },
    schedule: (callback, delay) => { const id = ++sequence; scheduled.set(id, { callback, delay }); return id; },
    clearSchedule: id => scheduled.delete(id), ...options };
  const session = createPageAutoSession(environment);
  async function flush() {
    for (let turn = 0; scheduled.size; turn++) {
      assert.ok(turn < 30, "bounded offline scans must settle");
      const [id, task] = scheduled.entries().next().value; scheduled.delete(id); await task.callback();
    }
  }
  const candidates = () => sent.filter(packet => packet.type === "pageAutoCandidate");
  function reply(packet, type, extra) {
    return session.receive({ type, requestId: packet.requestId, documentToken: packet.documentToken,
      generation: packet.generation, revision: packet.revision, ...extra });
  }
  async function start() { session.configure({ generation: 1, enabled: true, active: true, targetLanguage: "简体中文" }); await flush(); }
  function finish(packet = candidates().at(-1)) {
    assert.ok(packet, "fixture must exercise the production request path");
    reply(packet, "pageAutoAssessment", { needsTranslation: true, allowImages: true });
    return reply(packet, "pageAutoTranslations", { updates: packet.blocks.map((block, index) =>
      ({ id: block.id, text: `完整译文${index}。`, complete: true })), finished: true });
  }
  async function layout() { session.layoutChanged(); await flush(); }
  return { session, document, environment, body, sent, candidates, reply, start, finish, flush, layout };
}
function metadata(f) {
  for (const block of f.session.inspect().blocks.values()) {
    const raw = block.record.groupText, source = canonicalSource(raw).text;
    assert.equal(block.sourceGroupHash, createHash("sha256").update(source).digest("hex"));
    assert.equal(block.sourceGroupLength, source.length);
    assert.equal(source.slice(block.sourceStart, block.sourceStart + block.sourceLength), block.text);
    assert.equal(block.record.pieces.map(piece => piece.source).join(""), block.record.source);
  }
}
function resetWrites(nodes) { for (const node of nodes) node.writes.length = 0; }

test("first layout scan retains completed prose across ordinary collapsed inline separators and moving geometry", async () => {
  for (const separator of [" ", "\t  ", "\n"]) {
    const left = t("Read the complete research"), gap = t(separator), right = t("report before making a decision.");
    const p = e("P", left, e("SPAN", gap), e("STRONG", right)), f = fixture([p]);
    try {
      await f.start(); const old = f.candidates()[0]; f.finish(); const display = [left.nodeValue, right.nodeValue]; resetWrites([left, right]);
      for (const rendered of [false, true, false, true]) {
        gap.rendered = rendered; p.top = rendered ? 5000 : -200; await f.layout();
        assert.deepEqual([left.nodeValue, right.nodeValue], display);
        assert.deepEqual([left.writes, right.writes], [[], []], "no intermediate source withdrawal"); metadata(f);
      }
      assert.equal(f.candidates().length, 1); assert.equal(f.session.inspect().pending, 0);
      assert.equal(f.reply(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "过期回复", complete: true }], finished: true }), false);
      f.session.control("view-original"); assert.equal(p.textContent, "Read the complete research" + separator + "report before making a decision.");
      assert.equal(left.nodeValue, "Read the complete research"); assert.equal(right.nodeValue, "report before making a decision.");
    } finally { f.session.dispose(); }
  }
});

test("completed citations and short article headings survive hidden and revealed substantive members", async () => {
  for (const mode of ["range", "display", "visibility", "contentVisibility", "opacity"]) {
    const author = t("Rivera (2021). "), title = t("Research methods"), tail = t(". Revised edition, 2024."), heading = t("Overview");
    const inline = e("I", title), link = new Element("A", [inline], { href: "/paper" });
    const row = e("LI", author, link, tail), h1 = e("H1", heading), f = fixture([h1, row]);
    try {
      await f.start(); f.finish(); const display = [author.nodeValue, title.nodeValue, tail.nodeValue, heading.nodeValue];
      resetWrites([author, title, tail, heading]);
      for (const hidden of [true, false, true, false]) {
        if (mode === "range") title.rendered = !hidden;
        else inline.style[mode] = hidden ? ({ display: "none", visibility: "hidden", contentVisibility: "hidden", opacity: "0" })[mode] : "";
        f.session.mutated([{ type: "attributes", target: inline, attributeName: "style" }]); await f.flush(); await f.layout();
        assert.deepEqual([author.nodeValue, title.nodeValue, tail.nodeValue, heading.nodeValue], display, mode);
        assert.ok([author, title, tail, heading].every(node => node.writes.length === 0), mode); metadata(f);
      }
      assert.equal(f.candidates().length, 1); assert.equal(f.document.title, "The authored document title");
      f.session.control("view-original"); assert.equal(row.textContent, "Rivera (2021). Research methods. Revised edition, 2024.");
    } finally { f.session.dispose(); }
  }
});

test("revealing an originally hidden authored member retains finals and sends only newly readable source", async () => {
  const first = t("The public report describes an extensive research programme. "), later = t("Additional background explains the experimental design.");
  const span = e("SPAN", later); span.style.display = "none"; const f = fixture([e("P", first, span)]);
  try {
    await f.start(); assert.equal(f.candidates()[0].blocks.some(block => block.text.includes("Additional")), false); f.finish();
    const display = first.nodeValue; resetWrites([first]); span.style.display = "inline";
    f.session.mutated([{ type: "attributes", target: span, attributeName: "style" }]); await f.flush();
    assert.equal(first.nodeValue, display); assert.deepEqual(first.writes, []);
    assert.deepEqual(f.candidates()[1].blocks.map(block => block.text), [later.nodeValue]); metadata(f);
    f.finish(); assert.equal(first.nodeValue, display);
  } finally { f.session.dispose(); }
});

test("changed or removed source cannot retain a completed neighbour, including an unpainted fact", async () => {
  for (const change of ["prose", "numeric", "calendar", "remove", "hidden-fact"]) {
    const first = t("The published report records "), fact = t("12 experiments on May 8, 2024"), tail = t(" and explains the results.");
    if (change === "hidden-fact") fact.rendered = false;
    const p = e("P", first, e("SPAN", fact), tail), f = fixture([p]);
    try {
      await f.start(); const old = f.candidates()[0]; f.finish(); resetWrites([first]);
      if (change === "remove") { fact.isConnected = false; p.childNodes.splice(1, 1); }
      else fact.nodeValue = change === "prose" ? "a wholly different conclusion" : change === "calendar" ? "12 experiments on May 9, 2024" : "13 experiments on May 8, 2024";
      await f.layout();
      assert.equal(first.nodeValue, "The published report records ", change);
      assert.ok(first.writes.includes(first.nodeValue), change);
      assert.equal(f.reply(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "过期事实", complete: true }], finished: true }), false);
    } finally { f.session.dispose(); }
  }
});

test("whole unchanged groups rebind every member through replacement and regrouping while a member is hidden", async () => {
  for (const operation of ["text-replacement", "whole-replacement", "regroup"]) {
    const first = t("The research collection includes "), second = t("several translated publications."), span = e("SPAN", second);
    const p = e("P", first, span), holder = e("ARTICLE", p), f = fixture([holder]);
    try {
      await f.start(); f.finish(); const display = [first.nodeValue, second.nodeValue]; second.rendered = false; await f.layout();
      let nextFirst = first, nextSecond = second, next = p;
      if (operation === "text-replacement") {
        nextFirst = t("The research collection includes "); nextSecond = t("several translated publications."); nextSecond.rendered = false;
        first.isConnected = second.isConnected = false; const old = p.childNodes; p.childNodes = []; p.append(nextFirst, e("SPAN", nextSecond));
        f.session.mutated([{ type: "childList", target: p, removedNodes: old, addedNodes: p.childNodes }]);
      } else {
        if (operation === "regroup") p.childNodes = [];
        if (operation === "whole-replacement") {
          first.isConnected = second.isConnected = false; nextFirst = t("The research collection includes "); nextSecond = t("several translated publications."); nextSecond.rendered = false;
        }
        next = e("P", nextFirst, e("SPAN", nextSecond)); holder.childNodes = []; holder.append(next);
        f.session.mutated([{ type: "childList", target: holder, removedNodes: [p], addedNodes: [next] }]);
      }
      assert.equal(nextFirst.nodeValue, display[0], "the visible owner must rebind before returning from the mutation observer");
      await f.flush(); nextSecond.rendered = true; await f.layout();
      assert.deepEqual([nextFirst.nodeValue, nextSecond.nodeValue], display, operation); assert.equal(f.candidates().length, 1, operation);
      f.session.control("view-original"); assert.equal(next.textContent, "The research collection includes several translated publications.");
    } finally { f.session.dispose(); }
  }
});

test("original and pause remain authoritative after member visibility changes", async () => {
  for (const action of ["view-original", "pause"]) {
    const first = t("The source document provides "), last = t("a full explanation of the results."), f = fixture([e("P", first, e("SPAN", last))]);
    try {
      await f.start(); const old = f.candidates()[0]; f.finish(); last.rendered = false; await f.layout();
      f.session.control(action); last.rendered = true; await f.layout();
      assert.equal(first.nodeValue, "The source document provides "); assert.equal(last.nodeValue, "a full explanation of the results.");
      assert.equal(f.reply(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "迟到", complete: true }], finished: true }), false);
    } finally { f.session.dispose(); }
  }
});

test("hiding the first member preserves authored order through later whole-group rebinding", async () => {
  const first = t("The opening passage supplies context. "), middle = t("The central passage describes the evidence. "), last = t("The conclusion gives the result.");
  const p = e("P", first, e("SPAN", middle), last), f = fixture([p]);
  try {
    await f.start(); f.finish(); const display = [first.nodeValue, middle.nodeValue, last.nodeValue];
    first.rendered = middle.rendered = false; await f.layout();
    const replacements = [t("The opening passage supplies context. "), t("The central passage describes the evidence. "), t("The conclusion gives the result.")];
    replacements[0].rendered = replacements[1].rendered = false;
    const removed = p.childNodes; for (const node of [first, middle, last]) node.isConnected = false;
    p.childNodes = []; p.append(replacements[0], e("SPAN", replacements[1]), replacements[2]);
    f.session.mutated([{ type: "childList", target: p, removedNodes: removed, addedNodes: p.childNodes }]); await f.flush();
    replacements[0].rendered = replacements[1].rendered = true; await f.layout();
    assert.deepEqual(replacements.map(node => node.nodeValue), display); assert.equal(f.candidates().length, 1); metadata(f);
  } finally { f.session.dispose(); }
});

test("an unchanged source repaint during partial visibility reuses its exact authored group synchronously", async () => {
  const first = t("The publication provides detailed methods. "), last = t("The supplementary section lists the results."), p = e("P", first, e("SPAN", last)), f = fixture([p]);
  try {
    await f.start(); f.finish(); const display = first.nodeValue; last.rendered = false; await f.layout();
    first.nodeValue = "The publication provides detailed methods. "; first.writes.length = 0;
    f.session.mutated([{ type: "characterData", target: first }]);
    assert.equal(first.nodeValue, display); assert.deepEqual(first.writes, [display]); await f.flush();
    assert.equal(f.candidates().length, 1);
  } finally { f.session.dispose(); }
});

test("a visible subset removed from a partly hidden group is not a whole unchanged replacement", async () => {
  const first = t("The report evaluates the available research. "), last = t("Its conclusion includes 17 independent trials."), p = e("P", first, e("SPAN", last)), f = fixture([p]);
  try {
    await f.start(); f.finish(); last.rendered = false; await f.layout();
    first.isConnected = false; p.childNodes.shift();
    const replacement = t("The report evaluates the available research. "), next = e("P", replacement); f.body.append(next);
    f.session.mutated([{ type: "childList", target: f.body, removedNodes: [first], addedNodes: [next] }]);
    assert.equal(replacement.nodeValue, "The report evaluates the available research. ");
    await f.flush(); assert.equal(replacement.nodeValue, "The report evaluates the available research. ");
    assert.equal(last.nodeValue, "Its conclusion includes 17 independent trials.");
  } finally { f.session.dispose(); }
});

test("layout-only retention preserves an actual failed sibling without retracting its successful neighbour", async () => {
  const first = t("The successful paragraph fragment stays translated. "), last = t("This fragment has an actual provider failure."), f = fixture([e("P", first, e("SPAN", last))]);
  try {
    await f.start(); const packet = f.candidates()[0]; f.reply(packet, "pageAutoAssessment", { needsTranslation: true });
    f.reply(packet, "pageAutoTranslations", { updates: [{ id: packet.blocks[0].id, text: "成功片段。", complete: true },
      { id: packet.blocks[1].id, failed: true }], finished: true });
    const display = first.nodeValue; first.writes.length = 0; last.rendered = false; await f.layout(); last.rendered = true; await f.layout();
    assert.equal(first.nodeValue, display); assert.deepEqual(first.writes, []);
    const failed = f.session.inspect().records.get(last).pieces[0]; assert.equal(failed.failed, true); assert.equal(failed.value, failed.source);
    assert.equal(f.candidates().length, 1);
  } finally { f.session.dispose(); }
});

test("slot reassignment and link-owner reassignment cannot reuse finals from the prior member authority", async () => {
  for (const change of ["slot-layout", "slot-replacement", "link"]) {
    const first = t("The reference explains "), label = t("the public research findings"), last = t(" in the latest edition.");
    const link = new Element("A", [label], { href: "/findings" }), firstSlot = e("SLOT"), secondSlot = e("SLOT"); let slot = firstSlot;
    firstSlot.assignedNodes = () => slot === firstSlot ? [link] : [];
    secondSlot.assignedNodes = () => slot === secondSlot ? [link] : [];
    const p = change === "link" ? e("P", first, link, last) : e("P", first, firstSlot, secondSlot, last);
    if (change !== "link") link.assignedSlot = firstSlot;
    const f = fixture([p]);
    try {
      await f.start(); const old = f.candidates()[0]; f.finish();
      if (change === "link") {
        const nextLink = new Element("A", [label], { href: "/findings" }); link.childNodes = []; p.childNodes.splice(1, 1, nextLink); nextLink.parentElement = p;
      } else { slot = secondSlot; link.assignedSlot = secondSlot; }
      if (change === "slot-replacement") {
        const replacement = t("the public research findings"); label.isConnected = false; link.childNodes = []; link.append(replacement);
        f.session.mutated([{ type: "childList", target: link, removedNodes: [label], addedNodes: [replacement] },
          { type: "slotchange", target: firstSlot }, { type: "slotchange", target: secondSlot }]); await f.flush();
      } else await f.layout();
      assert.equal(first.nodeValue, "The reference explains ", change);
      assert.equal(f.candidates().length, 2, change);
      assert.equal(f.reply(old, "pageAutoTranslations", { updates: old.blocks.map(block => ({ id: block.id, text: "旧归属", complete: true })), finished: true }), false);
    } finally { f.session.dispose(); }
  }
});

test("deferred writers lose authority after source invalidation, source replacement and explicit original or pause", async () => {
  for (const action of ["changed-source", "replacement", "view-original", "pause"]) {
    const pending = [], cancelled = [], motion = { clearText() {}, softReveal() {}, textBegin() {}, textEnd() {},
      writer: { cancel: node => cancelled.push(node), write: (node, text, options) => { pending.push({ node, text, options }); return true; } } };
    const first = t("The unchanged source is displayed while a motion writer awaits its next frame."), p = e("P", first), f = fixture([p], { motion });
    try {
      await f.start(); const old = f.candidates()[0]; f.reply(old, "pageAutoAssessment", { needsTranslation: true });
      f.reply(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "待写入前缀" }] });
      const writer = pending.at(-1); assert.equal(writer.options.current(), true);
      if (action === "changed-source") {
        first.nodeValue = "A substantive new authored paragraph supplies a different result.";
        f.session.mutated([{ type: "characterData", target: first }]);
      } else if (action === "replacement") {
        first.isConnected = false; const replacement = t(first.nodeValue); p.childNodes = []; p.append(replacement);
        f.session.mutated([{ type: "childList", target: p, removedNodes: [first], addedNodes: [replacement] }]);
      } else f.session.control(action);
      const display = p.textContent;
      assert.equal(writer.options.current(), false, action); assert.equal(writer.options.commit(writer.text), false, action);
      assert.ok(cancelled.length > 0, action);
      assert.equal(p.textContent, display, "a retired writer cannot alter the current display");
    } finally { f.session.dispose(); }
  }
});

test("heading translations retain exact source and provenance when a trailing space-newline Text range disappears and returns", async () => {
  for (const tag of ["H1", "H2", "H3"]) for (const initiallyRendered of [true, false]) {
    const source = "Research highlights", heading = t(source), trailing = t(" \n"), element = e(tag, heading, trailing);
    trailing.rendered = initiallyRendered;
    const f = fixture([element, e("P", t("The public report describes the research programme and its supporting evidence."))]);
    try {
      await f.start(); const packet = f.candidates()[0];
      const headingBlock = packet.blocks.find(block => block.text === source); assert.ok(headingBlock);
      f.reply(packet, "pageAutoAssessment", { needsTranslation: true });
      f.reply(packet, "pageAutoTranslations", { updates: packet.blocks.map(block =>
        ({ id: block.id, text: block === headingBlock ? "研究亮点" : "报告介绍研究计划及其证据。", complete: true })), finished: true });
      assert.equal(heading.nodeValue, "研究亮点"); resetWrites([heading, trailing]);
      // The first scan is the screenshot-time geometry change, before any
      // explicit reflow or authored DOM mutation. The separator is a separate
      // trailing Text child, rather than an internal horizontal inline gap.
      for (const rendered of [!initiallyRendered, initiallyRendered, !initiallyRendered, initiallyRendered]) {
        trailing.rendered = rendered; element.top = rendered ? 2400 : -100;
        await f.layout();
        assert.equal(heading.nodeValue, "研究亮点"); assert.equal(trailing.nodeValue, " \n");
        assert.deepEqual([heading.writes, trailing.writes], [[], []], "no transient original heading write");
        const record = f.session.inspect().records.get(heading);
        assert.equal(record.source, source); assert.equal(record.groupElement, element);
        assert.equal(record.groupText, source + (rendered ? " \n" : ""));
        assert.deepEqual(record.authoredMembers.map(member => [member.node, member.source, member.sourceLink, [...member.renderSlots]]),
          [[heading, source, null, []], [trailing, " \n", null, []]]);
        const block = [...f.session.inspect().blocks.values()].find(block => block.record.node === heading);
        assert.equal(block.id, headingBlock.id); assert.equal(block.piece.complete, true);
        for (const key of ["sourceGroupHash", "sourceGroupLength", "sourceStart", "sourceLength"])
          assert.equal(block[key], headingBlock[key], key);
        assert.equal(f.document.title, "The authored document title"); metadata(f);
      }
      assert.equal(f.candidates().length, 1); assert.equal(f.session.inspect().pending, 0);
      f.session.control("view-original"); assert.equal(element.textContent, source + " \n");
    } finally { f.session.dispose(); }
  }
});

test("the default session scheduler honors layout and retry delays and cancels pending timers on disposal", async context => {
  context.mock.timers.enable({ apis: ["setTimeout"] });
  const nativeSetTimeout = globalThis.setTimeout, fired = [];
  const timers = context.mock.method(globalThis, "setTimeout", (callback, delay, ...args) =>
    nativeSetTimeout((...values) => { fired.push(delay); return callback(...values); }, delay, ...args));
  let styleReads = 0;
  // Undefined explicitly selects createPageAutoSession's actual defaults.
  // Only platform timers are mocked; no environment scheduler is supplied.
  const node = t("The report explains how the research programme evaluates its evidence."),
    f = fixture([e("P", node)], { schedule: undefined, clearSchedule: undefined,
      getStyle: element => { styleReads++; return element.style; } });
  async function settle() {
    for (let turn = 0; f.session.inspect().scanning; turn++) {
      assert.ok(turn < 1000, "the default-scheduled scan must settle");
      await new Promise(resolve => setImmediate(resolve));
    }
    await Promise.resolve();
  }
  try {
    f.session.configure({ generation: 1, enabled: true, active: true, targetLanguage: "简体中文" });
    assert.equal(timers.mock.calls.at(-1).arguments[1], 0);
    context.mock.timers.tick(0); await settle(); f.finish(); resetWrites([node]);

    const beforeLayout = styleReads;
    f.session.layoutChanged(); assert.equal(timers.mock.calls.at(-1).arguments[1], 120);
    context.mock.timers.tick(119); await settle(); assert.equal(styleReads, beforeLayout);
    context.mock.timers.tick(1); await settle(); assert.ok(styleReads > beforeLayout);
    assert.equal(f.candidates().length, 1); assert.deepEqual(node.writes, []);

    f.session.layoutChanged(); f.session.control("pause");
    const afterPause = { reads: styleReads, packets: f.sent.length, fired: fired.length, writes: node.writes.length };
    context.mock.timers.tick(120); await settle();
    assert.deepEqual({ reads: styleReads, packets: f.sent.length, fired: fired.length, writes: node.writes.length }, afterPause,
      "pause cancels the pending layout callback as well as its authority");
    f.session.control("resume"); assert.equal(timers.mock.calls.at(-1).arguments[1], 0);
    context.mock.timers.tick(0); await settle();

    f.session.layoutChanged(); assert.equal(timers.mock.calls.at(-1).arguments[1], 120);
    f.session.dispose();
    const afterDispose = { reads: styleReads, packets: f.sent.length, fired: fired.length, writes: node.writes.length };
    context.mock.timers.tick(12000); await settle();
    assert.deepEqual({ reads: styleReads, packets: f.sent.length, fired: fired.length, writes: node.writes.length }, afterDispose,
      "cancelled default timers must not invoke phantom callbacks or work");

    const retry = fixture([e("P", t("A separate research report supplies enough authored prose for assessment."))],
      { schedule: undefined, clearSchedule: undefined, send: async () => ({ accepted: false, reason: "desktop_busy" }) });
    try {
      retry.session.configure({ generation: 1, enabled: true, active: true, targetLanguage: "简体中文" });
      context.mock.timers.tick(0);
      for (let turn = 0; retry.session.inspect().scanning; turn++) {
        assert.ok(turn < 1000, "the rejected assessment scan must settle");
        await new Promise(resolve => setImmediate(resolve));
      }
      await Promise.resolve();
      assert.equal(timers.mock.calls.at(-1).arguments[1], 1000, "busy retries must retain their requested backoff");
      const beforeRetry = fired.length;
      context.mock.timers.tick(999); await Promise.resolve(); assert.equal(fired.length, beforeRetry);
      retry.session.dispose(); const afterCancel = fired.length;
      context.mock.timers.tick(12000); await Promise.resolve(); assert.equal(fired.length, afterCancel);
    } finally { retry.session.dispose(); }
  } finally { f.session.dispose(); }
});
