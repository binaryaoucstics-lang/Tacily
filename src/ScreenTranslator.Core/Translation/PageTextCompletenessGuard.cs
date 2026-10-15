using System.Text.RegularExpressions;
using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.Core.Translation;

public enum PageTextCompleteness { Clear, RetainedTerm, UntranslatedProse }

/// <summary>
/// Source-overlap evidence, not a language detector or a translation quality score.
/// A lone lowercase word may be ordinary prose OR a technical name. Request a
/// bounded repair for it, but never treat repetition as proof that it is wrong.
/// Only a retained phrase with multiple English grammar words is strong evidence.
/// Clear means no evidence from this guard, not proof of a correct translation.
/// </summary>
public static class PageTextCompletenessGuard
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);
    private static readonly Regex Words = new(@"(?<![A-Za-z0-9_])[a-z]+(?![A-Za-z0-9_])",
        RegexOptions.CultureInvariant, Budget);
    // Protect complete literals, including lowercase names within code/URLs/paths.
    // Quoting in the ANSWER alone is not evidence: only source literals exempt words.
    private static readonly Regex Literals = new(
        "`[^`\r\n]+`|\"[^\"\r\n]+\"|(?<![\\p{L}\\p{N}])'[^'\r\n]+'(?![\\p{L}\\p{N}])|“[^”\r\n]+”|‘[^’\r\n]+’|「[^」\r\n]+」"
        + @"|(?:https?://|www\.)[^\s<>""'`\u3400-\u9fff，。；！？]+|[\w.+-]+@[\w.-]+\.[A-Za-z]+"
        + @"|(?:[A-Za-z]:[\\/]|\.{0,2}/|\\\\)[^\s<>""'`\u3400-\u9fff，。；！？]+"
        + @"|(?<![\p{L}\p{N}_])\w+(?:[._/+:#-]+\w+)+(?![\p{L}\p{N}_])"
        + @"|(?<![\p{L}\p{N}_])[A-Za-z_]\w*(?=\()",
        RegexOptions.CultureInvariant, Budget);
    private static readonly HashSet<string> Grammar = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "for", "from", "with",
        "is", "are", "was", "were", "has", "have", "had", "this", "these",
        "those", "their", "our", "your", "which", "that", "will", "not"
    };

    public static PageTextCompleteness Assess(string source, string translation, string targetLanguage)
    {
        if (PageTranslationDecision.NormalizeLanguage(targetLanguage) != "zh"
            || source.Length == 0 || translation.Length == 0) return PageTextCompleteness.Clear;
        // Do not truncate into false lexical evidence or scan unbounded provider data.
        // Outside these bounds the classifier abstains; existing protocol/fact guards
        // retain their own limits. Timeout also means unknown, never a prose verdict.
        if (source.Length > 32_768 || translation.Length > 131_072) return PageTextCompleteness.RetainedTerm;
        // This guard concerns residue inside a Chinese prose answer. A handful
        // of Han label characters does not establish that an otherwise foreign
        // answer is Chinese prose. Whole-answer language/quality judgment needs
        // separate evidence; do not turn this check into a blanket Latin gate.
        if (translation.Count(character => character is >= '\u3400' and <= '\u9fff') < 3)
            return PageTextCompleteness.Clear;
        try
        {
            var sourceLiterals = Literals.Matches(source).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
            var plainSource = Literals.Replace(source, match => new string(' ', match.Length));
            var plainTranslation = Literals.Replace(translation, match => sourceLiterals.Contains(match.Value)
                ? new string(' ', match.Length) : match.Value);
            var sourceWords = WholeWords(plainSource).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
            var retained = WholeWords(plainTranslation).Where(match => sourceWords.Contains(match.Value)).ToArray();
            if (retained.Length == 0) return PageTextCompleteness.Clear;
            // An exact unquoted source phrase, with >=3 words and >=2 grammatical
            // anchors, avoids judging names, acronyms, "in vitro", or "redis cache"
            // by a site/vocabulary whitelist. Unknown single/plural nouns stay soft.
            for (var start = 0; start < retained.Length; start++)
            {
                var end = start;
                var anchors = Grammar.Contains(retained[start].Value) ? 1 : 0;
                while (end + 1 < retained.Length)
                {
                    var gap = translation.AsSpan(retained[end].Index + retained[end].Length,
                        retained[end + 1].Index - retained[end].Index - retained[end].Length);
                    if (gap.Length == 0 || !IsWhitespace(gap)) break;
                    end++;
                    if (Grammar.Contains(retained[end].Value)) anchors++;
                }
                if (end - start >= 2 && anchors >= 2)
                {
                    var phrase = translation[retained[start].Index..(retained[end].Index + retained[end].Length)];
                    if (ContainsPhrase(plainSource, phrase)) return PageTextCompleteness.UntranslatedProse;
                }
                start = end;
            }
            return PageTextCompleteness.RetainedTerm;
        }
        catch (RegexMatchTimeoutException) { return PageTextCompleteness.RetainedTerm; }
    }

    private static bool IsWhitespace(ReadOnlySpan<char> text)
    {
        foreach (var character in text) if (!char.IsWhiteSpace(character)) return false;
        return true;
    }

    private static bool ContainsPhrase(string source, string phrase)
    {
        for (var cursor = 0; cursor <= source.Length - phrase.Length;)
        {
            var at = source.IndexOf(phrase, cursor, StringComparison.Ordinal);
            if (at < 0) return false;
            var end = at + phrase.Length;
            if ((at == 0 || !ContinuesWord(source[at - 1]) && source[at - 1] != '_')
                && (end == source.Length || !ContinuesWord(source[end]) && source[end] != '_')) return true;
            cursor = at + 1;
        }
        return false;
    }

    // Chinese does not require spaces around a retained Latin word. Other
    // alphabetic neighbours/combining marks still belong to that word (café).
    private static IEnumerable<Match> WholeWords(string text) => Words.Matches(text).Where(match =>
        (match.Index == 0 || !ContinuesWord(text[match.Index - 1]))
        && (match.Index + match.Length == text.Length || !ContinuesWord(text[match.Index + match.Length])));

    private static bool ContinuesWord(char value) => value is not (>= '\u3400' and <= '\u9fff')
        && (char.IsLetterOrDigit(value) || char.GetUnicodeCategory(value) is
            System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark);
}
