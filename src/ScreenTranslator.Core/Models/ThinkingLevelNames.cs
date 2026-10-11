namespace ScreenTranslator.Core.Models;

/// <summary>One mapping for persisted levels, UI labels and API effort values.</summary>
public static class ThinkingLevelNames
{
    public static string Label(ThinkingLevel level) => level switch
    {
        ThinkingLevel.None => "关闭", ThinkingLevel.Minimal => "最低", ThinkingLevel.Low => "低",
        ThinkingLevel.Medium => "中", ThinkingLevel.High => "高", ThinkingLevel.XHigh => "超高",
        ThinkingLevel.Max => "最高", _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static ThinkingLevel FromLabel(string? label) => label switch
    {
        "最低" => ThinkingLevel.Minimal, "低" => ThinkingLevel.Low, "中" => ThinkingLevel.Medium,
        "高" => ThinkingLevel.High, "超高" => ThinkingLevel.XHigh, "最高" => ThinkingLevel.Max,
        _ => ThinkingLevel.None,
    };

    public static string Effort(ThinkingLevel level) => level switch
    {
        ThinkingLevel.None => "none", ThinkingLevel.Minimal => "minimal", ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium", ThinkingLevel.High => "high", ThinkingLevel.XHigh => "xhigh",
        ThinkingLevel.Max => "max", _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static bool TryParseEffort(string? effort, out ThinkingLevel level)
    {
        level = effort switch
        {
            "none" => ThinkingLevel.None, "minimal" => ThinkingLevel.Minimal, "low" => ThinkingLevel.Low,
            "medium" => ThinkingLevel.Medium, "high" => ThinkingLevel.High, "xhigh" => ThinkingLevel.XHigh,
            "max" => ThinkingLevel.Max, _ => (ThinkingLevel)(-1),
        };
        return Enum.IsDefined(level);
    }
}
