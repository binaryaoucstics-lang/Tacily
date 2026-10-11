"use strict";
// Calendar labels share the native service's local, deterministic behavior.
const test = require("node:test"), assert = require("node:assert/strict");
test("social paragraphs skip only when Chinese reaches half of letters, preserving Japanese kanji plus kana",()=>{
  const {socialChineseParagraph,socialForeignText}=require("../page-auto-policy.js");
  assert.equal(socialChineseParagraph("This release adds API support，测试。"),false);
  assert.equal(socialChineseParagraph("中文AB"),true);assert.equal(socialChineseParagraph("中文ABC"),false);
  assert.equal(socialChineseParagraph("这是一段以中文为主的介绍，支持 API。"),true);
  assert.equal(socialChineseParagraph("東京都の新しい漫画を公開しました。"),false);
  assert.equal(socialChineseParagraph("English words，中文，123456789 😀😀😀"),false);
  assert.equal(socialForeignText("This release adds API support，测试。\n这是一段以中文为主的介绍，支持 API。"),"This release adds API support，测试。");
});
const policy = require("../page-auto-policy.js");
test("complete English calendar and relative labels localize without a model or inferred year", () => {
  const cases = [["Sep 1", "9月1日"], ["Jul 13", "7月13日"], ["Sep. 1, 2026", "2026年9月1日"],
    ["1 Sept. 2026", "2026年9月1日"], ["Apr 10,2025", "2025年4月10日"], ["May 12", "5月12日"], ["Feb 29", "2月29日"],
    ["February 29, 2024", "2024年2月29日"], ["1 day ago", "1天前"], ["2 days ago", "2天前"], ["an hour ago", "1小时前"],
    ["about 15 hours ago", "约15小时前"], ["less than a minute ago", "不到1分钟前"], ["3 months ago", "3个月前"],
    ["2 days from now", "2天后"], ["yesterday", "昨天"], ["just now", "刚刚"], [" \nJul 13\t ", " \n7月13日\t "],
    ["Apr １０, ２０２５", "2025年4月10日"]];
  for (const [source, expected] of cases) assert.equal(policy.localCalendarText(source), expected, source);
  for (const source of ["The release is planned for May 12.", "Showcase a bold landing page with stylized title and button", "August", "Feb 30",
    "February 29, 2025", "Apr 0", "Apr 10, 0000", "12:30", "2026-10-10", "13:30 UTC", "june@example.org", null]) assert.equal(policy.localCalendarText(source), null, source);
  assert.equal(policy.localCalendarText("Sep 1", "English"), null);
  assert.equal(policy.localCalendarText("about 15 hours ago", "zh-Hant"), "約15小時前");
});
test("fast script counts preserve accented Latin, astral Han, kana, Hangul, symbols and whitespace", () => {
  assert.deepEqual(policy.languageCounts("Aé漢カ한𠀋9！💡\t\v\n"), { totalCharacters: 9, hanCharacters: 2,
    kanaCharacters: 1, latinCharacters: 2, hangulCharacters: 1 });
  assert.equal(policy.alreadyTargetText("漢𠀋 2026！", "zh-CN"), true);
  assert.equal(policy.alreadyTargetText("漢𠀋 é", "zh-CN"), false);
});
test("config rejects stale/noninteger generations and bounds worker preference", () => {
  assert.equal(policy.normalizeConfig({ generation: NaN }), null);
  assert.equal(policy.normalizeConfig({ generation: -1 }), null);
  assert.equal(policy.normalizeConfig({ generation: 1, enabled: true, concurrency: 200 }).concurrency, 10);
  assert.equal(policy.normalizeConfig({ generation: 1, judgementMode: "unsafe" }).judgementMode, "program");
  assert.equal(policy.normalizeConfig({ generation: 1, judgementMode: "Rules" }).judgementMode, "program");
  assert.equal(policy.normalizeConfig({ generation: 1, judgementMode: "Model" }).judgementMode, "program");
  assert.equal(policy.normalizeConfig({ generation: 1, judgementMode: "Hybrid" }).concurrency, 10);
});
test("manual availability is an explicit boolean capability and defaults off", () => {
  assert.equal(policy.normalizeConfig({ generation: 1 }).manualAvailable, false);
  assert.equal(policy.normalizeConfig({ generation: 1, manualAvailable: true }).manualAvailable, true);
  for (const manualAvailable of [false, null, 1, "true", {}, []])
    assert.equal(policy.normalizeConfig({ generation: 1, manualAvailable }).manualAvailable, false);
});

