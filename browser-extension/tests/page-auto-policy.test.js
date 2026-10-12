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

test("Chinese prose share configuration defaults for old or malformed messages and clamps to 10–90", () => {
  assert.equal(policy.normalizeConfig({ generation: 1 }).chineseProseShareThresholdPercent, 50);
  for (const value of [null, undefined, NaN, Infinity, 20.5, "30", true, {}, [], Number.MAX_SAFE_INTEGER + 1])
    assert.equal(policy.normalizeConfig({ generation: 1, chineseProseShareThresholdPercent: value }).chineseProseShareThresholdPercent, 50);
  for (const [value, expected] of [[-1, 10], [0, 10], [10, 10], [35, 35], [90, 90], [100, 90]])
    assert.equal(policy.normalizeConfig({ generation: 1, chineseProseShareThresholdPercent: value }).chineseProseShareThresholdPercent, expected);
});

test("body letter denominator includes all scripts and excludes headings, navigation, code and noise", () => {
  const result = policy.summarize([
    { text: "这是中文的正文 AI。", kind: "body" },
    { text: "Hello мир Ω 한 カ 東京 𠀋", kind: "body" },
    { text: "这是标题的文字，这里不属于正文。".repeat(10), kind: "heading" },
    { text: "English title wording".repeat(10), kind: "title" },
    { text: "English navigation wording".repeat(10), kind: "navigation" },
    { text: "const EnglishCode = true", kind: "code" },
    { text: "https://example.com/report", kind: "body" },
    { text: "Copyright © 2026 Example Inc.", kind: "body" },
    { text: "100 USD", kind: "body" }, { text: "123456789 😀！", kind: "body" }
  ]).pageSummary;
  assert.equal(result.hanProseCharacters, 7);
  assert.equal(result.nonChineseProseCharacters, 16);
  assert.equal(policy.localVerdict(result, "简体中文", 50), null);
});

test("Han symbols cannot inflate Chinese prose letters or make the denominator negative", () => {
  const result = policy.summarize([
    { text: "这是中文的正文。⼀⼁⼂⼃⼄⼅⼆⼇⼈⼉", kind: "body" },
    { text: "Foreign prose.", kind: "body" }
  ]).pageSummary;
  assert.equal(result.hanCharacters, 17); // Preserve the existing script statistics.
  assert.equal(result.hanProseCharacters, 7);
  assert.equal(result.nonChineseProseCharacters, 12);
  assert.equal(policy.localVerdict(result, "简体中文", 50), null);
});

test("the fixed default skips Chinese-dominant pages and equality but admits a foreign majority", () => {
  const chinese = "这是中文的正文，可以保持原文。";
  const english = "The article needs translation.";
  const dominant = policy.summarize([{ text: chinese.repeat(4), kind: "body" }, { text: english, kind: "body" }]).pageSummary;
  assert.equal(policy.localVerdict(dominant, "简体中文", 50).reason, "mixed_chinese_english");
  assert.equal(policy.localVerdict(dominant, "English", 50), null);
  const boundary = { totalCharacters: 24, hanProseCharacters: 6, nonChineseProseCharacters: 6, latinProseCharacters: 6 };
  assert.equal(policy.localVerdict(boundary, "简体中文", 50).needsTranslation, false);
  assert.equal(policy.localVerdict({ ...boundary, nonChineseProseCharacters: 7 }, "简体中文", 50), null);
  assert.equal(policy.localVerdict({ ...boundary, hanProseCharacters: 5, nonChineseProseCharacters: 0 }, "简体中文", 50), null);
});

test("product custom thresholds use >= while omitted thresholds retain the fixed 50 default", () => {
  const summary = { totalCharacters: 100, hanProseCharacters: 30, nonChineseProseCharacters: 70, latinProseCharacters: 70 };
  assert.equal(policy.localVerdict(summary, "简体中文"), null);
  assert.equal(policy.localVerdict(summary, "简体中文", 30).needsTranslation, false);
  assert.equal(policy.localVerdict(summary, "简体中文", 31), null);
  assert.equal(policy.localVerdict(summary, "简体中文", "30"), null);
});

