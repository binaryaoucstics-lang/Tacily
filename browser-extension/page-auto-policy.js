"use strict";
(function exposePagePolicy(root) {
  const maximumBlockCharacters = 900;
  const maximumBatchCharacters = 32768;
  const maximumBatchBlocks = 48;
  function normalizeConfig(value) {
    if (!value || !Number.isSafeInteger(value.generation) || value.generation < 0) return null;
    if (Object.hasOwn(value, "cacheScope") && (typeof value.cacheScope !== "string"
        || value.cacheScope !== "" && !/^[a-f0-9]{64}$/iu.test(value.cacheScope))) return null;
    if (Object.hasOwn(value, "cacheEpoch") && (!Number.isSafeInteger(value.cacheEpoch) || value.cacheEpoch < 0)) return null;
    return { enabled: value.enabled === true, active: value.active !== false, manualAvailable: value.manualAvailable === true,
      generation: value.generation, targetLanguage: String(value.targetLanguage || "简体中文").slice(0, 64),
      cacheScope: (value.cacheScope || "").toLowerCase(), cacheEpoch: value.cacheEpoch ?? 0,
      judgementMode: "program",
      siteMode: ["allow", "block"].includes(value.siteMode) ? value.siteMode : "default",
      concurrency: Math.max(1, Math.min(10, Number.isInteger(value.concurrency) ? value.concurrency : 10)) };
  }
  // Split at sentence/clause/word boundaries. Preserve every source character so
  // separate translated pieces can still be restored to their original text node.
  function splitText(text, limit = maximumBlockCharacters) {
    const chunks = []; let rest = String(text || "");
    while (rest.length > limit) {
      const head = rest.slice(0, limit + 1); let cut = 0;
      for (const expression of [/[。！？.!?](?:["'”’」』）)]*)\s*|\n+/gu, /[，,;；:：]\s*/gu, /\s+/gu]) {
        for (const match of head.matchAll(expression)) {
          const end = match.index + match[0].length;
          if (end <= limit && end >= Math.floor(limit / 3)) cut = end;
        }
        if (cut) break;
      }
      if (!cut) {
        // A pathological unbroken sentence still has to fit the transport budget.
        // Never cut between a UTF-16 surrogate pair.
        cut = limit;
        if (/[\uD800-\uDBFF]/u.test(rest[cut - 1]) && /[\uDC00-\uDFFF]/u.test(rest[cut])) cut--;
      }
      chunks.push(rest.slice(0, cut)); rest = rest.slice(cut);
    }
    if (rest) chunks.push(rest);
    return chunks;
  }
  const whitespace = /\s/u, han = /\p{Script=Han}/u, kana = /[\p{Script=Hiragana}\p{Script=Katakana}]/u;
  const latin = /\p{Script=Latin}/u, hangul = /\p{Script=Hangul}/u, letter = /\p{L}/u;
  function languageCounts(text) {
    const result = { totalCharacters: 0, hanCharacters: 0, kanaCharacters: 0, latinCharacters: 0, hangulCharacters: 0 };
    for (const character of String(text)) {
      const code = character.charCodeAt(0);
      if (code <= 127) {
        if (code === 32 || code >= 9 && code <= 13) continue;
        result.totalCharacters++;
        if (code >= 65 && code <= 90 || code >= 97 && code <= 122) result.latinCharacters++;
        continue;
      }
      if (whitespace.test(character)) continue;
      result.totalCharacters++;
      if (han.test(character)) result.hanCharacters++;
      else if (kana.test(character)) result.kanaCharacters++;
      else if (latin.test(character)) result.latinCharacters++;
      else if (hangul.test(character)) result.hangulCharacters++;
    }
    return result;
  }
  function stableHash(text) {
    let hash = 2166136261;
    for (let i = 0; i < text.length; i++) { hash ^= text.charCodeAt(i); hash = Math.imul(hash, 16777619); }
    return (hash >>> 0).toString(16).padStart(8, "0");
  }
  function isTimestamp(text) {
    return /^(?:(?:\d{4}[-/.]\d{1,2}[-/.]\d{1,2})(?:[ T]\d{1,2}:\d{2}(?::\d{2}(?:\.\d{1,3})?)?)?|\d{1,2}:\d{2}(?::\d{2}(?:\.\d{1,3})?)?)\s*(?:[ap]\.?m\.?|(?:utc|gmt)(?:[+-]\d{1,2}(?::?\d{2})?)?|z)?$/iu.test(String(text || "").trim());
  }
  function hasLanguageText(text) { return /\p{L}/u.test(String(text || "")) && !isTimestamp(text); }
  function alreadyTargetText(text, targetLanguage = "简体中文") {
    if (!/中文|Chinese|zh(?:-|$)/i.test(targetLanguage)) return false;
    // Han-only labels are already readable for a Chinese target. Keep their DOM
    // values untouched; Japanese kana and other foreign scripts remain eligible.
    let found = false;
    for (const character of String(text)) {
      if (!letter.test(character)) continue;
      found = true; if (!han.test(character)) return false;
    }
    return found;
  }
  const monthName = "(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)";
  const calendarMonth = "(?:january|jan|february|feb|march|mar|april|apr|may|june|jun|july|jul|august|aug|september|sept|sep|october|oct|november|nov|december|dec)";
  const calendarLabel = new RegExp("^(?:(" + calendarMonth + ")\\.?\\s+(\\d{1,2})(?:st|nd|rd|th)?|(\\d{1,2})(?:st|nd|rd|th)?\\s+(" + calendarMonth + ")\\.?)(?:(?:\\s*,\\s*|\\s+)(\\d{4}))?$", "iu");
  const calendarMonths = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
  function localCalendarText(text, targetLanguage = "简体中文") {
    if (!/中文|Chinese|^zh(?:[-_]|$)/iu.test(targetLanguage) || String(text ?? "").length > 128) return null;
    const source = String(text ?? "").trim().normalize("NFKC"), traditional = /繁|\bzh[-_](?:hant|tw|hk)\b/iu.test(targetLanguage);
    const date = calendarLabel.exec(source); let value = null;
    if (date) {
      const month = calendarMonths.indexOf((date[1] || date[4]).slice(0, 3).toLowerCase()) + 1, day = Number(date[2] || date[3]), year = date[5] ? Number(date[5]) : 2000;
      const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
      const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
      if (!month || year < 1 || day < 1 || day > days[month - 1]) return null;
      value = (date[5] ? year + "年" : "") + month + "月" + day + "日";
    } else {
      const age = /^(?:(about|around|approximately|less than|under|over|more than|almost)\s+)?(\d{1,8}|a|an|one)\s+(seconds?|minutes?|hours?|days?|weeks?|months?|years?)\s+(ago|from now|later)$/iu.exec(source);
      if (age) {
        const count = /^(?:a|an|one)$/iu.test(age[2]) ? 1 : Number(age[2]);
        const unit = { second: "秒", minute: traditional ? "分鐘" : "分钟", hour: traditional ? "小時" : "小时", day: "天", week: traditional ? "週" : "周", month: traditional ? "個月" : "个月", year: "年" }[age[3].toLowerCase().replace(/s$/u, "")];
        const qualifier = { about: traditional ? "約" : "约", around: traditional ? "約" : "约", approximately: traditional ? "約" : "约", "less than": "不到", under: "不到", over: traditional ? "超過" : "超过", "more than": traditional ? "超過" : "超过", almost: traditional ? "將近" : "将近" }[(age[1] || "").toLowerCase()] || "";
        value = qualifier + count + unit + (age[4].toLowerCase() === "ago" ? "前" : traditional ? "後" : "后");
      } else value = { "just now": traditional ? "剛剛" : "刚刚", today: "今天", yesterday: "昨天", "the day before yesterday": "前天", tomorrow: "明天" }[source.toLowerCase()] ?? null;
    }
    return value === null ? null : String(text).match(/^\s*/u)[0] + value + String(text).match(/\s*$/u)[0];
  }
  const namedDate = new RegExp("^(?:" + monthName + "\\.?\\s+\\d{1,2}(?:st|nd|rd|th)?(?:,?\\s+\\d{4})|\\d{1,2}(?:st|nd|rd|th)?\\s+" + monthName + "\\.?\\s+\\d{4})(?:\\s+\\d{1,2}:\\d{2}(?::\\d{2})?\\s*(?:[ap]m|utc|gmt|z)?)?$", "iu");
  function numericOrFormula(text) {
    if (/^[\p{N}\s.,，、:：;；/\\%％‰+＋\-−–—=＝×*÷()（）\[\]{}$€£¥￥°℃℉^²³<>≤≥≠∞√|]+$/u.test(text)) return true;
    // A numeric value and a known unit are metadata; "The price is $42" is prose.
    if (/^[+\-−]?(?:\d[\d,.]*|\.\d+)\s*(?:%|°[cf]|℃|℉|usd|eur|gbp|jpy|cny|rmb|kg|mg|g|km|cm|mm|m|mph|km\/h|kb|mb|gb|tb|ms|s|hz|khz|mhz|ghz|px|dpi|pt|kcal|cal|w|kw|v|a|ml|l)$/iu.test(text)) return true;
    if (/^(?:usd|eur|gbp|jpy|cny|rmb|kg|mg|km|cm|mm|mph|km\/h|kb|mb|gb|tb|ms|hz|khz|mhz|ghz|px|dpi|kcal|kw|ml|°[cf]|℃|℉)$/iu.test(text)) return true;
    // Only single-letter variables are allowed, and an operator is required.
    // This keeps words, names and short dialogue out of the formula rule.
    const words = text.match(/\p{L}+/gu) || [];
    return /[=＝+＋×*÷^≤≥≠<>]/u.test(text)
      && words.every(word => /^[a-z]$/iu.test(word) || ["sin", "cos", "tan", "log", "ln", "sqrt", "pi", "mc"].includes(word.toLowerCase()))
      && /^(?:[a-z](?:[_\p{N}²³]*)|\p{N}|[\s.,:;/\\%+＋\-−=＝×*÷()（）\[\]{}^²³<>≤≥≠∞√|])+$/iu.test(text);
  }
  function metadataOnly(text) {
    if (text.length > 180) return false;
    if (/^(?:(?:©|\(c\))\s*\d{0,4}|copyright\s*(?:©\s*)?\d{4})(?:\s*[-–]\s*\d{4})?\s*[^!?\n]{0,130}$/iu.test(text)) return true;
    if (/^all rights reserved\.?$/iu.test(text)) return true;
    if (/^(?:isbn(?:-1[03])?|issn|doi)\s*:?\s*(?:\d[\d\s./():-]*[\dx]|10\.\d{4,9}\/\S+)$/iu.test(text)) return true;
    // Credit syntax plus an actual provider is required. A bare person/publication
    // name, or a caption explaining what the photo shows, remains translatable.
    const provider = "(?:reuters|associated press|ap|afp|epa|epa-efe|getty images(?:\\/afp)?|shutterstock|unsplash|pexels|路透社|美联社|法新社|盖蒂|盖蒂图片)";
    const name = "(?:[\\p{Lu}\\p{Lt}][\\p{L}\\p{M}’'’-]*(?:\\.)?)(?:\\s+[\\p{Lu}\\p{Lt}][\\p{L}\\p{M}’'’-]*(?:\\.)?){1,5}";
    const prefix = text.match(/^(?:photos?|images?|illustrations?|credits?|sources?|photographs?)(?:\s+(?:credit|source))?\s*(?::|by)\s*/iu);
    const rest = prefix ? text.slice(prefix[0].length) : text;
    const parts = rest.split(/\s*[/|]\s*/u);
    const person = new RegExp("^" + name + "\\.?$", "u"), agency = new RegExp("^" + provider + "\\.?$", "iu");
    return parts.length === 2 && person.test(parts[0]) && agency.test(parts[1])
      || !!prefix && parts.length === 1 && (person.test(rest) || agency.test(rest));
  }
  function skipTranslationText(text, options = {}) {
    const source = String(text ?? "").trim().normalize("NFKC");
    if (!source) return "empty_text";
    const targetLanguage = options?.targetLanguage || "简体中文";
    if (options?.foreignHanContext !== true && alreadyTargetText(source, targetLanguage)) return "already_target_language";
    if (/^(?:https?:\/\/|ftp:\/\/|www\.)[^\s<>]+$/iu.test(source)) return "url";
    if (/^(?:mailto:)?[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?\.[a-z]{2,}$/iu.test(source)) return "email";
    if (/^(?:[a-z]:[\\/]|\\\\)[^\s<>]+$|^\/(?:[^\s/<>]+\/)+[^\s<>]*$/iu.test(source)) return "path";
    if (isTimestamp(source) || namedDate.test(source)) return "timestamp";
    if (numericOrFormula(source)) return "numeric_or_formula";
    if (!hasLanguageText(source)) return "non_language_content";
    if (options?.explicitSelection !== true) {
      const kind = String(options?.kind || "body").toLowerCase();
      if (["form", "editable", "code"].includes(kind)) return "navigation";
      if (metadataOnly(source)) return "metadata";
    }
    return "";
  }
  function summarize(groups, sampleGroups = groups) {
    const meaningful = groups.filter(group => hasLanguageText(group.text));
    const counted = meaningful.map(group => ({ group, counts: languageCounts(group.text) }));
    const counts = counted.reduce((sum, item) => {
      for (const key of Object.keys(sum)) sum[key] += item.counts[key];
      return sum;
    }, languageCounts(""));
    const prose = meaningful.filter(group => group.text.trim().length >= 24);
    const proseCounts = counted.filter(item => (!item.group.kind || item.group.kind === "body") && item.group.text.trim().length >= 12)
      .map(item => item.counts);
    const summary = { ...counts,
      // This is Chinese evidence, not all Han glyphs: Japanese paragraphs often
      // contain many Han characters alongside kana and must not trip the hard gate.
      hanProseCharacters: counted.filter(item => (!item.group.kind || item.group.kind === "body") && chineseProse(item.group.text))
        .reduce((sum, item) => sum + item.counts.hanCharacters, 0),
      latinProseCharacters: proseCounts.reduce((sum, count) => sum + count.latinCharacters, 0),
      kanaProseCharacters: proseCounts.reduce((sum, count) => sum + count.kanaCharacters, 0), proseBlocks: prose.length,
      sourceHash: stableHash(groups.map(group => group.text).join("\n")) };
    // Use the full page for script counts; spread the bounded model sample across
    // the beginning, middle and bottom rather than only above the fold.
    const source = sampleGroups;
    const sampled = []; const samples = Math.min(64, source.length); let remaining = 12000;
    for (let index = 0; index < samples && remaining > 0; index++) {
      const at = samples === 1 ? 0 : Math.round(index * (source.length - 1) / (samples - 1));
      const text = source[at].text.trim().slice(0, Math.min(187, remaining)); remaining -= text.length;
      if (text) sampled.push({ text, kind: source[at].kind || "body" });
    }
    return { pageSummary: summary, samples: sampled };
  }
  function localVerdict(summary, targetLanguage) {
    if (!summary) return { needsTranslation: false, reason: "too_little_prose" };
    const chineseTarget = /中文|Chinese|zh(?:-|$)/i.test(targetLanguage || "简体中文");
    if (!chineseTarget) return null;
    const hanProse = summary.hanProseCharacters ?? summary.hanCharacters;
    const latinProse = summary.latinProseCharacters ?? summary.latinCharacters;
    // Japanese naturally contains Han; do not confuse a Japanese paragraph with
    // a Chinese/English page. Actual Han + Latin prose is conservatively skipped.
    if (hanProse >= 12 && latinProse >= 12)
      return { needsTranslation: false, reason: "mixed_chinese_english" };
    if (summary.kanaCharacters > 0 || summary.hangulCharacters > 0) return null;
    if (hanProse >= 6 && hanProse >= latinProse)
      return { needsTranslation: false, reason: "already_target_language" };
    if (summary.totalCharacters < 24) return { needsTranslation: false, reason: "too_little_prose" };
    return null;
  }
  function chineseProse(text) {
    const count = languageCounts(text);
    const letters = (String(text).match(/\p{L}/gu) || []).length;
    const grammar = (String(text).match(/[的了是在和与为我你可请不该請為與這]/gu) || []).length;
    return count.kanaCharacters === 0 && count.hangulCharacters === 0 && count.hanCharacters * 2 >= letters
      && (count.hanCharacters >= 6 && grammar >= 2 && (count.hanCharacters >= 12 || count.hanCharacters * 4 >= count.latinCharacters)
        || count.hanCharacters >= 12 && count.hanCharacters >= count.latinCharacters * .4 && /[。！？]/u.test(text));
  }
  function isSocialFeed(href) {
    try { return /^(?:www\.)?(?:x\.com|twitter\.com)$/iu.test(new URL(href).hostname); }
    catch { return false; }
  }
  function socialChineseParagraph(text, targetLanguage = "简体中文") {
    if (!/中文|Chinese|^zh(?:[-_]|$)/iu.test(targetLanguage)) return false;
    const counts = languageCounts(text);
    // Use letters, not punctuation, digits or emoji, for the readable-language
    // share. Kana identifies Japanese, whose kanji are not Chinese evidence.
    const letters = (String(text).match(/\p{L}/gu) || []).length;
    return letters > 0 && counts.hanCharacters * 2 >= letters && counts.kanaCharacters === 0 && counts.hangulCharacters === 0;
  }
  function socialForeignText(text, targetLanguage) {
    return String(text).split(/\r\n|[\r\n]/u).filter(line => !socialChineseParagraph(line, targetLanguage)).join("\n");
  }
  const api = { maximumBlockCharacters, maximumBatchCharacters, maximumBatchBlocks,
    normalizeConfig, splitText, languageCounts, stableHash, isTimestamp, hasLanguageText, alreadyTargetText, skipTranslationText, localCalendarText, summarize, localVerdict, chineseProse, isSocialFeed, socialChineseParagraph, socialForeignText };
  root.ScreenTranslatorPageAutoPolicy = api;
  if (typeof module !== "undefined" && module.exports) module.exports = api;
})(globalThis);
