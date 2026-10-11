using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ScreenTranslator.Core.Authentication;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Translation;

public sealed record TranslationModelChoice(string Id, string DisplayName)
{
    // Null means metadata absent; an empty list means no recognized direct API efforts.
    public IReadOnlyList<ThinkingLevel>? SupportedReasoningLevels { get; init; }
    public ThinkingLevel? DefaultReasoningLevel { get; init; }
}
public sealed record TranslationModelList(IReadOnlyList<string> Models, bool IsTruncated = false)
{
    public IReadOnlyList<TranslationModelChoice> Choices { get; init; } = [];
}

public sealed class ModelCatalogException(string message) : Exception(message);

/// <summary>Reads model metadata only; never sends translation inputs or calls a generation endpoint.</summary>
public sealed class TranslationModelCatalog(HttpClient http)
{
    public const int MaximumModels = 2048;
    private const int MaximumPageBytes = 2 * 1024 * 1024;

    public async Task<TranslationModelList> ReadChatGptAsync(IChatGptTokenProvider tokens, string accountId, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, tokens.GetSessionCancellation(accountId));
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var access = await tokens.GetAccessTokenAsync(http, accountId, deadline.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw await ChatGptException.FromResponseAsync(response, deadline.Token).ConfigureAwait(false);
            using var json = await ChatGptProtocol.ReadJsonAsync(response, deadline.Token).ConfigureAwait(false);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                throw new ModelCatalogException("ChatGPT 未返回账户可用模型，请稍后重新读取。");
            var choices = new List<TranslationModelChoice>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in models.EnumerateArray())
            {
                var id = ChatGptProtocol.Text(model, "slug");
                if (ChatGptProtocol.Text(model, "visibility") != "list" || string.IsNullOrWhiteSpace(id)
                    || id.Length > 200 || id.Any(char.IsControl) || !seen.Add(id)) continue;
                var name = ChatGptProtocol.Text(model, "display_name");
                var levels = ReadReasoningLevels(model);
                var defaultEffort = ChatGptProtocol.Text(model, "default_reasoning_level");
                choices.Add(new(id, string.IsNullOrWhiteSpace(name) ? id : name)
                {
                    SupportedReasoningLevels = levels,
                    DefaultReasoningLevel = ThinkingLevelNames.TryParseEffort(defaultEffort, out var level)
                        && levels?.Contains(level) == true ? level : null,
                });
                if (choices.Count == MaximumModels) break;
            }
            deadline.Token.ThrowIfCancellationRequested();
            tokens.UpdateModelCapabilities(accountId, choices);
            return new(choices.Select(c => c.Id).ToArray(), models.GetArrayLength() > MaximumModels) { Choices = choices };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new ModelCatalogException("读取 ChatGPT 模型超时或账户已退出，请重试。"); }
        catch (HttpRequestException) { throw new ModelCatalogException("无法读取 ChatGPT 模型，请检查网络后重试。"); }
        catch (JsonException) { throw new ModelCatalogException("ChatGPT 模型列表格式无效，请稍后重试。"); }
    }

    private static IReadOnlyList<ThinkingLevel>? ReadReasoningLevels(JsonElement model)
    {
        if (!model.TryGetProperty("supported_reasoning_levels", out var entries)) return null;
        var levels = new List<ThinkingLevel>();
        if (entries.ValueKind == JsonValueKind.Array)
            foreach (var entry in entries.EnumerateArray())
                if (ThinkingLevelNames.TryParseEffort(ChatGptProtocol.Text(entry, "effort"), out var level)
                    && !levels.Contains(level)) levels.Add(level);
        return levels.AsReadOnly();
    }

    public static Uri Endpoint(TranslationProviderKind provider, string baseUrl)
    {
        var translation = TranslationProviderConfiguration.Endpoint(provider, baseUrl);
        var suffix = provider switch { TranslationProviderKind.OpenAI => "/responses", TranslationProviderKind.Claude => "/messages", _ => "/chat/completions" };
        return new Uri(translation.AbsoluteUri[..^suffix.Length] + "/models");
    }

    public async Task<TranslationModelList> ReadAsync(TranslationProviderKind provider, string baseUrl, string? apiKey,
        CancellationToken cancellationToken = default)
    {
        var endpoint = Endpoint(provider, baseUrl);
        var key = apiKey?.Trim();
        if (string.IsNullOrEmpty(key) && !endpoint.IsLoopback) throw new ModelCatalogException("请先填写或保存当前服务商的 API Key。");
        if (key?.Any(char.IsControl) == true) throw new ModelCatalogException("API Key 含有无效字符，请重新填写。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var models = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        try
        {
            for (var page = 0; page < 10; page++)
            {
                var uri = provider == TranslationProviderKind.Claude
                    ? new Uri(endpoint.AbsoluteUri + "?limit=1000" + (after is null ? "" : "&after_id=" + Uri.EscapeDataString(after))) : endpoint;
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (provider == TranslationProviderKind.Claude)
                {
                    if (!string.IsNullOrEmpty(key)) request.Headers.Add("x-api-key", key);
                    request.Headers.Add("anthropic-version", "2023-06-01");
                }
                else if (!string.IsNullOrEmpty(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
                if (response.Content.Headers.ContentLength > MaximumPageBytes) throw new ModelCatalogException("模型列表响应过大，请手动输入模型 ID。");
                using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream(); var chunk = new byte[8192];
                while (true)
                {
                    var count = await body.ReadAsync(chunk, deadline.Token).ConfigureAwait(false); if (count == 0) break;
                    if (buffer.Length + count > MaximumPageBytes) throw new ModelCatalogException("模型列表响应过大，请手动输入模型 ID。");
                    buffer.Write(chunk, 0, count);
                }
                using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                var root = json.RootElement; JsonElement entries;
                if (root.ValueKind == JsonValueKind.Array) entries = root;
                else if (root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("data", out entries) || root.TryGetProperty("models", out entries))) { }
                else throw new ModelCatalogException("服务商未返回可识别的模型列表，请手动输入模型 ID。");
                if (entries.ValueKind != JsonValueKind.Array) throw new ModelCatalogException("模型列表格式不正确，请手动输入模型 ID。");
                var truncated = false;
                foreach (var entry in entries.EnumerateArray())
                {
                    string? id = null;
                    if (entry.ValueKind == JsonValueKind.String) id = entry.GetString();
                    else if (entry.ValueKind == JsonValueKind.Object && (entry.TryGetProperty("id", out var field) || entry.TryGetProperty("name", out field)) && field.ValueKind == JsonValueKind.String) id = field.GetString();
                    id = id?.Trim();
                    if (string.IsNullOrEmpty(id) || id.Length > 200 || id.Any(char.IsControl)) continue;
                    if (models.Count == MaximumModels && !models.Contains(id)) { truncated = true; break; }
                    models.Add(id);
                }
                var more = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("has_more", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (truncated || !more) return Result(models, truncated);
                if (provider != TranslationProviderKind.Claude || page == 9 || models.Count == MaximumModels) return Result(models, true);
                if (!root.TryGetProperty("last_id", out var cursor) || cursor.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(after = cursor.GetString()) || after.Length > 200 || after.Any(char.IsControl) || !cursors.Add(after))
                    throw new ModelCatalogException("模型列表分页信息无效，请重试或手动输入模型 ID。");
            }
            return Result(models, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ModelCatalogException("读取模型列表超时，请重试。"); }
        catch (JsonException) { throw new ModelCatalogException("服务商返回的内容不是有效的模型列表，请检查接口地址或手动输入模型 ID。"); }
        catch (HttpRequestException) { throw new ModelCatalogException("无法连接模型列表接口，请检查地址与网络后重试。"); }
        catch (IOException) { throw new ModelCatalogException("模型列表读取中断，请重试。"); }
        catch (FormatException) { throw new ModelCatalogException("API Key 格式无效，请重新填写。"); }
    }

    private static TranslationModelList Result(HashSet<string> models, bool truncated) => new(models.Order(StringComparer.OrdinalIgnoreCase).ThenBy(x => x, StringComparer.Ordinal).ToArray(), truncated);
    private static ModelCatalogException Failure(HttpStatusCode status) => new(status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "无法读取模型列表：API Key 无效或没有访问权限。",
        HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => "此服务商不支持读取模型列表，请手动输入模型 ID。",
        HttpStatusCode.TooManyRequests => "读取模型列表过于频繁，请稍后重试。",
        >= HttpStatusCode.InternalServerError => "服务商的模型列表接口暂不可用，请稍后重试。",
        >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => "模型列表接口发生重定向，请填写最终接口地址。",
        _ => $"读取模型列表失败（HTTP {(int)status}），请检查接口配置或手动输入模型 ID。",
    });
}
