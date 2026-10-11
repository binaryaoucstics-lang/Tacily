"use strict";
const test = require("node:test"), assert = require("node:assert/strict");
const { extractPageSnapshot, createPageAutoSession, selectedTextSlices, canonicalSource, createRenderedTextTester, createOpenRootObserver, pageRouteKey } = require("../page-auto-content.js");
const fixtures = new Set();
test("a minority Chinese inline fragment is judged with its full foreign paragraph rather than in isolation", async () => {
  const chinese = new Text("中文介绍"), foreign = new Text(" explains the new image translation feature and how readers can use the browser toolbar.");
  const tweet = new Element("DIV", [new Element("STRONG", [chinese]), new Element("SPAN", [foreign])], {"data-testid":"tweetText",lang:"en"});
  const f = fixture([tweet]); f.document.location={href:"https://x.com/example/status/123"}; await enabled(f);
  assert.deepEqual(f.candidate().blocks.map(block=>block.text), ["中文介绍", foreign.nodeValue.trim()]);
  assert.ok(f.candidate().blocks.every(block=>block.context.includes("中文介绍"))); f.session.dispose();
});
test("a multilingual tweet retains its Chinese lines without sending them in text, context or samples",async()=>{
  const chinese="这是中文介绍 OpenAI API，保持这一行的原文。",japanese="今日は新しい作品を公開しました。皆さんに楽しんでいただけると嬉しいです！";
  const node=new Text(chinese+"\n"+japanese),tweet=new Element("DIV",[new Element("SPAN",[node])],{"data-testid":"tweetText",lang:"ja"}),f=fixture([tweet]);
  f.document.location={href:"https://x.com/home"};await enabled(f);const packet=f.candidate();assert.ok(packet);
  assert.deepEqual(packet.blocks.map(b=>b.text),[japanese]);assert.ok(packet.blocks.every(b=>!b.context.includes("中文介绍")));
  assert.ok(packet.samples.every(s=>!s.text.includes("中文介绍")));approved(f);finish(f,packet,"新作品已发布，希望大家喜欢！");
  assert.equal(node.nodeValue,chinese+"\n新作品已发布，希望大家喜欢！");f.session.layoutChanged();await tick();assert.equal(f.candidates().length,1);f.session.dispose();
});
test("X groups nested tweet spans, excludes Chinese bilingual paragraphs from requests and judgement evidence", async () => {
  const jp = new Element("DIV", [new Element("SPAN", [new Text("今日は新しい作品を公開しました。")]),
    new Element("DIV", [new Text("皆さんに楽しんでいただけると嬉しいです！")])], { "data-testid": "tweetText", lang: "ja" });
  const chinese = new Element("DIV", [new Text("这是中文版介绍 OpenAI and Claude API，以及其他产品的新功能。这些内容是已经可以直接阅读的中文说明。")], { "data-testid": "tweetText", lang: "zh" });
  const f = fixture([new Element("ARTICLE", [jp]), new Element("ARTICLE", [chinese])]);
  f.document.location = { href: "https://x.com/example/status/123" }; await enabled(f);
  const candidate = f.candidate(); assert.ok(candidate); assert.equal(new Set(candidate.blocks.map(b => b.groupId)).size, 1);
  assert.ok(candidate.blocks.every(b => b.context.includes("今日は") && !b.context.includes("中文版")));
  assert.equal(candidate.samples.some(s => s.text.includes("中文版")), false); assert.equal(candidate.pageSummary.hanProseCharacters, 0);
  approved(f); finish(f, candidate, "今天发布了新作品。"); assert.equal(chinese.textContent, "这是中文版介绍 OpenAI and Claude API，以及其他产品的新功能。这些内容是已经可以直接阅读的中文说明。");
  const node = jp.childNodes[0].childNodes[0]; node.nodeValue = "今日は新しい作品を公開しました。";
  f.session.mutated([{type:"characterData",target:node}]); await tick(); assert.equal(jp.textContent,"今天发布了新作品。");
  assert.equal(f.candidates().length,1); f.session.dispose();
});
test("X mixed-language additions never retract already translated Japanese posts", async () => {
  const jp = paragraph("今日は散歩に出かけました。新しいカメラで美しい写真を撮りました。皆さんにも見ていただきたいです。");
  const f = fixture([jp.element]); f.document.location={href:"https://x.com/home"}; await enabled(f); approved(f); finish(f);
  const mixed=paragraph("今天发布 new products and feature updates，欢迎来看看我们最新的功能。这里主要是已经可以直接阅读的中文说明。".repeat(3));
  f.body.append(mixed.element); f.session.mutated([{type:"childList",target:f.body,addedNodes:[mixed.element],removedNodes:[]}]); await tick();
  assert.equal(jp.node.nodeValue,"中文译文"); assert.equal(f.candidates().length,1); assert.equal(mixed.node.nodeValue.startsWith("今天发布"),true); f.session.dispose();
});
test("moving an unchanged translated tweet does not retire its paragraph or submit it again",async()=>{
  const jp=new Element("DIV",[new Text("今日は新しい漫画を公開しました。皆さんに読んでいただけると嬉しいです！")],{"data-testid":"tweetText",lang:"ja"});
  const article=new Element("ARTICLE",[jp]), f=fixture([article]); f.document.location={href:"https://x.com/home"}; await enabled(f); approved(f); finish(f);
  article.childNodes=[]; f.body.append(jp); f.session.mutated([{type:"childList",target:article,addedNodes:[],removedNodes:[jp]},
    {type:"childList",target:f.body,addedNodes:[jp],removedNodes:[]}]); await tick();
  assert.equal(jp.textContent,"中文译文"); assert.equal(f.candidates().length,1); f.session.dispose();
});
test("X completed plain tweet replays its translation when React changes inline text boundaries",async()=>{
  const source="今日は新しい漫画を公開しました。皆さんに読んでいただけると嬉しいです！";
  const jp=new Element("DIV",[new Element("SPAN",[new Text(source.slice(0,14))]),new Element("SPAN",[new Text(source.slice(14))])],{"data-testid":"tweetText",lang:"ja"});
  const f=fixture([new Element("ARTICLE",[jp])]);f.document.location={href:"https://x.com/home"};await enabled(f);approved(f);finish(f,undefined,"新漫画已发布，希望大家喜欢！");
  assert.equal(jp.textContent,"新漫画已发布，希望大家喜欢！");
  const old=jp.childNodes;for(const span of old)span.childNodes[0].isConnected=false;jp.textContent=source;
  f.session.mutated([{type:"childList",target:jp,removedNodes:old,addedNodes:jp.childNodes}]);await tick();
  assert.equal(jp.textContent,"新漫画已发布，希望大家喜欢！");assert.equal(f.candidates().length,1);
  jp.childNodes[0].nodeValue=source.replace("今日","明日");f.session.mutated([{type:"characterData",target:jp.childNodes[0]}]);await tick();
  assert.equal(f.candidates().length,2);assert.ok(f.candidate().blocks[0].text.includes("明日"));f.session.dispose();
});
test("manual picker chooses an image without cancelling a text batch, reports completion and rejects stale status",async()=>{
  const item=paragraph("This paragraph remains translating while a selected photo gets its own manual operation."),f=fixture([item.element]);
  await manualReady(f,true); const text=f.candidate(); approved(f); f.environment.pickImageAtPoint=()=>({imageId:"img-1",documentToken:"document-token"});
  f.session.control("manual-pick"); assert.equal(f.session.pickContent(60,70),true); await tick();
  const image=f.sent.find(p=>p.type==="manualImageCandidate"); assert.ok(image); assert.equal(image.previewBase64,undefined);
  assert.equal(f.sent.some(p=>p.type==="pageAutoCancel"),false); assert.equal(f.statuses.at(-1).state,"image_translating");
  assert.equal(f.session.receive({...image,type:"manualImageStatus",state:"completed",generation:99}),false);
  finish(f,text,"正文译文"); assert.equal(item.node.nodeValue,"正文译文"); assert.equal(f.statuses.at(-1).state,"image_translating");
  assert.equal(f.session.receive({...image,type:"manualImageStatus",state:"completed"}),true); assert.equal(f.statuses.at(-1).state,"image_completed"); f.session.dispose();
});
test("manual image rejection exits busy state and a disposed picker ignores late replies",async()=>{
  let answer; const f=fixture([],{send:packet=>new Promise(resolve=>{answer=resolve;})}); await manualReady(f); f.environment.pickImageAtPoint=()=>({imageId:"img-1",documentToken:"document-token"});
  f.session.control("manual-pick"); f.session.pickContent(1,1);
  answer({accepted:false,reason:"image_unavailable"}); await tick(); assert.equal(f.statuses.at(-1).state,"image_failed");
  f.session.control("manual-pick");f.session.pickContent(1,1);f.session.dispose();const count=f.statuses.length;answer({accepted:false});await tick();assert.equal(f.statuses.length,count);
});
async function tick() {
  await new Promise(resolve => setTimeout(resolve, 5));
  for (let at = 0; [...fixtures].some(f => f.session.inspect().scanning); at++) {
    if (at > 1500) throw new Error("source snapshot did not settle");
    await new Promise(resolve => setTimeout(resolve, 1));
  }
}
class Element {
  constructor(tagName, children = [], attributes = {}) {
    this.tagName = tagName; this.nodeType = 1; this.attributes = attributes; this.childNodes = [];
    this.style = {}; this.parentElement = null; this.hidden = false; this.top = 0;
    for (const child of children) this.append(child);
  }
  append(...children) { for (const child of children) { child.parentElement = this; this.childNodes.push(child); } return children[0]; }
  setAttribute(name, value) { this.attributes[name] = value; }
  remove() { if (this.parentElement) this.parentElement.childNodes = this.parentElement.childNodes.filter(child => child !== this); this.parentElement = null; }
  hasAttribute(name) { return Object.hasOwn(this.attributes, name); }
  getAttribute(name) { return this.attributes[name] ?? null; }
  get textContent() { return this.childNodes.map(child => child.textContent).join(""); }
  set textContent(value) { this.childNodes = []; this.append(new Text(value)); }
  getBoundingClientRect() { return { top: this.top, bottom: this.top + 60 }; }
}
class Text {
  constructor(value) { this.nodeType = 3; this.nodeValue = value; this.parentElement = null; this.isConnected = true; }
  get textContent() { return this.nodeValue; }
}
function fixture(children = [], options = {}) {
  const body = new Element("BODY", children), sent = [], statuses = [], decisions = [], views = []; let id = 0;
  const document = { body, visibilityState: "visible", documentElement: { lang: "en" },
    createElement: tag => new Element(tag.toUpperCase()), createTreeWalker: root => {
      const all = []; function visit(node) { if (node.nodeType === 3) all.push(node); else for (const child of node.childNodes) visit(child); } visit(root);
      let index = 0; return { nextNode: () => all[index++] || null };
    } };
  const environment = { document, token: "document-token", makeId: () => `id-${++id}`,
    getStyle: element => element.style, send: async message => { sent.push(message); return options.send ? options.send(message) : { accepted: true }; },
    onStatus: status => statuses.push(status), onDecision: decision => decisions.push(decision), onView: view => views.push(view), viewportHeight: 800 };
  const session = createPageAutoSession(environment);
  const result = { body, document, environment, session, sent, statuses, decisions, views,
    candidate: () => sent.filter(message => message.type === "pageAutoCandidate").at(-1),
    candidates: () => sent.filter(message => message.type === "pageAutoCandidate") };
  fixtures.add(result); return result;
}
function paragraph(text, top = 0) { const node = new Text(text), element = new Element("P", [node]); element.top = top; return { node, element }; }
test("letter animation translates styled phrases once and restores every authored span", async () => {
  const source = "One API for every LLM, built for production";
  const spans = [...source].map((letter, index) => { const span = new Element("SPAN", [new Text(letter)]);
    span.style = { color: index < 7 ? "navy" : index < 22 ? "black" : "blue", opacity: index > 2 ? "0" : "1" }; return span; });
  const h = new Element("H1", [new Element("SPAN", spans)]), body = paragraph("This introductory paragraph supplies the foreign-language page evidence."), f = fixture([h, body.element]);
  await enabled(f); await new Promise(resolve => setTimeout(resolve, 260)); await tick(); const candidate = f.candidate(), title = candidate.blocks.filter(block => block.context === source);
  assert.deepEqual(title.map(block => block.text), ["One API", "for every LLM,", "built for production"]);
  approved(f); finish(f, candidate, "译文完成"); assert.equal(h.textContent, "译文完成 译文完成 译文完成");
  for (const span of spans) span.style.opacity = "1";
  f.session.mutated(spans.map(target => ({ type: "attributes", target, attributeName: "style" }))); await tick();
  f.session.layoutChanged(); await tick(); assert.equal(f.candidates().length, 1); assert.equal(h.textContent, "译文完成 译文完成 译文完成");
  f.session.control("view-original"); assert.equal(h.textContent, source);
  f.session.control("view-translation"); assert.equal(h.textContent, "译文完成 译文完成 译文完成");
  assert.deepEqual(h.childNodes[0].childNodes, spans); f.session.dispose();
});
test("animated word spans are one sentence, while links and hidden containers remain protected", async () => {
  const words = "Route OpenAI and other providers through one compatible endpoint.".split(" ");
  const p = new Element("P", words.map(word => new Element("SPAN", [new Element("SPAN", [new Text(word)]), new Text(" ")])));
  const hidden = new Element("P", words.map(word => new Element("SPAN", [new Text(word)]))); hidden.style.opacity = "0";
  const linked = new Element("P", [...words.slice(0, 7).map(word => new Element("SPAN", [new Text(word + " ")])), new Element("A", [new Text("endpoint.")])]);
  const f = fixture([p, hidden, linked]); await enabled(f); await new Promise(resolve => setTimeout(resolve, 260)); await tick(); const candidate = f.candidate(), first = candidate;
  assert.equal(candidate.blocks.filter(block => block.context.startsWith("Route OpenAI")).filter(block => block.text.startsWith("Route OpenAI")).length, 1);
  assert.equal(candidate.blocks.some(block => block.context === words.join("")), false);
  assert.ok(first.blocks.some(block => block.text === "endpoint.")); approved(f); finish(f);
  assert.equal(p.textContent, "中文译文 "); assert.equal(linked.childNodes.at(-1).tagName, "A");
  assert.equal(hidden.textContent, words.join("")); f.session.dispose();
});
test("zero-width separator ranges keep animated word spacing and the translation owner is real text", async () => {
  const words = "Route OpenAI and other providers through one compatible endpoint.".split(" ");
  const p = new Element("P", words.map(word => new Element("SPAN", [new Element("SPAN", [new Text(word)]), new Text(" ")]))), f = fixture([p]);
  f.environment.isRenderedText = node => !!node.nodeValue.trim();
  await enabled(f); await new Promise(resolve => setTimeout(resolve, 260)); await tick(); const candidate = f.candidate();
  assert.equal(candidate.blocks.length, 1); assert.equal(candidate.blocks[0].text, words.join(" "));
  approved(f); finish(f, candidate, "通过统一接口连接各类模型。"); assert.equal(p.textContent, "通过统一接口连接各类模型。 ");
  f.session.layoutChanged(); await tick(); assert.equal(f.candidates().length, 1); assert.equal(p.textContent, "通过统一接口连接各类模型。 "); f.session.dispose();
});
test("a countdown beside translated promotional text never restarts the page stream", async () => {
  const label = new Text("Up to 90 percent off right now "), end = new Text("ends in "), clock = new Text("08:11:19"), clockSpan = new Element("SPAN", [clock]);
  const promo = new Element("DIV", [new Element("SPAN", [label]), new Element("SPAN", [end]), clockSpan]);
  const body = paragraph("This introductory paragraph supplies the foreign-language page evidence."), f = fixture([promo, body.element]);
  await enabled(f); const candidate = f.candidate(); approved(f);
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: candidate.blocks.map(block => ({ id: block.id, text: "正在输出" })) }));
  const revision = f.session.inspect().revision;
  for (let i = 18; i > 13; i--) { clock.nodeValue = `08:11:${i}`; f.session.mutated([{ type: "characterData", target: clock }]); await tick(); }
  assert.equal(f.session.inspect().revision, revision); assert.equal(f.candidates().length, 1);
  assert.equal(f.sent.some(packet => packet.type === "pageAutoCancel"), false);
  finish(f, candidate); assert.equal(label.nodeValue, "中文译文 "); assert.equal(end.nodeValue, "中文译文 ");
  f.session.layoutChanged(); await tick(); assert.equal(f.candidates().length, 1); assert.equal(body.node.nodeValue, "中文译文"); f.session.dispose();
});
test("replaced countdown nodes remain isolated but a changed time inside prose is a new fact", async () => {
  const label = new Text("Offer ends in "), clock = new Text("08:11:19"), span = new Element("SPAN", [clock]);
  const f = fixture([new Element("DIV", [label, span]), paragraph("The article content provides enough foreign-language evidence to translate automatically.").element]);
  await enabled(f); approved(f); finish(f); const revision = f.session.inspect().revision;
  clock.isConnected = false; span.textContent = "08:11:18";
  f.session.mutated([{ type: "childList", target: span, removedNodes: [clock], addedNodes: span.childNodes }]); await tick();
  assert.equal(f.session.inspect().revision, revision); assert.equal(f.candidates().length, 1); f.session.dispose();
  const time = new Text("08:11:19"), prose = new Element("P", [new Text("The departure time is "), new Element("SPAN", [time]), new Text(" tomorrow morning.")]), second = fixture([prose]);
  await enabled(second); approved(second); finish(second); time.nodeValue = "08:11:18";
  second.session.mutated([{ type: "characterData", target: time }]); await tick();
  assert.equal(second.candidates().length, 2); assert.ok(second.candidate().blocks.every(block => block.context.includes("08:11:18"))); second.session.dispose();
});
test("Updated and nested time form one model phrase, preserve natural order and replay the original DOM", async () => {
  const prefix = new Text("Updated "), age = new Text("about 19 hours ago"), time = new Element("TIME", [age], { datetime: "2026-10-09T00:00:00" });
  const label = new Element("SPAN", [prefix, time]), f = fixture([new Element("DIV", [label]), paragraph("These publicly listed image models are quantized from the base model.").element]);
  await enabled(f); const candidate = f.candidate();
  const phrase = candidate.blocks.find(b => b.text.startsWith("Updated")); assert.equal(phrase.text, "Updated about 19 hours ago");
  assert.equal(age.nodeValue, "about 19 hours ago"); approved(f);
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: phrase.id, text: "约19小时前" }] }));
  assert.equal(label.textContent, "约19小时前");
  finish(f, candidate, "约19小时前更新"); assert.equal(label.textContent, "约19小时前更新");
  assert.equal(label.childNodes[1], time); assert.equal(time.getAttribute("datetime"), "2026-10-09T00:00:00");
  f.session.control("view-original"); assert.equal(label.textContent, "Updated about 19 hours ago");
  f.session.control("view-translation"); assert.equal(label.textContent, "约19小时前更新");
  prefix.nodeValue = "Updated "; age.nodeValue = "about 19 hours ago";
  f.session.mutated([{ type: "characterData", target: prefix }, { type: "characterData", target: age }]); await tick();
  assert.equal(label.textContent, "约19小时前更新"); assert.equal(f.candidates().length, 1);
  age.nodeValue = "about 20 hours ago"; f.session.mutated([{ type: "characterData", target: age }]); await tick();
  assert.equal(f.candidate().blocks[0].text, "Updated about 20 hours ago"); approved(f); finish(f, f.candidate(), "约20小时前更新");
  assert.equal(label.textContent, "约20小时前更新"); f.session.dispose();
});

