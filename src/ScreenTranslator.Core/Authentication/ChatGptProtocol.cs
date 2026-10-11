using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenTranslator.Core.Translation;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Authentication;

public interface IChatGptTokenProvider
{
    Task<string> GetAccessTokenAsync(HttpClient http, string accountId, CancellationToken token);
    CancellationToken GetSessionCancellation(string accountId);
    void ReportInferenceFailure(string accountId, ChatGptException error);
    IReadOnlyList<ThinkingLevel>? GetSupportedThinkingLevels(string accountId, string model) => null;
    void UpdateModelCapabilities(string accountId, IReadOnlyList<TranslationModelChoice> models) { }
}

public sealed class ChatGptException : TranslationException
{
    public string? Code { get; }
    public HttpStatusCode? StatusCode { get; }
    public string? RequestId { get; }
    public string? Parameter { get; }
    public string? ErrorType { get; }
    public string? ServerMessage { get; }
    public string? Detail { get; }
    public string ResponseShape { get; }
    public bool IsUsageLimit => Code == "subscription_sharing_usage_limit_exceeded";
    public bool IsTerminalRefreshError => Code is "invalid_grant" or "invalid_refresh_token" or "token_expired"
        or "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused";

    public ChatGptException(string message, string? code = null, HttpStatusCode? status = null,
        string? requestId = null, string? parameter = null, string responseShape = "unknown",
        string? errorType = null, string? serverMessage = null, string? detail = null) : base(SafeText(message, 1200) ?? "ChatGPT 请求未完成。")
    {
        Code = SafeMetadata(code, 128); StatusCode = status; RequestId = SafeMetadata(requestId, 128);
        Parameter = SafeMetadata(parameter, 128); ResponseShape = SafeMetadata(responseShape, 32) ?? "unknown";
        ErrorType = SafeMetadata(errorType, 128); ServerMessage = SafeText(serverMessage); Detail = SafeText(detail);
    }