test("cache identity admits only an opaque scope and a nonnegative safe epoch", () => {
  const scope = "A".repeat(64);
  assert.equal(policy.normalizeConfig({ generation: 1, cacheScope: scope, cacheEpoch: 5 }).cacheScope, scope.toLowerCase());
  assert.equal(policy.normalizeConfig({ generation: 1 }).cacheEpoch, 0);
  assert.equal(policy.normalizeConfig({ generation: 1 }).cacheScope, "");
  for (const cacheScope of [null, 42, "short", "https://provider.example/api", "g".repeat(64)])
    assert.equal(policy.normalizeConfig({ generation: 1, cacheScope }), null);
  for (const cacheEpoch of [null, -1, 1.1, Number.MAX_SAFE_INTEGER + 1, "1"])
    assert.equal(policy.normalizeConfig({ generation: 1, cacheEpoch }), null);
});
test("long source pieces keep every source character and prefer sentence boundaries", () => {
  const source = "First sentence. ".repeat(180) + "😀".repeat(550);
  const pieces = policy.splitText(source);
  assert.equal(pieces.join(""), source);
  assert.ok(pieces.every(piece => piece.length <= 900));
  assert.ok(pieces[0].endsWith(". "));
  assert.ok(pieces.every(piece => !/[\uD800-\uDBFF]$/u.test(piece) && !/^[\uDC00-\uDFFF]/u.test(piece)));
});
test("full-page script counts catch mixed prose at the bottom without screenshot or model", () => {
  const groups = Array.from({ length: 180 }, (_, index) => ({ text: index === 179 ? "这里是中文说明和英文混排内容" : "The page contains a substantive English paragraph about local translation.", kind: "body" }));
  const summary = policy.summarize(groups);
  assert.ok(summary.pageSummary.hanProseCharacters >= 6);
  assert.equal(policy.localVerdict(summary.pageSummary, "简体中文").reason, "mixed_chinese_english");
  assert.ok(summary.samples.some(sample => /这里/u.test(sample.text)));
  assert.ok(summary.samples.length <= 64);
  assert.ok(summary.samples.reduce((sum, sample) => sum + sample.text.length, 0) <= 12000);
});
test("Chinese prose is retained while Japanese Han/kana requires actual assessment", () => {
  const chinese = policy.summarize([{ text: "这是中文正文，不需要再次翻译并且应当保持原样。" }]);
  assert.equal(policy.localVerdict(chinese.pageSummary, "简体中文").reason, "already_target_language");
  const japanese = policy.summarize([{ text: "この文章は日本語で書かれています。English.漫画を読みます。" }]);
  assert.equal(policy.localVerdict(japanese.pageSummary, "简体中文"), null);
  const longJapanese = policy.summarize([{ text: "東京の大学で日本語の文章を読みながら研究内容を説明します。旅行記事には電車の時間と駅の場所が詳しく掲載されています。", kind: "body" }]);
  assert.ok(longJapanese.pageSummary.hanCharacters >= 24);
  assert.equal(longJapanese.pageSummary.hanProseCharacters, 0);
  assert.equal(policy.localVerdict(longJapanese.pageSummary, "简体中文"), null);
});
test("sample kinds retain navigation distinction and source hash changes only with source", () => {
  const first = policy.summarize([{ text: "English paragraph, long enough for judgement.", kind: "body" }], [{ text: "主页 登录", kind: "navigation" }, { text: "English paragraph, long enough for judgement.", kind: "body" }]);
  assert.equal(first.samples[0].kind, "navigation");
  assert.equal(first.pageSummary.hanCharacters, 0);
  assert.notEqual(first.pageSummary.sourceHash, policy.summarize([{ text: "Changed English paragraph." }]).pageSummary.sourceHash);
  const chineseHeading = policy.summarize([{ text: "浏览器自动翻译测试示例页面的中文标题", kind: "heading" }, { text: "This is a substantive English article containing readable foreign body prose.", kind: "body" }]);
  assert.equal(chineseHeading.pageSummary.hanProseCharacters, 0);
  assert.equal(policy.localVerdict(chineseHeading.pageSummary, "简体中文"), null);
});
test("Chinese labels are retained deterministically but foreign kana and other scripts remain candidates", () => {
  assert.equal(policy.alreadyTargetText("首页 消息12。", "简体中文"), true);
  assert.equal(policy.alreadyTargetText("東京のニュース", "简体中文"), false);
  assert.equal(policy.alreadyTargetText("Hello 首页", "简体中文"), false);
  assert.equal(policy.alreadyTargetText("中文标签", "英文"), false);
  assert.equal(policy.hasLanguageText("2026-10-08 10:34:56 / 42"), false);
  assert.equal(policy.hasLanguageText("東京のニュース"), true);
  assert.equal(policy.hasLanguageText("12:34:56 PM"), false);
  assert.equal(policy.hasLanguageText("2026-10-08 12:34:56 UTC+08:00"), false);
  assert.equal(policy.hasLanguageText("Version 12.3 release notes"), true);
  assert.equal(policy.hasLanguageText("The price is $42 today."), true);
});
test("Chinese-majority paragraphs veto mixed pages while minority Chinese in a foreign paragraph remains eligible", () => {
  const english = "The English paragraph describes the complete local browser translation workflow. ".repeat(45);
  for (const chinese of ["这里的中文说明可以直接阅读", "这一段已经包含了可读的中文", "请保持这些已经可读的中文段落"]) {
    for (const text of [chinese + english, english.slice(0, 900) + chinese + english.slice(900), english + chinese]) {
      const summary = policy.summarize([{ text, kind: "body" }]);
      assert.equal(summary.pageSummary.hanProseCharacters, 0);
      assert.equal(policy.localVerdict(summary.pageSummary, "简体中文"), null);
    }
    const separate = policy.summarize([{ text: chinese, kind: "body" }, { text: english, kind: "body" }]);
    assert.equal(separate.pageSummary.hanProseCharacters, chinese.length);
    assert.equal(policy.localVerdict(separate.pageSummary, "简体中文").reason, "mixed_chinese_english");
  }
  const onlyTwo = policy.summarize([{ text: english + "姓名", kind: "body" }]);
  assert.equal(policy.localVerdict(onlyTwo.pageSummary, "简体中文"), null);
  const japanese = policy.summarize([{ text: english + "東京の日本語の記事を確認しています。", kind: "body" }]);
  assert.equal(japanese.pageSummary.hanProseCharacters, 0);
  assert.equal(policy.localVerdict(japanese.pageSummary, "简体中文"), null);
  const chineseNav = policy.summarize([{ text: english, kind: "body" }], [{ text: "中文说明", kind: "navigation" }, { text: english, kind: "body" }]);
  assert.equal(policy.localVerdict(chineseNav.pageSummary, "简体中文"), null);
});

