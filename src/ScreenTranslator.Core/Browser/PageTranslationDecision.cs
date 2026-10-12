using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Browser;

// Preserve the old persisted Rules value; migration maps retired 0/2 modes here.
public enum BrowserPageJudgementMode { Rules = 1 }
public sealed record PageTextSample(string Text, string Kind = "body", string? LanguageHint = null);
public sealed record PageTextStatistics(int TotalCharacters = 0, int HanCharacters = 0, int KanaCharacters = 0,
    int LatinCharacters = 0, int ProseBlocks = 0, int HanProseCharacters = 0,
    int LatinProseCharacters = 0, int KanaProseCharacters = 0, int NonChineseProseCharacters = 0);
public sealed record PageTranslationAssessment(bool NeedsTranslation, string Reason, double Confidence,
    string DetectedLanguage, bool IsMixedTargetLanguage)
{
    public string ReasonCode { get; init; } = "insufficient-evidence";
    public double ElapsedMilliseconds { get; init; }
    public IReadOnlyList<PageTextSample> EligibleBlocks { get; init; } = [];
}
public sealed record PageTranslationEvidence(PageTranslationAssessment RuleDecision, bool IsHardGate);

/// <summary>Bounded Unicode, prose and common-word rules. No weights, model loading or network.</summary>
public static partial class PageTranslationDecision
{
    public const string PolicyVersion = "page-program-prose-3";
    public const int DefaultChineseProseShareThresholdPercent = 50;
    public const int MinimumChineseProseShareThresholdPercent = 10;
    public const int MaximumChineseProseShareThresholdPercent = 90;
    public const int MaximumBlocks = 600;
    public const int MaximumSampleCharacters = 24_000;

    public static int NormalizeChineseProseShareThresholdPercent(int value) =>
        Math.Clamp(value, MinimumChineseProseShareThresholdPercent, MaximumChineseProseShareThresholdPercent);

    public static PageTranslationEvidence Inspect(IReadOnlyList<PageTextSample> samples, string targetLanguage,
        PageTextStatistics? statistics = null,
        int chineseProseShareThresholdPercent = DefaultChineseProseShareThresholdPercent)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var target = NormalizeLanguage(targetLanguage);
        var remaining = MaximumSampleCharacters;
        var eligible = new List<PageTextSample>();
        foreach (var sample in samples.Take(MaximumBlocks))
        {
            if (remaining <= 0) break;
            if (sample is null || string.IsNullOrWhiteSpace(sample.Text)
                || sample.Kind is "navigation" or "code" or "form" or "editable" or "hidden") continue;
            var text = sample.Text.Trim();
            if (LooksLikeCode(text) || !PageTextEligibility.NeedsTranslation(text, "und", kind: sample.Kind)) continue;
            var count = Count(text);
            if (count.Letters < 12 || count.Latin == count.Letters && Words().Matches(text).Count < 3) continue;
            text = text[..Math.Min(text.Length, Math.Min(4096, remaining))];
            remaining -= text.Length;
            eligible.Add(sample with { Text = text });
        }
        var allText = string.Join("\n", eligible.Select(block => block.Text));
        var all = Count(allText);
        var body = eligible.Where(block => block.Kind is not ("title" or "heading")).ToArray();
        PageTranslationEvidence Result(bool needs, string code, string reason, string language,
            double confidence = .95, bool mixed = false) => new(new(needs, reason, confidence, language, mixed)
            { ReasonCode = code, EligibleBlocks = eligible.ToArray() }, true);

