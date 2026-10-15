"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const policy = require("../page-auto-policy.js");

// Synthetic offline fixtures, mirrored in PageReadableChineseTermsTests.cs.
// These are node boundaries, not website/corpus accuracy measurements.
const readable = [
  "CCTV.直播", "关于CCTV.com", "TECH.频道", "CCTV-5+直播",
  "关于example.net的使用说明", "关于mirror.physics.example.edu", "关于ＣＣＴＶ．ｃｏｍ",
  "请查看 docs.example.dev 的技术文档，核对版本后继续操作。",
  "研究人员讨论了AI、OpenAI与Hilbert空间的最新进展，说明了模型训练所依赖的数学方法。",
  "团队将PyTorch用于模型训练，并说明了每个计算步骤。",
  "我们在JavaScript环境中运行这个程序，并核对所有输出结果。",
  "研究人员在Hilbert空间中研究算子，并给出了完整的证明过程。",
  "关于Noether定理的讲座已经发布，读者可以查阅相关材料。",
  "研究人员用Schrodinger方程解释实验结果，并逐项核对测量记录。",
  "Hilbert的研究成果为后续数学工作提供了重要基础。",
  "团队介绍了iPhone的接口设计，并解释了相关产品功能。",
  "OpenAI发布了新的研究报告，详细介绍了计算方法和评估过程。",
  "请核对RTX-5090Pro设备的运行记录，然后继续执行中文操作说明。",
  "Android16系统支持中文界面，用户可以查看完整的设置说明。",
  "设备编号ZX_2048+已经记录，研究人员将继续进行可靠性测试。",
  "研究人员记录了CCTV-5+频道的节目安排，并核对发布时刻。",
  "𠀀Hilbert𠮷𠀋", "关于Hilbert空间",
  "研究人员在𝓗ilbert空间中研究算子，并给出了完整的证明过程。"
];

for (const source of readable) test(`Chinese with a bound term: ${source}`, () => {
  for (const targetLanguage of ["简体中文", "zh-CN", "zh-Hant", "Chinese"]) {
    assert.equal(policy.alreadyTargetText(source, targetLanguage), true);
    assert.equal(policy.skipTranslationText(source, { targetLanguage }), "already_target_language");
    assert.equal(policy.skipTranslationText(source, { targetLanguage, explicitSelection: true }), "already_target_language");
    assert.equal(policy.skipTranslationText(source, { targetLanguage, foreignHanContext: true }), "");
    assert.equal(policy.skipTranslationText(source, { targetLanguage, foreignHanContext: true, explicitSelection: true }), "");
  }
  assert.equal(policy.skipTranslationText(source, { targetLanguage: "English" }), "");
});

const foreign = [
  "研究人员表示，Read the OpenAI report，然后继续核对中文记录。",
  "这里的操作要求是 Please restart Android16 now，请先保存文件。",
  "研究人员记录了PLEASE STOP这条英文命令，并保留原文供读者查阅。",
  "研究人员写下了“Help”，这是外国访客发出的求助信息，请保留原文。",
  "请在终端输入 npm install 然后继续执行中文操作说明。",
  "请保持lowercase foreign prose这段文字完整。",
  "团队比较了OpenAI and Hilbert的方案，并记录所有细节。",
  "研究团队持续改进设备，Alice介绍了CR450的新方案，请继续记录原文。",
  "研究人员在Hilbert空间中阅读Read the report这条指令。",
  "研究人员在Hilbert空间中讨论了café方法，并继续保留原文。",
  "研究人员在Hilbert空间中核对한文记录，并继续保留原文。",
  "研究人员在Hilbert\u0307空间中讨论数学方法，并继续保留原文。",
  "研究人员在Hіlbert空间中讨论数学方法，并继续保留原文。",
  "研究人员在Hilbert\u200d空间中讨论数学方法，并继续保留原文。",
  "研究人员在OpenAI\u0307环境中讨论计算方法，并继续保留原文。",
  "Hilbertの定理について説明します", "OpenAIﾆｭｰｽ", "OpenAI와 연구 결과",
  "OpenAI", "Hilbert", "PyTorch", "Hilbert proved the theorem.",
  "OpenAI released a model for 中文说明.", "Read docs.example.dev before continuing 中文说明。",
  "HELP", "NO WAR", "STOP 王曼昱", "关于MODEL123设备", "关于AB12CDE设备",
  "English 中文说明", "中文说明Api", "关于example.c的说明"
];