test("per-text filtering skips only complete non-language metadata and retains prose containing it", () => {
  const cases = [
    ["", "empty_text"], [" \n\t", "empty_text"], ["😀 ★ ☆", "non_language_content"],
    ["https://example.com/report?id=42#summary", "url"], ["www.example.com/news", "url"],
    ["alice.smith+news@example.org", "email"], ["mailto:alice@example.com", "email"],
    ["2026-10-09 10:34:56 UTC+08:00", "timestamp"], ["12:34 PM", "timestamp"],
    ["October 9, 2026", "timestamp"], ["9 Oct 2026", "timestamp"],
    ["42", "numeric_or_formula"], ["123,456.78%", "numeric_or_formula"], ["[12]", "numeric_or_formula"],
    ["$100.50", "numeric_or_formula"], ["30 kg", "numeric_or_formula"], ["MB", "numeric_or_formula"],
    ["x² + y² = 25", "numeric_or_formula"], ["sin(x)=0", "numeric_or_formula"],
    ["C:\\images\\page.png", "path"], ["/tmp/images/page.png", "path"],
    ["Copyright © 2026 Example Inc. All rights reserved.", "metadata"], ["© 2025–2026 Example Inc.", "metadata"],
    ["All rights reserved.", "metadata"], ["ISBN: 978-0-123456-47-2", "metadata"], ["DOI: 10.1234/example.12", "metadata"],
    ["Sarah Meyssonnier/Reuters", "metadata"], ["Photo: Jane Smith", "metadata"], ["Photo by Eric S. Lesser/AP", "metadata"],
    ["Image credit: Getty Images", "metadata"]
  ];
  for (const [source, reason] of cases) assert.equal(policy.skipTranslationText(source), reason, source);
  for (const source of [
    "Read the complete report at https://example.com/report.", "Email alice@example.com for a translated report.",
    "The price is $42 today.", "Version 12.3 release notes", "The meeting starts on October 9, 2026.",
    "x+y=3 is the formula we will discuss.", "Debt=income is an incomplete description.",
    "Copyright is complicated and authors should understand their rights.",
    "Photo: The reporters photographed a crowded square.", "The Financial Times", "Reuters", "Mira Chen",
    "Jane Smith", "Hello", "Help!", "Stop!", "Alice", "CIA", "PDF", "A", "I", "m", "Next", "Continue"
  ]) assert.equal(policy.skipTranslationText(source), "", source);
});