        if (target == "und") return Result(false, "unsupported-target", "目标语言未列入程序自动判断范围。", "und", .5);
        // Only actual Chinese prose vetoes an otherwise foreign page. Logos, names,
        // navigation labels and a few Han glyphs in an English article are insufficient.
        var chinese = body.Where(block => IsChineseProse(block.Text)).ToArray();
        var chineseLetters = chinese.Sum(block => Count(block.Text).Han);
        if (target == "zh" && (chineseLetters >= 6 || statistics is { HanProseCharacters: >= 24 }))
        {
            var mixed = all.Latin >= 12 || all.Kana >= 3 || all.Hangul >= 4
                || statistics is { LatinProseCharacters: >= 32 } or { KanaProseCharacters: >= 6 };
            return Result(false, mixed ? "mixed-target-body" : "target-body",
                mixed ? "正文已含可读中文和外语混排，保持原页。" : "正文已是中文，保持原页。", "zh", .98, mixed);
        }
        if (all.Letters < 24)
        {
            if (statistics is { LatinProseCharacters: >= 64 } && !LatinTargets.Contains(target))
                return Result(true, "foreign-script", "整页有足够的外语正文，分布采样未覆盖完整段落。", "latin");
            if (statistics is { KanaProseCharacters: >= 6 } && target != "ja")
                return Result(true, "foreign-script", "整页存在日语正文。", "ja");
            return Result(false, "insufficient-body", "可读正文不足，继续观察页面加载。", "und");
        }
        if (all.Han > 0 && all.Kana == 0 && all.Latin < 12 && all.Hangul == 0 && all.Other == 0)
            return target is "zh" or "ja"
                ? Result(false, "han-only-ambiguous", "仅汉字内容可直接阅读。", "zh-or-ja", .8)
                : Result(true, "foreign-script", "正文文字与目标语言不同。", "zh-or-ja");
        bool Dominant(int count, int minimum) => count >= minimum && (count >= all.Letters * .12 || count > all.Latin * .5);
        var language = Dominant(all.Kana, 3) || all.Kana >= 3 && all.Kana + all.Han > all.Latin * .5 ? "ja"
            : Dominant(all.Hangul, 4) ? "ko" : all.Han >= 12 && all.Han * 2 >= all.Letters ? "zh"
            : Dominant(all.Cyrillic, 12) ? "ru-script" : Dominant(all.Arabic, 12) ? "ar"
            : Dominant(all.Greek, 12) ? "el" : Dominant(all.Devanagari, 12) ? "hi" : Dominant(all.Thai, 12) ? "th"
            : all.Latin >= 24 ? "latin" : "und";
        if (language != "latin" && language != "und")
        {
            var matches = language == target || language == "ru-script" && target is "ru" or "uk" or "bg";
            return Result(!matches, matches ? "target-script" : "foreign-script",
                matches ? "正文文字与目标语言一致。" : "正文文字与目标语言不同。", language);
        }
        if (language == "latin")
        {
            if (!LatinTargets.Contains(target))
                return Result(true, "foreign-script", "正文以外语拉丁文字为主。", "latin");
            var detected = DetectLatinLanguage(allText);
            if (detected != "und") return Result(detected != target,
                detected == target ? "program-target-language" : "program-foreign-language",
                detected == target ? "正文常用词与目标语言一致。" : "正文常用词显示为其他语言。", detected, .9);
            return Result(false, "same-script-ambiguous", "同脚本语言证据不足，可使用手动翻译。", "latin", .5);
        }
        if (all.Other >= 12 && target is "zh" or "ja" or "ko")
            return Result(true, "foreign-script", "正文使用其他外语文字。", "other-script");
        return Result(false, "unidentified-script", "正文语言证据不足，继续观察页面加载。", "und", .5);
    }

    private static bool IsChineseProse(string text)
    {
        var count = Count(text);
        var grammar = text.Count(c => "的了是在和与为我你可请不该請為與這".Contains(c));
        return count.Kana == 0 && count.Hangul == 0 && count.Han * 2 >= count.Letters && (count.Han >= 6 && grammar >= 2
            && (count.Han >= 12 || count.Han * 4 >= count.Latin)
            || count.Han >= 12 && count.Han >= count.Latin * .4 && text.IndexOfAny(['。', '！', '？']) >= 0);
    }

    private static string DetectLatinLanguage(string text)
    {
        var words = Words().Matches(text.ToLowerInvariant()).Select(m => m.Value)
            .GroupBy(word => word).ToDictionary(group => group.Key, group => Math.Min(group.Count(), 4));
        var ranked = CommonWords.Select(pair => new
        {
            Language = pair.Key,
            Matches = pair.Value.Count(words.ContainsKey),
            Score = pair.Value.Sum(word => words.GetValueOrDefault(word)),
        }).OrderByDescending(value => value.Score).ToArray();
        return ranked[0].Matches >= 3 && ranked[0].Score >= 4
            && ranked[0].Score >= (ranked[1].Score + 2) * 1.25 ? ranked[0].Language : "und";
    }
    private static readonly Dictionary<string, HashSet<string>> CommonWords = new()
    {
        ["en"] = new("the this that with from your have are you can and for not which will their our into while each these what how".Split(' ')),
        ["fr"] = new("les des une est du avec pour dans sont que qui aux plus vous votre nous sur cette peut ne au".Split(' ')),
        ["de"] = new("der die das und ist mit für von den dem eine einen nicht sich werden auch sie wie zum zur".Split(' ')),
        ["es"] = new("los las una con para por del que está son como más este esta puede entre sobre sus sin".Split(' ')),
        ["it"] = new("gli una con per della delle che sono come questo questa più può nel nella non anche".Split(' ')),
        ["pt"] = new("uma com para por dos das que são como este esta pode você não mais sobre sua".Split(' ')),
        ["nl"] = new("het een van voor met zijn niet deze dit ook worden heeft maar die als naar uw".Split(' ')),
    };
    public static string NormalizeLanguage(string? value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        if (text.Contains("中文") || text.Contains("简体") || text.Contains("繁体") || text.Contains("chinese")) return "zh";
        if (text.Contains("英语") || text.Contains("英文") || text.Contains("english")) return "en";
        if (text.Contains("日文") || text.Contains("日语") || text.Contains("japanese")) return "ja";
        if (text.Contains("韩语") || text.Contains("韩文") || text.Contains("korean")) return "ko";
        if (text.Contains("法语") || text.Contains("french")) return "fr";
        if (text.Contains("德语") || text.Contains("german")) return "de";
        if (text.Contains("西班牙") || text.Contains("spanish")) return "es";
        if (text.Contains("葡萄牙") || text.Contains("portuguese")) return "pt";
        if (text.Contains("意大利") || text.Contains("italian")) return "it";
        if (text.Contains("荷兰") || text.Contains("dutch")) return "nl";
        if (text.Contains("丹麦") || text.Contains("danish")) return "da";
        if (text.Contains("挪威") || text.Contains("norwegian")) return "no";
        if (text.Contains("瑞典") || text.Contains("swedish")) return "sv";
        if (text.Contains("俄语") || text.Contains("russian")) return "ru";
        if (text.Contains("阿拉伯") || text.Contains("arabic")) return "ar";
        if (text.Contains("希腊") || text.Contains("greek")) return "el";
        if (text.Contains("越南") || text.Contains("vietnamese")) return "vi";
        text = text.Split('-', '_')[0];
        text = text switch { "eng" => "en", "zho" or "chi" => "zh", "jpn" => "ja", "kor" => "ko",
            "fra" or "fre" => "fr", "deu" or "ger" => "de", "spa" => "es", "ita" => "it", "por" => "pt",
            "nld" or "dut" => "nl", "dan" => "da", "nor" => "no", "swe" => "sv", "rus" => "ru", _ => text };
        return KnownTargets.Contains(text) ? text : "und";
    }

    private static readonly HashSet<string> LatinTargets = new(StringComparer.Ordinal)
        { "en", "fr", "de", "es", "it", "pt", "nl", "da", "no", "sv", "fi", "pl", "tr", "vi", "id", "ro", "cs" };
    private static readonly HashSet<string> KnownTargets = new(LatinTargets.Concat(
        new[] { "zh", "ja", "ko", "ru", "uk", "bg", "ar", "el", "hi", "th" }), StringComparer.Ordinal);

    private static bool LooksLikeCode(string text) =>
        text.Contains("=>", StringComparison.Ordinal) || text.Contains("function(", StringComparison.Ordinal) ||
        text.Contains("function (", StringComparison.Ordinal) || text.Contains("</", StringComparison.Ordinal) ||
        text.Contains("{\"", StringComparison.Ordinal) ||
        CodeKeywords().Matches(text).Count >= 2 && text.Count(character => character is '{' or '}' or ';') >= 3;

    private static ScriptCount Count(string text)
    {
        var result = new ScriptCount();
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            result.Letters++;
            var value = rune.Value;
            if (value is >= 0x3040 and <= 0x30ff or >= 0xff66 and <= 0xff9d) result.Kana++;
            else if (value is >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x3134f) result.Han++;
            else if (value is >= 0xac00 and <= 0xd7af or >= 0x1100 and <= 0x11ff) result.Hangul++;
            else if (value is >= 0x0041 and <= 0x024f or >= 0x1e00 and <= 0x1eff) result.Latin++;
            else
            {
                result.Other++;
                if (value is >= 0x0400 and <= 0x052f) result.Cyrillic++;
                if (value is >= 0x0600 and <= 0x06ff or >= 0x0750 and <= 0x077f or >= 0x08a0 and <= 0x08ff) result.Arabic++;
                if (value is >= 0x0370 and <= 0x03ff) result.Greek++;
                if (value is >= 0x0900 and <= 0x097f) result.Devanagari++;
                if (value is >= 0x0e00 and <= 0x0e7f) result.Thai++;
            }
        }
        return result;
    }

    private sealed class ScriptCount
    {
        public int Letters, Latin, Han, Kana, Hangul, Other, Cyrillic, Arabic, Greek, Devanagari, Thai;
    }

    [GeneratedRegex(@"[\p{L}]{2,}", RegexOptions.CultureInvariant)] private static partial Regex Words();
    [GeneratedRegex(@"\b(?:const|var|return|class|import|public|private|using|await|def)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CodeKeywords();
}