for (const source of foreign) test(`Foreign content remains eligible: ${source}`, () => {
  // Extra Chinese never hides an actual clause, command, script or mark.
  for (const value of [source, "研究团队继续记录所有实验情况。".repeat(20) + "，" + source]) {
    assert.equal(policy.alreadyTargetText(value), false);
    assert.equal(policy.skipTranslationText(value), "");
    assert.equal(policy.skipTranslationText(value, { explicitSelection: true }), "");
  }
});

test("literal ID suffixes are automatic metadata with selection preserved", () => {
  for (const source of ["CCTV-5+", "ZX_2048+", "RTX-5090Pro", "Android16", "AB12#", "ＡＢ１２＋"])
    for (const targetLanguage of ["简体中文", "English", "ja-JP"]) {
      assert.equal(policy.skipTranslationText(source, { targetLanguage }), "metadata");
      assert.equal(policy.skipTranslationText(source, { targetLanguage, explicitSelection: true }), "");
      assert.equal(policy.skipTranslationText(source, { targetLanguage, foreignHanContext: true, explicitSelection: true }), "");
    }
});

test("term bindings use Unicode scalar Han and White_Space, preserving internal BOM", () => {
  for (const source of ["\u0085关于Hilbert空间\ufeff", "关于\u0085Hilbert\u0085空间的研究报告已经发布，读者可以查阅证明。"])
    assert.equal(policy.skipTranslationText(source), "already_target_language");
  for (const source of ["关于\ufeffHilbert空间", "关于Hilbert\ufeff空间", "关于Hil\ufeffbert空间"])
    assert.equal(policy.skipTranslationText(source), "");
});

// Plain TitleCase is also normal command casing. Chinese neighbours, possessive
// 的 and extra Han must not turn these action words into personal names.
const actions = [
  "请Help我们完成这项工作", "请Delete这些文件", "请Read这份研究报告", "请Stop所有任务",
  "请Retry这次提交", "请Cancel当前操作", "请选择Accept继续操作", "请Execute这段程序",
  "系统要求Download这些文件后再继续", "用户必须Save这份研究报告以保留记录",
  "可以Run这段程序来核对计算结果", "接下来Build这个项目并检查输出",
  "操作员需要Copy这些记录以完成交接", "维护人员应该Restart这台设备并观察结果",
  "报告完成后Send给所有成员", "提交前Verify所有计算结果以保证完整",
  "请选择Update继续安装过程", "现在Connect这台设备以读取数据",
  "Help的意思是帮助，请按英文提示操作", "Read的操作需要先选择文件",
  "请\u0085Delete\u0085这些文件后继续", "请Ｄｅｌｅｔｅ这些文件后继续",
  "请𝑅𝑒𝑎𝑑这份研究报告后继续", "请Cancel𠀀𠮷𠀋", "请在OpenAI环境中Execute这段程序",
  "团队建议Retry这次提交，然后请Noether核对所有结果"
];
for (const source of actions) test(`Command casing is not a proper name: ${source}`, () => {
  for (const value of [source, "研究团队继续记录所有实验情况。".repeat(20) + source])
    for (const targetLanguage of ["简体中文", "zh-CN", "zh-Hant", "Chinese"])
      for (const explicitSelection of [false, true]) {
        assert.equal(policy.alreadyTargetText(value, targetLanguage), false);
        assert.equal(policy.skipTranslationText(value, { targetLanguage, explicitSelection }), "");
      }
});

const independentNames = [
  "请Hilbert核对这份研究报告", "请Noether继续研究这个问题",
  "团队请Alice介绍全部实验结果", "研究人员用Gauss定理计算曲面积分",
  "请Turing解释这段程序的计算过程", "我们请Euler核对所有公式并保留记录",
  "请在QuickRead环境中查看这份研究报告", "团队将AutoSave用于保护所有工作记录",
  "请OpenAI继续研究这个问题", "请核对Read2Pro设备的运行记录",
  "请查看accept.example.dev的完整说明", "关于retry.example.net的技术文档",
  "请\u0085Noether\u0085核对这份研究报告，逐项检查计算过程并保存所有结果", "请Ｎｏｅｔｈｅｒ核对这份研究报告"
];
for (const source of independentNames) test(`Names and literals retain their bindings: ${source}`, () => {
  for (const targetLanguage of ["简体中文", "zh-CN", "zh-Hant", "Chinese"])
    for (const explicitSelection of [false, true]) {
      assert.equal(policy.skipTranslationText(source, { targetLanguage, explicitSelection }), "already_target_language");
      assert.equal(policy.skipTranslationText(source, { targetLanguage, explicitSelection, foreignHanContext: true }), "");
    }
  assert.equal(policy.skipTranslationText(source, { targetLanguage: "English" }), "");
});

