using System.Net;

namespace ScreenTranslator.Core.Translation;

public class TranslationException : Exception
{
    public TranslationException(string message)
        : base(message)
    {
    }

    public TranslationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

public sealed class TranslationFormatException : TranslationException
{
    public TranslationFormatException(string message)
        : base(message)
    {
    }

    public TranslationFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class TranslationAuthenticationException : TranslationException
{
    public TranslationAuthenticationException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

public sealed class TranslationRateLimitException : TranslationException
{
    public TranslationRateLimitException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

public sealed class TranslationConfigurationException : TranslationException
{
    public TranslationConfigurationException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

public sealed class TranslationUnavailableException : TranslationException
{
    public TranslationUnavailableException(
        string message,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>A bounded streaming wait ended before answer content, distinct from transport/total timeouts.</summary>
public sealed class TranslationFirstContentTimeoutException : TranslationException
{
    public TranslationFirstContentTimeoutException(string message, bool hasReceivedContent = false)
        : base(message) => HasReceivedContent = hasReceivedContent;

    // Even a JSON prefix prevents an automatic second response being mixed with it.
    public bool HasReceivedContent { get; }
}
