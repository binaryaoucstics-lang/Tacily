namespace ScreenTranslator.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Windows = 4,
    Shift = 8,
}

public sealed record HotkeyGesture
{
    private static readonly HashSet<string> AllowedKeys = BuildAllowedKeys();

    private HotkeyGesture(HotkeyModifiers modifiers, string keyName)
    {
        Modifiers = modifiers;
        KeyName = keyName;
    }

    public static HotkeyGesture Default { get; } =
        new(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "T");

    public HotkeyModifiers Modifiers { get; }

    public string KeyName { get; }

    public static HotkeyGesture Create(HotkeyModifiers modifiers, string keyName)
    {
        var normalizedModifiers = modifiers &
            (HotkeyModifiers.Control |
             HotkeyModifiers.Alt |
             HotkeyModifiers.Windows |
             HotkeyModifiers.Shift);

        if (normalizedModifiers == HotkeyModifiers.None ||
            normalizedModifiers != modifiers)
        {
            throw InvalidGesture();
        }

        var normalizedKey = NormalizeKey(keyName);
        if (!AllowedKeys.Contains(normalizedKey) ||
            IsUnsafeSystemGesture(normalizedModifiers, normalizedKey))
        {
            throw InvalidGesture();
        }

        return new HotkeyGesture(normalizedModifiers, normalizedKey);
    }

    public static HotkeyGesture Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidGesture();
        }

        var parts = value.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw InvalidGesture();
        }

        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = ParseModifier(part);
            if ((modifiers & modifier) != 0)
            {
                throw InvalidGesture();
            }

            modifiers |= modifier;
        }

        return Create(modifiers, parts[^1]);
    }

    public static bool TryParse(string? value, out HotkeyGesture gesture)
    {
        try
        {
            gesture = Parse(value ?? string.Empty);
            return true;
        }
        catch (FormatException)
        {
            gesture = Default;
            return false;
        }
    }

    public string ToPersistedString() => string.Join("+", GetParts());

    public string ToDisplayString() => string.Join(" + ", GetParts());

    private IEnumerable<string> GetParts()
    {
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            yield return "Ctrl";
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            yield return "Alt";
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            yield return "Win";
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            yield return "Shift";
        }

        yield return KeyName;
    }

    private static HotkeyModifiers ParseModifier(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => HotkeyModifiers.Control,
            "ALT" => HotkeyModifiers.Alt,
            "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
            "SHIFT" => HotkeyModifiers.Shift,
            _ => throw InvalidGesture(),
        };

    private static string NormalizeKey(string value)
    {
        var key = value.Trim().ToUpperInvariant();
        return key switch
        {
            "LEFT" => "Left",
            "RIGHT" => "Right",
            "UP" => "Up",
            "DOWN" => "Down",
            "SPACE" => "Space",
            "HOME" => "Home",
            "END" => "End",
            "PAGEUP" => "PageUp",
            "PAGEDOWN" => "PageDown",
            _ => key,
        };
    }

    private static bool IsUnsafeSystemGesture(
        HotkeyModifiers modifiers,
        string keyName) =>
        (modifiers.HasFlag(HotkeyModifiers.Control) &&
         modifiers.HasFlag(HotkeyModifiers.Alt) &&
         keyName.Equals("Delete", StringComparison.OrdinalIgnoreCase)) ||
        (modifiers == HotkeyModifiers.Alt && keyName == "F4") ||
        (modifiers == HotkeyModifiers.Windows &&
         keyName is "L" or "D");

    private static HashSet<string> BuildAllowedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            keys.Add(letter.ToString());
        }

        for (var digit = '0'; digit <= '9'; digit++)
        {
            keys.Add(digit.ToString());
        }

        for (var index = 1; index <= 12; index++)
        {
            keys.Add($"F{index}");
        }

        keys.UnionWith(
            ["Left", "Right", "Up", "Down", "Space", "Home", "End", "PageUp", "PageDown"]);
        return keys;
    }

    private static FormatException InvalidGesture() =>
        new("快捷键必须包含至少一个修饰键和一个可用按键。");
}