    public static ChatGptException FromError(JsonElement root, HttpStatusCode? status = null, string? requestId = null)
    {
        var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var nested) ? nested
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out var nestedDetail)
                && nestedDetail.ValueKind == JsonValueKind.Object ? nestedDetail : root;
        var code = error.ValueKind == JsonValueKind.String ? error.GetString() : ChatGptProtocol.Text(error, "code");
        var parameter = ChatGptProtocol.Text(error, "param");
        var errorType = ChatGptProtocol.Text(error, "type");
        var serverMessage = SafeText(ChatGptProtocol.Text(error, "message")
            ?? ChatGptProtocol.Text(root, "error_description"));
        var detail = SafeText(ReadDetail(root) ?? ReadDetail(error));
        var shape = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out _) ? "detail"
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _) ? "error" : "unknown";
        var message = code switch
        {
            "subscription_sharing_usage_limit_exceeded" => "ChatGPT 额度已达到当前限制，已暂停此账户的新翻译请求。请点击“管理额度”查看，并在调整额度后点击“恢复翻译”。",
            "subscription_sharing_user_not_eligible" => "当前 ChatGPT 账户或工作区暂不支持使用订阅额度，请在设置中选择其他账户。",
            "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable" => "暂时无法查询 ChatGPT 额度或账户状态，请稍后重试。",
            "subscription_sharing_unsupported_capability" => "当前模型或输入不支持 ChatGPT 额度翻译，请重新读取模型列表并选择支持该输入的模型。",
            "subscription_sharing_route_not_supported" => "ChatGPT 额度翻译仅支持官方 Responses 接口，请检查接口配置。",
            "chatpass_v2_scope_not_authorized" or "chatpass_v2_invalid_authorization_context" => "ChatGPT 未授权此操作，请在账户设置中重新授权使用额度。",
            "subscription_sharing_invalid_user" => "ChatGPT 身份验证未通过，请检查所选账户，必要时重新登录。",
            "invalid_grant" or "invalid_refresh_token" or "token_expired" or "refresh_token_expired"
                or "refresh_token_invalidated" or "refresh_token_reused" => "ChatGPT 登录已失效，请使用原账户重新登录。",
            "invalid_client" => "ChatGPT 登录注册信息未被接受，请添加新的账户连接。",
            "access_denied" => "已取消 ChatGPT 授权，可以稍后重新登录。",
            _ => serverMessage ?? detail ?? status switch
            {
                HttpStatusCode.Unauthorized => "ChatGPT 授权未被接受，请检查账户和额度授权。",
                HttpStatusCode.Forbidden => "当前账户、工作区或所在区域不允许此 ChatGPT 请求。",
                HttpStatusCode.TooManyRequests => "ChatGPT 请求过于频繁，请稍后重试或点击“管理额度”查看限制。",
                >= HttpStatusCode.InternalServerError => "ChatGPT 服务暂不可用，请稍后重试。",
                _ => "ChatGPT 请求未完成，请检查账户权限、模型与输入后重试。",
            },
        };
        // Known recovery guidance remains actionable; unfamiliar failures expose their actual reason.
        if (serverMessage is not null && message != serverMessage) message += " 原因：" + serverMessage;
        if (detail is not null && detail != serverMessage && message != detail) message += " " + detail;
        if (parameter == "service_tier" || code?.Contains("service_tier", StringComparison.OrdinalIgnoreCase) == true
            || serverMessage?.Contains("service_tier", StringComparison.OrdinalIgnoreCase) == true
            || detail?.Contains("service_tier", StringComparison.OrdinalIgnoreCase) == true)
            message = "ChatGPT 快速模式请求未被接受，请关闭快速模式或更换可用模型。 " + message;
        message = SafeText(message, 1000) + StatusAndParameter(status, parameter);
        return new(message, code, status, requestId, parameter, shape, errorType, serverMessage, detail);
    }

    private static string? ReadDetail(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("detail", out var detail)) return null;
        return DetailText(detail);
    }

    private static string? DetailText(JsonElement detail) => detail.ValueKind switch
    {
        JsonValueKind.String => detail.GetString(),
        JsonValueKind.Object => ChatGptProtocol.Text(detail, "message") ?? ChatGptProtocol.Text(detail, "msg"),
        JsonValueKind.Array => string.Join("; ", detail.EnumerateArray().Take(4).Select(DetailText).Where(text => text is not null)),
        _ => null,
    };

    private static string StatusAndParameter(HttpStatusCode? status, string? parameter)
    {
        var parts = new List<string>();
        if (status is { } code) parts.Add("HTTP " + (int)code);
        if (SafeText(parameter, 128) is { } safeParameter) parts.Add("参数 " + safeParameter);
        return parts.Count == 0 ? string.Empty : "（" + string.Join("，", parts) + "）";
    }

    // Sanitize before the final display bound so a credential crossing that bound cannot leak.
    // The inspection window and regex timeout also bound work on hostile diagnostics.
    private static string? SafeMetadata(string? value, int limit)
    {
        // Metadata is machine vocabulary. Do not turn malformed fields into valid
        // identifiers by trimming/removing controls before the logger validates them.
        if (string.IsNullOrEmpty(value) || value.Length > limit || value.Any(c => char.IsWhiteSpace(c)
            || char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)) return null;
        return SafeText(value, limit);
    }

    private static string? SafeText(string? text, int limit = 512)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = new string(text.Take(8192).Select(c => char.IsControl(c) && char.IsWhiteSpace(c) ? ' ' : c).Where(c => !char.IsControl(c)
            && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
        try
        {
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
            var timeout = TimeSpan.FromMilliseconds(100);
            text = Regex.Replace(text, @"\b(?:Bearer|Basic)\s+[^\s,;""'<>]+", "[redacted]", options, timeout);
            text = Regex.Replace(text, @"\b(?:sk-|sess-)[a-z0-9_-]+|\beyJ[a-z0-9_-]*(?:\.[a-z0-9_-]*){1,2}|\b[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}", "[redacted]", options, timeout);
            text = Regex.Replace(text, @"\b(?:authorization|api[_-]?key|x-api-key|access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|password|credentials?|token|secret)\b[""']?\s*[:=]\s*(?:""[^""]*(?:""|$)|'[^']*(?:'|$)|[^\s,;<>]+)", "[redacted]", options, timeout);
            text = Regex.Replace(text, @"https?://[^/\s@]+@", "https://[redacted]@", options, timeout);
            text = Regex.Replace(text, @"\s+", " ", options, timeout).Trim();
        }
        catch (RegexMatchTimeoutException) { return "[redacted]"; }
        if (text.Length == 0) return null;
        return text.Length <= limit ? text : text[..(limit - 1)] + "…";
    }

    public static async Task<ChatGptException> FromResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        var requestId = ChatGptProtocol.RequestId(response);
        string body;
        try
        {
            body = await ChatGptProtocol.ReadBodyAsync(response, token).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return new("ChatGPT 错误响应超过接收长度预算。" + StatusAndParameter(response.StatusCode, null),
                status: response.StatusCode, requestId: requestId, responseShape: "response_too_large");
        }
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            return FromError(json.RootElement, response.StatusCode, requestId);
        }
        catch (JsonException)
        {
            var reason = SafeText(body);
            return new((reason ?? "ChatGPT 返回了无法识别的错误。") + StatusAndParameter(response.StatusCode, null),
                status: response.StatusCode, requestId: requestId, responseShape: "non_json", serverMessage: reason);
        }
    }
}

internal static class ChatGptProtocol
{
    internal const string Issuer = "https://auth.openai.com";
    internal const string Resource = "https://api.openai.com/v1";
    internal const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    internal const string SharingScope = "chatgpt.tokens.use.direct";
    internal const string DynamicClient = "dynamic_agent_client";
    internal static string? RequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-oai-request-id", out var values)
        || response.Headers.TryGetValues("x-request-id", out values)
        || response.Headers.TryGetValues("openai-request-id", out values) ? values.FirstOrDefault() : null;
    internal static string? Text(JsonElement root, string field) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
        => JsonDocument.Parse(await ReadBytesAsync(response, token).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 32 });

    internal static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
        => Encoding.UTF8.GetString(await ReadBytesAsync(response, token).ConfigureAwait(false));

    private static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, CancellationToken token)
    {
        const int limit = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new JsonException("Response too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > limit) throw new JsonException("Response too large.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }
}
