"use strict";
(function exposePagePolicy(root) {
  const maximumBlockCharacters = 900;
  const maximumBatchCharacters = 32768;
  const maximumBatchBlocks = 48;
  function normalizeChineseProseShareThreshold(value) {
    return Number.isSafeInteger(value) ? Math.max(10, Math.min(90, value)) : 50;
  }
  function normalizeConfig(value) {
    if (!value || !Number.isSafeInteger(value.generation) || value.generation < 0) return null;
    if (Object.hasOwn(value, "cacheScope") && (typeof value.cacheScope !== "string"
        || value.cacheScope !== "" && !/^[a-f0-9]{64}$/iu.test(value.cacheScope))) return null;
    if (Object.hasOwn(value, "cacheEpoch") && (!Number.isSafeInteger(value.cacheEpoch) || value.cacheEpoch < 0)) return null;
    return { enabled: value.enabled === true, active: value.active !== false, manualAvailable: value.manualAvailable === true,
      generation: value.generation, targetLanguage: String(value.targetLanguage || "简体中文").slice(0, 64),
      cacheScope: (value.cacheScope || "").toLowerCase(), cacheEpoch: value.cacheEpoch ?? 0,
      judgementMode: "program",
      chineseProseShareThresholdPercent: normalizeChineseProseShareThreshold(value.chineseProseShareThresholdPercent),
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
  // Same scalar ranges as Core IsHan; leave page-wide script counts unchanged.
  const perNodeHan = /^[\u3400-\u9fff\u{20000}-\u{323af}\uf900-\ufaff]$/u;
  // Unicode White_Space plus the edge BOM, matching Core's per-node trim.
  // Internal NEL remains whitespace; internal FEFF is not removed.
  function trimEligibilityText(text) {
    return String(text ?? "").replace(/^[\p{White_Space}\ufeff]+|[\p{White_Space}\ufeff]+$/gu, "");
  }
  // Established computing/science acronyms plus the reviewed public examples.
  // Keep this small, explicit uppercase set mirrored in Core PageTextEligibility;
  // membership alone never bypasses the surrounding Chinese evidence below.
  const technicalAbbreviations = new Set([
    "AI", "CPU", "WTT", "WSBK", "APEC",
    "API", "GPU", "USB", "SSD", "RAM", "DNS", "SQL", "HTML", "CSS",
    "JSON", "XML", "HTTP", "TCP", "UDP", "PDF", "DNA", "RNA", "MRI", "IPC"
  ]);
  // Common English imperative/UI verbs are lexical content, not name evidence.
  // TitleCase and Chinese neighbours (even 的) cannot disambiguate these as
  // proper names. Check whole words, including numbered actions: domains
  // and products with an independent suffix still have literal/name evidence.
  // Mirror this general command vocabulary in Core PageTextEligibility.
  const commandWords = new Set([
    "Accept", "Add", "Allow", "Apply", "Approve", "Browse", "Build", "Buy",
    "Cancel", "Change", "Check", "Choose", "Clear", "Click", "Close", "Confirm",
    "Connect", "Continue", "Copy", "Create", "Cut", "Delete", "Disable", "Download",
    "Edit", "Enable", "Enter", "Execute", "Exit", "Export", "Find", "Finish",
    "Follow", "Go", "Help", "Import", "Install", "Join", "Learn", "Listen", "Load",
    "Move", "Open", "Pause", "Play", "Press", "Print", "Quit", "Read", "Refresh",
    "Register", "Reject", "Reload", "Remove", "Rename", "Reply", "Reset", "Restart",
    "Restore", "Resume", "Retry", "Return", "Run", "Save", "Search", "Select", "Send",
    "Share", "Show", "Sign", "Skip", "Start", "Stop", "Submit", "Switch", "Try",
    "Uninstall", "Update", "Upload", "Use", "Verify", "View", "Wait", "Watch", "Write"
  ].map(word => word.toLowerCase()));
  // General document/learning headings, not a site or product vocabulary.
  // A number cannot distinguish "Chapter2" from a device ID. Prefer translating
  // ambiguous content words in any casing, including plural numbered headings.
  // Keep this vocabulary and the whole-token syntax mirrored in Core.
  const headingWords = new Set([
    "Activity", "Answer", "Appendix", "Appendices", "Article", "Assignment", "Book",
    "Chapter", "Conclusion", "Content", "Definition", "Diagram", "Episode", "Example",
    "Exercise", "Experiment", "Figure", "Hint", "Instruction", "Introduction", "Issue", "Lesson", "Module",
    "Note", "Objective", "Overview", "Page", "Paragraph", "Part", "Practice", "Problem", "Question", "Quiz",
    "Reference", "Result", "Review", "Scene", "Section", "Solution", "Stage", "Step", "Summary", "Table",
    "Task", "Topic", "Tutorial", "Unit", "Volume", "Warning", "Worksheet"
  ].map(word => word.toLowerCase()));
  function isContentWord(value) {
    const match = /^([A-Za-z]+)(?:[-_]?[0-9]{1,12}(?:\+{1,2}|#)?)?$/u.exec(value);
    if (!match) return false;
    const word = match[1].toLowerCase();
    return commandWords.has(word) || headingWords.has(word)
      || word.endsWith("s") && headingWords.has(word.slice(0, -1));
  }
  // Compact uppercase-number labels, not bare acronyms, names or English words.
  // Only automatic metadata gates use this without surrounding Chinese evidence.
  const structuredIdentifier = /^[A-Z]{1,4}[-_]?[0-9]{1,12}$/u;
  // Mirror Core's literal syntax; leave bare words and interleaved capitals
  // eligible. Automatic metadata only, so selection still translates IDs.
  const literalIdentifier = /^(?:[A-Z]{1,4}|[A-Z][a-z]+(?:[A-Z][a-z]+)*)[-_]?[0-9]{1,12}(?:[A-Z][a-z]+)?(?:\+{1,2}|#)?$/u;
  function alreadyTargetText(text, targetLanguage = "简体中文") {
    if (!/中文|Chinese|zh(?:-|$)/i.test(targetLanguage)) return false;
    const source = trimEligibilityText(text).normalize("NFKC");
    return readableChineseAbbreviations(source) || readableChineseTerms(source);
  }
  function readableChineseAbbreviations(source) {
    // Match Core PageTextEligibility's per-node rules after NFKC. Only ASCII
    // uppercase runs of 1..4 letters (optionally with digits, e.g. R7) are
    // candidates. Multi-letter runs must be known acronyms or compact
    // uppercase-number IDs (CR450, PAC-3), keeping HELP and
    // NO WAR eligible regardless of Han share. Space-separated runs must both
    // be known acronyms (AI CPU), not generic single-letter labels such as A B.
    // Other letters/combining marks keep mixed text eligible.
    // One abbreviation in a compact label needs >=3 Han (APEC中国年); spaced
    // labels/multiple abbreviations need >=6 Han and >=2 Han per Latin letter.
    // This retains "STOP 王曼昱!". Digits/punctuation/emoji do not inflate the share.
    // Same Unicode whitespace set as Rune.IsWhiteSpace (including NEL).
    const hasWhitespace = /[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]/u.test(source);
    let hanCount = 0, latinCount = 0, abbreviations = 0, runLetters = 0, hasCombiningMark = false;
    for (const character of source) {
      if (letter.test(character)) {
        if (perNodeHan.test(character)) { hanCount++; runLetters = 0; }
        else if (/^[A-Z]$/u.test(character)) {
          latinCount++;
          if (runLetters === 0) abbreviations++;
          if (++runLetters > 4) return false;
        } else return false;
      } else {
        if (!/^[0-9]$/u.test(character)) runLetters = 0;
        if (/\p{M}/u.test(character)) hasCombiningMark = true;
      }
    }
    if (latinCount === 0) return hanCount > 0;
    let previous = null;
    for (const run of source.matchAll(/[A-Z][A-Z0-9]*(?:[-_][0-9]+)?/gu)) {
      if (isContentWord(run[0])) return false;
      const letters = run[0].replace(/[^A-Z]/gu, "");
      if (letters.length > 1 && !technicalAbbreviations.has(letters) && !structuredIdentifier.test(run[0])) return false;
      if (previous) {
        const gap = source.slice(previous.index + previous[0].length, run.index);
        if (/^\p{White_Space}+$/u.test(gap)
            && (!technicalAbbreviations.has(previous[0]) || !technicalAbbreviations.has(run[0]))) return false;
      }
      previous = run;
    }
    return !hasCombiningMark && (abbreviations === 1 && hanCount >= 3 && !hasWhitespace
      || hanCount >= 6 && hanCount >= latinCount * 2);
  }
  // Names bind to Chinese on both sides (or possessive 的); internal capitals
  // also identify product names at a Chinese edge. A clause boundary is not a
  // binding: “，Alice介绍...” remains eligible. Lowercase prose, other scripts
  // and combining marks never become terms merely because Han predominates.
  function readableChineseTerms(source) {
    let hanCount = 0, latinCount = 0;
    for (const character of source) {
      if (letter.test(character)) {
        if (perNodeHan.test(character)) hanCount++;
        else if (/^[A-Za-z]$/u.test(character)) latinCount++;
        else return false;
      }
      if (/\p{M}/u.test(character)) return false;
    }
    if (hanCount === 0 || latinCount === 0) return false;
    const runs = [...source.matchAll(/[A-Za-z][A-Za-z0-9]*(?:[._:/+?#=&%~@-][A-Za-z0-9]+)*(?:\+{1,2}|#)?/gu)];
    let previous = null, novelTerm = false, literalOnly = true;
    for (const run of runs) {
      const value = run[0];
      if (isContentWord(value)) return false;
      // White_Space matches Rune/char.IsWhiteSpace; internal BOM is not a gap.
      const before = source.slice(0, run.index).replace(/\p{White_Space}+$/u, "");
      const after = source.slice(run.index + value.length).replace(/^\p{White_Space}+/u, "");
      const beforeHan = !!before && perNodeHan.test([...before].at(-1));
      const afterHan = !!after && perNodeHan.test([...after][0]);
      const dottedLabel = /^[A-Z]{2,}$/u.test(value) && after.startsWith(".")
        && after.length > 1 && perNodeHan.test([...after.slice(1)][0]);
      const literal = /^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*\.[A-Za-z]{2,63}(?:\/[A-Za-z0-9._~%+\-]*)?$/u.test(value)
        || literalIdentifier.test(value) || dottedLabel;
      if (literal) novelTerm = true;
      else if (technicalAbbreviations.has(value) || /^[A-Z]$/u.test(value)) literalOnly = false;
      else {
        if (!/^(?:[A-Z][a-z][A-Za-z]*|[a-z]+[A-Z][A-Za-z]*)$/u.test(value)
            || !(beforeHan && afterHan || after.startsWith("的") || /[a-z][A-Z]/u.test(value) && (beforeHan || afterHan))) return false;
        novelTerm = true;
        literalOnly = false;
      }
      if (previous) {
        const gap = source.slice(previous.index + previous[0].length, run.index);
        if (/^\p{White_Space}+$/u.test(gap)
            && (!technicalAbbreviations.has(previous[0]) || !technicalAbbreviations.has(value))) return false;
      }
      previous = run;
    }
    return novelTerm && (literalOnly && runs.length === 1 && hanCount >= 2
      || runs.length === 1 && hanCount >= 3 && !/\p{White_Space}/u.test(source)
      || hanCount >= 6 && hanCount >= latinCount * 2);
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
    if (/^[+\-−]?(?:\d[\d,.]*|\.\d+)\s*(?:%|°[cf]|℃|℉|usd|eur|gbp|jpy|cny|rmb|kg|mg|g|km|cm|mm|m|mph|km\/h|kb|mb|gb|tb|ms|s|hz|khz|mhz|ghz|px|dpi|ppi|fps|bps|kbps|mbps|gbps|pt|kcal|cal|w|kw|v|a|ml|l)$/iu.test(text)) return true;
    if (/^(?:usd|eur|gbp|jpy|cny|rmb|kg|mg|km|cm|mm|mph|km\/h|kb|mb|gb|tb|ms|hz|khz|mhz|ghz|px|dpi|ppi|fps|bps|kbps|mbps|gbps|kcal|kw|ml|°[cf]|℃|℉)$/iu.test(text)) return true;
    if (/^(?:[1-9][0-9]{1,4}\s*[x×]\s*[1-9][0-9]{1,4}(?:\s*px)?|[1-9][0-9]{2,3}p|[248]k)$/iu.test(text)) return true;
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
    if (!isContentWord(text) && (structuredIdentifier.test(text) || literalIdentifier.test(text))) return true;
    if (/^(?:[\u3400-\u9fff]{0,3}ICP\s*(?:备|備|证|證)\s*[0-9]{6,20}(?:号|號)?(?:-[0-9]{1,3})?|[\u3400-\u9fff]{1,3}公网安备\s*[0-9]{10,20}(?:号|號)?|(?:company\s+)?registration\s*(?:no\.?|number)\s*[:：]?\s*[A-Z]{0,4}-?[0-9]{4,20})$/iu.test(text)) return true;
    if (/^(?:(?:(?:estimated\s+)?reading|read)\s+time\s*:\s*[0-9]{1,3}\s*(?:min(?:ute)?s?|hours?)\.?|[0-9]{1,3}(?:\s+|-)(?:min(?:ute)?s?|hours?)(?:\s+|-)read\.?|(?:预计|預計)?(?:阅读|閱讀)(?:时间|時間)?\s*[:：]?\s*[0-9]{1,3}\s*(?:分钟|分鐘|小时|小時))$/iu.test(text)) return true;
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
    const source = trimEligibilityText(text).normalize("NFKC");
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
    const bodyProse = counted.filter(item => (!item.group.kind || item.group.kind === "body")
      && !skipTranslationText(item.group.text, { foreignHanContext: true }));
    const bodyLetters = bodyProse.reduce((sum, item) => sum + (item.group.text.match(/\p{L}/gu) || []).length, 0);
    const bodyKana = bodyProse.reduce((sum, item) => sum + item.counts.kanaCharacters, 0);
    // Same established 12% script signal as Core, using body groups only. Shared
    // Han news-list text follows substantial Japanese context, not a tiny quote.
    const japaneseBodyContext = bodyKana >= 3 && bodyKana >= bodyLetters * .12;
    const hanProseCharacters = bodyProse.filter(item => chineseProse(item.group.text, japaneseBodyContext))
      .reduce((sum, item) => sum + (item.group.text.match(/\p{L}/gu) || []).filter(character => han.test(character)).length, 0);
    const summary = { ...counts,
      // This is Chinese evidence, not all Han glyphs: Japanese paragraphs often
      // contain many Han characters alongside kana and must not trip the hard gate.
      hanProseCharacters,
      // Every other readable body letter counts, including Japanese Han, kana,
      // Cyrillic and Latin within a Chinese paragraph. Noise is not prose.
      nonChineseProseCharacters: bodyLetters - hanProseCharacters,
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
  function localVerdict(summary, targetLanguage, chineseProseShareThresholdPercent = 50) {
    if (!summary) return { needsTranslation: false, reason: "too_little_prose" };
    const chineseTarget = /中文|Chinese|zh(?:-|$)/i.test(targetLanguage || "简体中文");
    if (!chineseTarget) return null;
    const hanProse = summary.hanProseCharacters
      ?? (summary.kanaCharacters > 0 || summary.hangulCharacters > 0 ? 0 : summary.hanCharacters);
    const latinProse = summary.latinProseCharacters ?? summary.latinCharacters;
    const nonChineseProse = summary.nonChineseProseCharacters
      ?? ((latinProse || 0) + (summary.kanaProseCharacters ?? summary.kanaCharacters ?? 0) + (summary.hangulCharacters || 0));
    // Identified Chinese prose must reach the configured share of body letters.
    // Keep the established image policy for skipped mixed and Chinese-only pages.
    if (hanProse >= 6 && hanProse * 100 >= (hanProse + nonChineseProse)
        * normalizeChineseProseShareThreshold(chineseProseShareThresholdPercent))
      return { needsTranslation: false, reason: latinProse >= 12 ? "mixed_chinese_english" : "already_target_language" };
    if (summary.kanaCharacters > 0 || summary.hangulCharacters > 0) return null;
    if (summary.totalCharacters < 24) return { needsTranslation: false, reason: "too_little_prose" };
    return null;
  }
  function chineseProse(text, japaneseBodyContext = false) {
    const count = languageCounts(text);
    const letters = String(text).match(/\p{L}/gu) || [];
    const hanLetters = letters.filter(character => han.test(character)).length;
    const grammar = (String(text).match(/[的了是在和与为我你可请不该請為與這]/gu) || []).length;
    return count.kanaCharacters === 0 && count.hangulCharacters === 0 && hanLetters >= 6 && hanLetters * 2 >= letters.length
      && (!japaneseBodyContext || grammar >= 2);
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
    normalizeConfig, normalizeChineseProseShareThreshold, splitText, languageCounts, stableHash, isTimestamp, hasLanguageText, alreadyTargetText, skipTranslationText, localCalendarText, summarize, localVerdict, chineseProse, isSocialFeed, socialChineseParagraph, socialForeignText };
  root.ScreenTranslatorPageAutoPolicy = api;
  if (typeof module !== "undefined" && module.exports) module.exports = api;
})(globalThis);