// Numbered content is ambiguous prose, even when its shape resembles an ID.
// Independently authored fixtures; include headings beyond the reported eight.
const numberedContentWords = [
  "Chapter", "Section", "Lesson", "Part", "Question", "Exercise", "Step", "Volume",
  "Appendix", "Article", "Example", "Figure", "Module", "Page", "Paragraph", "Problem",
  "Scene", "Stage", "Table", "Task", "Topic", "Unit", "Worksheet", "Summary", "Introduction",
  "Chapters", "Appendices", "Instructions", "Warning", "Solution", "Overview",
  "Help", "Read", "Delete", "Run", "Save", "Retry", "Stop", "Start", "Execute", "Download", "Accept", "Verify"
];
test("reported numeric headings and mixed technical context remain translatable", () => {
  for (const token of ["Chapter2", "Section7", "Lesson1", "Part3", "Question4", "Exercise5", "Step2", "Volume6"])
    for (const source of [token, `请阅读${token}并完成练习`, `团队用OpenAI与Hilbert解释${token}的全部内容`,
      `请在AI环境中阅读${token}并核对说明`, `请阅读${token} and Summary9并完成练习`])
      for (const explicitSelection of [false, true]) {
        assert.equal(policy.alreadyTargetText(source), false);
        assert.equal(policy.skipTranslationText(source, { explicitSelection }), "");
      }
});
for (const word of numberedContentWords) test(`Numbered heading/action remains prose: ${word}`, () => {
  const casings = [word, word.toLowerCase(), word.toUpperCase(),
    [...word].map((c, i) => i % 2 ? c.toUpperCase() : c.toLowerCase()).join("")];
  for (const casing of casings)
    for (const separator of ["", "-", "_", " ", "\u0085", "\u00a0"])
      for (const number of ["2", "17", "１２", "٢"])
        for (const token of [casing + separator + number,
          casing.replace(/[A-Za-z]/gu, c => String.fromCharCode(c.charCodeAt(0) + 0xfee0)) + separator + number]) {
          const sources = [token, `请阅读${token}并完成练习`, `请${token}的内容继续说明`,
            `𠀀${token}𠮷𠀋`, "研究团队继续记录所有实验情况。".repeat(20) + `请阅读${token}并完成练习`];
          for (const source of sources)
            for (const targetLanguage of ["简体中文", "zh-CN", "zh-Hant", "Chinese", "English", "ja-JP"])
              for (const explicitSelection of [false, true]) {
                assert.equal(policy.alreadyTargetText(source, targetLanguage), false, source);
                assert.equal(policy.skipTranslationText(source, { targetLanguage, explicitSelection }), "", source);
                assert.equal(policy.skipTranslationText(source, { targetLanguage, explicitSelection, foreignHanContext: true }), "", source);
              }
        }
});

test("numeric content guard protects whole words, preserving product suffixes and domains", () => {
  for (const source of ["Android16", "RTX-5090Pro", "ZX_2048+", "Read2Pro", "Step2Pro", "Go2Pro", "READ2Pro"])
    for (const targetLanguage of ["简体中文", "English", "ja-JP"]) {
      assert.equal(policy.skipTranslationText(source, { targetLanguage }), "metadata");
      assert.equal(policy.skipTranslationText(source, { targetLanguage, explicitSelection: true }), "");
      const embedded = `请核对${source}设备的完整运行记录`;
      assert.equal(policy.skipTranslationText(embedded, { targetLanguage: "Chinese" }), "already_target_language");
      assert.equal(policy.skipTranslationText(embedded, { targetLanguage: "Chinese", foreignHanContext: true }), "");
    }
  for (const source of ["请在QuickRead环境中查看记录", "团队将AutoSave用于保护记录",
    "关于chapter2.example.dev的完整说明", "关于step.example.net的完整说明"])
    for (const explicitSelection of [false, true]) {
      assert.equal(policy.skipTranslationText(source, { explicitSelection }), "already_target_language");
      assert.equal(policy.skipTranslationText(source, { explicitSelection, foreignHanContext: true }), "");
    }
  for (const source of ["请READ2继续操作", "请STEP_2继续阅读", "请RUN-2继续操作", "请Help2+继续操作",
    "请Read2#继续操作", "请𝑅𝑒𝑎𝑑２继续操作", "请Chapter\u0307２继续阅读", "请Section\ufeff2继续阅读"])
    assert.equal(policy.skipTranslationText(source), "");
  for (const kind of ["form", "editable", "code"]) {
    assert.equal(policy.skipTranslationText("Chapter2", { kind }), "navigation");
    assert.equal(policy.skipTranslationText("Chapter2", { kind, explicitSelection: true }), "");
  }
});
