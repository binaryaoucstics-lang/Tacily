using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Authentication;

public sealed record ChatGptAccount(string Id, string Label, bool IsConnected, bool CanUsePlan, bool UsagePaused);
public sealed record ChatGptSignInResult(ChatGptAccount Account, bool ShowWelcome);
public sealed record ChatGptSignOutResult(bool RevocationConfirmed);

/// <summary>One shared runtime owns all registrations and rotating tokens. The secret store protects the entire record.</summary>
public sealed class ChatGptAccountService(ISecretStore secrets, Action<Uri> openBrowser, string appName = "全局 AI 翻译器") : IChatGptTokenProvider
{
    public const string SecretName = "chatgpt-oauth";
    public const string UsageUrl = "https://chatgpt.com/settings/usage";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _signInGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();
    private readonly ConcurrentDictionary<string, ChatGptException> _usageLimits = new();
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ThinkingLevel>>> _modelCapabilities = new();
    public event Action? AccountsChanged;

    public IReadOnlyList<ThinkingLevel>? GetSupportedThinkingLevels(string accountId, string model) =>
        _modelCapabilities.TryGetValue(accountId, out var models) && models.TryGetValue(model, out var levels) ? levels : null;

    public void UpdateModelCapabilities(string accountId, IReadOnlyList<TranslationModelChoice> models) =>
        _modelCapabilities[accountId] = models.Where(model => model.SupportedReasoningLevels is not null)
            .GroupBy(model => model.Id, StringComparer.Ordinal).ToDictionary(group => group.Key,
                group => (IReadOnlyList<ThinkingLevel>)Array.AsReadOnly(group.First().SupportedReasoningLevels!
                    .Where(level => Enum.IsDefined(level)).Distinct().ToArray()), StringComparer.Ordinal);

    public async Task<IReadOnlyList<ChatGptAccount>> ListAccountsAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return (await LoadAsync(token).ConfigureAwait(false)).Accounts.Select(Display).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<ChatGptSignInResult> SignInAsync(HttpClient http, string? accountId = null,
        bool requestConsent = false, CancellationToken token = default)
    {
        await _signInGate.WaitAsync(token).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            AccountRecord? previous;
            string hostId;
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                var saved = await LoadAsync(deadline.Token).ConfigureAwait(false);
                previous = string.IsNullOrEmpty(accountId) ? null : Find(saved, accountId);
                hostId = saved.HostId;
            }
            finally { _gate.Release(); }

