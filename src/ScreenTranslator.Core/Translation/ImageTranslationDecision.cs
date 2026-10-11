using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Translation;

/// <summary>
/// Script evidence, not a language model: Han-only text and different languages sharing
/// the target script are deliberately ambiguous. This never makes a network request.
/// </summary>
public static partial class ImageTranslationDecision
{
    public const string PolicyVersion = "local-script-3";

    public static ImageTranslationAssessment Assess(IReadOnlyList<OcrBlock> blocks, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var reliable = blocks.Where(block => double.IsFinite(block.Confidence) && block.Confidence >= .72 &&
            !string.IsNullOrWhiteSpace(block.Text) && block.Text.Length <= 2048)
            .Select(block => block with { Text = ImageTextEligibility.ReadableContent(block.Text) })
            .Where(block => block.Text.Length > 0).ToArray();
        var evidence = reliable.Select(block => new Evidence(block, Count(block.Text)))
            .Where(item => item.Script.Letters >= 2 || item.Block.Confidence >= .9 && ImageTextEligibility.IsShortForeignUtterance(item.Block.Text)).ToArray();
        var all = Count(string.Join(" ", evidence.Select(item => item.Block.Text)));
        var language = all.Kana >= 1 ? "ja" : all.Hangul >= 2 ? "ko" :
            all.Arabic >= 2 ? "ar-script" : all.Cyrillic >= 2 ? "cyrillic" : all.Greek >= 2 ? "el-script" :
            all.Han >= 2 && all.Latin < all.Han ? "zh-or-ja" : all.Latin >= 2 ? "latin" : "und";
        var targetScript = TargetScript(targetLanguage);
        var regions = evidence.Select(item => new TextRegion(item.Block.BoundsInCapturePixels, item.Block.Confidence)).ToArray();
        var confidence = evidence.Length == 0 ? 0 : evidence.Sum(item => item.Block.Confidence * item.Script.Letters) /
            evidence.Sum(item => item.Script.Letters);

        ImageTranslationAssessment Result(bool needs, string code, string reason) =>
            new(needs, reason, Math.Clamp(confidence, 0, 1), language, regions, 0)
            { ReasonCode = code, RecognizedText = reliable };

        if (evidence.Length == 0)
            return Result(false, blocks.Count == 0 ? "no-text" : "unreadable-or-noise", "未识别到足够可靠的文字；数字、标点和弱识读不自动翻译。");
        if (targetScript == Script.Unknown)
            return Result(false, "unsupported-target", "当前目标语言无法仅凭本地文字脚本可靠判断。");

        // Whole readable lines protect against artwork strokes, single-letter labels,
        // small watermarks and units such as 100 USD triggering a paid translation.
        bool Strong(Func<ScriptCount, int> count, int minimum, double threshold) =>
            evidence.Where(item => item.Block.Confidence >= threshold).Sum(item => count(item.Script)) >= minimum;
        var shortForeign = evidence.Any(item => item.Block.Confidence >= .9
            && ImageTextEligibility.IsShortForeignUtterance(item.Block.Text)
            && (item.Script.Kana > 0 && targetScript != Script.Japanese || item.Script.Latin > 0 && targetScript != Script.Latin));
        var foreignKana = targetScript != Script.Japanese && all.Kana >= 2 && Strong(s => s.Kana + s.Han, 4, .72);
        var foreignHangul = targetScript != Script.Korean && Strong(s => s.Hangul, 4, .78);
        var foreignArabic = targetScript != Script.Arabic && Strong(s => s.Arabic, 5, .8);
        var foreignCyrillic = targetScript != Script.Cyrillic && Strong(s => s.Cyrillic, 5, .8);
        var foreignGreek = targetScript != Script.Greek && Strong(s => s.Greek, 5, .8);
        var latinLines = evidence.Where(item => item.Block.Confidence >= .84 && item.Script.Latin >= 3).ToArray();
        var latinWords = latinLines.SelectMany(item => LatinWord().Matches(item.Block.Text).Select(match => match.Value))
            .Where(word => word.Length >= 2).ToArray();
        var foreignLatin = targetScript != Script.Latin &&
            (latinWords.Sum(word => word.Length) >= 7 && latinWords.Length >= 2 ||
             latinLines.Any(item => item.Block.Confidence >= .9 && LatinWord().Matches(item.Block.Text)
                 .Any(match => match.Length >= 8)));
        var foreignHan = targetScript is not (Script.Chinese or Script.Japanese) && Strong(s => s.Han, 4, .84);

        if (shortForeign || foreignKana || foreignHangul || foreignArabic || foreignCyrillic || foreignGreek || foreignLatin || foreignHan)
            return Result(true, "foreign-readable-text", "本地识别到与目标语言脚本不同的可读文字。");

        if (targetScript == Script.Chinese && all.Han >= 2 && all.Kana == 0)
            return Result(false, "target-or-han-ambiguous", "以汉字为主；汉字日文与中文不能仅凭脚本可靠区分，跳过自动翻译。");
        if (targetScript == Script.Latin && all.Latin >= 2)
            return Result(false, "same-script-ambiguous", "拉丁文字可能已经是目标语言；本地脚本规则无法可靠区分英语等语言。");
        var matchesTarget = targetScript switch
        {
            Script.Japanese => all.Kana >= 1 || all.Han >= 2,
            Script.Korean => all.Hangul >= 2,
            Script.Arabic => all.Arabic >= 2,
            Script.Cyrillic => all.Cyrillic >= 2,
            Script.Greek => all.Greek >= 2,
            _ => false
        };
        return matchesTarget ? Result(false, "target-script", "识别文字与目标语言脚本一致。") :
            Result(false, "insufficient-evidence", "可读文字过短或置信度不足，跳过自动翻译。");
    }