test("a standalone calendar stays local while a date inside an ordinary paragraph goes to the model", async () => {
  const alone = new Element("TIME", [new Text("Sep 1")]);
  const before = new Text("Published on "), date = new Text("Sep 1"), after = new Text(" after testing.");
  const p = new Element("P", [before, new Element("TIME", [date]), after]), f = fixture([alone, p]);
  await enabled(f); assert.equal(alone.textContent, "9月1日");
  assert.deepEqual(f.candidate().blocks.map(b => b.text), ["Published on Sep 1 after testing."]);
  assert.equal(p.textContent, "Published on Sep 1 after testing."); approved(f); finish(f, f.candidate(), "测试后于9月1日发布。");
  assert.equal(p.textContent, "测试后于9月1日发布。"); f.session.dispose();
});
function message(candidate, type, extra) { return { type, requestId: candidate.requestId, documentToken: candidate.documentToken,
  generation: candidate.generation, revision: candidate.revision, ...extra }; }
async function enabled(f) { f.session.configure({ generation: 1, enabled: true, active: true, targetLanguage: "简体中文", concurrency: 8 }); await tick(); }
function approved(f, candidate = f.candidate()) { return f.session.receive(message(candidate, "pageAutoAssessment", { needsTranslation: true, allowImages: true })); }
function finish(f, candidate = f.candidate(), translation = "中文译文") {
  return f.session.receive(message(candidate, "pageAutoTranslations", { updates: candidate.blocks.map(block => ({ id: block.id, text: translation, complete: true })), finished: true }));
}
function select(f, nodes, firstStart = 0, lastEnd = nodes.at(-1)?.nodeValue.length || 0) {
  const range = { collapsed: false, startContainer: nodes[0], startOffset: firstStart, endContainer: nodes.at(-1), endOffset: lastEnd,
    intersectsNode: node => nodes.includes(node) };
  f.document.getSelection = () => ({ isCollapsed: false, rangeCount: 1, getRangeAt: () => range });
}
async function manualReady(f, enabled = false) {
  f.session.configure({ generation: 1, enabled, active: true, manualAvailable: true, cacheScope: "a".repeat(64), targetLanguage: "简体中文" }); await tick();
}

test("whitelisting a locally skipped mixed page starts only its foreign paragraphs and blacklisting retains manual access", async () => {
  const english = paragraph("This English paragraph should be translated when the website is explicitly whitelisted by its reader.");
  const chinese = paragraph("这是一个已经能够阅读的中文段落，用于验证中英混排页面的语言判断。网站加入白名单后，这段中文仍然保留原文，不应该被发送到翻译服务。".repeat(3));
  const f = fixture([english.element, chinese.element]); await manualReady(f, true);
  assert.equal(f.candidates().length, 0);
  f.session.configure({ ...f.session.inspect().config, siteMode: "allow" }); await tick();
  assert.equal(f.candidates().length, 1); assert.deepEqual(f.candidate().blocks.map(block => block.text), [english.node.nodeValue]);
  approved(f); finish(f); const completed = english.node.nodeValue;
  f.session.configure({ ...f.session.inspect().config, siteMode: "block", enabled: false }); await tick();
  assert.equal(f.session.inspect().config.manualAvailable, true); assert.equal(english.node.nodeValue, completed);
  assert.equal(f.candidates().length, 1); f.session.dispose();
});

test("concurrency-only configuration changes keep the accepted stream identity and completed prefix", async () => {
  const item = paragraph("This paragraph continues streaming while the user adjusts the concurrency control."), f = fixture([item.element]);
  await manualReady(f, true); const candidate = f.candidate(); approved(f, candidate);
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "正在继续" }] }));
  f.session.configure({ ...f.session.inspect().config, concurrency: 3 }); await tick();
  assert.equal(f.session.inspect().revision, candidate.revision); assert.equal(f.candidates().length, 1);
  assert.equal(f.sent.some(packet => packet.type === "pageAutoCancel"), false);
  assert.equal(finish(f, candidate, "继续完成。"), true); assert.equal(item.node.nodeValue, "继续完成。"); f.session.dispose();
});

function clickGeometry(f, hit, rectangles) {
  f.document.elementFromPoint = () => hit;
  f.document.createRange = () => { let node; return { selectNodeContents: selected => { node = selected; },
    getClientRects: () => rectangles.get(node) || [], detach() {} }; };
}
function line(left = 10, top = 10, width = 400, height = 24) { return { left, top, width, height, right: left + width, bottom: top + height }; }

test("click selection picks the whole nearby paragraph and its inline link without a highlighted selection", async () => {
  const first = new Text("Read the "), link = new Text("official documentation"), last = new Text(" before installing this application.");
  const anchor = new Element("A", [link], { href: "https://example.com/help" }), inline = new Element("SPAN", [last]);
  const p = new Element("P", [first, anchor, inline]), other = paragraph("This other paragraph must stay private to the page."), f = fixture([p, other.element]);
  clickGeometry(f, anchor, new Map([[link, [line(80)]]])); await manualReady(f);
  assert.equal(f.session.control("manual-pick"), true); assert.equal(f.candidates().length, 0);
  assert.equal(f.session.pickParagraph(100, 20), true); await tick();
  assert.equal(f.candidate().manualMode, "selection");
  assert.equal(f.candidate().blocks.map(b => b.text).join(" "), p.textContent);
  assert.equal(f.candidate().blocks.some(b => b.text.includes("private")), false);
  approved(f); finish(f); assert.equal(other.node.nodeValue, "This other paragraph must stay private to the page.");
  assert.equal(p.childNodes[1], anchor); assert.equal(anchor.getAttribute("href"), "https://example.com/help");
  assert.equal(f.session.inspect().pickingParagraph, false); f.session.dispose();
});

test("near-line padding picks one semantic paragraph, blank container padding and editing fields do not", async () => {
  const first = paragraph("Translate this first paragraph when its nearby line is clicked.");
  const second = paragraph("Keep this second paragraph outside the selected source scope."), card = new Element("DIV", [first.element, second.element]);
  const input = new Element("TEXTAREA", [new Text("private editing buffer")]), f = fixture([card, input]); await manualReady(f);
  const rects = new Map([[first.node, [line(10, 10)]], [second.node, [line(10, 100)]]]); clickGeometry(f, card, rects);
  f.session.control("manual-pick"); assert.equal(f.session.pickParagraph(30, 46), true); await tick();
  assert.deepEqual(f.candidate().blocks.map(b => b.text), [first.node.nodeValue]); approved(f); finish(f);
  f.session.control("manual-pick"); assert.equal(f.session.pickParagraph(900, 400), false); assert.equal(f.session.inspect().pickingParagraph, true);
  clickGeometry(f, input, new Map()); assert.equal(f.session.pickParagraph(10, 10), false);
  f.session.control("cancel-pick"); assert.equal(f.session.inspect().pickingParagraph, false); assert.equal(f.candidates().length, 1); f.session.dispose();
});

test("clicking an already translated paragraph reuses the original source and completed result", async () => {
  const item = paragraph("This paragraph was already translated and must remain cached."), f = fixture([item.element]);
  await manualReady(f, true); approved(f); finish(f, f.candidate(), "该段已翻译并缓存。");
  clickGeometry(f, item.element, new Map([[item.node, [line()]]])); f.session.control("manual-pick");
  assert.equal(f.session.pickParagraph(20, 20), true); await tick();
  assert.equal(f.candidates().length, 1); assert.equal(item.node.nodeValue, "该段已翻译并缓存。"); f.session.dispose();
});

test("paragraph click reaches an open shadow root and navigation ends the armed mode", async () => {
  const inner = paragraph("This component contains an English paragraph inside an open shadow root.");
  const host = new Element("CUSTOM-TEXT"), tree = { nodeType: 11, mode: "open", host, childNodes: [inner.element], elementFromPoint: () => inner.element };
  host.shadowRoot = tree; inner.element.getRootNode = () => tree;
  const f = fixture([host]); f.document.location = { href: "https://example.com/first" }; await manualReady(f);
  clickGeometry(f, host, new Map([[inner.node, [line()]]])); f.session.control("manual-pick");
  assert.equal(f.session.pickParagraph(20, 20), true); await tick(); assert.ok(f.candidate()); approved(f); finish(f);
  f.session.control("manual-pick"); f.document.location.href = "https://example.com/next"; f.session.navigationChanged();
  assert.equal(f.session.inspect().pickingParagraph, false); f.session.dispose();
});

test("SPA pagination leaves manual page mode and translates new cards automatically, then reuses a previous page", async () => {
  const first = paragraph("The first directory page describes an application that generates music from text."), f = fixture([first.element]);
  const firstSource = first.node.nodeValue;
  f.document.location = { href: "https://directory.example/spaces?p=2" };
  await manualReady(f, true); approved(f); finish(f, f.candidate(), "第一页应用介绍");
  f.session.control("manual-page"); await tick();
  assert.equal(f.session.inspect().manualMode, "page");
  first.node.isConnected = false; first.element.remove();
  const second = paragraph("The next directory page describes a different application that edits images."), secondSource = second.node.nodeValue;
  f.body.append(second.element); f.document.location.href = "https://directory.example/spaces?p=3";
  f.session.mutated([{ type: "childList", target: f.body, addedNodes: [second.element], removedNodes: [first.element] }]);
  await new Promise(resolve => setTimeout(resolve, 140)); await tick();
  assert.equal(f.session.inspect().manualMode, null); assert.equal(f.candidates().length, 2);
  const next = f.candidate(); assert.equal(next.manualMode, undefined); assert.deepEqual(next.blocks.map(b => b.text), [secondSource]);
  approved(f, next); finish(f, next, "第二页应用介绍");
  const beforeReturn = f.candidates().length;
  second.node.isConnected = false; second.element.remove(); const returned = paragraph(firstSource);
  f.body.append(returned.element); f.document.location.href = "https://directory.example/spaces?p=2";
  f.session.mutated([{ type: "childList", target: f.body, addedNodes: [returned.element], removedNodes: [second.element] }]);
  await new Promise(resolve => setTimeout(resolve, 140)); await tick();
  assert.equal(returned.node.nodeValue, "第一页应用介绍"); assert.equal(f.candidates().length, beforeReturn); f.session.dispose();
});

test("replacing a live SPA page cancels only its request without pausing subsequent automatic work", async () => {
  const oldPage = paragraph("The first directory page is still streaming while its list is replaced."), f = fixture([oldPage.element]);
  let backendPaused = false;
  f.environment.send = async packet => { f.sent.push(packet); if (packet.type === "pageAutoCancel" && !packet.requestId) backendPaused = true;
    return packet.type === "pageAutoCandidate" && backendPaused ? { accepted: false, reason: "page_paused" } : { accepted: true }; };
  await enabled(f); const old = f.candidate(); approved(f, old);
  oldPage.node.isConnected = false; oldPage.element.remove(); const next = paragraph("The second directory page needs an automatic translation of its new content."); f.body.append(next.element);
  f.session.mutated([{ type: "childList", target: f.body, addedNodes: [next.element], removedNodes: [oldPage.element] }]); await tick();
  const cancellation = f.sent.find(packet => packet.type === "pageAutoCancel");
  assert.equal(cancellation.requestId, old.requestId); assert.equal(backendPaused, false);
  const candidate = f.candidate(); assert.notEqual(candidate.requestId, old.requestId); assert.equal(candidate.manualMode, undefined);
  assert.equal(f.session.receive(message(old, "pageAutoTranslations", { finished: true, updates: [{ id: old.blocks[0].id, text: "旧结果", complete: true }] })), false);
  approved(f, candidate); finish(f, candidate, "新目录译文"); assert.equal(next.node.nodeValue, "新目录译文"); f.session.dispose();
});

test("SPA URL notification discovers a reused list without a mutation signal and duplicate notifications submit once", async () => {
  const item = paragraph("The first application generates speech from an English text prompt."), f = fixture([item.element]);
  f.document.location = { href: "https://directory.example/spaces?p=2" }; await enabled(f); approved(f); finish(f);
  const revision = f.session.inspect().revision;
  item.node.nodeValue = "The second application edits photographs with natural language instructions.";
  f.document.location.href = "https://directory.example/spaces?p=3";
  assert.equal(f.session.navigationChanged(), true); assert.equal(f.session.navigationChanged(), false);
  await new Promise(resolve => setTimeout(resolve, 140)); await tick();
  assert.equal(f.candidates().length, 2); assert.ok(f.candidate().revision > revision);
  assert.deepEqual(f.candidate().blocks.map(b => b.text), [item.node.nodeValue]); approved(f); finish(f); f.session.dispose();
});

test("ordinary anchors retain streaming work but hash app routes and filter queries change the content identity", async () => {
  const item = paragraph("This article continues translating during an in-page jump to its pricing section."), f = fixture([item.element]);
  f.document.location = { href: "https://directory.example/spaces?p=2" }; await enabled(f); const old = f.candidate(); approved(f);
  f.document.location.href += "#pricing"; assert.equal(f.session.navigationChanged(), false);
  assert.equal(f.session.inspect().revision, old.revision); assert.equal(f.session.inspect().jobs, 1); assert.equal(f.sent.some(p => p.type === "pageAutoCancel"), false);
  finish(f, old); f.session.dispose();
  assert.equal(pageRouteKey("https://directory.example/spaces?p=2#pricing"), pageRouteKey("https://directory.example/spaces?p=2"));
  assert.notEqual(pageRouteKey("https://directory.example/spaces?p=3"), pageRouteKey("https://directory.example/spaces?p=2"));
  assert.notEqual(pageRouteKey("https://directory.example/#/page/2"), pageRouteKey("https://directory.example/#/page/3"));
  assert.notEqual(pageRouteKey("https://directory.example/#!/page/2"), pageRouteKey("https://directory.example/#!/page/3"));
  assert.equal(pageRouteKey("invalid"), null);
});

test("SPA pagination ends manual selection scope and rejects its late output before automatic translation", async () => {
  const first = paragraph("The user selects the first application description for a manual translation."), f = fixture([first.element]);
  f.document.location = { href: "https://directory.example/spaces?p=2" }; await manualReady(f, true); approved(f); finish(f);
  first.node.nodeValue = "A newly authored description needs to be selected before leaving this route.";
  f.session.mutated([{ type: "characterData", target: first.node }]); await tick();
  select(f, [first.node]); f.session.control("manual-selection"); await tick(); const old = f.candidate();
  assert.equal(old.manualMode, "selection");
  first.node.isConnected = false; first.element.remove(); const next = paragraph("A new page must automatically translate all descriptions outside that old selection."); f.body.append(next.element);
  f.document.location.href = "https://directory.example/spaces?p=3"; f.session.navigationChanged();
  await new Promise(resolve => setTimeout(resolve, 140)); await tick();
  assert.equal(f.session.inspect().manualMode, null); assert.equal(f.session.inspect().selectionPending, false);
  assert.equal(f.candidate().manualMode, undefined); assert.equal(finish(f, old, "过期选区译文"), false);
  approved(f); finish(f); assert.equal(next.node.nodeValue, "中文译文"); f.session.dispose();
});