test("Han-only target text is skipped unless the original group is known to be Japanese", () => {
  assert.equal(policy.skipTranslationText("这是已经翻译好的中文正文。"), "already_target_language");
  assert.equal(policy.skipTranslationText("東京大学", { foreignHanContext: true }), "");
  assert.equal(policy.skipTranslationText("東京大学", { foreignHanContext: "true" }), "already_target_language");
  assert.equal(policy.skipTranslationText("東京大学", { targetLanguage: "English" }), "");
  for (const source of ["東京のニュース", "こんにちは！", "English 中文说明", "你好 Hello", "도와줘!"])
    assert.equal(policy.skipTranslationText(source), "", source);
});

test("explicit selection preserves navigation wording and credited names but still skips pure noise", () => {
  for (const kind of ["form", "editable", "code"]) {
    assert.equal(policy.skipTranslationText("Alice", { kind }), "navigation", kind);
    assert.equal(policy.skipTranslationText("Alice", { kind, explicitSelection: true }), "", kind);
    assert.equal(policy.skipTranslationText("Alice", { kind, explicitSelection: "true" }), "navigation", kind);
  }
  for (const source of ["Read more", "Privacy policy", "Photo: Jane Smith", "Sarah Meyssonnier/Reuters"])
    assert.equal(policy.skipTranslationText(source, { explicitSelection: true }), "", source);
  for (const source of ["12345", "https://example.com", "alice@example.com", "12:34 PM", "这是中文"])
    assert.ok(policy.skipTranslationText(source, { explicitSelection: true }), source);
});

test("admitted pages translate public navigation and button labels but retain native-language and numeric filtering", () => {
  for (const kind of ["navigation", "button", "heading", "body"])
    for (const source of ["Features", "Pricing", "Sign Out", "Read more", "Dashboard", "How does the free trial work?"])
      assert.equal(policy.skipTranslationText(source, { kind }), "", `${kind}: ${source}`);
  for (const source of ["登录", "2026", "https://example.com", "12:34 PM"])
    assert.ok(policy.skipTranslationText(source, { kind: "button" }), source);
});

test("long body paragraphs and metadata-adjacent names are never cut by a short-text heuristic", () => {
  const prose = "The investors examined the French national budget and discussed interest payments. ".repeat(30);
  assert.equal(policy.skipTranslationText(prose), "");
  assert.equal(policy.skipTranslationText("The investors discussed the copyright claim. " + prose), "");
  for (const source of ["やった！", "やたっ♪", "ええ", "助けて", "李明", "謝謝"])
    assert.equal(policy.skipTranslationText(source, { explicitSelection: true, foreignHanContext: true }), "", source);
});

test("Chinese agency aliases filter only a complete photographer credit rather than mixed-language article prose", () => {
  for (const agency of ["路透社", "美联社", "法新社", "盖蒂", "盖蒂图片"]) {
    const credit = "Sarah Meyssonnier/" + agency;
    assert.equal(policy.skipTranslationText(credit), "metadata", credit);
    assert.equal(policy.skipTranslationText("Photo: " + credit), "metadata", agency);
    assert.equal(policy.skipTranslationText(credit, { explicitSelection: true }), "", agency);
    for (const source of [
      "周三，法国警察发生冲突，要求改善学校条件。 " + credit,
      "这是摄影师 Sarah Meyssonnier/" + agency + " 提供的报道。",
      "The article discusses interest payments and credits " + credit + " for this image.",
      "Sarah Meyssonnier/" + agency + " reported on the public budget."
    ]) assert.equal(policy.skipTranslationText(source), "", source);
  }
});