            using var listener = new ChatGptLoopbackListener();
            var state = RandomValue(); var nonce = RandomValue(); var verifier = RandomValue(64);
            var parameters = new Dictionary<string, string>
            {
                ["client_id"] = previous?.ClientId ?? ChatGptProtocol.DynamicClient,
                ["ext_agent_host_id"] = hostId, ["response_type"] = "code",
                ["redirect_uri"] = listener.RedirectUri.AbsoluteUri, ["scope"] = ChatGptProtocol.Scope,
                ["resource"] = ChatGptProtocol.Resource, ["state"] = state, ["nonce"] = nonce,
                ["code_challenge_method"] = "S256", ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            };
            if (previous is null) parameters["agent_name_hint"] = appName;
            else
            {
                if (!string.IsNullOrEmpty(previous.IdToken)) parameters["id_token_hint"] = previous.IdToken;
                if (!string.IsNullOrEmpty(previous.Email)) parameters["login_hint"] = previous.Email;
            }
            if (requestConsent) parameters["prompt"] = "consent";
            // Authorization URLs may contain id_token_hint; never log or put them in UI state.
            openBrowser(new Uri(ChatGptProtocol.Issuer + "/api/accounts/authorize?" + Query(parameters)));
            var callback = await listener.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            if (!callback.TryGetValue("state", out var actualState) || !Equal(state, actualState))
                throw new ChatGptException("无法验证本次登录，请重新点击登录。", "invalid_state");
            if (callback.TryGetValue("error", out var callbackError))
                throw new ChatGptException(callbackError == "access_denied" ? "已取消 ChatGPT 授权，可以稍后重试。" : "ChatGPT 登录未完成，请重试。", callbackError);
            if (!callback.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                throw new ChatGptException("登录回调缺少授权码，请重试。", "missing_code");
            var clientId = callback.GetValueOrDefault("client_id") ?? previous?.ClientId;
            if (string.IsNullOrWhiteSpace(clientId) || clientId == ChatGptProtocol.DynamicClient
                || clientId.Length > 256 || clientId.Any(char.IsControl)
                || previous is not null && clientId != previous.ClientId)
                throw new ChatGptException("登录返回的账户注册信息不匹配，请重新登录。", "invalid_client");
            var pending = previous ?? new AccountRecord { Id = Guid.NewGuid().ToString("N"), ClientId = clientId };
            // Preserve the issued registration even when code exchange fails. It remains separate from the active account.
            if (previous is null) await SaveAccountAsync(pending, deadline.Token).ConfigureAwait(false);
            using var data = await TokenRequestAsync(http, new()
            {
                ["grant_type"] = "authorization_code", ["client_id"] = clientId,
                ["code"] = code, ["code_verifier"] = verifier,
                ["redirect_uri"] = listener.RedirectUri.AbsoluteUri, ["resource"] = ChatGptProtocol.Resource,
            }, deadline.Token).ConfigureAwait(false);
            var idToken = ChatGptProtocol.Text(data.RootElement, "id_token")
                ?? throw new ChatGptException("ChatGPT 未返回可验证的身份，请重新登录。", "missing_id_token");
            var identity = await OpenAiIdTokenValidator.ValidateAsync(http, idToken, clientId, nonce, deadline.Token).ConfigureAwait(false);
            if (previous is { Subject.Length: > 0 } && (identity.Subject != previous.Subject || identity.Issuer != previous.Issuer))
                throw new ChatGptException("登录身份与所选账户不同，请使用“添加账户”创建独立连接。", "account_mismatch");
            var account = ApplyTokens(pending with { Issuer = identity.Issuer, Subject = identity.Subject,
                Email = identity.Email, Name = identity.Name }, data.RootElement, initial: true);
            await SaveAccountAsync(account, deadline.Token).ConfigureAwait(false);
            _sessions.AddOrUpdate(account.Id, _ => new(), (_, previousSession) => { previousSession.Cancel(); return new(); });
            _usageLimits.TryRemove(account.Id, out _);
            NotifyAccountsChanged();
            return new(Display(account), account.CanUsePlan && !account.WelcomeShown);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new ChatGptException("ChatGPT 登录等待超时，请重新点击登录。", "sign_in_timeout"); }
        catch (HttpRequestException) { throw new ChatGptException("无法连接 ChatGPT 登录服务，请检查网络后重试。"); }
        catch (JsonException) { throw new ChatGptException("ChatGPT 登录响应无效，请重试。"); }
        finally { _signInGate.Release(); }
    }

    public CancellationToken GetSessionCancellation(string accountId) => _sessions.GetOrAdd(accountId, _ => new()).Token;

    public async Task<string> GetAccessTokenAsync(HttpClient http, string accountId, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, GetSessionCancellation(accountId));
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            if (_usageLimits.TryGetValue(accountId, out var limit)) throw limit;
            var saved = await LoadAsync(deadline.Token).ConfigureAwait(false);
            var account = Find(saved, accountId);
            if (!account.IsConnected) throw new ChatGptException("请在翻译设置中登录所选 ChatGPT 账户。", "sign_in_required");
            if (!account.CanUsePlan) throw new ChatGptException("此账户尚未授权使用 ChatGPT 额度，请点击“授权使用额度”。", "plan_permission_required");
            var now = DateTimeOffset.UtcNow;
            if (account.ExpiresAt > now.AddMinutes(2) || account.ExpiresAt > now && account.EarliestRefreshAt > now)
                return account.AccessToken!;
            if (string.IsNullOrEmpty(account.RefreshToken)) throw new ChatGptException("ChatGPT 登录已过期，请重新登录。", "sign_in_required");
            try
            {
                using var data = await TokenRequestAsync(http, new()
                {
                    ["grant_type"] = "refresh_token", ["client_id"] = account.ClientId,
                    ["refresh_token"] = account.RefreshToken, ["resource"] = ChatGptProtocol.Resource,
                }, deadline.Token).ConfigureAwait(false);
                if (ChatGptProtocol.Text(data.RootElement, "id_token") is { } idToken)
                {
                    var identity = await OpenAiIdTokenValidator.ValidateAsync(http, idToken, account.ClientId, null, deadline.Token).ConfigureAwait(false);
                    if (identity.Subject != account.Subject || identity.Issuer != account.Issuer)
                        throw new ChatGptException("续期身份与已保存账户不匹配，请重新登录。", "account_mismatch");
                }
                account = ApplyTokens(account, data.RootElement, initial: false);
                Replace(saved, account);
                await SaveAsync(saved, deadline.Token).ConfigureAwait(false);
                if (!account.CanUsePlan) throw new ChatGptException("ChatGPT 额度授权已关闭，请重新授权。", "plan_permission_required");
                return account.AccessToken!;
            }
            catch (ChatGptException error) when (error.IsTerminalRefreshError)
            {
                Replace(saved, account.WithoutTokens());
                await SaveAsync(saved, CancellationToken.None).ConfigureAwait(false);
                _sessions[accountId].Cancel();
                NotifyAccountsChanged();
                throw;
            }
        }
        catch (HttpRequestException) { throw new ChatGptException("无法续期 ChatGPT 登录，请检查网络后重试。已保留账户连接。"); }
        catch (JsonException) { throw new ChatGptException("ChatGPT 续期响应无效，请稍后重试。已保留账户连接。"); }
        finally { _gate.Release(); }
    }

    public async Task<ChatGptSignOutResult> SignOutAsync(HttpClient http, string accountId, CancellationToken token = default)
    {
        _sessions.GetOrAdd(accountId, _ => new()).Cancel();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var saved = await LoadAsync(token).ConfigureAwait(false);
            var account = Find(saved, accountId);
            var confirmed = string.IsNullOrEmpty(account.RefreshToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                if (!confirmed)
                {
                    using var discoveryResponse = await http.GetAsync(ChatGptProtocol.Issuer + "/.well-known/openid-configuration", deadline.Token).ConfigureAwait(false);
                    if (!discoveryResponse.IsSuccessStatusCode) throw new HttpRequestException();
                    using var discovery = await ChatGptProtocol.ReadJsonAsync(discoveryResponse, deadline.Token).ConfigureAwait(false);
                    var endpoint = ChatGptProtocol.Text(discovery.RootElement, "revocation_endpoint");
                    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "auth.openai.com"
                        || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
                        throw new JsonException();
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                            { ["token"] = account.RefreshToken!, ["token_type_hint"] = "refresh_token", ["client_id"] = account.ClientId });
                            using var response = await http.PostAsync(uri, form, deadline.Token).ConfigureAwait(false);
                            confirmed = response.StatusCode == System.Net.HttpStatusCode.OK;
                            if (confirmed || (int)response.StatusCode < 500) break;
                        }
                        catch (HttpRequestException) when (attempt < 2) { }
                        if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), deadline.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException) { }
            Replace(saved, account.WithoutTokens());
            await SaveAsync(saved, CancellationToken.None).ConfigureAwait(false);
            _usageLimits.TryRemove(accountId, out _);
            NotifyAccountsChanged();
            return new(confirmed);
        }
        finally { _gate.Release(); }
    }

    public void ReportInferenceFailure(string accountId, ChatGptException error)
    {
        if (!error.IsUsageLimit) return;
        _usageLimits[accountId] = error;
        NotifyAccountsChanged();
    }

    public void ResumeUsage(string accountId) { _usageLimits.TryRemove(accountId, out _); NotifyAccountsChanged(); }

    public async Task MarkWelcomeShownAsync(string accountId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var saved = await LoadAsync(token).ConfigureAwait(false);
            Replace(saved, Find(saved, accountId) with { WelcomeShown = true });
            await SaveAsync(saved, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<State> LoadAsync(CancellationToken token)
    {
        var text = await secrets.GetAsync(SecretName, token).ConfigureAwait(false);
        if (text is null)
        {
            var created = new State { HostId = "urn:uuid:" + Guid.NewGuid().ToString("D") };
            await SaveAsync(created, token).ConfigureAwait(false);
            return created;
        }
        var state = JsonSerializer.Deserialize<State>(text, JsonOptions);
        if (state is null || !state.HostId.StartsWith("urn:uuid:", StringComparison.Ordinal)
            || !Guid.TryParseExact(state.HostId[9..], "D", out _) || state.Accounts is null
            || state.Accounts.Any(a => !Guid.TryParseExact(a.Id, "N", out _) || string.IsNullOrWhiteSpace(a.ClientId)
                || a.ClientId == ChatGptProtocol.DynamicClient) || state.Accounts.Select(a => a.Id).Distinct().Count() != state.Accounts.Count)
            throw new ChatGptException("已保存的 ChatGPT 账户信息无效，请检查本机凭据存储。", "invalid_saved_registration");
        return state;
    }

    private Task SaveAsync(State state, CancellationToken token) => secrets.SetAsync(SecretName, JsonSerializer.Serialize(state, JsonOptions), token);
    private async Task SaveAccountAsync(AccountRecord account, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { var saved = await LoadAsync(token).ConfigureAwait(false); Replace(saved, account); await SaveAsync(saved, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private static AccountRecord Find(State state, string id) => state.Accounts.FirstOrDefault(a => a.Id == id)
        ?? throw new ChatGptException("请选择已保存的 ChatGPT 账户，或点击登录添加账户。", "sign_in_required");
    private static void Replace(State state, AccountRecord account)
    {
        var index = state.Accounts.FindIndex(a => a.Id == account.Id);
        if (index < 0) state.Accounts.Add(account); else state.Accounts[index] = account;
    }
    private ChatGptAccount Display(AccountRecord a) => new(a.Id,
        (a.Email ?? a.Name ?? "ChatGPT 连接") + " · " + a.Id[..8], a.IsConnected, a.CanUsePlan, _usageLimits.ContainsKey(a.Id));
    private void NotifyAccountsChanged()
    {
        if (AccountsChanged is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch { /* Display observers cannot change credential or inference outcomes. */ }
    }

    private static AccountRecord ApplyTokens(AccountRecord previous, JsonElement data, bool initial)
    {
        var access = ChatGptProtocol.Text(data, "access_token");
        var refresh = ChatGptProtocol.Text(data, "refresh_token");
        if (string.IsNullOrWhiteSpace(access) || access.Any(char.IsControl)
            || !string.Equals(ChatGptProtocol.Text(data, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase)
            || !data.TryGetProperty("expires_in", out var expires) || !expires.TryGetInt32(out var seconds) || seconds is <= 0 or > 86400
            || !initial && string.IsNullOrEmpty(refresh))
            throw new ChatGptException("ChatGPT 返回的登录凭据无效，请重新登录。", "invalid_token_response");
        var scope = ChatGptProtocol.Text(data, "scope");
        if (initial && scope is null) throw new ChatGptException("ChatGPT 未返回授权范围，请重新登录。", "missing_scope");
        var earliest = DateTimeOffset.MinValue;
        if (data.TryGetProperty("earliest_refresh_at", out var timestamp) && timestamp.TryGetInt64(out var unix) && unix is > 0 and < 253402300800)
            earliest = DateTimeOffset.FromUnixTimeSeconds(unix);
        return previous with
        {
            AccessToken = access, RefreshToken = refresh ?? previous.RefreshToken,
            IdToken = ChatGptProtocol.Text(data, "id_token") ?? previous.IdToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds), EarliestRefreshAt = earliest,
            Scopes = scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray() ?? previous.Scopes,
        };
    }

    private static async Task<JsonDocument> TokenRequestAsync(HttpClient http, Dictionary<string, string> fields, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var form = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync(ChatGptProtocol.Issuer + "/api/accounts/oauth/token", form, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await ChatGptException.FromResponseAsync(response, deadline.Token).ConfigureAwait(false);
        return await ChatGptProtocol.ReadJsonAsync(response, deadline.Token).ConfigureAwait(false);
    }

    private static string RandomValue(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool Equal(string expected, string actual) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    private static string Query(Dictionary<string, string> parameters) => string.Join('&', parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

    private sealed class State
    {
        public string HostId { get; init; } = "";
        public List<AccountRecord> Accounts { get; init; } = [];
        public override string ToString() => "Protected ChatGPT registrations";
    }
    private sealed record AccountRecord
    {
        public string Id { get; init; } = "";
        public string ClientId { get; init; } = "";
        public string Issuer { get; init; } = "";
        public string Subject { get; init; } = "";
        public string? Email { get; init; }
        public string? Name { get; init; }
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public string? IdToken { get; init; }
        public string[] Scopes { get; init; } = [];
        public DateTimeOffset ExpiresAt { get; init; }
        public DateTimeOffset EarliestRefreshAt { get; init; }
        public bool WelcomeShown { get; init; }
        public bool IsConnected => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(Subject);
        public bool CanUsePlan => IsConnected && Scopes.Contains(ChatGptProtocol.SharingScope) && Scopes.Contains("resource.invoke");
        public AccountRecord WithoutTokens() => this with { AccessToken = null, RefreshToken = null, IdToken = null, Scopes = [] };
        public override string ToString() => "Protected ChatGPT account";
    }
}