test("legacy statistics fall back to Latin and kana without identifying Japanese Han as Chinese", () => {
  const legacy = { totalCharacters: 100, hanCharacters: 60, latinCharacters: 40, kanaCharacters: 0 };
  assert.equal(policy.localVerdict(legacy, "简体中文").needsTranslation, false);
  assert.equal(policy.localVerdict({ ...legacy, latinCharacters: 80 }, "简体中文"), null);
  assert.equal(policy.localVerdict({ ...legacy, kanaCharacters: 10 }, "简体中文"), null);
  const japanese = policy.summarize([{ text: "東京の大学で日本語の文章を読みながら研究内容を説明します。".repeat(5), kind: "body" },
    { text: "这是中文的正文，可以保持原文。", kind: "body" }]).pageSummary;
  assert.equal(japanese.hanProseCharacters, 13);
  assert.ok(japanese.nonChineseProseCharacters > japanese.hanProseCharacters);
  assert.equal(policy.localVerdict(japanese, "简体中文", 50), null);
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
test("full-page script counts retain minority Chinese at the bottom without vetoing foreign prose", () => {
  const groups = Array.from({ length: 180 }, (_, index) => ({ text: index === 179 ? "这里是中文说明和英文混排内容" : "The page contains a substantive English paragraph about local translation.", kind: "body" }));
  const summary = policy.summarize(groups);
  assert.ok(summary.pageSummary.hanProseCharacters >= 6);
  assert.equal(policy.localVerdict(summary.pageSummary, "简体中文", 50), null);
  assert.ok(summary.pageSummary.nonChineseProseCharacters > summary.pageSummary.hanProseCharacters);
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
test("minority Chinese passages and inline notes on a foreign-dominant page remain eligible", () => {
  const english = "The English paragraph describes the complete local browser translation workflow. ".repeat(45);
  for (const chinese of ["这里的中文说明可以直接阅读", "这一段已经包含了可读的中文", "请保持这些已经可读的中文段落"]) {
    for (const text of [chinese + english, english.slice(0, 900) + chinese + english.slice(900), english + chinese]) {
      const summary = policy.summarize([{ text, kind: "body" }]);
      assert.equal(summary.pageSummary.hanProseCharacters, 0);
      assert.equal(policy.localVerdict(summary.pageSummary, "简体中文"), null);
    }
    const separate = policy.summarize([{ text: chinese, kind: "body" }, { text: english, kind: "body" }]);
    assert.equal(separate.pageSummary.hanProseCharacters, chinese.length);
    assert.equal(policy.localVerdict(separate.pageSummary, "简体中文", 50), null);
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

// Public development examples only; kept identical to the Core fixtures.
test("readable Chinese with short abbreviations is skipped only for Chinese targets", () => {
  for (const source of [
    "WTT中国大满贯赛：王曼昱女单卫冕",
    "“张雪机车”德比斯获WSBK葡萄牙站次回合第三",
    "新华社权威AI时政资讯智能体“新华语典”",
    "APEC“中国年”",
    "AI时代需要更多数控铣技术人才",
    "智界R7焕新上市 售价23.98万-31.98万元",
    "新华网B站号",
    "A股三季报披露将启幕 业绩预告勾勒盈利图景",
    "ＡＰＥＣ“中国年”", "𠀀𠀋𠮷AI", "中文技术支持说明方案AI CPU"
  ]) {
    for (const targetLanguage of ["简体中文", "zh-CN", "zh-Hans", "zh-Hant", "Chinese", "繁体中文"]) {
      assert.equal(policy.skipTranslationText(source, { targetLanguage }), "already_target_language", source);
      assert.equal(policy.skipTranslationText(source, { targetLanguage, explicitSelection: true }), "already_target_language", source);
      assert.equal(policy.skipTranslationText(source, { targetLanguage, foreignHanContext: true }), "", source);
      assert.equal(policy.skipTranslationText(source, { targetLanguage, foreignHanContext: "true" }), "already_target_language", source);
    }
    for (const targetLanguage of ["English", "en-US", "ja-JP", "ko-KR", "fr-FR"])
      assert.equal(policy.skipTranslationText(source, { targetLanguage }), "", source);
  }
});

// Synthetic computing/science boundaries; mirrored in the Core tests.
test("common technical acronyms require readable Chinese context", () => {
  for (const acronym of [
    "API", "GPU", "USB", "SSD", "RAM", "DNS", "SQL", "HTML", "CSS",
    "JSON", "XML", "HTTP", "TCP", "UDP", "PDF", "DNA", "RNA", "MRI"
  ]) {
    for (const source of [`中文说明${acronym}`, `中文技术说明方案 ${acronym}`]) {
      for (const targetLanguage of ["简体中文", "zh-CN", "zh-Hans", "zh-Hant", "Chinese", "繁体中文"]) {
        assert.equal(policy.skipTranslationText(source, { targetLanguage }), "already_target_language", source);
        assert.equal(policy.skipTranslationText(source, { targetLanguage, explicitSelection: true }), "already_target_language", source);
        assert.equal(policy.skipTranslationText(source, { targetLanguage, foreignHanContext: true }), "", source);
        assert.equal(policy.skipTranslationText(source, { targetLanguage, foreignHanContext: "true" }), "already_target_language", source);
      }
      for (const targetLanguage of ["English", "en-US", "ja-JP", "ko-KR", "fr-FR"])
        assert.equal(policy.skipTranslationText(source, { targetLanguage }), "", source);
    }
    for (const source of [acronym, `${acronym}李明`, `Read the ${acronym} guide for 中文说明.`,
      `中文接口技术支持说明方案 USE ${acronym}`]) {
      assert.equal(policy.skipTranslationText(source), "", source);
      assert.equal(policy.skipTranslationText(source, { explicitSelection: true }), "", source);
    }
  }
});

test("technical acronyms preserve Chinese ratios and foreign phrase boundaries", () => {
  for (const [source, needsTranslation] of [
    ["API使用指南", false], ["API接口", true],
    ["中文说明方案 API", false], ["中文说明方 API", true],
    ["中文说明方案 HTTP", true], ["中文技术说明方案 HTTP", false], ["中文技术说明方 HTTP", true],
    ["中文接口技术支持说明方案API GPU", false], ["中文接口技术支持说明方API GPU", true],
    ["中文接口技术支持说明方案DNA/RNA", false], ["中文接口技术支持说明方DNA/RNA", true],
    ["GPU NEWS 中文接口技术支持说明方案", true], ["中文说明APIGPU", true], ["中文说明Api", true],
    ["中文说明ＡＰＩ", false], ["中文接口技术支持说明方案API2 GPU", true],
    ["中文技术说明方案APIの説明", true], ["中文技术说明方案GPU한", true]
  ]) {
    assert.equal(policy.skipTranslationText(source), needsTranslation ? "" : "already_target_language", source);
    assert.equal(policy.skipTranslationText(source, { explicitSelection: true }), needsTranslation ? "" : "already_target_language", source);
    assert.equal(policy.skipTranslationText(source, { foreignHanContext: true }), "", source);
    assert.equal(policy.skipTranslationText(source, { targetLanguage: "English" }), "", source);
  }
});

test("per-node Unicode trim distinguishes edge and internal whitespace without changing page counts", () => {
  for (const [source, needsTranslation] of [
    ["\u0085APEC中国年", false], ["APEC中国年\u0085", false],
    ["\ufeffAPEC中国年", false], ["APEC中国年\ufeff", false],
    ["\ufeff \u0085APEC中国年\u0085 \ufeff", false], ["APEC\u0085中国年", true],
    ["APEC\ufeff中国年", false], ["AP\ufeffEC中国年", true]
  ]) {
    assert.equal(policy.skipTranslationText(source), needsTranslation ? "" : "already_target_language", source);
    assert.equal(policy.alreadyTargetText(source), !needsTranslation, source);
    assert.equal(policy.skipTranslationText(source, { foreignHanContext: true }), "", source);
    assert.equal(policy.skipTranslationText(source, { targetLanguage: "English" }), "", source);
  }
  // Keep the existing page-wide script accounting, including JS whitespace semantics.
  assert.equal(policy.languageCounts("\u0085").totalCharacters, 1);
  assert.equal(policy.languageCounts("\ufeff").totalCharacters, 0);
});

test("Unicode edge trim preserves noise and metadata boundaries", () => {
  for (const source of [
    "\u0085", "\ufeff", "\ufeff \u0085 \ufeff", "\u0085100 USD\u0085", "\ufeff100 USD\ufeff",
    "\u0085https://example.com\u0085", "\ufeffhttps://example.com\ufeff"
  ]) {
    assert.ok(policy.skipTranslationText(source), source);
    assert.ok(policy.skipTranslationText(source, { targetLanguage: "English", explicitSelection: true }), source);
  }
});

test("ordinary uppercase commands and phrases remain eligible regardless of Han share", () => {
  for (const source of [
    "全世界的人民大声疾呼 NO WAR", "请大家帮助这个受困的人 HELP", "请大家帮助这个受困的人HELP",
    "请大家帮助这个受困的人 HELP NOW", "中文说明文章A B", "中文技术支持说明方案AI GO",
    "这是一个包含多个普通大写英文单词的很长中文说明 NO WAR"
  ]) {
    assert.equal(policy.skipTranslationText(source), "", source);
    assert.equal(policy.skipTranslationText(source, { explicitSelection: true }), "", source);
    assert.equal(policy.skipTranslationText(source, { foreignHanContext: true }), "", source);
    assert.equal(policy.skipTranslationText(source, { targetLanguage: "English" }), "", source);
  }
});

test("foreign prose, scripts and insufficient Chinese evidence remain eligible", () => {
  for (const source of [
    "Wang defeated 王曼昱 in the WTT final.", "AI NEWS FROM 王曼昱", "STOP 王曼昱!", "GO 王曼昱", "AI 中国年", "AI\u0085中国年",
    "English 中文说明", "你好 Hello",
    "AI李明", "APEC中国", "中文说明ABCDE", "中文说明AB12CDE", "中文说明文A B", "中文技术支持说明方AI CPU",
    "123456789 AI李明 😀😀😀", "这是中文标题包含api", "这是中文标题包含éAI", "这是中文标题包含АI",
    "这是中文标题包含ΩAI", "中文说明々AI", "这是中文标题AI\u0307", "AI中国年のニュース", "AI中国年ﾆｭｰｽ", "AI中国年한"
  ]) assert.equal(policy.skipTranslationText(source), "", source);
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