test("SPA pagination respects automatic translation being disabled and an explicit page pause", async () => {
  for (const auto of [false, true]) {
    const item = paragraph("The first application description is available for manual translation."), f = fixture([item.element]);
    f.document.location = { href: "https://directory.example/spaces?p=2" }; await manualReady(f, auto);
    if (auto) { approved(f); finish(f); f.session.control("pause"); }
    else { f.session.control("manual-page"); await tick(); finish(f); }
    const before = f.candidates().length;
    item.node.nodeValue = "A new page stays original while automatic translation is disabled or paused.";
    f.document.location.href = "https://directory.example/spaces?p=3"; f.session.navigationChanged();
    await new Promise(resolve => setTimeout(resolve, 140)); await tick();
    assert.equal(f.candidates().length, before); assert.equal(f.session.inspect().active, false); assert.equal(f.session.inspect().paused, auto);
    assert.equal(f.session.inspect().manualMode, null); f.session.dispose();
  }
});
test("scroll reveals unreadable lazy prose without retiring a live batch or retranslating completed paragraphs", async () => {
  const first = paragraph("An initially visible English paragraph should translate while the reader scrolls."), lazy = paragraph("A lazy English footnote needs automatic translation when it becomes rendered."), f = fixture([first.element, lazy.element]);
  let reveal = false;
  f.environment.isRenderedText = node => node !== lazy.node || reveal;
  await enabled(f); const candidate = f.candidate(); approved(f);
  reveal = true; f.session.layoutChanged(); await tick();
  assert.equal(f.candidates().length, 1); assert.equal(f.session.inspect().jobs, 1);
  finish(f, candidate); await tick(); await new Promise(resolve => setTimeout(resolve, 150));
  const next = f.candidate(); assert.equal(f.candidates().length, 2);
  assert.deepEqual(next.blocks.map(block => block.text), [lazy.node.nodeValue]);
  approved(f, next); finish(f, next); f.session.layoutChanged(); await new Promise(resolve => setTimeout(resolve, 150)); await tick();
  assert.equal(f.candidates().length, 2); assert.equal(first.node.nodeValue, "中文译文"); assert.equal(lazy.node.nodeValue, "中文译文"); f.session.dispose();
});

test("continuous independent lazy additions do not starve preparation or abort streaming paragraphs", async () => {
  const item = paragraph("The main English article remains stable while the page lazily appends other paragraphs."), f = fixture([item.element]);
  await enabled(f); const candidate = f.candidate(); approved(f); const revision = f.session.inspect().revision;
  for (let at = 0; at < 12; at++) {
    const extra = paragraph("Another independently loaded English paragraph has enough prose for translation.");
    f.body.append(extra.element); f.session.mutated([{ type: "childList", target: f.body, addedNodes: [extra.element], removedNodes: [] }]);
  }
  assert.equal(f.session.inspect().revision, revision); assert.equal(f.candidates().length, 1);
  assert.equal(f.sent.filter(packet => packet.type === "pageAutoCancel").length, 0);
  finish(f, candidate); await tick(); const next = f.candidate();
  assert.equal(item.node.nodeValue, "中文译文"); assert.equal(next.blocks.length, 12);
  approved(f, next); finish(f, next); f.session.dispose();
});

test("a desktop rejection finishing before assessment closes preparation rather than waiting forever", async () => {
  const f = fixture([paragraph("A new public English page needs an initial automatic language assessment.").element]);
  await enabled(f); const candidate = f.candidate();
  f.session.receive(message(candidate, "pageAutoTranslations", { finished: true, error: "invalid-admission" }));
  assert.equal(f.session.inspect().jobs, 0); assert.equal(f.session.inspect().pending, 0);
  assert.equal(f.statuses.at(-1).state, "failed"); assert.equal(f.statuses.at(-1).detail, "invalid-admission"); f.session.dispose();
});

test("transient native preparation rejection retries locally and a permanently unavailable bridge stops after three retries", async () => {
  for (const unavailable of [false, true]) {
    const f = fixture([paragraph("A new English article should recover when its native document probe was temporarily unavailable.").element]);
    await enabled(f); const first = f.candidate();
    f.session.receive(message(first, "pageAutoTranslations", { finished: true, error: "page-unavailable" }));
    await new Promise(resolve => setTimeout(resolve, 300)); await tick();
    assert.equal(f.candidates().length, 2); const retry = f.candidate(); assert.notEqual(retry.revision, first.revision);
    if (!unavailable) { approved(f, retry); finish(f, retry); assert.equal(f.statuses.at(-1).state, "completed"); }
    else {
      f.session.receive(message(retry, "pageAutoTranslations", { finished: true, error: "automatic-queue-unavailable" }));
      await new Promise(resolve => setTimeout(resolve, 550)); await tick(); const last = f.candidate(); assert.equal(f.candidates().length, 3);
      f.session.receive(message(last, "pageAutoTranslations", { finished: true, error: "page-unavailable" }));
      await new Promise(resolve => setTimeout(resolve, 1100)); await tick(); const final = f.candidate(); assert.equal(f.candidates().length, 4);
      f.session.receive(message(final, "pageAutoTranslations", { finished: true, error: "page-unavailable" }));
      assert.equal(f.statuses.at(-1).state, "failed"); assert.equal(f.session.inspect().jobs, 0); assert.equal(f.session.inspect().pending, 0);
    }
    f.session.dispose();
  }
});

test("semantic snapshot keeps inline links and bold text nodes with shared paragraph context", async () => {
  const before = new Text("Read the "), strongText = new Text("complete guide"), after = new Text(" for detailed browser translation instructions.");
  const strong = new Element("STRONG", [strongText]), anchor = new Element("A", [strong], { href: "https://example.com/guide" });
  const p = new Element("P", [before, anchor, after]); const f = fixture([p]);
  const snapshot = await extractPageSnapshot(f.environment);
  assert.equal(snapshot.blocks.length, 3);
  assert.equal(new Set(snapshot.blocks.map(block => block.groupId)).size, 1);
  assert.ok(snapshot.blocks.every(block => /complete guide/u.test(block.context)));
  await enabled(f); approved(f); finish(f);
  assert.equal(p.childNodes[1], anchor); assert.equal(anchor.childNodes[0], strong);
  assert.equal(anchor.getAttribute("href"), "https://example.com/guide");
  assert.equal(strongText.nodeValue, "中文译文"); f.session.dispose();
});

test("a scan shares clipping/style geometry while the next scan sees newly hidden or revealed content", async () => {
  const p = new Element("P", [new Text("The readable first half "), new Element("STRONG", [new Text("and the readable ending.")])]);
  const clip = new Element("DIV", [p]); clip.style.overflow = "hidden";
  let rectangleReads = 0;
  clip.getBoundingClientRect = () => { rectangleReads++; return { left: 0, right: 500, top: 0, bottom: 100 }; };
  const f = fixture([clip]), styles = new Map();
  f.environment.getStyle = element => { styles.set(element, (styles.get(element) || 0) + 1); return element.style; };
  f.document.createRange = () => ({ selectNodeContents() {}, collapse() {},
    getClientRects: () => [{ left: 10, right: 200, top: 10, bottom: 40, width: 190, height: 30 }] });
  f.environment.isRenderedText = createRenderedTextTester(f.document);
  const first = await extractPageSnapshot(f.environment);
  assert.equal(first.blocks.length, 2); assert.equal(rectangleReads, 1);
  assert.ok([...styles.values()].every(count => count === 1));
  clip.style.visibility = "hidden";
  assert.equal((await extractPageSnapshot(f.environment)).blocks.length, 0);
  clip.style.visibility = "visible";
  assert.equal((await extractPageSnapshot(f.environment)).blocks.length, 2);
  f.session.dispose();
});

test("unchanged source digests stay stable and changing one inline fact invalidates the whole paragraph digest", async () => {
  const first = new Text("The library opens in "), fact = new Text("2026"), last = new Text(" for every visitor.");
  const p = new Element("P", [first, new Element("STRONG", [fact]), last]), f = fixture([p]);
  const before = await extractPageSnapshot(f.environment);
  const previous = new Map(before.records.map(record => [record.node, record]));
  const unchanged = await extractPageSnapshot(f.environment, previous);
  assert.deepEqual(unchanged.blocks.map(block => block.sourceGroupHash), before.blocks.map(block => block.sourceGroupHash));
  fact.nodeValue = "2027";
  const after = await extractPageSnapshot(f.environment, previous);
  assert.notEqual(after.blocks[0].sourceGroupHash, before.blocks[0].sourceGroupHash);
  assert.ok(after.blocks.every(block => block.sourceGroupHash === after.blocks[0].sourceGroupHash));
  assert.ok(after.blocks.every(block => block.context.includes("2027") && !block.context.includes("2026")));
  f.session.dispose();
});

test("unchanged CSS animation during an initial time-sliced scan does not repeatedly retire the snapshot", async () => {
  const paragraphs = Array.from({ length: 130 }, (_, index) => paragraph(`The public English paragraph ${index} explains a reliable browser translation workflow.`));
  const f = fixture(paragraphs.map(item => item.element)); let clock = 0, frames = 0;
  f.environment.now = () => clock += 20;
  f.environment.yieldWork = async () => { if (frames++ < 8) {
    paragraphs[0].element.style.transform = `translateY(${frames}px)`;
    f.session.mutated([{ type: "attributes", target: paragraphs[0].element, attributeName: "style" }]);
  } };
  await enabled(f);
  assert.equal(f.candidates().length, 1);
  assert.equal(f.candidate().revision, 1);
  assert.ok(f.candidate().blocks.some(block => block.text === paragraphs[0].node.nodeValue));
  f.session.dispose();
});

test("real visibility loss during the initial scan still retires it and never admits the now hidden source", async () => {
  const paragraphs = Array.from({ length: 130 }, (_, index) => paragraph(`The public English paragraph ${index} explains a reliable browser translation workflow.`));
  const f = fixture(paragraphs.map(item => item.element)); let clock = 0, changed = false;
  f.environment.now = () => clock += 20;
  f.environment.yieldWork = async () => { if (!changed) {
    changed = true; paragraphs[0].element.style.display = "none";
    f.session.mutated([{ type: "attributes", target: paragraphs[0].element, attributeName: "style" }]);
  } };
  await enabled(f); await tick();
  assert.ok(f.candidate());
  assert.ok(f.candidates().every(candidate => candidate.blocks.every(block => block.text !== paragraphs[0].node.nodeValue)));
  f.session.dispose();
});

test("revealing Chinese prose during the scan still blocks auto-translation whether it was already visited or not", async () => {
  for (const at of [0, 129]) {
    const paragraphs = Array.from({ length: 130 }, (_, index) => paragraph(`The public English paragraph ${index} explains a reliable browser translation workflow.`));
    paragraphs[at] = paragraph("这里是完整的中文正文，当前混合语言页面不需要自动翻译。");
    paragraphs[at].element.style.display = "none";
    const f = fixture(paragraphs.map(item => item.element)); let clock = 0, changed = false;
    f.environment.now = () => clock += 20;
    f.environment.yieldWork = async () => { if (!changed) {
      changed = true; paragraphs[at].element.style.display = "block";
      f.session.mutated([{ type: "attributes", target: paragraphs[at].element, attributeName: "style" }]);
    } };
    await enabled(f); await tick();
    assert.equal(f.candidates().length, 0);
    assert.equal(f.decisions.at(-1).allowImages, false);
    f.session.dispose();
  }
});

test("independent article link cards have separate groups and one failed card cannot roll back its neighbours", async () => {
  const cards = ["What is a dedicated IP VPN?", "Is a dedicated IP recommended for online banking and 2FA?", "Which ports are available for the VPN connection?"]
    .map(text => new Element("A", [new Element("SPAN", [new Text(text)]), new Element("SPAN", [new Text("→")])], { href: "/article" }));
  const f = fixture([new Element("DIV", cards)]); await enabled(f);
  const candidate=f.candidate(); assert.equal(new Set(candidate.blocks.map(block=>block.groupId)).size,3);
  assert.ok(candidate.blocks.every(block=>!block.context.includes("2FA")||block.text.includes("2FA")));
  approved(f); f.session.receive(message(candidate,"pageAutoTranslations",{finished:true,updates:candidate.blocks.map((block,index)=>({id:block.id,text:index===1?"":"中文文章标题",complete:index!==1,failed:index===1}))}));
  assert.equal(cards[0].childNodes[0].textContent,"中文文章标题");
  assert.equal(cards[1].childNodes[0].textContent,"Is a dedicated IP recommended for online banking and 2FA?");
  assert.equal(cards[2].childNodes[0].textContent,"中文文章标题");f.session.dispose();
});
test("input values, code, editable, hidden and translator UI are never uploaded", async () => {
  const good = paragraph("An ordinary readable English paragraph that should be translated.");
  const excluded = [new Element("FORM", [new Element("INPUT", [new Text("Private form data")]), new Element("TEXTAREA", [new Text("Private draft data")])]), new Element("CODE", [new Text("source code")]),
    new Element("DIV", [new Text("private editor")]), new Element("P", [new Text("hidden body")]),
    new Element("DIV", [new Text("自己的界面")], { "data-screen-translator": "status" })];
  excluded[2].isContentEditable = true; excluded[3].style.display = "none";
  const f = fixture([...excluded, good.element]); const snapshot = await extractPageSnapshot(f.environment);
  assert.deepEqual(snapshot.blocks.map(block => block.text), [good.node.nodeValue]);
  assert.equal(snapshot.pageSummary.hanCharacters, 0); f.session.dispose();
});

test("public navigation and button labels translate without replacing links buttons or their authored handlers", async () => {
  const labels = ["Features", "Pricing", "How It Works", "FAQ", "Blog", "Contact", "Dashboard", "Sign Out"];
  const controls = labels.map((text, index) => new Element(index < 6 ? "A" : "BUTTON", [new Text(text)], { href: `#section-${index}` }));
  const question = new Element("BUTTON", [new Element("SPAN", [new Text("2. How does the free trial work?")])]);
  const privateControl = new Element("TEXTAREA", [new Text("Private account form contents")]);
  const f = fixture([new Element("HEADER", [new Element("NAV", controls)]), question,
    new Element("FORM", [privateControl]), paragraph("This public English page describes residential connections and their setup.").element]);
  const snapshot = await extractPageSnapshot(f.environment);
  assert.ok(labels.every(label => snapshot.blocks.some(block => block.text === label)));
  assert.ok(snapshot.blocks.some(block => block.text === "2. How does the free trial work?"));
  assert.ok(snapshot.blocks.every(block => !block.text.includes("Private")));
  assert.equal(new Set(snapshot.blocks.filter(block => labels.includes(block.text)).map(block => block.groupId)).size, labels.length);
  await enabled(f); approved(f); finish(f);
  assert.equal(f.body.childNodes[0].childNodes[0].childNodes[0], controls[0]);
  assert.equal(controls[0].getAttribute("href"), "#section-0");
  assert.equal(controls[7].tagName, "BUTTON"); assert.equal(controls[7].textContent, "中文译文");
  f.session.dispose();
});

test("visible inference form copy is translated while input contents stay excluded and form copy cannot activate page judgment", async () => {
  const labels = ["Inference Providers", "Text-to-Image", "This model isn't deployed by any Inference Provider.", "Ask for provider support"];
  const controls = labels.map((text, index) => new Element(index === 3 ? "BUTTON" : "DIV", [new Text(text)]));
  const input = new Element("INPUT", [new Text("private account identifier")]), draft = new Element("TEXTAREA", [new Text("private prompt draft")]);
  const form = new Element("FORM", [...controls, input, draft]), f = fixture([form]);
  const formOnly = await extractPageSnapshot(f.environment);
  assert.ok(labels.every(text => formOnly.blocks.some(block => block.text === text)));
  assert.equal(formOnly.pageSummary.totalCharacters, 0); assert.equal(formOnly.pageSummary.latinProseCharacters, 0);
  f.body.append(paragraph("This image generation model creates images from text and optional reference photographs.").element);
  await enabled(f); assert.ok(f.candidate().blocks.every(block => !block.text.includes("private"))); approved(f); finish(f);
  assert.ok(controls.every(control => control.textContent === "中文译文")); assert.equal(input.textContent, "private account identifier"); assert.equal(draft.textContent, "private prompt draft");
  assert.equal(form.childNodes[3], controls[3]); f.session.dispose();
});

test("card dates localize without entering API batches and a relative date repaint cannot revoke a completed description", async () => {
  const source = "Showcase a bold landing page with stylized title and button", description = paragraph(source);
  const labels = ["Sep 1", "Apr 10, 2025", "1 day ago", "about 15 hours ago"], expected = ["9月1日", "2025年4月10日", "1天前", "约15小时前"];
  const dates = labels.map(text => new Element("TIME", [new Text(text)])), f = fixture([description.element, ...dates]);
  await enabled(f); assert.deepEqual(f.candidate().blocks.map(block => block.text), [source]);
  assert.deepEqual(dates.map(date => date.textContent), expected); approved(f); finish(f, f.candidate(), "展示一个带有风格化标题和按钮的醒目落地页");
  dates[3].childNodes[0].nodeValue = "about 16 hours ago";
  f.session.mutated([{ type: "characterData", target: dates[3].childNodes[0] }]); await tick();
  assert.equal(dates[3].textContent, "约16小时前"); assert.equal(f.candidates().length, 1); assert.equal(description.node.nodeValue, "展示一个带有风格化标题和按钮的醒目落地页");
  f.session.control("view-original"); assert.equal(dates[0].textContent, labels[0]); assert.equal(dates[3].textContent, "about 16 hours ago");
  f.session.control("view-translation"); assert.equal(dates[0].textContent, expected[0]); f.session.dispose();
});

