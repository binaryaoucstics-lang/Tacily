using ScreenTranslator.Core.Translation;
using System.IO;
using System.Net;
using ScreenTranslator.Core.Authentication;

namespace ScreenTranslator.App.Services.Browser;

public static class PageAutoFailure
{
    public static bool IsConfiguration(Exception error) => IsFastModeConfiguration(error)
        || error is TranslationAuthenticationException or TranslationConfigurationException;
    public static string Code(Exception error) => IsFastModeConfiguration(error) ? "api_configuration" : error switch
    {
        TranslationAuthenticationException => "api_key_error",
        TranslationConfigurationException => "api_configuration",
        TranslationRateLimitException => "rate_limit",
        TranslationUnavailableException => "translation_unavailable",
        TranslationFormatException => "translation_format",
        IOException => "desktop_unavailable",
        _ => "page_unavailable",
    };

    private static bool IsFastModeConfiguration(Exception error) => error is ChatGptException { Parameter: "service_tier" } chatGpt
        && (chatGpt.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden
            || chatGpt.Code == "subscription_sharing_unsupported_capability" || chatGpt.ErrorType == "invalid_request_error");
}
