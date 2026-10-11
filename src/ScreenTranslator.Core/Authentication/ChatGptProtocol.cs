using System.Net;
using System.Text.Json;
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
    public string ResponseShape { get; }
    public bool IsUsageLimit => Code == "subscription_sharing_usage_limit_exceeded";
    public bool IsTerminalRefreshError => Code is "invalid_grant" or "invalid_refresh_token" or "token_expired"
        or "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused";

    public ChatGptException(string message, string? code = null, HttpStatusCode? status = null,
        string? requestId = null, string? parameter = null, string responseShape = "unknown") : base(message)
    { Code = code; StatusCode = status; RequestId = requestId; Parameter = parameter; ResponseShape = responseShape; }

    public static ChatGptException FromError(JsonElement root, HttpStatusCode? status = null, string? requestId = null)
    {
        var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var nested) ? nested : root;
        var code = error.ValueKind == JsonValueKind.String ? error.GetString() : ChatGptProtocol.Text(error, "code");
        var parameter = ChatGptProtocol.Text(error, "param");
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
            _ => status switch
            {
                HttpStatusCode.Unauthorized => "ChatGPT 授权未被接受，请检查账户和额度授权。",
                HttpStatusCode.Forbidden => "当前账户、工作区或所在区域不允许此 ChatGPT 请求。",
                HttpStatusCode.TooManyRequests => "ChatGPT 请求过于频繁，请稍后重试或点击“管理额度”查看限制。",
                >= HttpStatusCode.InternalServerError => "ChatGPT 服务暂不可用，请稍后重试。",
                _ => "ChatGPT 请求未完成，请检查账户权限、模型与输入后重试。",
            },
        };
        return new(message, code, status, requestId, parameter, shape);
    }

    public static async Task<ChatGptException> FromResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        var requestId = response.Headers.TryGetValues("x-request-id", out var values)
            || response.Headers.TryGetValues("openai-request-id", out values) ? values.FirstOrDefault() : null;
        try
        {
            using var json = await ChatGptProtocol.ReadJsonAsync(response, token).ConfigureAwait(false);
            return FromError(json.RootElement, response.StatusCode, requestId);
        }
        catch (JsonException) { return new("ChatGPT 返回了无法识别的错误，请稍后重试。", status: response.StatusCode, requestId: requestId, responseShape: "non_json"); }
    }
}

internal static class ChatGptProtocol
{
    internal const string Issuer = "https://auth.openai.com";
    internal const string Resource = "https://api.openai.com/v1";
    internal const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    internal const string SharingScope = "chatgpt.tokens.use.direct";
    internal const string DynamicClient = "dynamic_agent_client";
    internal static string? Text(JsonElement root, string field) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
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
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }
}
