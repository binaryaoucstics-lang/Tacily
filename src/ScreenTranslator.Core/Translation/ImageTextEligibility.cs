using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenTranslator.Core.Translation;

/// <summary>
/// Local content gates for image text. Metadata patterns are whole lines, never
/// keyword searches inside dialogue. A model's short matching anchors are not a
/// complete source paragraph and must not be used for same-language decisions.
/// </summary>
public static partial class ImageTextEligibility
{
    public static string ReadableContent(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        var text = source.Trim();
        if (text.Contains('\n') || text.Contains('\r')) return string.Join('\n', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(ReadableContent).Where(line => line.Length > 0));
        if (ExplicitMetadata().IsMatch(text.Normalize(NormalizationForm.FormKC))) return string.Empty;
        // Preserve a real caption when its photographer/agency credit shares the
        // same OCR line. The agency alone is not sufficient evidence of dialogue.
        text = PhotoCreditSuffix().Replace(text, string.Empty).Trim();
        var normalized = text.Normalize(NormalizationForm.FormKC);
        if (text.Length == 0 || AgencyLogo().IsMatch(normalized)
            || Address().IsMatch(normalized) || NumericUnit().IsMatch(normalized) || IsFormula(normalized)) return string.Empty;
        var letters = text.EnumerateRunes().Count(Rune.IsLetter);
        if (letters == 0) return string.Empty;
        // Historical OCR heading noise only. Ordinary short utterances/names
        // such as No!, え？ or Alice are deliberately retained.
        if (text.StartsWith("##", StringComparison.Ordinal)
            && text.EnumerateRunes().Count(Rune.IsLetterOrDigit) <= 3) return string.Empty;
        return text;
    }

    public static bool IsShortForeignUtterance(string source)
    {
        var value = string.Concat(source.Normalize(NormalizationForm.FormKC).Where(char.IsLetter)).ToLowerInvariant();
        return value is "hi" or "hello" or "help" or "wait" or "stop" or "run" or "yes" or "no" or "okay" or "ok"
            or "はい" or "え" or "ええ" or "うん" or "なに" or "何" or "やたっ";
    }

    public static bool HasJapaneseScript(string source) => source.EnumerateRunes().Any(rune =>
        rune.Value is >= 0x3040 and <= 0x30ff or >= 0xff66 and <= 0xff9f);

    /// <param name="preserveHanAmbiguity">When Japanese text is present nearby,
    /// shared Han-only names/captions are retained unless distinctly Chinese.</param>
    public static bool IsAlreadyTargetLanguage(string source, string targetLanguage, bool preserveHanAmbiguity = false)
    {
        var text = ReadableContent(source);
        if (text.Length == 0) return false;
        var target = (targetLanguage ?? string.Empty).Trim().ToLowerInvariant();
        var runes = text.EnumerateRunes().Where(Rune.IsLetter).ToArray();
        var han = runes.Count(r => r.Value is >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x323af);
        var kana = runes.Count(r => r.Value is >= 0x3040 and <= 0x30ff or >= 0xff66 and <= 0xff9f);
        var latin = runes.Count(r => r.Value is >= 0x0041 and <= 0x024f or >= 0x1e00 and <= 0x1eff or >= 0xff21 and <= 0xff5a);
        bool HanTarget() => han > 0 && han == runes.Length
            && (!preserveHanAmbiguity || DistinctChinese().IsMatch(text));
        if (target.StartsWith("zh") || target.Contains("中文") || target.Contains("简体")
            || target.Contains("繁体") || target.Contains("chinese")) return HanTarget();
        if (target.StartsWith("ja") || target.Contains("日语") || target.Contains("日文") || target.Contains("japanese"))
            return han + kana == runes.Length && han + kana > 0;
        if (target.StartsWith("ko") || target.Contains("韩语") || target.Contains("korean"))
            return runes.Length > 0 && runes.All(r => r.Value is >= 0xac00 and <= 0xd7af or >= 0x1100 and <= 0x11ff);
        // Shared Latin/Cyrillic/Arabic scripts do not prove a matching language.
        // Only English's unambiguous common utterances can be recognized locally.
        if (target is "en" || target.StartsWith("en-") || target.Contains("英语") || target.Contains("英文") || target.Contains("english"))
            return latin == runes.Length && IsShortForeignUtterance(text);
        return false;
    }

    private static bool IsFormula(string text)
    {
        if (!text.Any(c => c is '=' or '≈' or '≠' or '≤' or '≥' or '+' or '×' or '÷' or '^' or '√')) return false;
        var words = FormulaWords().Matches(text).Select(match => match.Value).ToArray();
        // Variables and mathematical function names only; a sentence containing
        // an equation still has words to translate.
        return words.All(word => word.EnumerateRunes().Count() == 1 || word.ToLowerInvariant() is "sin" or "cos" or "tan" or "log" or "ln" or "sqrt" or "pi" or "mc");
    }

    private const string Agencies = @"(?:Reuters|AP|AFP|Associated\s+Press|Getty(?:\s+Images)?|Shutterstock|Alamy|iStock|Dreamstime|路透社|法新社|美联社|蓋蒂圖片|盖蒂图片)";
    // Capitalized personal names only; do not strip a whole caption before its
    // final credit. A slash plus an explicit photo agency supplies the context.
    [GeneratedRegex(@"(?:\b(?:[\p{Lu}][\p{L}\p{M}'’\-]+[ \t]+){0,3}[\p{Lu}][\p{L}\p{M}'’\-]+|[\p{IsCJKUnifiedIdeographs}]{2,12})\s*[/|]\s*(?i:" + Agencies + @")\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PhotoCreditSuffix();
    [GeneratedRegex(@"^(?:(?:photo(?:graph)?s?|image|illustration|artwork)\s*(?:by\s+|credit\s*:\s*|:\s*)|credits?\s*:\s*|courtesy\s+of\s+|©\s*|copyright\s+(?:©\s*)?\d*\s*|CC\s+BY(?:-(?:NC|SA|ND))*\s*|(?:图片来源|圖片來源|摄影|攝影|拍摄|拍攝|版权|版權)\s*[：:]\s*).*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitMetadata();
    [GeneratedRegex(@"^" + Agencies + @"$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgencyLogo();
    [GeneratedRegex(@"^(?:(?:https?://|www\.)\S+|[\w.+\-]+@[\w.\-]+\.[A-Za-z]{2,}|@[A-Za-z0-9_]{2,}|(?:[A-Za-z0-9\-]+\.)+(?:com|net|org|io|co|jp|cn)(?:/\S*)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Address();
    [GeneratedRegex(@"^[\$€£¥]?\s*[+\-]?[\d.,]+\s*(?:%|‰|USD|EUR|JPY|GBP|RMB|CNY|AUD|CAD|kg|g|mg|km|cm|mm|m|km/h|mph|fps|px|dpi|Hz|kHz|MHz|GHz|KB|MB|GB|TB|°C|°F)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumericUnit();
    [GeneratedRegex(@"[\p{L}]+", RegexOptions.CultureInvariant)]
    private static partial Regex FormulaWords();
    [GeneratedRegex(@"[这吗们谢认让觉错您]|(?:我們|你們|他們|現在|為什麼|沒有|已經|怎麼|謝謝|這個|這裡)", RegexOptions.CultureInvariant)]
    private static partial Regex DistinctChinese();
}