test("new accordion answers stream after initial completion and recreated answers reuse completed translation", async () => {
  const first = paragraph("The initial public FAQ answer describes how a residential internet connection works.");
  const secondSource = "Eligible new customers may start with a three-day free trial. Payment information is required to begin the trial.";
  const firstCard = new Element("DIV", [new Element("BUTTON", [new Text("1. What exactly is a residential internet connection?")]), first.element]);
  const secondCard = new Element("DIV", [new Element("BUTTON", [new Text("2. How does the free trial work?")])]);
  const f = fixture([firstCard, secondCard]); await enabled(f); approved(f); finish(f);
  const second = paragraph(secondSource); secondCard.append(second.element);
  f.session.mutated([{ type: "childList", target: secondCard, addedNodes: [second.element], removedNodes: [] }]); await tick();
  assert.deepEqual(f.candidate().blocks.map(block => block.text), [secondSource]);
  const candidate = f.candidate(); approved(f);
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "新客户可享" }] }));
  assert.equal(second.node.nodeValue, "新客户可享"); assert.equal(first.node.nodeValue, "中文译文");
  finish(f, candidate, "新客户可享三天免费试用。开始试用前需要提供付款信息。");
  second.element.remove(); second.node.isConnected = false;
  f.session.mutated([{ type: "childList", target: secondCard, addedNodes: [], removedNodes: [second.element] }]); await tick();
  const requestCount = f.candidates().length;
  const recreated = paragraph(secondSource); secondCard.append(recreated.element);
  f.session.mutated([{ type: "childList", target: secondCard, addedNodes: [recreated.element], removedNodes: [] }]); await tick();
  assert.equal(f.candidates().length, requestCount); assert.equal(recreated.node.nodeValue, "新客户可享三天免费试用。开始试用前需要提供付款信息。");
  assert.equal(first.node.nodeValue, "中文译文"); f.session.dispose();
});

test("closed native details expose only the summary and opening queues their newly visible answer", async () => {
  const answer = paragraph("The folded public English answer should be translated only after the disclosure opens.");
  const summary = new Element("SUMMARY", [new Text("How does the free trial work?")]);
  const details = new Element("DETAILS", [summary, answer.element]); details.open = false;
  const f = fixture([details, paragraph("An English article explains the residential connection and its free trial.").element]);
  const snapshot = await extractPageSnapshot(f.environment);
  assert.ok(snapshot.blocks.every(block => !block.text.includes("folded public")));
  assert.ok(snapshot.blocks.some(block => block.text === "How does the free trial work?"));
  await enabled(f); approved(f); finish(f);
  details.open = true; f.session.mutated([{ type: "attributes", target: details, attributeName: "open" }]); await tick();
  assert.deepEqual(f.candidate().blocks.map(block => block.text), [answer.node.nodeValue]); approved(f); finish(f);
  const count = f.candidates().length;
  details.open = false; f.session.mutated([{ type: "attributes", target: details, attributeName: "open" }]); await tick();
  details.open = true; f.session.mutated([{ type: "attributes", target: details, attributeName: "open" }]); await tick();
  assert.equal(f.candidates().length, count); assert.equal(answer.node.nodeValue, "中文译文"); f.session.dispose();
});

test("a folded streaming paragraph resumes when reopened while unrelated finished translations remain intact", async () => {
  const good = paragraph("The public first paragraph already has an accepted complete translation.");
  const folded = paragraph("Another public English answer is still being translated when its disclosure closes.");
  const f = fixture([good.element, folded.element]); let visible = true;
  f.environment.isRenderedText = node => node !== folded.node || visible;
  await enabled(f); const before = f.candidate(); approved(f);
  f.session.receive(message(before, "pageAutoTranslations", { updates: [{ id: before.blocks[0].id, text: "完成译文", complete: true }, { id: before.blocks[1].id, text: "流式前缀" }] }));
  visible = false; f.session.mutated([{ type: "attributes", target: folded.element, attributeName: "style" }]); await tick();
  const count = f.candidates().length;
  visible = true; f.session.mutated([{ type: "attributes", target: folded.element, attributeName: "style" }]); await tick();
  assert.equal(f.candidates().length, count); assert.equal(good.node.nodeValue, "完成译文");
  f.session.receive(message(before, "pageAutoTranslations", { updates: [{ id: before.blocks[1].id, text: "完整的答案译文", complete: true }], finished: true }));
  await tick(); assert.equal(folded.node.nodeValue, "完整的答案译文"); assert.equal(f.candidates().length, count); f.session.dispose();
});

test("text range proof respects zero-height overflow clipping without rejecting below-fold document prose", () => {
  const clipped = paragraph("A CSS collapsed answer is not rendered even if its text range has dimensions.");
  const bottom = paragraph("A below-fold public paragraph remains readable without requiring a viewport intersection.", 9000);
  clipped.element.style = { overflowY: "hidden", overflowX: "visible" };
  clipped.element.getBoundingClientRect = () => ({ top: 0, bottom: 0, left: 0, right: 500, width: 500, height: 0 });
  const f = fixture([clipped.element, bottom.element]); let selected;
  f.document.defaultView = { getComputedStyle: element => element.style };
  f.document.createRange = () => ({ selectNodeContents: node => { selected = node; }, collapse() {},
    getClientRects: () => [{ top: selected === bottom.node ? 9000 : 12, bottom: selected === bottom.node ? 9020 : 32, left: 0, right: 400, width: 400, height: 20 }] });
  const rendered = createRenderedTextTester(f.document);
  assert.equal(rendered(clipped.node), false); assert.equal(rendered(bottom.node), true); f.session.dispose();
});

test("disclosure observer watches open state and final layout visibility events and removes its listeners when disabled", () => {
  const callbacks = new Map(), mutations = [], watched = [];
  const document = { addEventListener: (type, fn) => callbacks.set(type, fn), removeEventListener: type => callbacks.delete(type) };
  const manager = createOpenRootObserver({ document, onMutations: items => mutations.push(...items),
    createObserver: () => ({ observe: (_tree, options) => watched.push(options), disconnect() {} }) });
  manager.configure(true);
  assert.ok(watched[0].attributeFilter.includes("open")); assert.ok(watched[0].attributeFilter.includes("aria-expanded"));
  const panel = new Element("DIV"); callbacks.get("transitionend")({ type: "transitionend", target: panel, propertyName: "max-height" });
  assert.equal(mutations.at(-1).target, panel);
  const count = mutations.length; callbacks.get("transitionend")({ type: "transitionend", target: panel, propertyName: "background-color" });
  assert.equal(mutations.length, count);
  callbacks.get("toggle")({ type: "toggle", target: new Element("DETAILS") }); assert.equal(mutations.length, count + 1);
  manager.dispose(); assert.equal(callbacks.size, 0);
});
test("below-fold Chinese mixed prose blocks both text and image translation locally", async () => {
  const first = paragraph("An English article with a long explanation about a browser page.");
  const bottom = paragraph("这段中文正文属于同一个中英混排页面，不需要翻译。", 9000);
  const f = fixture([first.element, bottom.element]); await enabled(f);
  assert.equal(f.candidates().length, 0);
  assert.equal(f.statuses.at(-1).detail, "mixed_chinese_english");
  assert.deepEqual(f.decisions.at(-1), { enabled: true, assessed: true, allowImages: false, generation: 1, revision: 1 }); f.session.dispose();
});
test("Chinese-only page keeps native text while allowing independent image judgement", async () => {
  const f = fixture([paragraph("这是中文正文，不需要翻译。图片中的外语仍然可以单独判断和翻译。").element]); await enabled(f);
  assert.equal(f.candidates().length, 0); assert.equal(f.decisions.at(-1).allowImages, true); f.session.dispose();
});

