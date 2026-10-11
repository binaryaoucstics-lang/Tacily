using ScreenTranslator.Core.Translation;
using System.IO;

namespace ScreenTranslator.App.Services.Browser;

public static class PageAutoFailure
{
    public static bool IsConfiguration(Exception error) => error is TranslationAuthenticationException or TranslationConfigurationException;
    public static string Code(Exception error) => error switch
    {
        TranslationAuthenticationException => "api_key_error",
        TranslationConfigurationException => "api_configuration",
        TranslationRateLimitException => "rate_limit",
        TranslationUnavailableException => "translation_unavailable",
        TranslationFormatException => "translation_format",
        IOException => "desktop_unavailable",
        _ => "page_unavailable",
    };
}
