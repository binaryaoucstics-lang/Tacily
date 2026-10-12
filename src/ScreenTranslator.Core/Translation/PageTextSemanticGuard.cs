using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Translation;

public sealed record PageTextNumericTransfer(string OwnerId, string ReceivingId);
public sealed record PageTextSemanticAssessment(
    IReadOnlyList<string> UnsafeIds, IReadOnlyList<PageTextNumericTransfer> NumericTransfers);

/// <summary>
/// A small, local safeguard for DOM replacements, not a general translation scorer.
/// Protects independently located calendar/number facts and rejects punctuation-only
/// output for actual words. Never attempts to infer names, pronouns or gender.
/// </summary>
public static class PageTextSemanticGuard
{
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(100);
    private const string Numeral = @"[零〇一二两兩三四五六七八九十百千万萬亿億]+";
    // The 一 in 统一/唯一 is a word ending, not the first digit of the next
    // quantity (统一四种 must retain 4, rather than becoming the digit run 14).
    private const string ChineseNumeric = @"(?!(?<=[统統唯])一)[负負]?" + Numeral + @"(?:[点點][零〇一二两兩三四五六七八九]+[十百千万萬亿億]*)?";
    private const string ArabicNumeric = @"[+-]?(?:\d{1,3}(?:[, .\u00a0\u202f]\d{3})+|\d+)(?:[.,]\d+)?";
    private const string Numeric = @"(?:" + ArabicNumeric + "|" + ChineseNumeric + ")";
    private const string CurrencyPrefix = @"US\$|USD\b|EUR\b|\$|€";
    private const string CurrencySuffix = @"(?:US|U\.S\.)\s+dollars?\b|dollars?\b|euros?\b|USD\b|EUR\b|美元|美金|欧元|歐元";
    private const string QuantityUnits = @"分钟|分鐘|小时|小時|居民|用户|用戶|传感器|傳感器|摄氏度|攝氏度|毫升|公斤|千米|毫米|厘米|参数|參數|维度|維度|维|維|令牌|词元|詞元|标记|標記|字节|字節|字符|向量|编码器|編碼器|种|種|类|類|项|項|组|組|层|層|帧|幀|倍|tokens?\b|天|个|個|位|名|人|户|戶|只|台|枚|件|本|瓶|所|艘|辆|輛|栋|棟|秒|歳|岁|歲|次|米|比|号|號";
    private static readonly Regex FollowingQuantityUnit = MakeRegex(@"\G\s*(?:多|余|餘|来|來)?\s*(?:" + QuantityUnits + @")");
    private static readonly Regex ExplicitHanBillion = MakeRegex(@"^[负負]?[零〇一二两兩三四五六七八九十百千]+(?:[点點][零〇一二两兩三四五六七八九]+[十百千]*)?(?:万亿|萬億|亿|億)$");
    private static readonly Regex ByteContext = MakeRegex(@"\b(?:bytes?|packets?|buffers?|files?|storage|memory|disk|downloads?)\b|字节|字節|数据包|資料包|缓冲|緩衝|文件大小|檔案大小|内存|記憶體");
    private static readonly Regex CountContext = MakeRegex(@"\b(?:parameters?|params?|models?|weights?|tokens?)\b|参数|參數|模型|权重|權重|词元|詞元");
    // Attached, case-sensitive magnitude suffixes are common on model cards (740M,
    // 1B, 8K). Do not consume m (metres), MB/KB bytes, or B2/version identifiers.
    private static readonly Regex Quantities = MakeRegex(@"(?<![A-Za-z0-9])(?:(?<sign>[+-])?\s*(?<prefix>" + CurrencyPrefix
        + @")\s*)?(?<number>" + Numeric + @")(?:(?<scale>(?-i:[KMBT]|k)(?![A-Za-z0-9_]))|\s*(?<scale>trillion\b|billion\b|million\b|thousand\b|[十百千]?(?:万亿|萬億|亿|億|万|萬)|千|百|十))?(?:\s*(?<currency>" + CurrencySuffix + @"))?");
    private static readonly Regex Percentages = MakeRegex(@"(?:(?<fraction>百分之|千分之|万分之|萬分之)\s*(?<number>" + Numeric
        + @"))|(?<![A-Za-z0-9])(?<number>" + Numeric + @")\s*(?<unit>percentage\s+points?\b|percent\s+points?\b|个百分点|個百分點|百分點|percentage\b|percent\b|per\s+cent\b|%)");
    private static readonly Regex Weekdays = MakeRegex(
        @"\b(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|lundi|mardi|mercredi|jeudi|vendredi|samedi|dimanche)\b|[月火水木金土日](?:曜日|曜)|(?:周|週|星期|礼拜|禮拜)[一二三四五六日天1-7]");
    private static readonly Regex Clock = MakeRegex(
        @"(?<!\d)(?:(?<period>上午|下午|晚上|凌晨|中午|午前|午後)\s*)?(?<hour>\d{1,2}|[零〇一二两兩三四五六七八九十]{1,3})\s*(?::\s*(?<minute>\d{2})(?!\d)|(?:時|时|点|點|h\b)\s*(?:(?<minute>\d{1,2}|[零〇一二两兩三四五六七八九十]{1,3})\s*分?|(?<half>半))?)\s*(?<ampm>a\.?m\.?|p\.?m\.?)?");
    private static readonly Regex AmPmClock = MakeRegex(@"(?<!\d)(?<hour>\d{1,2})(?::(?<minute>\d{1,2}))?\s*(?<ampm>a\.?m\.?|p\.?m\.?)\b");
    private static readonly Regex Noon = MakeRegex(@"\b(?:noon|midday|midi|midnight)\b|正午|中午|午夜");
    private static readonly Regex ImplicitClock = MakeRegex(@"\bat\s+(?<hour>\d{1,2})(?=\s*(?:$|[.!?。！？,;()](?!\d)|\b(?:tonight|tomorrow|today|(?:in the|this)\s+(?:morning|afternoon|evening))\b))");
    private static readonly Regex HanOrdinal = MakeRegex(@"第(?<number>" + ChineseNumeric + @")");
    private static readonly Regex YearMonthDay = MakeRegex(@"(?<!\d)(?<year>\d{4}|[零〇一二三四五六七八九]{4})\s*(?:年|[-/])\s*(?<month>\d{1,2}|[一二三四五六七八九十]{1,3})\s*(?:月|[-/])\s*(?<day>\d{1,2}|[一二三四五六七八九十]{1,3})\s*日?");
    private static readonly Regex MonthDay = MakeRegex(@"(?<month>\d{1,2}|[一二三四五六七八九十]{1,3})\s*月\s*(?<day>\d{1,2}|[一二三四五六七八九十]{1,3})\s*日");
    // Japanese event notices spell 11/8(日). Only a date-linked weekday is evidence;
    // a bare fraction, model version, or the 日 inside a Japanese word is not.
    private const string DateWeekdayWord = @"[月火水木金土日](?:曜日|曜)?|(?:周|週|星期|礼拜|禮拜)[一二三四五六日天1-7]";
    private static readonly Regex JapaneseDateWeekday = MakeRegex(@"(?<![\d./])(?<month>\d{1,2})\s*/\s*(?<day>\d{1,2})\s*\(\s*(?<weekday>" + DateWeekdayWord + @")\s*\)");
    private static readonly Regex DateWeekdaySuffix = MakeRegex(@"\G\s*\(\s*(?<weekday>" + DateWeekdayWord + @")\s*\)");
    private const string MonthWords = PageCalendarText.EnglishMonthPattern + @"|janvier|février|fevrier|mars|avril|mai|juin|juillet|août|aout|septembre|octobre|novembre|décembre|decembre";
    // A model/mission identifier such as Tianwen-3 Mars is not "3 March".
    private static readonly Regex NamedDate = MakeRegex(@"\b(?:(?<month>" + MonthWords + @")\b\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?\b|(?<![\p{L}\p{N}][_-])(?<day>\d{1,2})(?:st|nd|rd|th)?\s+(?<month>" + MonthWords + @")\b\.?)(?:\s*,?\s*(?<year>\d{4})(?!\d))?");
    private static readonly Regex Arabic = MakeRegex(@"(?<![A-Za-z0-9])" + ArabicNumeric);
    private static readonly Regex TwoFactorAuthentication = MakeRegex(
        @"\b2FA\b|\b(?:two|dual|2)[\s-]+factor\s+(?:authentication|verification)\b"
        + @"|(?:双因素|雙因素|双因子|雙因子|两因素|兩因素|二因素)\s*(?:身份|身分)?\s*(?:认证|認證|验证|驗證)"
        + @"|(?:双重|雙重)\s*(?:(?:身份|身分)\s*(?:认证|認證|验证|驗證)|认证|認證)");
    private static readonly Regex ContextualTwoFactor = MakeRegex(
        @"(?:双重|雙重|两步|兩步|二步|2步|二次|2次)\s*(?:身份|身分)?\s*(?:认证|認證|验证|驗證)");
    private static readonly Regex ChineseNumber = MakeRegex(ChineseNumeric
        + @"(?=\s*(?:年|月|日|天|个|個|位|名|人|只|台|枚|件|本|瓶|分钟|分鐘|分|秒|小时|小時|歳|岁|歲|次|米|毫升|公斤|比|号|號|%))|(?<=比)" + ChineseNumeric);
    private static readonly Regex WholeChineseNumber = MakeRegex(@"^\s*(?<number>" + ChineseNumeric + @")\s*[。.!]?\s*$");
    private const string CalendarDigits = @"[零〇一二三四五六七八九]";
    // Explicit phase synonyms only; match longer forms before 初/中/末 so the
    // shared century/decade parser retains the full early/mid/late distinction.
    private const string CalendarPhase = @"早期|初期|中期|中叶|中葉|晚期|后期|後期|末期|初|中|末";
    private static readonly Regex ChineseDecades = MakeRegex(
        @"(?<![\d零〇一二三四五六七八九十百千])(?:(?<century>\d{1,2}|[零〇一二三四五六七八九十]{1,4})\s*世[纪紀]\s*(?<decade>\d{1,2}|[零〇一二三四五六七八九十]{1,3})|(?<year>\d{4}|" + CalendarDigits + @"{4}))\s*年代\s*(?<phase>" + CalendarPhase + @")?");
    private static readonly Regex EnglishDecades = MakeRegex(
        @"(?<![\p{L}\p{N}_.+])(?:(?<phase>early|mid|late)[\s-]+)?(?<year>\d{4})['’]?s\b");
    private static readonly Regex CalendarDecadePrefix = MakeRegex(@"\bthe\s+$");
    private static readonly Regex DurationPrefix = MakeRegex(@"\b(?:for|lasts?|lasted|lasting|takes?|took|taking|wait|waited|waiting|duration|timeout|delay|runtime|elapsed)\s*(?:(?:of|was|is)\s*|[=:]\s*)?$");
    private static readonly Regex DurationSuffix = MakeRegex(@"^\s*(?:elapsed|long|duration|timeout|delay|runtime|wait)\b");
    private static readonly Regex DurationContext = MakeRegex(@"\b(?:duration|timeout|delay|runtime|elapsed|wait|waited|waiting|lasted|lasting|takes?|took|taking|completed|finished|seconds?)\b");
    private static readonly Regex Centuries = MakeRegex(
        @"(?<![\d零〇一二三四五六七八九十百千])(?<century>\d{1,2}|[零〇一二三四五六七八九十]{1,4})\s*世[纪紀]\s*(?<phase>" + CalendarPhase + @")?"
        + @"|\b(?:(?<phase>early|mid|late)[\s-]+)?(?<century>\d{1,2})(?:st|nd|rd|th)\s+century\b");
    private static readonly IReadOnlyDictionary<string, string> DayNumbers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Monday"] = "1", ["lundi"] = "1", ["月"] = "1",
        ["Tuesday"] = "2", ["mardi"] = "2", ["火"] = "2",
        ["Wednesday"] = "3", ["mercredi"] = "3", ["水"] = "3",
        ["Thursday"] = "4", ["jeudi"] = "4", ["木"] = "4",
        ["Friday"] = "5", ["vendredi"] = "5", ["金"] = "5",
        ["Saturday"] = "6", ["samedi"] = "6", ["土"] = "6",
        ["Sunday"] = "7", ["dimanche"] = "7", ["日"] = "7",
        ["一"] = "1", ["二"] = "2", ["三"] = "3", ["四"] = "4",
        ["五"] = "5", ["六"] = "6", ["天"] = "7",
    };

    public static IReadOnlyList<string> FindUnsafeIds(
        IReadOnlyList<BrowserPageTextBlock> source,
        IReadOnlyDictionary<string, string> translations)
        => Assess(source, translations).UnsafeIds;

    /// <summary>Whether this source has facts requiring validation before display.</summary>
    public static bool HasProtectedFacts(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var facts = Facts(text, sourceClock: true);
        return facts.Numbers.Count != 0 || facts.Dates.Count != 0
            || facts.Times.Count != 0 || facts.Weekdays.Count != 0;
    }

    /// <summary>Reports the same guard verdict plus actual numeric ownership edges.</summary>
    public static PageTextSemanticAssessment Assess(
        IReadOnlyList<BrowserPageTextBlock> source,
        IReadOnlyDictionary<string, string> translations)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translations);
        var originals = source.ToDictionary(block => block.Id, block => Facts(block.Text, sourceClock:true), StringComparer.Ordinal);
        var translated = source.ToDictionary(block => block.Id,
            block => translations.TryGetValue(block.Id, out var text)
                ? Facts(text, originals[block.Id].Numbers.Contains("auth-factors:2")) : new TextFacts([], [], [], []), StringComparer.Ordinal);
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var transfers = new HashSet<PageTextNumericTransfer>();
        foreach (var block in source)
        {
            if (!translations.TryGetValue(block.Id, out var text)
                || string.IsNullOrWhiteSpace(text)
                || (block.Text.Any(char.IsLetter) && !text.Any(char.IsLetterOrDigit)))
            { rejected.Add(block.Id); continue; }
            var before = originals[block.Id];
            var after = translated[block.Id];
            // A weekday/time must stay on its own node, even when a neighbouring node owns it.
            if (!before.Weekdays.SetEquals(after.Weekdays) || !TimesEquivalent(before.Times,after.Times)
                || !before.Dates.SetEquals(after.Dates)
                || !before.Numbers.Where(IsUnitFact).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(after.Numbers.Where(IsUnitFact))
                || !before.Numbers.IsSubsetOf(after.Numbers))
                rejected.Add(block.Id);
        }
        foreach (var owner in source)
        {
            var missing = originals[owner.Id].Numbers.Except(translated[owner.Id].Numbers).ToArray();
            foreach (var receiving in source.Where(block => block.Id != owner.Id))
                if (missing.Any(value => translated[receiving.Id].Numbers.Contains(value)
                    && !originals[receiving.Id].Numbers.Contains(value)))
                {
                    rejected.Add(owner.Id); rejected.Add(receiving.Id);
                    transfers.Add(new(owner.Id, receiving.Id));
                }
        }
        return new(source.Where(block => rejected.Contains(block.Id)).Select(block => block.Id).ToArray(),
            transfers.OrderBy(edge => edge.OwnerId, StringComparer.Ordinal)
                .ThenBy(edge => edge.ReceivingId, StringComparer.Ordinal).ToArray());
    }

    private static bool TimesEquivalent(HashSet<string> expected,HashSet<string> actual)
    {
        var remaining=new HashSet<string>(actual,StringComparer.Ordinal);
        foreach(var time in expected.Where(value=>!value.StartsWith("ambiguous:",StringComparison.Ordinal)))
            if(!remaining.Remove(time)) return false;
        foreach(var time in expected.Where(value=>value.StartsWith("ambiguous:",StringComparison.Ordinal)))
        {
            var minute=int.Parse(time[10..],CultureInfo.InvariantCulture);
            var candidate=remaining.FirstOrDefault(value=>int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var concrete)&&concrete%720==minute);
            if(candidate is null)return false;remaining.Remove(candidate);
        }
        return remaining.Count==0;
    }
    private static TextFacts Facts(string text, bool twoFactorContext = false,bool sourceClock=false)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC).Replace('−', '-');
        var days = new HashSet<string>(StringComparer.Ordinal);
        var times = new HashSet<string>(StringComparer.Ordinal);
        var dates = new HashSet<string>(StringComparer.Ordinal);
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        // 2FA may be translated as 双重身份验证 without retaining the digit 2.
        // Keep the authentication concept separately from ordinary quantities.
        // Ambiguous 二次/两步/双重验证 is recognized only when the source says 2FA;
        // a general instruction to double-check must not acquire an auth fact.
        string ReadTwoFactor(Match _) { numbers.Add("auth-factors:2"); return " "; }
        normalized = TwoFactorAuthentication.Replace(normalized, ReadTwoFactor);
        if (twoFactorContext) normalized = ContextualTwoFactor.Replace(normalized, ReadTwoFactor);
        // Keep a decade separate from its component numbers: 1960s is exactly
        // 20世纪60年代, not the year 1960 or an unspecified century beginning.
        // These typed numbers also participate in the existing per-ID transfer check.
        normalized = ChineseDecades.Replace(normalized, match =>
        {
            decimal year;
            if (match.Groups["year"].Success)
            {
                if (!TryNumber(match.Groups["year"].Value, out year)) return match.Value;
            }
            else
            {
                if (!TryNumber(match.Groups["century"].Value, out var century) || century is < 1 or > 99
                    || !TryNumber(match.Groups["decade"].Value, out var decade) || decade is < 0 or > 90 || decade % 10 != 0)
                    return match.Value;
                year = (century - 1) * 100 + decade;
            }
            if (year is < 1000 or > 9990 || year % 10 != 0) return match.Value;
            numbers.Add("decade:" + Canonical(year) + ":" + Phase(match.Groups["phase"].Value));
            return " ";
        });
        normalized = EnglishDecades.Replace(normalized, match =>
        {
            if (!TryNumber(match.Groups["year"].Value, out var year) || year is < 1000 or > 9990 || year % 10 != 0)
                return match.Value;
            // A hyphen after another decade is a range separator, not a sign.
            if (match.Index > 0 && normalized[match.Index - 1] == '-'
                && (match.Index < 2 || char.ToLowerInvariant(normalized[match.Index - 2]) != 's')) return match.Value;
            // Attached s can mean seconds. Explicit calendar qualifiers take priority;
            // local duration cues leave the quantity to the ordinary number parser.
            var prefix = normalized[Math.Max(0, match.Index - 80)..match.Index];
            var clauseStart = prefix.LastIndexOfAny(['.', ';', '\n', '。', '；']);
            if (clauseStart >= 0) prefix = prefix[(clauseStart + 1)..];
            var suffix = normalized[(match.Index + match.Length)..Math.Min(normalized.Length, match.Index + match.Length + 80)];
            if (!match.Groups["phase"].Success && (DurationSuffix.IsMatch(suffix)
                || (!CalendarDecadePrefix.IsMatch(prefix) && (DurationPrefix.IsMatch(prefix) || DurationContext.IsMatch(prefix)))))
                return match.Value;
            numbers.Add("decade:" + Canonical(year) + ":" + Phase(match.Groups["phase"].Value));
            return " ";
        });
        normalized = Centuries.Replace(normalized, match =>
        {
            if (!TryNumber(match.Groups["century"].Value, out var century) || century is < 1 or > 99) return match.Value;
            numbers.Add("century:" + Canonical(century) + ":" + Phase(match.Groups["phase"].Value));
            return " ";
        });
        // Extract units before clocks/bare numbers: 十二点五 percent is 12.5%, not 12:05,
        // and 90 billion euros is the same fact as 900亿欧元. Keep currency identity;
        // a currency conversion is not inferred or accepted by this local safeguard.
        normalized = Percentages.Replace(normalized, match =>
        {
            if (!TryNumber(match.Groups["number"].Value, out var value)) return match.Value;
            var fraction = match.Groups["fraction"].Value;
            if (fraction == "千分之") value /= 10;
            else if (fraction is "万分之" or "萬分之") value /= 100;
            var unit = match.Groups["unit"].Value;
            var points = unit.Contains("point", StringComparison.OrdinalIgnoreCase) || unit is "个百分点" or "個百分點" or "百分點";
            numbers.Add((points ? "percentage-points:" : "percent:") + Canonical(value));
            return " ";
        });
        normalized = Quantities.Replace(normalized, match =>
        {
            var numeral = match.Groups["number"].Value;
            var prefix = Currency(match.Groups["prefix"].Value);
            var suffix = Currency(match.Groups["currency"].Value);
            var scale = match.Groups["scale"].Value;
            // A bare B also means bytes. Keep the previous numeric treatment for
            // clearly byte-related text; parameter/model B and money keep magnitude.
            if (scale == "B" && prefix.Length == 0 && suffix.Length == 0
                && IsByteContext(normalized, match.Index, match.Length)) return match.Value;
            // Leave ordinary digits in place for dates/times and the existing bare-number
            // guard. Pure Han numerals need a quantity unit or an explicit 亿 magnitude:
            // 万一 and 千万不要 are words, while 参数少于十亿 is a numeric fact.
            // Consume explicit decimal quantities before Clock (三点五米 is 3.5m).
            if (prefix.Length == 0 && suffix.Length == 0 && scale.Length == 0
                && !FollowingQuantityUnit.IsMatch(normalized, match.Index + match.Length)
                && !ExplicitHanBillion.IsMatch(numeral)) return match.Value;
            if (!TryNumber(numeral, out var value)) return match.Value;
            try { value *= Scale(scale); }
            catch (OverflowException) { return match.Value; }
            if (match.Groups["sign"].Value == "-") value = -value;
            var currency = prefix.Length == 0 ? suffix : suffix.Length == 0 || suffix == prefix ? prefix : prefix + "/" + suffix;
            numbers.Add((currency.Length == 0 ? "" : "money:" + currency + ":") + Canonical(value));
            return " ";
        });
        var whole = WholeChineseNumber.Match(normalized);
        if (whole.Success && TryNumber(whole.Groups["number"].Value, out var wholeValue))
        { numbers.Add(Canonical(wholeValue)); normalized = " "; }
        string ReadDate(Match match)
        {
            var monthText = match.Groups["month"].Value;
            // Lowercase English "may" is commonly a modal verb after a quantity
            // ("children under 12 may join"). A year or capitalized month is
            // necessary before treating that ambiguous word as a date here.
            if (monthText == "may" && !match.Groups["year"].Success) return match.Value;
            if (!TryNumber(monthText, out var month))
            {
                month = PageCalendarText.EnglishMonthNumber(monthText);
                var names = new[] { "january|janvier", "february|février|fevrier", "march|mars", "april|avril", "may|mai", "june|juin",
                    "july|juillet", "august|août|aout", "september|septembre", "october|octobre", "november|novembre", "december|décembre|decembre" };
                if (month == 0) month = Array.FindIndex(names, namesForMonth => namesForMonth.Split('|').Contains(monthText, StringComparer.OrdinalIgnoreCase)) + 1;
            }
            if (month is < 1 or > 12 || !TryNumber(match.Groups["day"].Value, out var day) || day is < 1 or > 31) return match.Value;
            dates.Add("md:" + month.ToString(CultureInfo.InvariantCulture) + "-" + day.ToString(CultureInfo.InvariantCulture));
            var weekday = match.Groups["weekday"].Success ? match.Groups["weekday"].Value
                : DateWeekdaySuffix.Match(normalized, match.Index + match.Length).Groups["weekday"].Value;
            var weekdayKey = weekday.StartsWith("周", StringComparison.Ordinal) || weekday.StartsWith("週", StringComparison.Ordinal)
                || weekday.StartsWith("星期", StringComparison.Ordinal) || weekday.StartsWith("礼拜", StringComparison.Ordinal)
                || weekday.StartsWith("禮拜", StringComparison.Ordinal) ? weekday[^1..] : weekday.Length > 0 ? weekday[..1] : "";
            if (DayNumbers.TryGetValue(weekdayKey, out var weekdayNumber)) days.Add(weekdayNumber);
            if (match.Groups["year"].Success && TryNumber(match.Groups["year"].Value, out var year))
                dates.Add("year:" + year.ToString(CultureInfo.InvariantCulture));
            return " ";
        }
        normalized = JapaneseDateWeekday.Replace(normalized, ReadDate);
        normalized = YearMonthDay.Replace(normalized, ReadDate);
        normalized = MonthDay.Replace(normalized, ReadDate);
        normalized = NamedDate.Replace(normalized, ReadDate);
        normalized = Weekdays.Replace(normalized, match =>
        {
            var word = match.Value;
            var key = word.EndsWith("曜日", StringComparison.Ordinal) || word.EndsWith("曜", StringComparison.Ordinal)
                ? word[..1] : word.StartsWith("周", StringComparison.Ordinal) || word.StartsWith("週", StringComparison.Ordinal)
                    || word.StartsWith("星期", StringComparison.Ordinal) || word.StartsWith("礼拜", StringComparison.Ordinal)
                    || word.StartsWith("禮拜", StringComparison.Ordinal) ? word[^1..] : word;
            days.Add(DayNumbers.TryGetValue(key, out var value) ? value : key);
            return " ";
        });
        string ReadClock(Match match)
        {
            if (!TryNumber(match.Groups["hour"].Value, out var hour) || hour is < 0 or > 23) return match.Value;
            var minute = match.Groups["half"].Success ? 30m : 0m;
            if (match.Groups["minute"].Success && !TryNumber(match.Groups["minute"].Value, out minute)) return match.Value;
            if (minute is < 0 or > 59) return match.Value;
            var period = match.Groups["period"].Value;
            var ampm = match.Groups["ampm"].Value.ToLowerInvariant();
            // 一時停止 / 一时停止 means a pause, not a clock time.
            if(hour==1 && period.Length==0 && ampm.Length==0 && !match.Groups["minute"].Success && !match.Groups["half"].Success
                && match.Value.Contains('一') && normalized.AsSpan(match.Index+match.Length).StartsWith("停止",StringComparison.Ordinal)) return match.Value;
            if ((period is "下午" or "晚上" or "午後" || ampm.StartsWith('p')) && hour < 12) hour += 12;
            if ((period is "上午" or "凌晨" or "午前" || ampm.StartsWith('a')) && hour == 12) hour = 0;
            times.Add((sourceClock && period.Length==0 && ampm.Length==0 && hour<=12 ? "ambiguous:" : "")
                +(sourceClock && period.Length==0 && ampm.Length==0 && hour<=12 ? (hour*60+minute)%720 : hour*60+minute).ToString(CultureInfo.InvariantCulture));
            return " ";
        }
        normalized = AmPmClock.Replace(normalized, ReadClock);
        normalized = Clock.Replace(normalized, ReadClock);
        normalized = ImplicitClock.Replace(normalized,match=>
        {
            if(!sourceClock || !TryNumber(match.Groups["hour"].Value,out var hour)||hour is <1 or >12)return match.Value;
            times.Add("ambiguous:"+((hour*60)%720).ToString(CultureInfo.InvariantCulture));return " ";
        });
        normalized = Noon.Replace(normalized, match =>
        { times.Add(match.Value.Equals("midnight", StringComparison.OrdinalIgnoreCase) || match.Value == "午夜" ? "0" : "720"); return " "; });
        normalized=HanOrdinal.Replace(normalized,match=>
        {
            if(!TryNumber(match.Groups["number"].Value,out var number))return match.Value;
            numbers.Add(Canonical(number));return " ";
        });
        foreach (Match match in Arabic.Matches(normalized))
            if (TryNumber(match.Value, out var value)) numbers.Add(Canonical(value));
        foreach (Match match in ChineseNumber.Matches(normalized))
            if (TryNumber(match.Value, out var value)) numbers.Add(Canonical(value));
        return new(days, times, dates, numbers);
    }

    private static bool TryNumber(string value, out decimal result)
    {
        value = value.Trim();
        // Limit work for malformed numeral runs received from arbitrary webpages.
        // Valid decimal amounts fit within 29 significant digits; this also bounds
        // recursive Chinese magnitude parsing independently of the regex deadline.
        if (value.Length > 128) { result = 0; return false; }
        if (value.Any(char.IsAsciiDigit)) return TryArabicNumber(value, out result);
        try { return TryChineseNumber(value, out result); }
        catch (OverflowException) { result = 0; return false; }
    }

    private static bool IsByteContext(string text, int index, int length)
    {
        var start = Math.Max(0, index - 80);
        var end = Math.Min(text.Length, index + length + 80);
        // Bound the hint to the current clause so a nearby model count does not
        // change the meaning of a separate packet/file quantity (or vice versa).
        for (var offset = index - 1; offset >= start; offset--)
            if (text[offset] is '.' or ';' or '\n' or '。' or '；') { start = offset + 1; break; }
        for (var offset = index + length; offset < end; offset++)
            if (text[offset] is '.' or ';' or '\n' or '。' or '；') { end = offset; break; }
        var context = text[start..end];
        return ByteContext.IsMatch(context) && !CountContext.IsMatch(context);
    }

    private static bool TryArabicNumber(string value, out decimal result)
    {
        value = value.Replace(" ", "", StringComparison.Ordinal).Replace("\u00a0", "", StringComparison.Ordinal).Replace("\u202f", "", StringComparison.Ordinal);
        var comma = value.LastIndexOf(',');
        var dot = value.LastIndexOf('.');
        if (comma >= 0 && dot >= 0)
        {
            // When both separators occur, the last is the decimal mark (1,250.50 /
            // 1.250,50); preceding separators are grouping marks.
            var decimalMark = comma > dot ? ',' : '.';
            var groupingMark = decimalMark == ',' ? '.' : ',';
            value = value.Replace(groupingMark.ToString(), "", StringComparison.Ordinal).Replace(decimalMark, '.');
        }
        else if (comma >= 0)
        {
            var parts = value.Split(',');
            var grouped = parts[0].Length is >= 1 and <= 3 && parts.Skip(1).All(part => part.Length == 3);
            value = grouped ? value.Replace(",", "", StringComparison.Ordinal) : value.Replace(',', '.');
        }
        else if (value.Count(character => character == '.') > 1)
        {
            var parts = value.Split('.');
            if (parts[0].Length is >= 1 and <= 3 && parts.Skip(1).All(part => part.Length == 3))
                value = value.Replace(".", "", StringComparison.Ordinal);
        }
        return decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryChineseNumber(string value, out decimal result)
    {
        if (value.Length > 0 && value[0] is '负' or '負')
        {
            if (!TryChineseNumber(value[1..], out result)) return false;
            result = -result;
            return true;
        }
        var point = value.IndexOfAny(['点', '點']);
        if (point < 0) return TryChineseInteger(value, out result);
        result = 0;
        if (!TryChineseInteger(value[..point], out var integer)) return false;
        var endOfFraction = point + 1;
        decimal fraction = 0, denominator = 1;
        while (endOfFraction < value.Length && ChineseDigit(value[endOfFraction]) is >= 0 and <= 9)
        {
            fraction = fraction * 10 + ChineseDigit(value[endOfFraction]);
            denominator *= 10;
            endOfFraction++;
        }
        if (endOfFraction == point + 1) return false;
        var multiplier = 1m;
        if (endOfFraction < value.Length && !TryChineseInteger(value[endOfFraction..], out multiplier)) return false;
        result = (integer + fraction / denominator) * multiplier;
        return true;
    }

    private static bool TryChineseInteger(string value, out decimal result)
    {
        // Split from the greatest written unit so 万亿 and 一万二千五百亿 retain
        // their multiplier, rather than adding an unrelated 亿 to a previous 万.
        foreach (var (unit, multiplier) in new (string, decimal)[]
        { ("万亿", 1_000_000_000_000m), ("萬億", 1_000_000_000_000m), ("亿", 100_000_000m), ("億", 100_000_000m), ("万", 10_000m), ("萬", 10_000m) })
        {
            var index = value.LastIndexOf(unit, StringComparison.Ordinal);
            if (index < 0) continue;
            var prefix = 1m;
            var suffix = 0m;
            if (index > 0 && !TryChineseInteger(value[..index], out prefix)) { result = 0; return false; }
            if (index + unit.Length < value.Length && !TryChineseInteger(value[(index + unit.Length)..], out suffix)) { result = 0; return false; }
            result = prefix * multiplier + suffix;
            return true;
        }
        result = 0;
        decimal section = 0, digit = 0;
        var hadNumeral = false;
        var hasUnits = value.Any(character => "十百千".Contains(character));
        foreach (var character in value)
        {
            var number = ChineseDigit(character);
            if (number >= 0)
            { digit = hasUnits ? number : digit * 10 + number; hadNumeral = true; continue; }
            var unit = character switch { '十' => 10, '百' => 100, '千' => 1_000, _ => 0 };
            if (unit == 0) return false;
            hadNumeral = true;
            section += (digit == 0 ? 1 : digit) * unit;
            digit = 0;
        }
        result += section + digit;
        return hadNumeral;
    }

    private static int ChineseDigit(char character) => character switch
    { '〇' => 0, '两' or '兩' => 2, _ => "零一二三四五六七八九".IndexOf(character) };

    private static decimal Scale(string value) => value.ToLowerInvariant() switch
    {
        "trillion" or "t" or "万亿" or "萬億" => 1_000_000_000_000m,
        "billion" or "b" => 1_000_000_000m,
        "亿" or "億" => 100_000_000m,
        "million" or "m" => 1_000_000m,
        "万" or "萬" => 10_000m,
        "thousand" or "k" or "千" => 1_000m,
        _ => TryChineseInteger(value, out var multiplier) ? multiplier : 1m
    };

    private static string Currency(string value)
    {
        if (value.Length == 0) return "";
        return value.Contains('€') || value.Contains("eur", StringComparison.OrdinalIgnoreCase) || value is "欧元" or "歐元" ? "EUR" : "USD";
    }

    private static string Canonical(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string Phase(string value) => value.ToLowerInvariant() switch
    {
        "early" or "初" or "初期" or "早期" => "early",
        "mid" or "中" or "中期" or "中叶" or "中葉" => "mid",
        "late" or "末" or "末期" or "晚期" or "后期" or "後期" => "late",
        _ => "all"
    };

    private static bool IsUnitFact(string value) => value.StartsWith("money:", StringComparison.Ordinal)
        || value.StartsWith("percent:", StringComparison.Ordinal) || value.StartsWith("percentage-points:", StringComparison.Ordinal)
        || value.StartsWith("auth-factors:", StringComparison.Ordinal)
        || value.StartsWith("decade:", StringComparison.Ordinal) || value.StartsWith("century:", StringComparison.Ordinal);

    private static Regex MakeRegex(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
    private sealed record TextFacts(HashSet<string> Weekdays, HashSet<string> Times, HashSet<string> Dates, HashSet<string> Numbers);
}