test("view-mode visibility evidence requires actual translated text and survives original view", async () => {
  const item = paragraph("A public English paragraph has meaningful text for the translation toolbar."); const f = fixture([item.element]);
  await enabled(f); assert.equal(f.statuses.at(-1).hasTranslation, false); approved(f);
  const candidate = f.candidate(); f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "译文" }] }));
  assert.equal(f.statuses.at(-1).hasTranslation, true);
  f.session.control("view-original"); assert.equal(f.statuses.at(-1).hasTranslation, true); assert.ok(item.node.nodeValue.startsWith("A public"));
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, failed: true }], finished: true }));
  assert.equal(f.statuses.at(-1).hasTranslation, false); f.session.dispose();
});
test("large loaded page translates every paragraph beyond 128, one bounded batch in flight", async () => {
  const paragraphs = Array.from({ length: 221 }, (_, index) => paragraph(`Paragraph ${index} contains meaningful English body text about the translation workflow.`, index < 3 ? 0 : 2000 + index * 100));
  const f = fixture(paragraphs.map(item => item.element)); await enabled(f);
  const first = f.candidate(); assert.equal(f.candidates().length, 1);
  assert.ok(first.blocks.length <= 48); assert.ok(first.samples.length <= 64); assert.equal(first.pageSummary.proseBlocks, 221);
  approved(f, first); assert.equal(f.candidates().length, 1);
  const seen = new Set();
  while (f.session.inspect().jobs > 0) {
    const candidate = f.candidate(); for (const block of candidate.blocks) seen.add(block.id);
    assert.ok(candidate.blocks.length <= 48);
    assert.ok(candidate.blocks.reduce((sum, block) => sum + block.text.length + (block.context?.length || 0), 0) <= 32768);
    finish(f, candidate);
  }
  assert.equal(seen.size, 221); assert.equal(f.candidates().length, 5);
  assert.ok(paragraphs.every(item => item.node.nodeValue === "中文译文"));
  assert.equal(f.session.inspect().pending, 0); assert.equal(f.statuses.at(-1).state, "completed"); f.session.dispose();
});
test("streams real text progressively without HTML execution and original toggle restores exactly", async () => {
  const item = paragraph("  Original English sentence, with surrounding whitespace preserved.\n");
  const f = fixture([item.element]); await enabled(f); const candidate = f.candidate(); approved(f);
  const id = candidate.blocks[0].id;
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id, text: "逐渐", complete: false }] }));
  assert.equal(item.node.nodeValue, "  逐渐\n");
  f.session.control("original"); assert.equal(item.node.nodeValue, "  Original English sentence, with surrounding whitespace preserved.\n");
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id, text: "<b>文字而非HTML</b>", complete: true }], finished: true }));
  assert.ok(item.node.nodeValue.startsWith("  Original"));
  f.session.control("original"); assert.equal(item.node.nodeValue, "  <b>文字而非HTML</b>\n");
  f.session.configure({ generation: 2, enabled: false }); assert.equal(item.node.nodeValue, "  Original English sentence, with surrounding whitespace preserved.\n"); f.session.dispose();
});
test("pause cancels old revision, restores source and rejects late translation", async () => {
  const item = paragraph("Original readable English prose about parallel translation."); const f = fixture([item.element]); await enabled(f);
  const old = f.candidate(); approved(f); f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "部分译文" }] }));
  f.session.control("pause"); assert.equal(item.node.nodeValue, "Original readable English prose about parallel translation.");
  assert.equal(f.decisions.at(-1).allowImages, false); assert.equal(f.decisions.at(-1).enabled, false);
  assert.equal(f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "晚到文字", complete: true }], finished: true })), false);
  f.session.control("resume"); await tick(); assert.equal(f.candidates().length, 2);
  assert.notEqual(f.candidate().revision, old.revision); f.session.dispose();
});
test("external source mutation is retained, old job discarded and rescanned using originals", async () => {
  const first = paragraph("Original English first paragraph has sufficiently descriptive prose.");
  const second = paragraph("Original English second paragraph is also useful for judging."); const f = fixture([first.element, second.element]); await enabled(f);
  const old = f.candidate(); approved(f); finish(f);
  first.node.nodeValue = "Updated English source from the application, never overwrite this text.";
  f.session.mutated([{ type: "characterData", target: first.node }]); await tick();
  assert.equal(second.node.nodeValue, "中文译文");
  assert.ok(f.candidate().blocks.some(block => block.text.includes("Updated English")));
  assert.ok(!f.candidate().blocks.some(block => block.text.includes("second paragraph")));
  assert.equal(f.candidate().pageSummary.hanCharacters, 0);
  assert.equal(f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "旧译文", complete: true }], finished: true })), false);
  assert.ok(first.node.nodeValue.startsWith("Updated")); f.session.dispose();
});
test("own stream mutations never invalidate the language decision or resubmit translated Chinese", async () => {
  const item = paragraph("The original English paragraph explains how safe source snapshots work."); const f = fixture([item.element]); await enabled(f);
  const candidate = f.candidate(); approved(f); const revision = f.session.inspect().revision;
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "自己的中文译文" }] }));
  f.session.mutated([{ type: "characterData", target: item.node }]); await tick();
  assert.equal(f.session.inspect().revision, revision); assert.equal(f.candidates().length, 1); f.session.dispose();
});
test("long single text node merges independent chunk streams in source order", async () => {
  const source = "A long English sentence that should be divided at natural sentence boundaries. ".repeat(40);
  const item = paragraph(source); const f = fixture([item.element]); await enabled(f); const candidate = f.candidate(); approved(f);
  assert.ok(candidate.blocks.length > 1); assert.ok(candidate.blocks.every(block => block.text.length <= 900));
  assert.ok(candidate.blocks.every(block => block.context.length <= 512));
  const reverse = [...candidate.blocks].reverse();
  for (const block of reverse) f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: block.id, text: `译${candidate.blocks.indexOf(block)}`, complete: true }] }));
  f.session.receive(message(candidate, "pageAutoTranslations", { finished: true }));
  assert.equal(item.node.nodeValue, candidate.blocks.map((block, index) => `译${index} `).join(""));
  f.session.control("pause"); assert.equal(item.node.nodeValue, source); f.session.dispose();
});
test("failed and missing block completion rolls partial output back to the original text", async () => {
  const a = paragraph("First English paragraph that will fail during streaming."); const b = paragraph("Second English paragraph that never receives a final record.");
  const f = fixture([a.element, b.element]); await enabled(f); const candidate = f.candidate(); approved(f);
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "不完整译文" }, { id: candidate.blocks[1].id, text: "片段" }] }));
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, failed: true }], finished: true }));
  assert.ok(a.node.nodeValue.startsWith("First English")); assert.ok(b.node.nodeValue.startsWith("Second English")); f.session.dispose();
});
test("one failed group keeps its original and final status reports partial success without prefix warnings", async () => {
  const good = paragraph("A good English paragraph has a valid translation that will be shown.");
  const bad = paragraph("A guarded English paragraph should remain in the original language."); const f = fixture([good.element, bad.element]);
  await enabled(f); const candidate = f.candidate(); approved(f);
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "正常部分" }, { id: candidate.blocks[1].id, failed: true }] }));
  assert.equal(f.statuses.at(-1).state, "translating");
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "正常段落译文", complete: true }], finished: true }));
  assert.equal(good.node.nodeValue, "正常段落译文"); assert.ok(bad.node.nodeValue.startsWith("A guarded English"));
  assert.equal(f.statuses.at(-1).state, "partial"); assert.equal(f.statuses.at(-1).completed, 1); assert.equal(f.statuses.at(-1).failedCount, 1);
  assert.equal(f.statuses.at(-1).total, 2); f.session.dispose();
});
test("hidden tab accepts an already-started assessment but never starts new jobs", async () => {
  const item = paragraph("An English paragraph that belongs to the active browser tab."); const f = fixture([item.element]); await enabled(f);
  const old = f.candidate(); f.document.visibilityState = "hidden"; f.session.visibilityChanged();
  assert.equal(f.session.receive(message(old, "pageAutoAssessment", { needsTranslation: true, allowImages: true })), true);
  assert.equal(f.decisions.at(-1).allowImages, false); f.document.visibilityState = "visible";
  f.session.configure({ generation: 2, enabled: true, active: false }); await tick(); assert.equal(f.candidates().length, 1); f.session.dispose();
});
test("hidden/code mutation tickers do not cancel or restart an active page translation", async () => {
  const item = paragraph("An active English page has readable body text that is being translated.");
  const hidden = new Element("DIV"), code = new Element("PRE"); hidden.style.display = "none";
  const f = fixture([item.element, hidden, code]); await enabled(f); const revision = f.session.inspect().revision;
  const ticker = hidden.append(new Text("a hidden live ticker")), codeText = code.append(new Text("console.log('tick')"));
  f.session.mutated([{ type: "childList", target: hidden, addedNodes: [ticker] }, { type: "childList", target: code, addedNodes: [codeText] }]); await tick();
  assert.equal(f.session.inspect().revision, revision); assert.equal(f.candidates().length, 1); f.session.dispose();
});
test("class/style uncovering loaded prose reassesses, irrelevant class animation does not", async () => {
  const first = paragraph("The visible English paragraph has enough meaningful prose to translate.");
  const hidden = paragraph("A previously folded English paragraph is now visible and also needs translation."); hidden.element.style.display = "none";
  const f = fixture([first.element, hidden.element]); await enabled(f); const initial = f.session.inspect().revision;
  first.element.attributes.class = "animated"; f.session.mutated([{ type: "attributes", target: first.element, attributeName: "class" }]); await tick();
  assert.equal(f.session.inspect().revision, initial);
  hidden.element.style.display = "block"; f.session.mutated([{ type: "attributes", target: hidden.element, attributeName: "style" }]); await tick();
  assert.equal(f.candidates().length, 1); approved(f); finish(f); await tick();
  assert.ok(f.session.inspect().revision > initial); assert.ok(f.candidate().blocks.some(block => /previously folded/u.test(block.text))); f.session.dispose();
});
test("rejected later batch releases slot and still attempts all remaining loaded paragraphs", async () => {
  const f = fixture(Array.from({ length: 101 }, (_, index) => paragraph(`English body paragraph ${index} is sufficiently descriptive for automatic translation.`).element));
  const defaultSend = f.environment.send; let candidates = 0;
  f.environment.send = async packet => {
    const accepted = await defaultSend(packet); if (packet.type === "pageAutoCandidate" && ++candidates === 2) return { accepted: false, reason: "busy" }; return accepted;
  };
  // The session already captured send; make a new instance using the updated seam.
  f.session.dispose(); f.session = createPageAutoSession(f.environment); await enabled(f); approved(f); finish(f); await tick();
  assert.equal(f.candidates().length, 3); assert.equal(f.candidate().blocks.length, 5);
  finish(f); assert.equal(f.session.inspect().pending, 0);
  assert.equal(f.statuses.at(-1).state, "partial"); assert.equal(f.statuses.at(-1).failedCount, 48); assert.equal(f.statuses.at(-1).completed, 53); f.session.dispose();
});
test("late batch rejection rolls an already streamed half sentence back before the remaining batch continues", async () => {
  const paragraphs = Array.from({ length: 101 }, (_, index) => paragraph(`English body paragraph ${index} contains sufficiently descriptive source prose.`));
  const f = fixture(paragraphs.map(item => item.element)), defaultSend = f.environment.send; let count = 0, rejectSecond;
  f.environment.send = async packet => {
    const accepted = await defaultSend(packet);
    if (packet.type === "pageAutoCandidate" && ++count === 2) return new Promise(resolve => { rejectSecond = resolve; }); return accepted;
  };
  f.session.dispose(); f.session = createPageAutoSession(f.environment); await enabled(f); approved(f); finish(f); await tick();
  const second = f.candidate(); f.session.receive(message(second, "pageAutoTranslations", { updates: [{ id: second.blocks[0].id, text: "半句" }] }));
  assert.equal(paragraphs[48].node.nodeValue, "半句"); rejectSecond({ accepted: false, reason: "busy" }); await tick();
  assert.ok(paragraphs[48].node.nodeValue.startsWith("English body paragraph 48")); finish(f);
  assert.equal(f.statuses.at(-1).state, "partial"); assert.equal(f.statuses.at(-1).failedCount, 48); assert.equal(f.statuses.at(-1).completed, 53); f.session.dispose();
});
test("BFCache suspend and persisted pageshow retain complete translation without a new request", async () => {
  const item = paragraph("An English paragraph survives browser back and forward cache navigation."); const f = fixture([item.element]); await enabled(f);
  const old = f.candidate(); approved(f); finish(f); f.session.suspend(); assert.equal(item.node.nodeValue, "中文译文");
  assert.equal(f.session.receive(message(old, "pageAutoTranslations", { updates: [], finished: true })), false);
  f.session.resumeDocument(); await tick(); assert.equal(f.candidates().length, 1);
  assert.equal(item.node.nodeValue, "中文译文"); assert.ok(f.session.inspect().revision > old.revision); f.session.dispose();
});
test("identical enabled config at DOMContentLoaded starts a document_start scan skipped before body existed", async () => {
  const f = fixture([paragraph("A newly navigated English page needs automatic translation after its body loads.").element]);
  const body = f.document.body; f.document.body = null;
  const config = { generation: 1, enabled: true, active: true, targetLanguage: "简体中文" };
  f.session.configure(config); await tick(); assert.equal(f.candidates().length, 0);
  f.document.body = body; f.session.configure(config); await tick(); assert.equal(f.candidates().length, 1); f.session.dispose();
});
test("Chinese navigation and numeric labels remain original and are never sent to the translator", async () => {
  const navText = new Text("首页 登录 消息 12"), nav = new Element("NAV", [navText]);
  const counter = paragraph("2026-10-08 12:34:56"), body = paragraph("This English body article is independently readable and needs automatic translation.");
  const f = fixture([nav, counter.element, body.element]); await enabled(f); const candidate = f.candidate();
  assert.deepEqual(candidate.blocks.map(block => block.text), [body.node.nodeValue]); approved(f); finish(f);
  assert.equal(navText.nodeValue, "首页 登录 消息 12"); assert.equal(counter.node.nodeValue, "2026-10-08 12:34:56");
  navText.nodeValue = "首页 登录 消息 13"; const revision = f.session.inspect().revision;
  f.session.mutated([{ type: "characterData", target: navText }]); await tick(); assert.equal(f.session.inspect().revision, revision); f.session.dispose();
});
test("Han-only inline fragments within Japanese paragraphs remain eligible with their shared kana context", async () => {
  const kanji = new Text("東京都"), strong = new Element("STRONG", [kanji]);
  const p = new Element("P", [strong, new Text("に住んでいます。日本語の記事を読んで情報を確認しています。")]);
  const f = fixture([p]); await enabled(f); const candidate = f.candidate();
  assert.ok(candidate.blocks.some(block => block.text === "東京都"));
  assert.equal(new Set(candidate.blocks.map(block => block.groupId)).size, 1); approved(f); finish(f);
  assert.equal(p.childNodes[0], strong); assert.equal(kanji.nodeValue, "中文译文"); f.session.dispose();
});
test("Japanese page prose identifies standalone Han headings while Chinese navigation remains original", async () => {
  const title = new Text("図書館案内"), heading = new Element("H1", [title]), header = new Element("HEADER", [heading]);
  const navText = new Text("首页 登录 消息"), nav = new Element("NAV", [navText]);
  const body = paragraph("図書館の利用方法を説明します。日本語の記事を読みながら開館時間を確認してください。");
  const f = fixture([header, nav, body.element]); await enabled(f); const candidate = f.candidate();
  assert.ok(candidate.blocks.some(block => block.text === "図書館案内"));
  assert.ok(candidate.blocks.every(block => block.text !== "首页 登录 消息"));
  approved(f); finish(f); assert.equal(title.nodeValue, "中文译文"); assert.equal(navText.nodeValue, "首页 登录 消息"); f.session.dispose();
});
test("unrecorded and recorded visible numeric clocks never cancel streaming body translation", async () => {
  const body = paragraph("The English article remains translated while the visible clock keeps counting.");
  const clock = paragraph("10:12:00"), f = fixture([body.element, clock.element]); await enabled(f); const candidate = f.candidate(); approved(f);
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "正在翻译正文" }] }));
  const freshClock = f.body.append(new Text("1000")), revision = f.session.inspect().revision;
  f.session.mutated([{ type: "childList", target: f.body, addedNodes: [freshClock] }]);
  for (let index = 1; index <= 5; index++) {
    clock.node.nodeValue = `10:12:0${index}`; freshClock.nodeValue = String(1000 + index);
    f.session.mutated([{ type: "characterData", target: clock.node }, { type: "characterData", target: freshClock }]);
  }
  await tick(); assert.equal(f.session.inspect().revision, revision); assert.equal(f.candidates().length, 1);
  assert.equal(body.node.nodeValue, "正在翻译正文"); f.session.dispose();
});
test("strict AM/PM and UTC clocks keep the stream and changed version prose is rescanned after unaffected work", async () => {
  const body = paragraph("This English article remains translated while several browser clocks keep counting.");
  const local = paragraph("12:34:00 PM"), utc = paragraph("2026-10-08 12:34:00 UTC+08:00");
  const version = paragraph("Version 12.3 has changed the browser translation settings."); const f = fixture([body.element, local.element, utc.element, version.element]);
  await enabled(f); const candidate = f.candidate(); approved(f);
  assert.ok(candidate.blocks.every(block => !/12:34/u.test(block.text)));
  const revision = f.session.inspect().revision;
  for (let index = 1; index <= 3; index++) {
    local.node.nodeValue = `12:34:0${index} PM`; utc.node.nodeValue = `2026-10-08 12:34:0${index} UTC+08:00`;
    f.session.mutated([{ type: "characterData", target: local.node }, { type: "characterData", target: utc.node }]);
  }
  await tick(); assert.equal(f.session.inspect().revision, revision); assert.equal(f.candidates().length, 1);
  version.node.nodeValue = "Version 12.4 has changed the browser translation settings.";
  f.session.mutated([{ type: "characterData", target: version.node }]); await tick();
  assert.equal(f.session.inspect().revision, revision); assert.equal(f.candidates().length, 1);
  finish(f, candidate, "正文已完成"); await tick();
  assert.equal(body.node.nodeValue, "正文已完成");
  assert.ok(f.session.inspect().revision > revision); assert.ok(f.candidate().blocks.some(block => /Version 12.4/u.test(block.text))); f.session.dispose();
});
test("a rotating announcement retires only its own blocks while a slow title translation completes", async () => {
  const title = paragraph("One API for every model built for production."), announcement = paragraph("A new model announcement appears here."), f = fixture([title.element, announcement.element]);
  await enabled(f); const candidate = f.candidate(); approved(f);
  const heading = candidate.blocks.find(block => block.text.startsWith("One API"));
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: heading.id, text: "统一接口" }] }));
  for (let i = 0; i < 4; i++) { announcement.node.nodeValue = `Another model announcement is shown in rotation number ${i}.`;
    f.session.mutated([{ type: "characterData", target: announcement.node }]); await tick(); }
  assert.equal(f.session.inspect().revision, candidate.revision); assert.equal(title.node.nodeValue, "统一接口");
  assert.equal(f.sent.some(packet => packet.type === "pageAutoCancel"), false);
  finish(f, candidate, "统一接口，面向生产环境。"); await tick(); assert.equal(title.node.nodeValue, "统一接口，面向生产环境。");
  assert.equal(f.candidates().length, 2); assert.equal(f.candidate().blocks.length, 1);
  assert.match(f.candidate().blocks[0].text, /number 3/); f.session.dispose();
});
test("numeric placeholders becoming English or Japanese prose are discovered and reassessed", async () => {
  for (const next of ["A new English paragraph arrives after the numeric loading placeholder finishes.", "東京の大学で日本語の文章を読みながら研究内容を説明します。旅行記事には駅の場所が掲載されています。"] ) {
    const body = paragraph("An existing English article provides enough original body prose for classification.");
    const placeholder = paragraph("0"), f = fixture([body.element, placeholder.element]); await enabled(f); approved(f); finish(f);
    const oldRevision = f.session.inspect().revision; placeholder.node.nodeValue = next;
    f.session.mutated([{ type: "characterData", target: placeholder.node }]); await tick();
    assert.ok(f.session.inspect().revision > oldRevision); assert.ok(f.candidate().blocks.some(block => block.text === next)); f.session.dispose();
  }
});
test("numeric placeholder becoming Chinese prose reassesses a now mixed page using original English", async () => {
  const body = paragraph("The existing English article needs translation until Chinese prose is loaded.");
  const placeholder = paragraph("0"), f = fixture([body.element, placeholder.element]); await enabled(f); approved(f); finish(f);
  placeholder.node.nodeValue = "这是后来加载的中文正文，当前页面已经成为中英混排，应当保持原文而不要继续自动翻译。";
  f.session.mutated([{ type: "characterData", target: placeholder.node }]); await tick();
  assert.equal(f.statuses.at(-1).detail, "mixed_chinese_english"); assert.equal(f.decisions.at(-1).allowImages, false);
  assert.equal(f.candidates().length, 1); assert.ok(body.node.nodeValue.startsWith("The existing English")); f.session.dispose();
});
test("translated node externally becoming numeric is retired without restarting peer paragraphs", async () => {
  const first = paragraph("This English paragraph is replaced by a numeric live value later.");
  const second = paragraph("This peer English paragraph should keep its valid streamed translation.");
  const f = fixture([first.element, second.element]); await enabled(f); const candidate = f.candidate(); approved(f);
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "第一段" }, { id: candidate.blocks[1].id, text: "另一段" }] }));
  const revision = f.session.inspect().revision; first.node.nodeValue = "42";
  f.session.mutated([{ type: "characterData", target: first.node }]);
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "晚到旧段", complete: true }, { id: candidate.blocks[1].id, text: "另一段完整译文", complete: true }], finished: true }));
  assert.equal(f.session.inspect().revision, revision); assert.equal(first.node.nodeValue, "42"); assert.equal(second.node.nodeValue, "另一段完整译文");
  assert.equal(f.candidates().length, 1); f.session.control("pause"); assert.equal(first.node.nodeValue, "42"); f.session.dispose();
});
test("minority Chinese notes outside the bounded sample do not veto a foreign paragraph", async () => {
  const english = "A complete English browser article contains meaningful explanatory sentences. ".repeat(35);
  for (const chinese of ["这里的中文说明可以直接阅读", "这一段已经包含了可读的中文", "请保持这些已经可读的中文段落"]) {
    for (const text of [chinese + english, english + chinese]) {
      const item = paragraph(text), f = fixture([item.element]); await enabled(f);
      assert.equal(f.candidates().length, 1); assert.ok(f.candidate().blocks.map(block => block.text).join("").includes(chinese));
      assert.equal(item.node.nodeValue, text); f.session.dispose();
    }
  }
});

