using System.Text;
using System.Text.RegularExpressions;
using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.Core.Translation;

/// <summary>
/// Per-node content gates after a page has already been admitted for translation.
/// Short body text and inline names are meaningful even when they could not
/// independently activate whole-page translation. Tests apply to a whole value;
/// URLs, numbers or a menu word inside a sentence never remove that sentence.
/// </summary>
public static partial class PageTextEligibility
{
    public static bool NeedsTranslation(string? text, string targetLanguage,
        bool foreignHanContext = false, string? kind = null, bool explicitSelection = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string source;
        try { source = text.Trim().Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { source = text.Trim(); }
        if (source.Length == 0) return false;

        // Han-only text is readable for the Chinese target. In a Japanese page
        // or semantic group, it can be a name/caption and must remain eligible.
        // Shared Latin scripts do not establish a matching language.
        if (!foreignHanContext && PageTranslationDecision.NormalizeLanguage(targetLanguage) == "zh")
        {
            var letters = source.EnumerateRunes().Where(Rune.IsLetter).ToArray();
            if (letters.Length > 0 && letters.All(IsHan)) return false;
        }

        if (Url().IsMatch(source) || Email().IsMatch(source) || Path().IsMatch(source)
            || Timestamp().IsMatch(source) || NamedDate().IsMatch(source)
            || NumericUnit().IsMatch(source) || StandaloneUnit().IsMatch(source)
            || IsFormula(source) || !source.EnumerateRunes().Any(Rune.IsLetter)) return false;

        if (!explicitSelection)
        {
            var normalizedKind = (kind ?? "body").Trim().ToLowerInvariant();
            if (normalizedKind is "form" or "editable" or "code") return false;
            if (IsMetadataOnly(source)) return false;
        }
        return true;
    }

    public static bool HasJapaneseScript(string? text) => text is not null && text.EnumerateRunes().Any(rune =>
        rune.Value is >= 0x3040 and <= 0x30ff or >= 0xff66 and <= 0xff9f);

    private static bool IsHan(Rune rune) => rune.Value is >= 0x3400 and <= 0x9fff
        or >= 0x20000 and <= 0x323af or >= 0xf900 and <= 0xfaff;

    private static bool IsFormula(string source)
    {
        if (!source.Any(character => character is '=' or '+' or '×' or '*' or '÷' or '^'
            or '≤' or '≥' or '≠' or '<' or '>')) return false;
        // Require mathematical syntax and recognized variable/function tokens.
        // Merely containing '=' must not classify "Debt = income" as metadata.
        if (!FormulaCharacters().IsMatch(source)) return false;
        return Words().Matches(source).All(match => match.Value.EnumerateRunes().Count() == 1
            || MathWords.Contains(match.Value));
    }

    private static bool IsMetadataOnly(string source)
    {
        if (source.Length > 180) return false;
        if (Copyright().IsMatch(source) || Identifier().IsMatch(source)) return true;
        var prefix = CreditPrefix().Match(source);
        var rest = prefix.Success ? source[prefix.Length..] : source;
        var parts = rest.Split(['/', '|'], StringSplitOptions.TrimEntries);
        return parts.Length == 2 && PersonName().IsMatch(parts[0]) && Agency().IsMatch(parts[1])
            || prefix.Success && parts.Length == 1 && (PersonName().IsMatch(rest) || Agency().IsMatch(rest));
    }

    private static readonly HashSet<string> MathWords = new(StringComparer.OrdinalIgnoreCase)
        { "sin", "cos", "tan", "log", "ln", "sqrt", "pi", "mc" };

    private const string Agencies = @"(?:reuters|associated press|ap|afp|epa|epa-efe|getty images(?:/afp)?|shutterstock|unsplash|pexels|路透社|美联社|美聯社|法新社|盖蒂(?:图片)?|蓋蒂(?:圖片)?)";
    private const string MonthName = @"(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)";

    [GeneratedRegex(@"^(?:(?:https?://|ftp://|www\.)[^\s<>]+|(?:[a-z0-9\-]+\.)+(?:com|net|org|edu|gov|io|co|jp|cn)(?:[/\?#][^\s<>]*)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();
    [GeneratedRegex(@"^(?:mailto:)?[a-z0-9.!#$%&'*+/=?^_`{|}~\-]+@[a-z0-9](?:[a-z0-9.\-]*[a-z0-9])?\.[a-z]{2,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Email();
    [GeneratedRegex(@"^(?:[a-z]:[\\/]|\\\\|\.{1,2}[\\/]|/)[^\s<>]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Path();
    [GeneratedRegex(@"^(?:(?:\d{4}[-/.]\d{1,2}[-/.]\d{1,2})(?:[ T]\d{1,2}:\d{2}(?::\d{2}(?:\.\d{1,3})?)?)?|\d{1,2}:\d{2}(?::\d{2}(?:\.\d{1,3})?)?)\s*(?:[ap]\.?m\.?|(?:utc|gmt)(?:[+\-]\d{1,2}(?::?\d{2})?)?|z)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();
    [GeneratedRegex(@"^(?:" + MonthName + @"\.?\s+\d{1,2}(?:st|nd|rd|th)?(?:,?\s+\d{4})|\d{1,2}(?:st|nd|rd|th)?\s+" + MonthName + @"\.?\s+\d{4})(?:\s+\d{1,2}:\d{2}(?::\d{2})?\s*(?:[ap]m|utc|gmt|z)?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedDate();
    [GeneratedRegex(@"^[+\-−]?(?:\d[\d,.]*|\.\d+)\s*(?:%|°[cf]|℃|℉|usd|eur|gbp|jpy|cny|rmb|kg|mg|g|km|cm|mm|m|mph|km/h|kb|mb|gb|tb|ms|s|hz|khz|mhz|ghz|px|dpi|pt|kcal|cal|w|kw|v|a|ml|l)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumericUnit();
    // A/I/m and other single letters may be dialogue; never gate them alone.
    [GeneratedRegex(@"^(?:°[cf]|℃|℉|usd|eur|gbp|jpy|cny|rmb|kg|mg|km|cm|mm|mph|km/h|kb|mb|gb|tb|ms|hz|khz|mhz|ghz|px|dpi|kcal|kw|ml)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneUnit();
    [GeneratedRegex(@"^[\p{L}\p{N}\s.,:;/\\%+\-−=×*÷()\[\]{}^<>≤≥≠∞√|]+$", RegexOptions.CultureInvariant)]
    private static partial Regex FormulaCharacters();
    [GeneratedRegex(@"[\p{L}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
    [GeneratedRegex(@"^(?:(?:(?:©|\(c\))\s*\d{0,4}|copyright\s*(?:©\s*)?\d{4})(?:\s*[-–]\s*\d{4})?\s*[^!?\r\n]{0,130}|all rights reserved\.?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Copyright();
    [GeneratedRegex(@"^(?:isbn(?:-1[03])?|issn|doi)\s*:?\s*(?:\d[\d\s./():\-]*[\dx]|10\.\d{4,9}/\S+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
    [GeneratedRegex(@"^(?:photo|image|illustration|credit|source|photograph)s?(?:\s+(?:credit|source))?\s*(?::|\bby\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreditPrefix();
    [GeneratedRegex(@"^(?:[\p{Lu}\p{Lt}][\p{L}\p{M}’'\-]*(?:\.)?)(?:\s+[\p{Lu}\p{Lt}][\p{L}\p{M}’'\-]*(?:\.)?){1,5}\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex PersonName();
    [GeneratedRegex(@"^" + Agencies + @"\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Agency();
}