    private static Script TargetScript(string? target)
    {
        var value = (target ?? "").Trim().ToLowerInvariant();
        if (value.Contains("中文") || value.Contains("简体") || value.Contains("繁体") || value.StartsWith("zh") || value.Contains("chinese")) return Script.Chinese;
        if (value.Contains("日语") || value.Contains("日文") || value.StartsWith("ja") || value.Contains("japanese")) return Script.Japanese;
        if (value.Contains("韩语") || value.StartsWith("ko") || value.Contains("korean")) return Script.Korean;
        if (value.Contains("阿拉伯") || value.StartsWith("ar") || value.Contains("arabic")) return Script.Arabic;
        if (value.Contains("俄语") || value.StartsWith("ru") || value.StartsWith("uk") || value.StartsWith("bg") || value.Contains("russian")) return Script.Cyrillic;
        if (value.Contains("希腊") || value.StartsWith("el") || value.Contains("greek")) return Script.Greek;
        if (value.Contains("英语") || value.Contains("英文") || value.Contains("english") ||
            new[] { "en", "fr", "de", "es", "it", "pt", "nl", "vi", "tr", "pl", "sv", "da", "no", "fi", "id" }
                .Any(tag => value == tag || value.StartsWith(tag + "-"))) return Script.Latin;
        return Script.Unknown;
    }

    private static ScriptCount Count(string text)
    {
        var result = new ScriptCount();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter)) continue;
            var value = rune.Value;
            if (value is >= 0x3040 and <= 0x30ff or >= 0xff66 and <= 0xff9f) result.Kana++;
            else if (value is >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x323af) result.Han++;
            else if (value is >= 0xac00 and <= 0xd7af or >= 0x1100 and <= 0x11ff) result.Hangul++;
            else if (value is >= 0x0041 and <= 0x024f or >= 0x1e00 and <= 0x1eff or >= 0xff21 and <= 0xff5a) result.Latin++;
            else if (value is >= 0x0400 and <= 0x052f) result.Cyrillic++;
            else if (value is >= 0x0370 and <= 0x03ff) result.Greek++;
            else if (value is >= 0x0600 and <= 0x06ff or >= 0x0750 and <= 0x077f or >= 0xfb50 and <= 0xfdff or >= 0xfe70 and <= 0xfeff) result.Arabic++;
        }
        return result;
    }

    [GeneratedRegex(@"[A-Za-z\u00c0-\u024f\u1e00-\u1eff\uff21-\uff5a]+", RegexOptions.CultureInvariant)]
    private static partial Regex LatinWord();
    private enum Script { Unknown, Chinese, Japanese, Korean, Latin, Arabic, Cyrillic, Greek }
    private sealed record Evidence(OcrBlock Block, ScriptCount Script);
    private sealed class ScriptCount
    {
        public int Kana, Han, Hangul, Latin, Arabic, Cyrillic, Greek;
        public int Letters => Kana + Han + Hangul + Latin + Arabic + Cyrillic + Greek;
    }
}