test("three view modes keep a live stream and exact original while bilingual uses one semantic paragraph annotation", async () => {
  const before = new Text("Read the "), label = new Text("complete guide"), after = new Text(" before translating the entire browser page.");
  const anchor = new Element("A", [new Element("STRONG", [label])], { href: "/guide" });
  const p = new Element("P", [before, anchor, after]), original = p.textContent, f = fixture([p]); await enabled(f);
  const candidate = f.candidate(); approved(f); const revision = f.session.inspect().revision;
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "阅读" }] }));
  f.session.control("view-bilingual");
  assert.equal(before.nodeValue, "Read the "); assert.equal(label.nodeValue, "complete guide");
  assert.equal(p.childNodes[1], anchor); assert.equal(f.session.inspect().annotations.size, 1);
  const annotation = p.childNodes.at(-1); assert.equal(annotation.getAttribute("data-screen-translator"), "page-bilingual");
  assert.equal(annotation.childNodes[0].textContent, "译文"); assert.equal(annotation.childNodes[1].textContent, "阅读 complete guide before translating the entire browser page.");
  f.session.mutated([{ type: "childList", target: p, addedNodes: [annotation] }, { type: "childList", target: annotation.childNodes[1], addedNodes: [new Text("自己的译文")] }]);
  assert.equal(f.session.inspect().revision, revision); assert.equal(f.candidates().length, 1);
  const snapshot = await extractPageSnapshot(f.environment, f.session.inspect().records);
  assert.equal(snapshot.pageSummary.hanCharacters, 0); assert.ok(snapshot.samples.every(sample => !sample.text.includes("译文")));
  f.session.control("view-original"); assert.equal(p.textContent, original); assert.equal(f.session.inspect().annotations.size, 0);
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: candidate.blocks.map((block, index) => ({ id: block.id, text: ["阅读", "完整指南", "再翻译网页。"][index], complete: true })), finished: true }));
  assert.equal(p.textContent, original);
  f.session.control("view-bilingual"); assert.equal(p.childNodes.at(-1).childNodes[1].textContent, "阅读 完整指南 再翻译网页。");
  f.session.control("view-translation"); assert.equal(p.textContent, "阅读 完整指南 再翻译网页。"); assert.equal(f.session.inspect().annotations.size, 0);
  assert.equal(f.session.inspect().revision, revision); assert.equal(f.candidates().length, 1);
  assert.equal(f.sent.filter(packet => packet.type === "pageAutoControl" || packet.type === "pageAutoCancel").length, 0);
  assert.deepEqual(f.views.at(-1), { documentToken: "document-token", generation: 1, revision, enabled: true, mode: "translation" });
  f.session.dispose(); assert.equal(p.textContent, original);
});
test("bilingual rendering treats model HTML as literal and failure rolls the translated line back", async () => {
  const item = paragraph("Original English paragraph about source handling and safe inline presentation."), f = fixture([item.element]); await enabled(f); const candidate = f.candidate(); approved(f);
  f.session.control("view-bilingual"); f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "<img onerror=alert(1)>" }] }));
  const annotation = item.element.childNodes.at(-1); assert.equal(annotation.childNodes[1].textContent, "<img onerror=alert(1)>");
  assert.equal(item.node.nodeValue, candidate.blocks[0].text);
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, failed: true }], finished: true }));
  assert.equal(f.session.inspect().annotations.size, 0); assert.equal(item.element.childNodes.length, 1); assert.equal(f.statuses.at(-1).state, "partial"); f.session.dispose();
});
test("unfinished bilingual prefix disappears on a finished batch without a valid final result", async () => {
  const item = paragraph("This English source paragraph cannot be replaced by an unvalidated incomplete answer."), f = fixture([item.element]); await enabled(f); const candidate = f.candidate(); approved(f);
  f.session.control("view-bilingual"); f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "半段" }] }));
  assert.equal(f.session.inspect().annotations.size, 1);
  f.session.receive(message(candidate, "pageAutoTranslations", { finished: true }));
  assert.equal(f.session.inspect().annotations.size, 0); assert.equal(item.element.childNodes.length, 1); f.session.dispose();
});
test("new loaded paragraphs preserve unrelated completed and in-flight translations without a source flash", async () => {
  const done = paragraph("A completed English paragraph retains its translation through dynamic page loading.");
  const streaming = paragraph("An English paragraph is still streaming while the next article section becomes visible."), f = fixture([done.element, streaming.element]); await enabled(f);
  const old = f.candidate(); approved(f); f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "已完成", complete: true }, { id: old.blocks[1].id, text: "正在输出" }] }));
  const fresh = paragraph("Newly loaded English prose should join the translation queue without resetting previous paragraphs."); f.body.append(fresh.element);
  f.session.mutated([{ type: "childList", target: f.body, addedNodes: [fresh.element] }]);
  assert.equal(done.node.nodeValue, "已完成"); assert.equal(streaming.node.nodeValue, "正在输出"); await tick();
  assert.equal(done.node.nodeValue, "已完成"); assert.equal(streaming.node.nodeValue, "正在输出");
  assert.equal(f.candidates().length, 1);
  assert.equal(f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[1].id, text: "中文译文", complete: true }], finished: true })), true);
  await tick(); assert.equal(f.candidate().blocks.length, 1); assert.ok(!f.candidate().blocks.some(block => block.text.startsWith("A completed")));
  approved(f); finish(f); assert.equal(done.node.nodeValue, "已完成"); assert.equal(streaming.node.nodeValue, "中文译文"); f.session.dispose();
});
test("changed inline context invalidates its entire group while unrelated translated paragraphs stay stable", async () => {
  const prefix = new Text("The deadline is "), date = new Text("Thursday, May 8"), suffix = new Text(" and registration costs 12 dollars."), strong = new Element("STRONG", [date]);
  const p = new Element("P", [prefix, strong, suffix]), peer = paragraph("An unrelated English paragraph keeps the correct previously completed Chinese translation."), f = fixture([p, peer.element]); await enabled(f); approved(f); finish(f);
  const old = f.candidate(); date.nodeValue = "Friday, May 9"; f.session.mutated([{ type: "characterData", target: date }]);
  assert.equal(prefix.nodeValue, "The deadline is "); assert.equal(suffix.nodeValue, " and registration costs 12 dollars."); assert.equal(date.nodeValue, "Friday, May 9"); assert.equal(peer.node.nodeValue, "中文译文"); await tick();
  assert.equal(f.candidate().blocks.length, 3); assert.ok(f.candidate().blocks.every(block => /Friday, May 9/u.test(block.context)));
  assert.equal(f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "周四", complete: true }], finished: true })), false); f.session.dispose();
});
test("scroll-driven hiding and showing retains originals, completed translation and no duplicate candidate", async () => {
  const item = paragraph("This English paragraph is temporarily collapsed by a browser scroll animation."), f = fixture([item.element]); await enabled(f); approved(f); finish(f);
  const source = f.session.inspect().records.get(item.node).source;
  item.element.style.display = "none"; f.session.mutated([{ type: "attributes", target: item.element, attributeName: "style" }]); await tick();
  assert.equal(item.node.nodeValue, "中文译文"); assert.equal(f.candidates().length, 1);
  item.element.style.display = "block"; item.element.top = 9000; f.session.mutated([{ type: "attributes", target: item.element, attributeName: "style" }]); await tick();
  assert.equal(item.node.nodeValue, "中文译文"); assert.equal(f.candidates().length, 1);
  f.session.control("view-original"); assert.equal(item.node.nodeValue, source); f.session.control("view-translation"); assert.equal(item.node.nodeValue, "中文译文"); f.session.dispose();
});
test("hidden tab and same-generation active toggle keep completed source snapshots across return", async () => {
  const item = paragraph("A translated English paragraph should not flash back when the same browser tab resumes."), f = fixture([item.element]); await enabled(f); approved(f); finish(f);
  f.document.visibilityState = "hidden"; f.session.visibilityChanged(); assert.equal(item.node.nodeValue, "中文译文");
  f.document.visibilityState = "visible"; f.session.visibilityChanged(); await tick(); assert.equal(item.node.nodeValue, "中文译文"); assert.equal(f.candidates().length, 1);
  const config = f.session.inspect().config; f.session.configure({ ...config, active: false }); assert.equal(item.node.nodeValue, "中文译文");
  f.session.configure({ ...config, active: true }); await tick(); assert.equal(item.node.nodeValue, "中文译文"); assert.equal(f.candidates().length, 1); f.session.dispose();
});
test("pause and configuration disable remove bilingual annotations and prevent stale response reappearance", async () => {
  const item = paragraph("An English paragraph streams into a bilingual view before the user pauses translation."), f = fixture([item.element]); await enabled(f); const old = f.candidate(); approved(f);
  f.session.control("view-bilingual"); f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "进行中" }] }));
  f.session.control("pause"); assert.equal(f.session.inspect().annotations.size, 0); assert.equal(item.element.childNodes.length, 1); assert.equal(item.node.nodeValue, old.blocks[0].text);
  f.session.control("view-original"); assert.equal(f.statuses.at(-1).state, "paused"); assert.equal(f.session.inspect().paused, true);
  f.session.control("view-bilingual"); assert.equal(f.statuses.at(-1).state, "paused"); assert.equal(f.session.inspect().annotations.size, 0);
  assert.equal(f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "晚到", complete: true }], finished: true })), false);
  f.session.control("resume"); await tick(); approved(f); finish(f); assert.equal(f.session.inspect().viewMode, "bilingual"); assert.equal(f.session.inspect().annotations.size, 1);
  f.session.configure({ generation: 2, enabled: false }); assert.equal(f.session.inspect().annotations.size, 0); assert.equal(item.node.nodeValue, old.blocks[0].text); assert.equal(f.views.at(-1).enabled, false); f.session.dispose();
});
test("virtualised page repainting the unchanged original immediately reapplies its prefix without another job", async () => {
  const item = paragraph("A virtualised reader writes its unchanged original paragraph when the scroll position changes."), f = fixture([item.element]); await enabled(f); const candidate = f.candidate(); approved(f);
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "正在翻译" }] }));
  const revision = f.session.inspect().revision;
  item.node.nodeValue = candidate.blocks[0].text; f.session.mutated([{ type: "characterData", target: item.node }]);
  assert.equal(item.node.nodeValue, "正在翻译"); assert.equal(f.session.inspect().revision, revision); assert.equal(f.candidates().length, 1);
  finish(f); item.node.nodeValue = candidate.blocks[0].text; f.session.mutated([{ type: "characterData", target: item.node }]);
  assert.equal(item.node.nodeValue, "中文译文"); assert.equal(f.candidates().length, 1); f.session.dispose();
});
test("unchanged original repaint cannot retain a neighbour's obsolete changed fact in the same paragraph", async () => {
  const prefix = new Text("The deadline is "), date = new Text("Thursday, May 8"), suffix = new Text(" and registration costs 12 dollars.");
  const p = new Element("P", [prefix, new Element("STRONG", [date]), suffix]), f = fixture([p]); await enabled(f); approved(f); finish(f);
  prefix.nodeValue = "The deadline is "; date.nodeValue = "Friday, May 9";
  f.session.mutated([{ type: "characterData", target: prefix }, { type: "characterData", target: date }]); await tick();
  assert.equal(prefix.nodeValue, "The deadline is "); assert.equal(date.nodeValue, "Friday, May 9"); assert.equal(suffix.nodeValue, " and registration costs 12 dollars.");
  assert.equal(f.candidate().blocks.length, 3); assert.ok(f.candidate().blocks.every(block => /Friday, May 9/u.test(block.context))); f.session.dispose();
});
test("unchanged Han heading returns to its native value when removed Japanese prose changes paragraph eligibility", async () => {
  const heading = new Text("東京都"), h1 = new Element("H1", [heading]);
  const japanese = paragraph("これは日本語の記事です。ページ内の見出しと本文を一緒に翻訳してください。");
  const english = paragraph("An existing English paragraph still needs translation after the Japanese section is removed."), f = fixture([h1, japanese.element, english.element]); await enabled(f); approved(f); finish(f);
  assert.equal(heading.nodeValue, "中文译文"); japanese.node.isConnected = false; f.body.childNodes = f.body.childNodes.filter(node => node !== japanese.element);
  f.session.mutated([{ type: "childList", target: f.body, removedNodes: [japanese.element] }]); await tick();
  assert.equal(heading.nodeValue, "東京都"); assert.equal(english.node.nodeValue, "中文译文"); assert.equal(f.candidates().length, 1); f.session.dispose();
});

test("textContent replacement rebinds its in-flight block and final result without a source flash or reassessment", async () => {
  const item = paragraph("This original English paragraph is replaced by new Text nodes during reader scrolling."), f = fixture([item.element]);
  await enabled(f); const old = f.candidate(); approved(f);
  f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "正在输出" }] }));
  const revision = f.session.inspect().revision; item.node.isConnected = false;
  item.element.textContent = old.blocks[0].text; const replacement = item.element.childNodes[0];
  f.session.mutated([{ type: "childList", target: item.element, removedNodes: [item.node], addedNodes: [replacement] }]);
  assert.equal(replacement.nodeValue, "正在输出"); assert.equal(f.session.inspect().revision, revision);
  finish(f, old, "完整译文"); assert.equal(replacement.nodeValue, "完整译文"); await tick(); assert.equal(f.candidates().length, 1);
  f.session.control("view-original"); assert.equal(replacement.nodeValue, old.blocks[0].text); f.session.dispose();
});

test("cloned visible Chinese text keeps its true English source instead of vetoing the partly translated page", async () => {
  const item = paragraph("The original English text must remain the language evidence after a rendered clone."), peer = paragraph("Another English paragraph still awaits its complete streamed translation."), f = fixture([item.element, peer.element]);
  await enabled(f); const old = f.candidate(); approved(f);
  f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "这是一段很长的已翻译中文内容。", complete: true }] }));
  const rendered = item.node.nodeValue; item.node.isConnected = false; item.element.textContent = rendered;
  const clone = item.element.childNodes[0]; f.session.mutated([{ type: "childList", target: item.element, removedNodes: [item.node], addedNodes: [clone] }]);
  assert.equal(clone.nodeValue, rendered); assert.equal(f.session.inspect().records.get(clone).source, old.blocks[0].text);
  finish(f, old, "后续译文"); assert.equal(peer.node.nodeValue, "后续译文");
  f.session.control("view-original"); assert.equal(clone.nodeValue, old.blocks[0].text); assert.equal(f.candidates().length, 1); f.session.dispose();
});

test("detached complete paragraph is restored immediately into new DOM nodes from the bounded local cache", async () => {
  const item = paragraph("This complete English paragraph will be removed and recreated by a virtual list."), f = fixture([item.element]);
  await enabled(f); approved(f); finish(f, f.candidate(), "从缓存恢复的中文段落");
  const source = f.candidate().blocks[0].text; item.node.isConnected = false; f.body.childNodes = [];
  f.session.mutated([{ type: "childList", target: f.body, removedNodes: [item.element] }]); await tick();
  assert.equal(f.session.inspect().records.size, 0);
  const replacement = paragraph(source); f.body.append(replacement.element);
  f.session.mutated([{ type: "childList", target: f.body, addedNodes: [replacement.element] }]);
  assert.equal(replacement.node.nodeValue, "从缓存恢复的中文段落"); await tick();
  assert.equal(f.candidates().length, 1); assert.equal(f.statuses.at(-1).state, "completed");
  assert.equal(f.session.inspect().cachedGroups, 1); f.session.control("view-original"); assert.equal(replacement.node.nodeValue, source); f.session.dispose();
});

test("completed cache rebinds inline links but never carries a changed neighbouring fact", async () => {
  const prefix = new Text("The deadline is "), date = new Text("Thursday, May 8"), suffix = new Text(" and registration costs 12 dollars.");
  const p = new Element("P", [prefix, new Element("STRONG", [date]), suffix]), f = fixture([p]); await enabled(f); approved(f); finish(f);
  for (const node of [prefix, date, suffix]) node.isConnected = false;
  const nextPrefix = new Text("The deadline is "), nextDate = new Text("Friday, May 9"), nextSuffix = new Text(" and registration costs 12 dollars.");
  const link = new Element("A", [nextDate], { href: "/new-date" }), replacement = new Element("P", [nextPrefix, link, nextSuffix]);
  f.body.childNodes = []; f.body.append(replacement);
  f.session.mutated([{ type: "childList", target: f.body, removedNodes: [p], addedNodes: [replacement] }]);
  assert.equal(nextPrefix.nodeValue, "The deadline is "); await tick(); assert.equal(f.candidates().length, 2);
  assert.ok(f.candidate().blocks.every(block => /Friday, May 9/u.test(block.context))); assert.equal(link.getAttribute("href"), "/new-date"); f.session.dispose();
});

test("stream prefixes and failed final records never enter the completed cache when an entire paragraph is recreated", async () => {
  for (const failed of [false, true]) {
    const source = "This English paragraph has only an incomplete or failed translation and must be retried.", item = paragraph(source), f = fixture([item.element]);
    await enabled(f); const old = f.candidate(); approved(f);
    f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "不完整前缀" }] }));
    if (failed) f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, failed: true }], finished: true }));
    item.node.isConnected = false; f.body.childNodes = [];
    f.session.mutated([{ type: "childList", target: f.body, removedNodes: [item.element] }]); await tick();
    const replacement = paragraph(source); f.body.append(replacement.element);
    f.session.mutated([{ type: "childList", target: f.body, addedNodes: [replacement.element] }]);
    assert.equal(replacement.node.nodeValue, source); assert.equal(f.session.inspect().cachedGroups, 0); await tick();
    assert.equal(f.candidates().length, 2); assert.equal(f.candidate().blocks[0].text, source); f.session.dispose();
  }
});

test("whole paragraph clone of rendered Chinese rebinds the in-flight source rather than introducing a mixed-language veto", async () => {
  const source = "This English paragraph is copied as a complete rendered paragraph by the browser reader.", item = paragraph(source), peer = paragraph("Other English prose still needs its valid final output while the paragraph gets cloned."), f = fixture([item.element, peer.element]);
  await enabled(f); const old = f.candidate(); approved(f);
  f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "这一段是已显示的中文前缀。" }] }));
  const clone = paragraph(item.node.nodeValue); item.node.isConnected = false; f.body.childNodes = [peer.element]; f.body.append(clone.element);
  const revision = f.session.inspect().revision;
  f.session.mutated([{ type: "childList", target: f.body, removedNodes: [item.element], addedNodes: [clone.element] }]);
  assert.equal(f.session.inspect().records.get(clone.node).source, source); assert.equal(f.session.inspect().revision, revision);
  finish(f, old, "完整翻译保留"); assert.equal(clone.node.nodeValue, "完整翻译保留"); assert.equal(peer.node.nodeValue, "完整翻译保留");
  assert.equal(f.candidates().length, 1); f.session.control("view-original"); assert.equal(clone.node.nodeValue, source); f.session.dispose();
});

test("bilingual paragraph clone retires its orphan annotation and toggles exactly one translated note", async () => {
  const source = "This English paragraph is cloned with its bilingual annotation by a framework reader.", item = paragraph(source), f = fixture([item.element]);
  await enabled(f); approved(f); finish(f, f.candidate(), "单份双语译文"); f.session.control("view-bilingual");
  const annotation = item.element.childNodes.at(-1), clone = paragraph(source);
  const note = new Element("SPAN", [new Element("SPAN", [new Text("译文")], { "data-screen-translator": "page-bilingual-label" }),
    new Element("SPAN", [new Text(annotation.childNodes[1].textContent)], { "data-screen-translator": "page-bilingual-text" })], { "data-screen-translator": "page-bilingual" });
  clone.element.append(note); item.node.isConnected = false; f.body.childNodes = []; f.body.append(clone.element);
  f.session.mutated([{ type: "childList", target: f.body, removedNodes: [item.element], addedNodes: [clone.element] }]);
  assert.equal(clone.element.childNodes.filter(node => node.getAttribute?.("data-screen-translator") === "page-bilingual").length, 1);
  assert.equal(clone.element.childNodes.at(-1).childNodes[1].textContent, "单份双语译文");
  f.session.control("view-original"); assert.equal(clone.element.textContent, source); assert.equal(clone.element.childNodes.length, 1);
  f.session.control("view-bilingual"); assert.equal(clone.element.childNodes.length, 2); assert.equal(f.candidates().length, 1); f.session.dispose();
});

test("tab switch retains job IDs and buffers its final result without painting the inactive document", async () => {
  const item = paragraph("This English paragraph continues its already-started translation while the tab is hidden."), f = fixture([item.element]);
  await enabled(f); const old = f.candidate(); approved(f); const config = f.session.inspect().config, revision = f.session.inspect().revision;
  f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "当前前缀" }] }));
  f.document.visibilityState = "hidden"; f.session.visibilityChanged(); f.session.configure({ ...config, active: false });
  assert.equal(f.session.inspect().revision, revision); finish(f, old, "后台已完成的译文"); assert.equal(item.node.nodeValue, "当前前缀");
  f.document.visibilityState = "visible"; f.session.configure({ ...config, active: true }); f.session.visibilityChanged();
  assert.equal(item.node.nodeValue, "后台已完成的译文"); await tick(); assert.equal(f.candidates().length, 1);
  assert.equal(f.sent.filter(packet => packet.type === "pageAutoCancel").length, 0); assert.equal(f.session.inspect().revision, revision); f.session.dispose();
});

test("returning before a stream finishes cannot replace its block IDs during a deferred rescan", async () => {
  const item = paragraph("This English paragraph is still streaming when the browser tab returns to the foreground."), f = fixture([item.element]);
  await enabled(f); const old = f.candidate(); approved(f); const revision = f.session.inspect().revision;
  f.document.visibilityState = "hidden"; f.session.visibilityChanged(); f.document.visibilityState = "visible"; f.session.visibilityChanged(); await tick();
  assert.equal(f.session.inspect().revision, revision); assert.equal(f.candidates().length, 1);
  finish(f, old, "流式完成仍然保留"); await tick(); assert.equal(item.node.nodeValue, "流式完成仍然保留"); assert.equal(f.candidates().length, 1); f.session.dispose();
});

test("rejected-page decision survives tab switches and identical source DOM reconstruction without another judgement", async () => {
  const source = "This foreign English text is deliberately rejected by the existing local page classifier.", item = paragraph(source), f = fixture([item.element]);
  await enabled(f); const old = f.candidate(); f.session.receive(message(old, "pageAutoAssessment", { needsTranslation: false, allowImages: false, reason: "not_needed" }));
  f.document.visibilityState = "hidden"; f.session.visibilityChanged(); f.document.visibilityState = "visible"; f.session.visibilityChanged(); await tick();
  const replacement = paragraph(source); item.node.isConnected = false; f.body.childNodes = []; f.body.append(replacement.element);
  f.session.mutated([{ type: "childList", target: f.body, removedNodes: [item.element], addedNodes: [replacement.element] }]); await tick();
  assert.equal(f.candidates().length, 1); assert.equal(f.session.inspect().assessed, false); assert.equal(f.decisions.at(-1).allowImages, false); f.session.dispose();
});

