using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.Core.Translation;

/// <summary>Localizes complete calendar/age labels without inferring a year or time zone.</summary>
public static partial class PageCalendarText
{
    internal const string EnglishMonthPattern = "January|Jan|February|Feb|March|Mar|April|Apr|May|June|Jun|July|Jul|August|Aug|September|Sept|Sep|October|Oct|November|Nov|December|Dec";

    internal static int EnglishMonthNumber(string month) => month.ToLowerInvariant() switch
    {
        "january" or "jan" => 1, "february" or "feb" => 2, "march" or "mar" => 3,
        "april" or "apr" => 4, "may" => 5, "june" or "jun" => 6, "july" or "jul" => 7,
        "august" or "aug" => 8, "september" or "sept" or "sep" => 9,
        "october" or "oct" => 10, "november" or "nov" => 11, "december" or "dec" => 12, _ => 0
    };

    public static string? TranslateLabel(string? text, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 128 || PageTranslationDecision.NormalizeLanguage(targetLanguage) != "zh") return null;
        string source;
        try { source = text.Trim().Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { return null; }
        var traditional = targetLanguage.Contains('繁') || TraditionalTarget().IsMatch(targetLanguage);
        string? value = null;
        var date = CalendarLabel().Match(source);
        if (date.Success && int.TryParse(date.Groups["day"].Value, out var day))
        {
            var month = EnglishMonthNumber(date.Groups["month"].Value);
            var hasYear = date.Groups["year"].Success;
            var year = hasYear && int.TryParse(date.Groups["year"].Value, out var parsed) ? parsed : 2000;
            if (month == 0 || year is < 1 or > 9999 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
            value = (hasYear ? year.ToString(CultureInfo.InvariantCulture) + "年" : "") + month.ToString(CultureInfo.InvariantCulture) + "月" + day.ToString(CultureInfo.InvariantCulture) + "日";
        }
        else
        {
            var age = RelativeLabel().Match(source);
            if (age.Success)
            {
                var count = age.Groups["number"].Value.ToLowerInvariant() is "a" or "an" or "one" ? "1"
                    : int.Parse(age.Groups["number"].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                var unit = age.Groups["unit"].Value.ToLowerInvariant().TrimEnd('s') switch
                {
                    "second" => "秒", "minute" => traditional ? "分鐘" : "分钟", "hour" => traditional ? "小時" : "小时",
                    "day" => "天", "week" => traditional ? "週" : "周", "month" => traditional ? "個月" : "个月", "year" => "年", _ => ""
                };
                var qualifier = age.Groups["qualifier"].Value.ToLowerInvariant() switch
                {
                    "about" or "around" or "approximately" => traditional ? "約" : "约",
                    "less than" or "under" => "不到", "over" or "more than" => traditional ? "超過" : "超过", "almost" => traditional ? "將近" : "将近", _ => ""
                };
                value = qualifier + count + unit + (age.Groups["direction"].Value.Equals("ago", StringComparison.OrdinalIgnoreCase) ? "前" : "后");
                if (traditional) value = value.Replace('后', '後');
            }
            else value = source.ToLowerInvariant() switch
            {
                "just now" => traditional ? "剛剛" : "刚刚", "today" => "今天", "yesterday" => "昨天",
                "the day before yesterday" => "前天", "tomorrow" => "明天", _ => null
            };
        }
        if (value is null) return null;
        var leading = text.Length - text.TrimStart().Length;
        var trailing = text.Length - text.TrimEnd().Length;
        return text[..leading] + value + (trailing == 0 ? "" : text[^trailing..]);
    }

    [GeneratedRegex(@"^(?:(?<month>" + EnglishMonthPattern + @")\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?|(?<day>\d{1,2})(?:st|nd|rd|th)?\s+(?<month>" + EnglishMonthPattern + @")\.?)(?:(?:\s*,\s*|\s+)(?<year>\d{4}))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CalendarLabel();
    [GeneratedRegex(@"^(?:(?<qualifier>about|around|approximately|less than|under|over|more than|almost)\s+)?(?<number>\d{1,8}|a|an|one)\s+(?<unit>seconds?|minutes?|hours?|days?|weeks?|months?|years?)\s+(?<direction>ago|from now|later)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeLabel();
    [GeneratedRegex(@"\bzh[-_](?:hant|tw|hk)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TraditionalTarget();
}
