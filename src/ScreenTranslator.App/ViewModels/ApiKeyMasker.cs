namespace ScreenTranslator.App.ViewModels;

public static class ApiKeyMasker
{
    private const int VisibleSuffixLength = 4;
    private const int MaskLength = 12;

    public static string Mask(string? apiKey)
    {
        var trimmed = apiKey?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.Length <= VisibleSuffixLength)
        {
            return new string('*', trimmed.Length);
        }

        return new string('*', MaskLength) + trimmed[^VisibleSuffixLength..];
    }
}