test("generation or target changes isolate completed cache and pause cannot accept hidden late output", async () => {
  const source = "This English paragraph must not reuse a translation from a different target or configuration.", item = paragraph(source), f = fixture([item.element]);
  await enabled(f); approved(f); finish(f); assert.equal(f.session.inspect().cachedGroups, 1);
  f.session.configure({ ...f.session.inspect().config, generation: 2, targetLanguage: "日本語" }); await tick();
  assert.equal(f.session.inspect().cachedGroups, 0); assert.equal(f.candidates().length, 2); assert.equal(f.candidate().blocks[0].text, source);
  const next = f.candidate(); f.session.control("pause"); f.document.visibilityState = "hidden";
  assert.equal(f.session.receive(message(next, "pageAutoTranslations", { updates: [{ id: next.blocks[0].id, text: "晚到结果", complete: true }], finished: true })), false);
  assert.equal(item.node.nodeValue, source); f.session.dispose();
});

test("changed numeric fact inside a translated paragraph invalidates peer context while an unrelated ticker does not", async () => {
  const prefix = new Text("The interest rate is "), amount = new Text("12"), suffix = new Text(" percent until the end of this year.");
  const p = new Element("P", [prefix, new Element("SPAN", [amount]), suffix]), ticker = paragraph("100"), f = fixture([p, ticker.element]);
  await enabled(f); approved(f); finish(f); const revision = f.session.inspect().revision;
  ticker.node.nodeValue = "101"; f.session.mutated([{ type: "characterData", target: ticker.node }]); assert.equal(f.session.inspect().revision, revision);
  amount.nodeValue = "13"; f.session.mutated([{ type: "characterData", target: amount }]);
  assert.equal(prefix.nodeValue, "The interest rate is "); assert.equal(suffix.nodeValue, " percent until the end of this year.");
  await tick(); assert.equal(f.candidates().length, 2); assert.ok(f.candidate().blocks.every(block => /is 13 percent/u.test(block.context)));
  assert.equal(amount.nodeValue, "13"); f.session.dispose();
});

test("numeric textContent replacement also invalidates a paragraph's changed fact without restarting a separate ticker", async () => {
  const prefix = new Text("The interest rate is "), amount = new Text("12"), suffix = new Text(" percent until the end of this year."), span = new Element("SPAN", [amount]);
  const p = new Element("P", [prefix, span, suffix]), ticker = paragraph("100"), f = fixture([p, ticker.element]);
  await enabled(f); approved(f); finish(f); const revision = f.session.inspect().revision;
  ticker.node.isConnected = false; ticker.element.textContent = "101";
  f.session.mutated([{ type: "childList", target: ticker.element, removedNodes: [ticker.node], addedNodes: ticker.element.childNodes }]);
  assert.equal(f.session.inspect().revision, revision); amount.isConnected = false; span.textContent = "13";
  f.session.mutated([{ type: "childList", target: span, removedNodes: [amount], addedNodes: span.childNodes }]);
  assert.equal(prefix.nodeValue, "The interest rate is "); assert.equal(suffix.nodeValue, " percent until the end of this year.");
  await tick(); assert.equal(f.candidates().length, 2); assert.ok(f.candidate().blocks.every(block => /is 13 percent/u.test(block.context)));
  assert.equal(span.textContent, "13"); f.session.dispose();
});

test("same numeric node clone retains paragraph jobs without introducing a false fact change", async () => {
  const prefix = new Text("The interest rate is "), amount = new Text("12"), suffix = new Text(" percent until the end of this year."), span = new Element("SPAN", [amount]);
  const p = new Element("P", [prefix, span, suffix]), f = fixture([p]); await enabled(f); const candidate = f.candidate(); approved(f);
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "利率为" }] }));
  const revision = f.session.inspect().revision; amount.isConnected = false; span.textContent = "12";
  f.session.mutated([{ type: "childList", target: span, removedNodes: [amount], addedNodes: span.childNodes }]);
  assert.equal(f.session.inspect().revision, revision); finish(f, candidate); assert.equal(f.candidates().length, 1);
  assert.equal(span.textContent, "12"); assert.equal(prefix.nodeValue, "中文译文 "); f.session.dispose();
});

test("unrelated paragraph clone cannot suppress a numeric fact change in the same mutation", async () => {
  const prefix = new Text("The interest rate is "), amount = new Text("12"), suffix = new Text(" percent until the end of this year.");
  const peer = paragraph("An unrelated English paragraph will be cloned in the same framework update."), div = new Element("DIV", [prefix, amount, suffix, peer.element]), f = fixture([div]);
  await enabled(f); approved(f); finish(f); const clone = paragraph(peer.node.nodeValue), changed = new Text("13");
  amount.isConnected = false; peer.node.isConnected = false; div.childNodes = [prefix, suffix]; div.append(changed, clone.element);
  // Preserve the authored order for the changed DIV group.
  div.childNodes = [prefix, changed, suffix, clone.element];
  f.session.mutated([{ type: "childList", target: div, removedNodes: [amount, peer.element], addedNodes: [changed, clone.element] }]);
  assert.equal(prefix.nodeValue, "The interest rate is "); assert.equal(suffix.nodeValue, " percent until the end of this year.");
  await tick(); assert.equal(f.candidates().length, 2); assert.ok(f.candidate().blocks.every(block => /is 13 percent/u.test(block.context)));
  assert.equal(clone.node.nodeValue, "中文译文"); f.session.dispose();
});

test("stable cache scope reuses final translations and decisions across generation changes while retiring unfinished prefixes", async () => {
  const done = paragraph("A complete English paragraph belongs to the same stable translation provider cache scope."), pending = paragraph("An incomplete English paragraph must not keep its prefix when its generation retires."), f = fixture([done.element, pending.element]);
  const config = { generation: 1, enabled: true, active: true, targetLanguage: "简体中文", cacheScope: "a".repeat(64), cacheEpoch: 0 };
  f.session.configure(config); await tick(); const old = f.candidate(); approved(f);
  f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "成功译文", complete: true }, { id: old.blocks[1].id, text: "退休前缀" }] }));
  f.session.configure({ ...config, generation: 2 }); assert.equal(done.node.nodeValue, "成功译文"); assert.ok(pending.node.nodeValue.startsWith("An incomplete"));
  assert.equal(f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[1].id, text: "旧完成", complete: true }], finished: true })), false);
  await tick(); assert.equal(f.candidate().blocks.length, 1); assert.ok(f.candidate().blocks[0].text.startsWith("An incomplete"));
  assert.equal(f.session.inspect().cachedGroups, 1); assert.equal(f.session.inspect().assessed, true); finish(f, f.candidate()); f.session.dispose();
});

test("scope change and explicit cache clear cannot restore another provider's completed paragraph or old page decision", async () => {
  for (const change of [{ cacheScope: "b".repeat(64) }, { cacheEpoch: 1 }]) {
    const item = paragraph("This English paragraph uses a complete translation that must be cleared by a new cache identity."), f = fixture([item.element]);
    const config = { generation: 1, enabled: true, active: true, cacheScope: "a".repeat(64), cacheEpoch: 0 };
    f.session.configure(config); await tick(); approved(f); finish(f); assert.equal(f.session.inspect().cachedGroups, 1);
    f.session.configure({ ...config, ...change, generation: 2 });
    assert.equal(f.session.inspect().cachedGroups, 0); assert.equal(f.session.inspect().rememberedDecisions, 0);
    assert.ok(item.node.nodeValue.startsWith("This English")); await tick(); assert.equal(f.candidates().length, 2); f.session.dispose();
  }
});

test("stable provider cache scope preserves a negative page judgement across a generation update", async () => {
  const item = paragraph("This English-looking page is rejected by the existing classifier and retains its decision."), f = fixture([item.element]);
  const config = { generation: 1, enabled: true, active: true, cacheScope: "a".repeat(64), cacheEpoch: 0 };
  f.session.configure(config); await tick(); const old = f.candidate();
  f.session.receive(message(old, "pageAutoAssessment", { needsTranslation: false, allowImages: false, reason: "not_needed" }));
  f.session.configure({ ...config, generation: 2 }); await tick();
  assert.equal(f.candidates().length, 1); assert.equal(f.session.inspect().assessed, false); assert.equal(f.decisions.at(-1).allowImages, false);
  assert.equal(item.node.nodeValue, old.blocks[0].text); f.session.dispose();
});

test("malformed cache identity is ignored without wiping a valid translation or accepting a new job generation", async () => {
  const item = paragraph("This translated English paragraph must survive a malformed or missing opaque cache identity."), f = fixture([item.element]);
  const config = { generation: 1, enabled: true, active: true, cacheScope: "a".repeat(64), cacheEpoch: 0 };
  f.session.configure(config); await tick(); approved(f); finish(f);
  assert.equal(f.session.configure({ ...config, generation: 2, cacheScope: "http://invalid" }), false);
  assert.equal(f.session.configure({ ...config, generation: 2, cacheEpoch: "1" }), false);
  assert.equal(item.node.nodeValue, "中文译文"); assert.equal(f.session.inspect().config.generation, 1); assert.equal(f.candidates().length, 1); f.session.dispose();
});

test("manual controls require explicit bridge permission and do not auto-start when available with master disabled", async () => {
  const item = paragraph("A readable English page remains untouched until the user explicitly starts manual translation."), f = fixture([item.element]);
  assert.equal(f.session.control("manual-page"), false); await manualReady(f);
  assert.equal(f.candidates().length, 0); assert.equal(f.statuses.at(-1).state, "manual_ready");
  assert.equal(f.session.control("manual-page"), true); await tick(); assert.equal(f.candidate().manualMode, "page");
  finish(f); assert.equal(item.node.nodeValue, "中文译文"); assert.equal(f.session.inspect().manualRunning, false);
  f.session.control("view-original"); assert.ok(item.node.nodeValue.startsWith("A readable English"));
  f.session.control("view-translation"); assert.equal(item.node.nodeValue, "中文译文"); f.session.dispose();
});

test("manual whole-page bypasses mixed/short page veto but filters native metadata and preserves meaningful short names", async () => {
  const keep = ["这是不需要翻译的中文正文，保持原样。", "https://example.invalid/keep", "qa.test@example.invalid", "Sarah Meyssonnier / Reuters", "2026-10-09", "E = mc²", "1 2 3"];
  const items = [...keep.map(paragraph), paragraph("Help!"), paragraph("Alice"), paragraph("An English article must be translated on an explicitly requested mixed page.")], f = fixture(items.map(item => item.element));
  await manualReady(f, true); assert.equal(f.candidates().length, 0); assert.equal(f.statuses.at(-1).detail, "mixed_chinese_english");
  f.session.control("manual-page"); await tick(); const candidate = f.candidate();
  assert.deepEqual(candidate.blocks.map(block => block.text), ["Help!", "Alice", "An English article must be translated on an explicitly requested mixed page."]);
  assert.equal(candidate.manualMode, "page"); finish(f); assert.ok(keep.every((text, index) => items[index].node.nodeValue === text)); f.session.dispose();
});

test("selection Range emits only chosen safe slices and preserves unselected text and the original link node", async () => {
  const before = new Text("Before the selected words, "), first = new Text("visitors can reserve "), linkText = new Text("a quiet desk"), last = new Text(" beside the garden"), after = new Text("; after the selected words stays unchanged.");
  const link = new Element("A", [linkText], { href: "/desk" }), p = new Element("P", [before, new Element("SPAN", [first]), link, new Element("SPAN", [last]), after]);
  const secret = paragraph("Unselected private foreign prose from another paragraph must never enter the manual request."), f = fixture([p, secret.element]);
  await manualReady(f); select(f, [first, linkText, last]); f.session.control("manual-selection"); await tick(); const candidate = f.candidate();
  assert.equal(candidate.manualMode, "selection"); assert.deepEqual(candidate.blocks.map(block => block.text), ["visitors can reserve", "a quiet desk", "beside the garden"]);
  assert.ok(candidate.blocks.every(block => block.context === "visitors can reserve a quiet desk beside the garden" && block.contextKind === "selection"));
  assert.ok(!JSON.stringify(candidate).includes(secret.node.nodeValue)); assert.ok(!JSON.stringify(candidate).includes(before.nodeValue)); assert.ok(!JSON.stringify(candidate).includes(after.nodeValue));
  finish(f, candidate); assert.equal(before.nodeValue, "Before the selected words, "); assert.equal(after.nodeValue, "; after the selected words stays unchanged.");
  assert.equal(p.childNodes[2], link); assert.equal(link.getAttribute("href"), "/desk"); assert.equal(secret.node.nodeValue, "Unselected private foreign prose from another paragraph must never enter the manual request.");
  f.session.control("view-original"); assert.equal(first.nodeValue, "visitors can reserve "); assert.equal(linkText.nodeValue, "a quiet desk"); f.session.dispose();
});

test("partial Text selection translates exact offsets without editing before or after the requested substring", async () => {
  const source = "Keep this prefix. Translate only this sentence. Keep this suffix.", item = paragraph(source), f = fixture([item.element]); await manualReady(f);
  const start = source.indexOf("Translate"), end = source.indexOf(" Keep this suffix"); select(f, [item.node], start, end);
  f.session.control("manual-selection"); await tick(); const candidate = f.candidate(); assert.equal(candidate.blocks[0].text, "Translate only this sentence.");
  assert.equal(candidate.blocks[0].sourceStart, start); assert.equal(candidate.blocks[0].sourceLength, end - start); assert.equal(candidate.blocks[0].sourceGroupLength, source.length);
  finish(f, candidate, "仅翻译这一句。"); assert.equal(item.node.nodeValue, "Keep this prefix. 仅翻译这一句。 Keep this suffix.");
  f.session.control("view-bilingual"); assert.equal(item.node.nodeValue, source); assert.equal(item.element.childNodes.at(-1).childNodes[1].textContent, "仅翻译这一句。");
  f.session.dispose();
});

test("empty selection waits for a deliberate selection instead of silently sending the page", async () => {
  const item = paragraph("This English paragraph is not authorised for submission when the selection is empty."), f = fixture([item.element]); await manualReady(f);
  f.document.getSelection = () => ({ isCollapsed: true, rangeCount: 0 }); f.session.control("manual-selection");
  assert.equal(f.candidates().length, 0); assert.equal(f.statuses.at(-1).state, "selection_pending");
  select(f, [item.node]); f.session.selectionChanged(); assert.equal(f.statuses.at(-1).state, "selection_ready"); assert.equal(f.candidates().length, 0);
  f.session.control("manual-selection"); await tick(); assert.equal(f.candidates().length, 1); f.session.dispose();
});

test("manual selection excludes protected editable nodes and already-translated display text instead of exposing their source", async () => {
  const editable = new Text("Private editor text must never be submitted even if it is selected."), editor = new Element("DIV", [editable]); editor.isContentEditable = true;
  const good = paragraph("This English paragraph will receive a complete translation before it is selected again."), f = fixture([editor, good.element]); await manualReady(f);
  select(f, [editable]); f.session.control("manual-selection"); await tick(); assert.equal(f.candidates().length, 0);
  f.session.control("manual-page"); await tick(); finish(f, f.candidate(), "这是已经翻译过的中文段落。"); const count = f.candidates().length;
  select(f, [good.node]); f.session.control("manual-selection"); await tick(); assert.equal(f.candidates().length, count); assert.equal(f.statuses.at(-1).state, "selection_not_needed"); f.session.dispose();
});

test("same-generation automatic flag updates preserve explicit manual streaming with the master off", async () => {
  const item = paragraph("A manual translation remains in flight while the automatic master switch changes."), f = fixture([item.element]); await manualReady(f);
  f.session.control("manual-page"); await tick(); const candidate = f.candidate(), revision = f.session.inspect().revision;
  f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "手动前缀" }] }));
  const config = f.session.inspect().config; f.session.configure({ ...config, enabled: true }); f.session.configure({ ...config, enabled: false });
  assert.equal(item.node.nodeValue, "手动前缀"); assert.equal(f.session.inspect().revision, revision); assert.equal(f.session.inspect().jobs, 1);
  finish(f, candidate, "手动译文完成"); assert.equal(item.node.nodeValue, "手动译文完成"); assert.equal(f.candidates().length, 1); f.session.dispose();
});

test("pause cancels a manual job by its exact request ID and rejects its late response while auto is off", async () => {
  const item = paragraph("A manual job is cancelled specifically when the user presses the floating pause button."), f = fixture([item.element]); await manualReady(f);
  f.session.control("manual-page"); await tick(); const candidate = f.candidate(); f.session.control("pause");
  const cancelled = f.sent.filter(packet => packet.type === "pageAutoCancel").at(-1); assert.equal(cancelled.requestId, candidate.requestId);
  assert.equal(f.session.receive(message(candidate, "pageAutoTranslations", { updates: [{ id: candidate.blocks[0].id, text: "晚到", complete: true }], finished: true })), false);
  assert.equal(item.node.nodeValue, candidate.blocks[0].text); f.session.control("manual-page"); await tick(); assert.equal(f.candidates().length, 2); f.session.dispose();
});

