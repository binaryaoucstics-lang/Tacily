namespace ScreenTranslator.App.Services.Ocr;

public sealed class OcrLanguageUnavailableException : InvalidOperationException
{
    public OcrLanguageUnavailableException(string? languageTag)
        : base(CreateMessage(languageTag))
    {
        LanguageTag = languageTag;
    }

    public string? LanguageTag { get; }

    private static string CreateMessage(string? languageTag) =>
        string.IsNullOrWhiteSpace(languageTag)
            ? "当前用户配置的语言中没有可用的 Windows OCR 语言包。"
            : $"Windows OCR 语言包“{languageTag}”不可用，请先在系统语言设置中安装。";
}
