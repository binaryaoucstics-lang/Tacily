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
        try { source = TrimEligibilityText(text).Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { source = TrimEligibilityText(text); }
        if (source.Length == 0) return false;

        // Readable Chinese can contain technical terms and literal identifiers. In a
        // foreign semantic group it can still be a name/caption to translate.
        if (!foreignHanContext && PageTranslationDecision.NormalizeLanguage(targetLanguage) == "zh")
        {
            if (IsReadableChinese(source)) return false;
        }

        if (Url().IsMatch(source) || Email().IsMatch(source) || Path().IsMatch(source)
            || Timestamp().IsMatch(source) || NamedDate().IsMatch(source)
            || NumericUnit().IsMatch(source) || StandaloneUnit().IsMatch(source) || Resolution().IsMatch(source)
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

    // Unicode White_Space plus the edge BOM, matching the extension's per-node
    // trim. Internal NEL remains whitespace; internal FEFF is not removed.
    private static string TrimEligibilityText(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && (char.IsWhiteSpace(text[start]) || text[start] == '\ufeff')) start++;
        while (end > start && (char.IsWhiteSpace(text[end - 1]) || text[end - 1] == '\ufeff')) end--;
        return text[start..end];
    }

    // Established computing/science acronyms plus the reviewed public examples.
    // Keep this small, explicit uppercase set mirrored in page-auto-policy.js;
    // membership alone never bypasses the surrounding Chinese evidence below.
    private static readonly HashSet<string> TechnicalAbbreviations = new(StringComparer.Ordinal)
    {
        "AI", "CPU", "WTT", "WSBK", "APEC",
        "API", "GPU", "USB", "SSD", "RAM", "DNS", "SQL", "HTML", "CSS",
        "JSON", "XML", "HTTP", "TCP", "UDP", "PDF", "DNA", "RNA", "MRI", "IPC"
    };

    // Common English imperative/UI verbs are lexical content, not name evidence.
    // TitleCase and Chinese neighbours (even 的) cannot disambiguate these as
    // proper names. Check whole words, including numbered actions: domains
    // and products with an independent suffix still have literal/name evidence.
    // Mirror this general command vocabulary in page-auto-policy.js.
    private static readonly HashSet<string> CommandWords = new(StringComparer.OrdinalIgnoreCase)
    {
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
    };

    // General document/learning headings, not a site or product vocabulary.
    // A number cannot distinguish "Chapter2" from a device ID. Prefer translating
    // ambiguous content words in any casing, including plural numbered headings.
    // Keep this vocabulary and the whole-token syntax mirrored in JS.
    private static readonly HashSet<string> HeadingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Activity", "Answer", "Appendix", "Appendices", "Article", "Assignment", "Book",
        "Chapter", "Conclusion", "Content", "Definition", "Diagram", "Episode", "Example",
        "Exercise", "Experiment", "Figure", "Hint", "Instruction", "Introduction", "Issue", "Lesson", "Module",
        "Note", "Objective", "Overview", "Page", "Paragraph", "Part", "Practice", "Problem", "Question", "Quiz",
        "Reference", "Result", "Review", "Scene", "Section", "Solution", "Stage", "Step", "Summary", "Table",
        "Task", "Topic", "Tutorial", "Unit", "Volume", "Warning", "Worksheet"
    };

    private static bool IsContentWord(string value)
    {
        var match = ContentWordToken().Match(value);
        if (!match.Success) return false;
        var word = match.Groups[1].Value;
        return CommandWords.Contains(word) || HeadingWords.Contains(word)
            || (word.EndsWith('s') || word.EndsWith('S')) && HeadingWords.Contains(word[..^1]);
    }

    [GeneratedRegex(@"^([A-Za-z]+)(?:[-_]?[0-9]{1,12}(?:\+{1,2}|#)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ContentWordToken();

    // Keep these per-node rules in sync with page-auto-policy.js. Count Unicode
    // letters after NFKC, not digits, punctuation or emoji. Only ASCII uppercase
    // runs of 1..4 letters (optionally with digits, e.g. R7) are candidates.
    // Multi-letter runs must be known acronyms or compact uppercase-number IDs
    // (CR450, PAC-3), so HELP and NO WAR remain eligible
    // regardless of Han share. Space-separated runs must both be known acronyms
    // (AI CPU); generic single-letter labels such as A B are not that evidence.
    // Lowercase, accented/other scripts and combining marks keep mixed text eligible.
    // A compact label needs >=3 Han and one abbreviation (APEC中国年). Spaced
    // labels/multiple abbreviations need >=6 Han and >=2 Han per Latin letter,
    // preserving short foreign commands such as "STOP 王曼昱!". Han-only is unchanged.
    private static bool IsReadableChinese(string source) => IsReadableChineseAbbreviations(source)
        || IsReadableChineseTerms(source);

    private static bool IsReadableChineseAbbreviations(string source)
    {
        var hanCount = 0;
        var latinCount = 0;
        var abbreviations = 0;
        var runLetters = 0;
        var hasCombiningMark = false;
        var hasWhitespace = false;
        foreach (var rune in source.EnumerateRunes())
        {
            if (Rune.IsLetter(rune))
            {
                if (IsHan(rune))
                {
                    hanCount++;
                    runLetters = 0;
                }
                else if (rune.Value is >= 'A' and <= 'Z')
                {
                    latinCount++;
                    if (runLetters == 0) abbreviations++;
                    if (++runLetters > 4) return false;
                }
                else return false;
            }
            else
            {
                if (Rune.IsWhiteSpace(rune)) hasWhitespace = true;
                if (rune.Value is not (>= '0' and <= '9')) runLetters = 0;
                if (Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark
                    or System.Globalization.UnicodeCategory.SpacingCombiningMark
                    or System.Globalization.UnicodeCategory.EnclosingMark) hasCombiningMark = true;
            }
        }
        if (latinCount == 0) return hanCount > 0;
        Match? previous = null;
        foreach (Match run in AbbreviationRuns().Matches(source))
        {
            if (IsContentWord(run.Value)) return false;
            var letters = string.Concat(run.Value.Where(character => character is >= 'A' and <= 'Z'));
            if (letters.Length > 1 && !TechnicalAbbreviations.Contains(letters)
                && !StructuredIdentifier().IsMatch(run.Value)) return false;
            if (previous is not null)
            {
                var gap = source[(previous.Index + previous.Length)..run.Index];
                if (gap.Length > 0 && gap.All(char.IsWhiteSpace)
                    && (!TechnicalAbbreviations.Contains(previous.Value) || !TechnicalAbbreviations.Contains(run.Value)))
                    return false;
            }
            previous = run;
        }
        return !hasCombiningMark && (abbreviations == 1 && hanCount >= 3 && !hasWhitespace
            || hanCount >= 6 && hanCount >= latinCount * 2);
    }

    [GeneratedRegex(@"[A-Z][A-Z0-9]*(?:[-_][0-9]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex AbbreviationRuns();

    // Require a short uppercase prefix followed by digits, with no words/spaces.
    // Automatic metadata only: explicit selection may still translate a label.
    [GeneratedRegex(@"^[A-Z]{1,4}[-_]?[0-9]{1,12}$", RegexOptions.CultureInvariant)]
    private static partial Regex StructuredIdentifier();

    // A second, conservative lexical path for terms the acronym rule cannot
    // describe. Names must bind to Chinese on both sides (or a possessive 的),
    // while internal capitals also identify product names at a Chinese edge.
    // A clause boundary is not a name binding: “，Alice介绍...” stays eligible.
    // Never infer a term from a lowercase word, Han share alone or a site name.
    private static bool IsReadableChineseTerms(string source)
    {
        var hanCount = 0;
        var latinCount = 0;
        var hasWhitespace = false;
        foreach (var rune in source.EnumerateRunes())
        {
            if (Rune.IsLetter(rune))
            {
                if (IsHan(rune)) hanCount++;
                else if (rune.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latinCount++;
                else return false;
            }
            if (Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark
                or System.Globalization.UnicodeCategory.SpacingCombiningMark
                or System.Globalization.UnicodeCategory.EnclosingMark) return false;
            if (Rune.IsWhiteSpace(rune)) hasWhitespace = true;
        }
        if (hanCount == 0 || latinCount == 0) return false;
        var runs = ReadableTermRuns().Matches(source);
        Match? previous = null;
        var novelTerm = false;
        var literalOnly = true;
        foreach (Match run in runs)
        {
            var value = run.Value;
            if (IsContentWord(value)) return false;
            var before = source[..run.Index].TrimEnd();
            var after = source[(run.Index + run.Length)..].TrimStart();
            var beforeHan = before.Length > 0 && IsHan(before.EnumerateRunes().Last());
            var afterHan = after.Length > 0 && IsHan(after.EnumerateRunes().First());
            var dottedLabel = UppercaseLabel().IsMatch(value) && after.StartsWith('.')
                && after.Length > 1 && IsHan(after[1..].EnumerateRunes().First());
            var literal = EmbeddedDomain().IsMatch(value) || LiteralIdentifier().IsMatch(value) || dottedLabel;
            if (literal) novelTerm = true;
            else if (TechnicalAbbreviations.Contains(value) || value.Length == 1 && value[0] is >= 'A' and <= 'Z')
                literalOnly = false;
            else
            {
                if (!TermName().IsMatch(value)
                    || !(beforeHan && afterHan || after.StartsWith('的')
                        || InternalCapital().IsMatch(value) && (beforeHan || afterHan))) return false;
                novelTerm = true;
                literalOnly = false;
            }
            // Adjacent foreign words remain a phrase, even when they look like
            // names or IDs. Preserve the existing AI CPU acronym exception.
            if (previous is not null)
            {
                var gap = source[(previous.Index + previous.Length)..run.Index];
                if (gap.Length > 0 && gap.All(char.IsWhiteSpace)
                    && (!TechnicalAbbreviations.Contains(previous.Value) || !TechnicalAbbreviations.Contains(value)))
                    return false;
            }
            previous = run;
        }
        return novelTerm && (literalOnly && runs.Count == 1 && hanCount >= 2
            || runs.Count == 1 && hanCount >= 3 && !hasWhitespace
            || hanCount >= 6 && hanCount >= latinCount * 2);
    }

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9]*(?:[._:/+?#=&%~@-][A-Za-z0-9]+)*(?:\+{1,2}|#)?", RegexOptions.CultureInvariant)]
    private static partial Regex ReadableTermRuns();
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*\.[A-Za-z]{2,63}(?:/[A-Za-z0-9._~%+\-]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedDomain();
    // Existing compact numeric prefixes, optional product suffix and literal
    // +/# variants. Long all-uppercase words and interleaved capitals are prose.
    [GeneratedRegex(@"^(?:[A-Z]{1,4}|[A-Z][a-z]+(?:[A-Z][a-z]+)*)[-_]?[0-9]{1,12}(?:[A-Z][a-z]+)?(?:\+{1,2}|#)?$", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralIdentifier();
    [GeneratedRegex(@"^[A-Z]{2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex UppercaseLabel();
    [GeneratedRegex(@"^(?:[A-Z][a-z][A-Za-z]*|[a-z]+[A-Z][A-Za-z]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TermName();
    [GeneratedRegex(@"[a-z][A-Z]", RegexOptions.CultureInvariant)]
    private static partial Regex InternalCapital();

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
        if (Copyright().IsMatch(source) || Identifier().IsMatch(source)
            || !IsContentWord(source) && (StructuredIdentifier().IsMatch(source) || LiteralIdentifier().IsMatch(source))
            || Registration().IsMatch(source)
            || ReadingTime().IsMatch(source)) return true;
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
    [GeneratedRegex(@"^[+\-−]?(?:\d[\d,.]*|\.\d+)\s*(?:%|°[cf]|℃|℉|usd|eur|gbp|jpy|cny|rmb|kg|mg|g|km|cm|mm|m|mph|km/h|kb|mb|gb|tb|ms|s|hz|khz|mhz|ghz|px|dpi|ppi|fps|bps|kbps|mbps|gbps|pt|kcal|cal|w|kw|v|a|ml|l)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumericUnit();
    // A/I/m and other single letters may be dialogue; never gate them alone.
    [GeneratedRegex(@"^(?:°[cf]|℃|℉|usd|eur|gbp|jpy|cny|rmb|kg|mg|km|cm|mm|mph|km/h|kb|mb|gb|tb|ms|hz|khz|mhz|ghz|px|dpi|ppi|fps|bps|kbps|mbps|gbps|kcal|kw|ml)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneUnit();
    [GeneratedRegex(@"^(?:[1-9][0-9]{1,4}\s*[x×]\s*[1-9][0-9]{1,4}(?:\s*px)?|[1-9][0-9]{2,3}p|[248]k)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Resolution();
    [GeneratedRegex(@"^[\p{L}\p{N}\s.,:;/\\%+\-−=×*÷()\[\]{}^<>≤≥≠∞√|]+$", RegexOptions.CultureInvariant)]
    private static partial Regex FormulaCharacters();
    [GeneratedRegex(@"[\p{L}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
    [GeneratedRegex(@"^(?:(?:(?:©|\(c\))\s*\d{0,4}|copyright\s*(?:©\s*)?\d{4})(?:\s*[-–]\s*\d{4})?\s*[^!?\r\n]{0,130}|all rights reserved\.?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Copyright();
    [GeneratedRegex(@"^(?:isbn(?:-1[03])?|issn|doi)\s*:?\s*(?:\d[\d\s./():\-]*[\dx]|10\.\d{4,9}/\S+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
    [GeneratedRegex(@"^(?:[\u3400-\u9fff]{0,3}ICP\s*(?:备|備|证|證)\s*[0-9]{6,20}(?:号|號)?(?:-[0-9]{1,3})?|[\u3400-\u9fff]{1,3}公网安备\s*[0-9]{10,20}(?:号|號)?|(?:company\s+)?registration\s*(?:no\.?|number)\s*[:：]?\s*[A-Z]{0,4}-?[0-9]{4,20})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Registration();
    [GeneratedRegex(@"^(?:(?:(?:estimated\s+)?reading|read)\s+time\s*:\s*[0-9]{1,3}\s*(?:min(?:ute)?s?|hours?)\.?|[0-9]{1,3}(?:\s+|-)(?:min(?:ute)?s?|hours?)(?:\s+|-)read\.?|(?:预计|預計)?(?:阅读|閱讀)(?:时间|時間)?\s*[:：]?\s*[0-9]{1,3}\s*(?:分钟|分鐘|小时|小時))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadingTime();
    [GeneratedRegex(@"^(?:photo|image|illustration|credit|source|photograph)s?(?:\s+(?:credit|source))?\s*(?::|\bby\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreditPrefix();
    [GeneratedRegex(@"^(?:[\p{Lu}\p{Lt}][\p{L}\p{M}’'\-]*(?:\.)?)(?:\s+[\p{Lu}\p{Lt}][\p{L}\p{M}’'\-]*(?:\.)?){1,5}\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex PersonName();
    [GeneratedRegex(@"^" + Agencies + @"\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Agency();
}