test("full source SHA256 and UTF16 source ranges remain exact through whitespace and inline boundaries", async () => {
  const first = new Text("  Read 😀 the "), word = new Text("complete guide"), last = new Text(" before proceeding. \n"), p = new Element("P", [first, new Element("STRONG", [word]), last]), f = fixture([p]);
  const snapshot = await extractPageSnapshot(f.environment), source = first.nodeValue + word.nodeValue + last.nodeValue;
  const normal = canonicalSource(source).text, expected = require("node:crypto").createHash("sha256").update(normal).digest("hex");
  assert.ok(snapshot.blocks.every(block => block.sourceGroupHash === expected && block.sourceGroupLength === normal.length && block.contextKind === "source"));
  for (const block of snapshot.blocks) assert.equal(normal.slice(block.sourceStart, block.sourceStart + block.sourceLength), block.text);
  assert.ok(snapshot.blocks.every(block => block.context === normal)); f.session.dispose();
});

test("bounded batching keeps a fitting semantic paragraph together instead of cutting it at block 48", async () => {
  const initial = Array.from({ length: 47 }, (_, index) => paragraph(`English paragraph ${index} provides enough body text for reliable local page judgement.`).element);
  const linked = new Element("P", [new Text("Read the "), new Element("A", [new Text("complete guide")]), new Text(" before translating the browser page.")]), f = fixture([...initial, linked]);
  await enabled(f); approved(f); const first = f.candidate(); assert.equal(first.blocks.length, 47); finish(f);
  const next = f.candidate(); assert.equal(next.blocks.length, 3); assert.equal(new Set(next.blocks.map(block => block.groupId)).size, 1);
  assert.equal(new Set(next.blocks.map(block => block.sourceGroupHash)).size, 1); f.session.dispose();
});

test("paused manual results cannot be resurrected by a cached virtual-list repaint", async () => {
  const source = "This English paragraph stays in its original form after the user has explicitly paused translation.", item = paragraph(source), f = fixture([item.element]);
  await manualReady(f); f.session.control("manual-page"); await tick(); finish(f); f.session.control("pause");
  item.node.isConnected = false; f.body.childNodes = []; f.session.mutated([{ type: "childList", target: f.body, removedNodes: [item.element] }]);
  const next = paragraph(source); f.body.append(next.element); f.session.mutated([{ type: "childList", target: f.body, addedNodes: [next.element] }]);
  assert.equal(f.session.inspect().paused, true); assert.equal(next.node.nodeValue, source); assert.equal(f.candidates().length, 1);
  f.session.control("manual-page"); await tick(); assert.equal(f.session.inspect().paused, false); assert.equal(next.node.nodeValue, "中文译文"); f.session.dispose();
});

test("selection source drift before its first scan cancels without ever submitting newly inserted private text", async () => {
  for (const observe of [false, true]) {
    const source = "prefix Please translate these words. private suffix", item = paragraph(source), f = fixture([item.element]); await manualReady(f);
    select(f, [item.node], source.indexOf("Please"), source.indexOf(" private")); f.session.control("manual-selection");
    item.node.nodeValue = "PRIVATE SECRET THAT WAS NEVER SELECTED. " + source;
    if (observe) f.session.mutated([{ type: "characterData", target: item.node }]);
    await tick(); assert.equal(f.candidates().length, 0); assert.equal(f.statuses.at(-1).state, "selection_changed");
    assert.ok(item.node.nodeValue.startsWith("PRIVATE SECRET")); assert.equal(f.session.inspect().manualRunning, false); f.session.dispose();
  }
});

test("selection source drift during streaming rejects the old result and requires another deliberate selection", async () => {
  const source = "Keep this prefix. Translate this selected sentence. Keep this suffix.", item = paragraph(source), f = fixture([item.element]); await manualReady(f);
  select(f, [item.node], source.indexOf("Translate"), source.indexOf(" Keep this suffix")); f.session.control("manual-selection"); await tick(); const old = f.candidate();
  f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "前缀" }] }));
  item.node.nodeValue = "Entirely new authored English text should not be selected by the old offsets.";
  f.session.mutated([{ type: "characterData", target: item.node }]); await tick();
  assert.equal(f.candidates().length, 1); assert.equal(f.statuses.at(-1).state, "selection_changed");
  assert.equal(f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "晚到", complete: true }], finished: true })), false);
  assert.equal(item.node.nodeValue, "Entirely new authored English text should not be selected by the old offsets."); f.session.dispose();
});

test("unrelated newly loaded paragraphs do not cancel or expand an in-flight explicit selection", async () => {
  const item = paragraph("Translate this chosen English paragraph without submitting text that appears elsewhere later."), f = fixture([item.element]); await manualReady(f);
  select(f, [item.node]); f.session.control("manual-selection"); await tick(); const old = f.candidate(), revision = f.session.inspect().revision;
  const unselected = paragraph("This later unselected private paragraph must never be silently submitted by a manual selection job."); f.body.append(unselected.element);
  f.session.mutated([{ type: "childList", target: f.body, addedNodes: [unselected.element] }]); await tick();
  assert.equal(f.session.inspect().revision, revision); assert.equal(f.candidates().length, 1); finish(f, old);
  assert.equal(item.node.nodeValue, "中文译文"); assert.ok(unselected.node.nodeValue.startsWith("This later unselected")); f.session.dispose();
});

test("a cached partial selection restores its exact piece boundaries after separate unmount and remount", async () => {
  const source = "Keep prefix. Translate this middle sentence. Keep suffix.", item = paragraph(source), f = fixture([item.element]); await manualReady(f);
  select(f, [item.node], source.indexOf("Translate"), source.indexOf(" Keep suffix")); f.session.control("manual-selection"); await tick(); finish(f, f.candidate(), "只翻译中间。");
  item.node.isConnected = false; f.body.childNodes = []; f.session.mutated([{ type: "childList", target: f.body, removedNodes: [item.element] }]);
  const next = paragraph(source); f.body.append(next.element); f.session.mutated([{ type: "childList", target: f.body, addedNodes: [next.element] }]);
  assert.equal(next.node.nodeValue, "Keep prefix. 只翻译中间。 Keep suffix."); assert.equal(f.candidates().length, 1);
  f.session.control("view-original"); assert.equal(next.node.nodeValue, source); f.session.control("view-bilingual");
  assert.equal(next.element.childNodes.at(-1).childNodes[1].textContent, "只翻译中间。"); f.session.dispose();
});

test("canonical source proof survives HTML whitespace hydration and viewport prioritisation while restoration stays raw", async () => {
  const source = " \nRead\t the  ", word = "complete guide", tail = "\r\n before proceeding. \n", first = new Text(source), linked = new Text(word), last = new Text(tail), p = new Element("P", [first, new Element("A", [linked]), last]), f = fixture([p]);
  const initial = await extractPageSnapshot(f.environment), hash = initial.blocks[0].sourceGroupHash;
  first.nodeValue = "Read the "; last.nodeValue = " before proceeding."; p.top = 9000;
  const hydrated = await extractPageSnapshot(f.environment);
  assert.equal(hydrated.blocks[0].sourceGroupHash, hash); assert.equal(hydrated.pageSummary.sourceHash, initial.pageSummary.sourceHash);
  assert.deepEqual(hydrated.samples, initial.samples); assert.equal(hydrated.blocks[0].visible, false);
  assert.deepEqual(hydrated.blocks.map(block => [block.text, block.sourceStart, block.sourceLength]), initial.blocks.map(block => [block.text, block.sourceStart, block.sourceLength]));
  const ranges = canonicalSource("  A😀\t\nB\u00a0C \n"); assert.equal(ranges.text, "A😀 B\u00a0C"); assert.equal(ranges.positions[5], 3); f.session.dispose();
});

test("long semantic groups split within the transport budget but retain a complete stable canonical source proof", async () => {
  const nodes = Array.from({ length: 55 }, (_, index) => new Element("SPAN", [new Text(`English fragment ${index} is meaningful. `)])), p = new Element("P", nodes), f = fixture([p]);
  await enabled(f); approved(f); const first = f.candidate(); assert.equal(first.blocks.length, 48); const hash = first.blocks[0].sourceGroupHash, length = first.blocks[0].sourceGroupLength;
  finish(f); const next = f.candidate(); assert.equal(next.blocks.length, 7); assert.ok(next.blocks.every(block => block.sourceGroupHash === hash && block.sourceGroupLength === length));
  assert.ok(next.blocks[0].sourceStart > first.blocks.at(-1).sourceStart); f.session.dispose();
});

test("pure Han selection retains reliable local Japanese evidence without uploading the unselected kana", async () => {
  const kanji = new Text("東京都"), context = new Text("に住んでいます。日本語の説明を読むことができます。"), p = new Element("P", [new Element("A", [kanji]), context]), f = fixture([p]); await manualReady(f);
  select(f, [kanji]); f.session.control("manual-selection"); await tick(); const candidate = f.candidate();
  assert.equal(candidate.blocks[0].text, "東京都"); assert.equal(candidate.blocks[0].foreignHanContext, true);
  assert.equal(candidate.blocks[0].context, "東京都"); assert.ok(!JSON.stringify(candidate).includes(context.nodeValue)); f.session.dispose();
});

test("insecure content contexts obtain canonical SHA only from the local extension and use secure random IDs", async () => {
  const vm = require("node:vm"), fs = require("node:fs"), crypto = require("node:crypto"), calls = [];
  const sandbox = { ScreenTranslatorPageAutoPolicy: require("../page-auto-policy.js"), crypto: { getRandomValues: bytes => crypto.webcrypto.getRandomValues(bytes) },
    TextEncoder, module: { exports: {} } };
  vm.runInNewContext(fs.readFileSync(require.resolve("../page-auto-content.js"), "utf8"), sandbox);
  const insecure = sandbox.module.exports, item = paragraph("An HTTP page can still cache its translated text using the local extension SHA helper."), f = fixture([item.element]);
  const snapshot = await insecure.extractPageSnapshot({ ...f.environment, hashSourceGroup: async text => { calls.push(text); return crypto.createHash("sha256").update(text).digest("hex"); } });
  assert.equal(snapshot.blocks[0].sourceGroupHash, crypto.createHash("sha256").update(item.node.nodeValue).digest("hex"));
  assert.deepEqual(calls, [item.node.nodeValue]); assert.equal(f.sent.length, 0);
  assert.match(insecure.randomIdentifier(), /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u); f.session.dispose();
});

test("live translated normalize merges two known fine Text records before observer invalidation and keeps peers translated", async () => {
  for (const characterFirst of [true, false]) {
    const first = new Text("The library opens every Tuesday and "), second = new Text("visitors can reserve a quiet desk near the garden."), p = new Element("P", [first, second]);
    const peer = paragraph("A separate English paragraph remains translated while the first paragraph normalizes."), f = fixture([p, peer.element]);
    const authored = first.nodeValue + second.nodeValue; await enabled(f); approved(f); finish(f); const revision = f.session.inspect().revision;
    const rendered = first.nodeValue + second.nodeValue; first.nodeValue = rendered; second.isConnected = false; p.childNodes = [first];
    const characters = { type: "characterData", target: first }, children = { type: "childList", target: p, removedNodes: [second], addedNodes: [] };
    f.session.mutated(characterFirst ? [characters, children] : [children, characters]); await tick();
    assert.equal(first.nodeValue, rendered); assert.equal(peer.node.nodeValue, "中文译文"); assert.equal(f.candidates().length, 1); assert.equal(f.session.inspect().revision, revision);
    assert.equal(f.session.inspect().records.get(first).source, authored); assert.equal(f.session.inspect().records.get(first).pieces.length, 2);
    f.session.control("view-original"); assert.equal(first.nodeValue, authored); f.session.control("view-translation"); assert.equal(first.nodeValue, rendered); f.session.dispose();
  }
});

test("live normalize preserves in-flight fine block IDs and selected raw range when the output is coalesced", async () => {
  const first = new Text("Keep this original prefix. "), second = new Text("Translate only this selected English sentence."), p = new Element("P", [first, second]), f = fixture([p]);
  const source = first.nodeValue + second.nodeValue; await manualReady(f); select(f, [second]); f.session.control("manual-selection"); await tick(); const old = f.candidate();
  f.session.receive(message(old, "pageAutoTranslations", { updates: [{ id: old.blocks[0].id, text: "当前选中前缀" }] }));
  first.nodeValue += second.nodeValue; second.isConnected = false; p.childNodes = [first];
  f.session.mutated([{ type: "characterData", target: first }, { type: "childList", target: p, removedNodes: [second], addedNodes: [] }]);
  finish(f, old, "所选句子的完整译文。"); assert.equal(first.nodeValue, "Keep this original prefix. 所选句子的完整译文。");
  assert.equal(f.candidates().length, 1); f.session.control("view-original"); assert.equal(first.nodeValue, source); f.session.dispose();
});

test("coarse replacement with genuinely new authored source never rebinds an old translated concatenation", async () => {
  const first = new Text("The deadline is Thursday and "), second = new Text("registration costs twelve euros."), p = new Element("P", [first, second]), peer = paragraph("A peer English paragraph has unchanged original source and keeps its translation."), f = fixture([p, peer.element]);
  await enabled(f); approved(f); finish(f); const originalCandidate = f.candidate(), updated = "The deadline is Friday and registration costs thirteen euros.";
  first.nodeValue = updated; second.isConnected = false; p.childNodes = [first];
  f.session.mutated([{ type: "characterData", target: first }, { type: "childList", target: p, removedNodes: [second], addedNodes: [] }]); await tick();
  assert.equal(first.nodeValue, updated); assert.equal(peer.node.nodeValue, "中文译文"); assert.equal(f.candidates().length, 2);
  assert.equal(f.candidate().blocks[0].text, updated); assert.notEqual(f.candidate().revision, originalCandidate.revision); f.session.dispose();
});

test("selection context uses exact adjacent canonical slices and one space only across source gaps", async () => {
  for (const separated of [false, true]) {
    const first = new Text("Note"), last = new Text("book"), gap = new Text(separated ? "  \n" : ""), p = new Element("P", [first, gap, new Element("SPAN", [last])]), f = fixture([p]);
    await manualReady(f); select(f, [first, last]); f.session.control("manual-selection"); await tick(); const candidate = f.candidate();
    assert.ok(candidate.blocks.every(block => block.context === (separated ? "Note book" : "Notebook") && block.contextKind === "selection"));
    assert.equal(candidate.blocks[1].sourceStart - candidate.blocks[0].sourceLength, separated ? 1 : 0); f.session.dispose();
  }
});

test("filtered selected URL remains literal and never enters block text or the selected context hint", async () => {
  const first = new Text("Visit "), url = new Text("https://example.invalid/private-path"), last = new Text(" today."), p = new Element("P", [first, new Element("A", [url]), last]), f = fixture([p]);
  await manualReady(f); select(f, [first, url, last]); f.session.control("manual-selection"); await tick(); const candidate = f.candidate();
  assert.deepEqual(candidate.blocks.map(block => block.text), ["Visit", "today."]); assert.ok(candidate.blocks.every(block => block.context === "Visit today."));
  finish(f, candidate); assert.equal(url.nodeValue, "https://example.invalid/private-path"); f.session.dispose();
});


test("a transient rejection of a later batch retains completed paragraphs and retries only untouched sources", async () => {
  const paragraphs = Array.from({length:55},(_,i)=>paragraph(`This is an English paragraph with enough readable words for the page, number ${i}.`));
  const f=fixture(paragraphs.map(item=>item.element));await enabled(f);
  const first=f.candidate();approved(f,first);finish(f,first);
  const next=f.candidate();assert.notEqual(next.requestId,first.requestId);
  f.session.receive(message(next,"pageAutoTranslations",{finished:true,error:"page_busy"}));
  assert.equal(f.statuses.at(-1).state,"retrying");assert.equal(paragraphs[0].node.nodeValue,"中文译文");
  await new Promise(resolve=>setTimeout(resolve,1100));await tick();
  const retry=f.candidate();assert.notEqual(retry.requestId,next.requestId);
  assert.ok(retry.blocks.every(block=>!first.blocks.some(done=>done.text===block.text)));
  assert.equal(paragraphs[0].node.nodeValue,"中文译文");approved(f,retry);finish(f,retry);
  assert.equal(f.statuses.at(-1).state,"completed");f.session.dispose();
});

test("API configuration errors stop automatic retry but a saved-key generation can resume the page",async()=>{
  const item=paragraph("This English article should resume after the user saves a corrected API Key."),f=fixture([item.element]);
  await enabled(f);const candidate=f.candidate();approved(f,candidate);
  f.session.receive(message(candidate,"pageAutoTranslations",{finished:true,error:"api_key_error"}));
  assert.equal(f.statuses.at(-1).state,"failed");assert.equal(f.statuses.at(-1).detail,"api_key_error");
  assert.equal(f.session.inspect().jobs,0);assert.equal(f.session.inspect().pending,0);
  f.session.configure({generation:2,enabled:true,active:true,targetLanguage:"简体中文",concurrency:10});await tick();
  assert.equal(f.candidates().length,2);approved(f);finish(f);assert.equal(f.statuses.at(-1).state,"completed");f.session.dispose();
});

test("site verification waits without translating its loader and recovers when genuine content appears",async()=>{
  const item=paragraph("Please wait while the browser is verifying that the visitor is human."),f=fixture([item.element]);
  f.document.title="Just a moment...";await enabled(f);
  assert.equal(f.candidates().length,0);assert.equal(f.statuses.at(-1).state,"waiting_for_page");
  assert.equal(f.decisions.at(-1).allowImages,false);
  f.document.title="A public article";item.node.nodeValue="The real English article is now available and has enough public prose to translate.";
  f.session.mutated([{type:"characterData",target:item.node}]);await tick();
  assert.equal(f.candidates().length,1);approved(f);finish(f);assert.equal(f.statuses.at(-1).state,"completed");f.session.dispose();
});
