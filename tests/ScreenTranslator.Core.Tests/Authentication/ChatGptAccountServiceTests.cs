using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Authentication;

namespace ScreenTranslator.Core.Tests.Authentication;

public sealed class ChatGptAccountServiceTests
{
    [Fact]
    public async Task Registration_Uses_PKCE_And_Persisted_Host_Then_Resumes_After_Restart()
    {
        using var h = new Harness();
        var result = await h.SignInAsync();
        var authorize = h.Authorizations.Single();
        var exchange = h.TokenForms.Single();
        Assert.Equal("dynamic_agent_client", authorize["client_id"]);
        Assert.Equal("全局 AI 翻译器", authorize["agent_name_hint"]);
        Assert.StartsWith("urn:uuid:", authorize["ext_agent_host_id"]);
        Assert.Contains("chatgpt.tokens.use.direct", authorize["scope"]);
        Assert.Equal("https://api.openai.com/v1", authorize["resource"]);
        Assert.Equal("S256", authorize["code_challenge_method"]);
        Assert.Equal(B64(SHA256.HashData(Encoding.ASCII.GetBytes(exchange["code_verifier"]))), authorize["code_challenge"]);
        Assert.Equal(authorize["redirect_uri"], exchange["redirect_uri"]);
        Assert.Equal("127.0.0.1", new Uri(exchange["redirect_uri"]).Host);
        Assert.Equal("/auth/callback", new Uri(exchange["redirect_uri"]).AbsolutePath);
        Assert.Equal("oaiapp_test_1", exchange["client_id"]);
        Assert.False(exchange.ContainsKey("client_secret"));
        Assert.True(result.Account.CanUsePlan);
        Assert.True(result.ShowWelcome);
        Assert.DoesNotContain("test-access", result.Account.ToString());
        var restarted = new ChatGptAccountService(h.Secrets, _ => throw new InvalidOperationException());
        Assert.Equal("test-access-1", await restarted.GetAccessTokenAsync(h.Http, result.Account.Id, default));
        Assert.Single(h.TokenForms);
        await h.Service.MarkWelcomeShownAsync(result.Account.Id);
        var reauthorized = await h.SignInAsync(result.Account.Id);
        Assert.False(reauthorized.ShowWelcome);
        Assert.Equal(authorize["ext_agent_host_id"], h.Authorizations[1]["ext_agent_host_id"]);
        Assert.Equal("oaiapp_test_1", h.Authorizations[1]["client_id"]);
        Assert.False(h.Authorizations[1].ContainsKey("agent_name_hint"));
        Assert.True(h.Authorizations[1].ContainsKey("id_token_hint"));
        Assert.NotEqual(authorize["nonce"], h.Authorizations[1]["nonce"]);
        Assert.NotEqual(authorize["state"], h.Authorizations[1]["state"]);
    }

    [Fact]
    public async Task Valid_Identity_Without_Granted_Plan_Scope_Does_Not_Enable_Inference()
    {
        using var h = new Harness { GrantedScopes = "openid profile email offline_access resource.invoke" };
        var result = await h.SignInAsync();
        Assert.True(result.Account.IsConnected);
        Assert.False(result.Account.CanUsePlan);
        Assert.False(result.ShowWelcome);
        var error = await Assert.ThrowsAsync<ChatGptException>(() => h.Service.GetAccessTokenAsync(h.Http, result.Account.Id, default));
        Assert.Equal("plan_permission_required", error.Code);
        await h.SignInAsync(result.Account.Id, consent: true);
        Assert.Equal("consent", h.Authorizations[1]["prompt"]);
        Assert.Contains("chatgpt.tokens.use.direct", h.Authorizations[1]["scope"]);
    }

    [Theory]
    [InlineData("invalid_state")]
    [InlineData("access_denied")]
    [InlineData("missing_client")]
    [InlineData("duplicate_state")]
    public async Task Invalid_Callback_Never_Exchanges_A_Code(string failure)
    {
        using var h = new Harness { CallbackFailure = failure };
        await Assert.ThrowsAsync<ChatGptException>(() => h.SignInAsync());
        Assert.Empty(h.TokenForms);
        Assert.Empty(await h.Service.ListAccountsAsync());
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("expiry")]
    public async Task Invalid_Id_Token_Cannot_Save_Usable_Credentials(string failure)
    {
        using var h = new Harness { IdentityFailure = failure };
        var error = await Assert.ThrowsAsync<ChatGptException>(() => h.SignInAsync());
        Assert.Equal("invalid_id_token", error.Code);
        var pending = Assert.Single(await h.Service.ListAccountsAsync());
        Assert.False(pending.IsConnected);
        Assert.DoesNotContain("test-access", h.Secrets.Values[ChatGptAccountService.SecretName]);
        h.IdentityFailure = null;
        await h.SignInAsync(pending.Id);
        Assert.Equal("oaiapp_test_1", h.Authorizations[1]["client_id"]);
    }

    [Fact]
    public async Task Accounts_With_The_Same_Email_Stay_Separate_And_Reauthorization_Requires_The_Same_Subject()
    {
        using var h = new Harness();
        var a = await h.SignInAsync(); var b = await h.SignInAsync();
        Assert.NotEqual(a.Account.Id, b.Account.Id);
        Assert.NotEqual(a.Account.Label, b.Account.Label);
        Assert.Equal(h.Authorizations[0]["ext_agent_host_id"], h.Authorizations[1]["ext_agent_host_id"]);
        Assert.Equal("test-access-1", await h.Service.GetAccessTokenAsync(h.Http, a.Account.Id, default));
        h.Subject = "different-user";
        var error = await Assert.ThrowsAsync<ChatGptException>(() => h.SignInAsync(a.Account.Id));
        Assert.Equal("account_mismatch", error.Code);
        Assert.Equal("test-access-1", await h.Service.GetAccessTokenAsync(h.Http, a.Account.Id, default));
        Assert.Equal("test-access-2", await h.Service.GetAccessTokenAsync(h.Http, b.Account.Id, default));
    }

    [Fact]
    public async Task Near_Expiry_Concurrent_Requests_Rotate_The_Refresh_Token_Once()
    {
        using var h = new Harness { InitialLifetime = 1 };
        var result = await h.SignInAsync();
        var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.Service.GetAccessTokenAsync(h.Http, result.Account.Id, default)));
        Assert.All(tokens, value => Assert.Equal("test-access-refreshed", value));
        var refresh = Assert.Single(h.TokenForms, f => f["grant_type"] == "refresh_token");
        Assert.Equal("oaiapp_test_1", refresh["client_id"]);
        Assert.Equal("test-refresh-1", refresh["refresh_token"]);
        Assert.Equal("https://api.openai.com/v1", refresh["resource"]);
        Assert.False(refresh.ContainsKey("scope"));
        Assert.Contains("test-refresh-rotated", h.Secrets.Values[ChatGptAccountService.SecretName]);
        Assert.DoesNotContain("test-refresh-1", h.Secrets.Values[ChatGptAccountService.SecretName]);
    }

    [Fact]
    public async Task Terminal_Refresh_Error_Clears_Tokens_And_Retains_Registration()
    {
        using var h = new Harness { InitialLifetime = 1, RefreshFailure = "invalid_grant" };
        var result = await h.SignInAsync();
        var error = await Assert.ThrowsAsync<ChatGptException>(() => h.Service.GetAccessTokenAsync(h.Http, result.Account.Id, default));
        Assert.Equal("invalid_grant", error.Code);
        Assert.False(Assert.Single(await h.Service.ListAccountsAsync()).IsConnected);
        var saved = h.Secrets.Values[ChatGptAccountService.SecretName];
        Assert.Contains("oaiapp_test_1", saved);
        Assert.DoesNotContain("test-access", saved);
        Assert.DoesNotContain("test-refresh", saved);
        h.RefreshFailure = null;
        await h.SignInAsync(result.Account.Id);
        Assert.Equal("oaiapp_test_1", h.Authorizations[1]["client_id"]);
    }

    [Fact]
    public async Task Temporary_Refresh_Failure_Preserves_Credentials()
    {
        using var h = new Harness { InitialLifetime = 1, RefreshFailure = "temporary" };
        var result = await h.SignInAsync();
        var saved = h.Secrets.Values[ChatGptAccountService.SecretName];
        await Assert.ThrowsAsync<ChatGptException>(() => h.Service.GetAccessTokenAsync(h.Http, result.Account.Id, default));
        Assert.Equal(saved, h.Secrets.Values[ChatGptAccountService.SecretName]);
        Assert.True(Assert.Single(await h.Service.ListAccountsAsync()).IsConnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sign_Out_Revokes_Or_Reports_Unconfirmed_Revocation_And_Retains_The_Client(bool unavailable)
    {
        using var h = new Harness { RevocationUnavailable = unavailable };
        var result = await h.SignInAsync();
        var lifetime = h.Service.GetSessionCancellation(result.Account.Id);
        var logout = await h.Service.SignOutAsync(h.Http, result.Account.Id);
        Assert.Equal(!unavailable, logout.RevocationConfirmed);
        Assert.True(lifetime.IsCancellationRequested);
        Assert.Equal(unavailable ? 3 : 1, h.RevocationForms.Count);
        Assert.Equal("test-refresh-1", h.RevocationForms[0]["token"]);
        Assert.Equal("refresh_token", h.RevocationForms[0]["token_type_hint"]);
        Assert.False(Assert.Single(await h.Service.ListAccountsAsync()).IsConnected);
        Assert.DoesNotContain("test-refresh", h.Secrets.Values[ChatGptAccountService.SecretName]);
        await h.SignInAsync(result.Account.Id);
        Assert.Equal("oaiapp_test_1", h.Authorizations[1]["client_id"]);
        Assert.False(h.Authorizations[1].ContainsKey("id_token_hint"));
        Assert.False(h.Service.GetSessionCancellation(result.Account.Id).IsCancellationRequested);
    }

    [Fact]
    public async Task Quota_Error_Pauses_Only_That_Account_Until_The_User_Resumes()
    {
        using var h = new Harness();
        var a = await h.SignInAsync(); var b = await h.SignInAsync();
        using var json = JsonDocument.Parse("""{"error":{"code":"subscription_sharing_usage_limit_exceeded"}}""");
        h.Service.ReportInferenceFailure(a.Account.Id, ChatGptException.FromError(json.RootElement));
        Assert.True((await h.Service.ListAccountsAsync()).Single(x => x.Id == a.Account.Id).UsagePaused);
        await Assert.ThrowsAsync<ChatGptException>(() => h.Service.GetAccessTokenAsync(h.Http, a.Account.Id, default));
        Assert.Equal("test-access-2", await h.Service.GetAccessTokenAsync(h.Http, b.Account.Id, default));
        h.Service.ResumeUsage(a.Account.Id);
        Assert.Equal("test-access-1", await h.Service.GetAccessTokenAsync(h.Http, a.Account.Id, default));
    }

    private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Dictionary<string, string> Parse(string text) => text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')),
            p => Uri.UnescapeDataString(p[1].Replace('+', ' ')), StringComparer.Ordinal);

    private sealed class MemorySecrets : ISecretStore
    {
        internal ConcurrentDictionary<string, string> Values { get; } = new();
        public Task<string?> GetAsync(string name, CancellationToken token = default) => Task.FromResult(Values.GetValueOrDefault(name));
        public Task SetAsync(string name, string value, CancellationToken token = default) { Values[name] = value; return Task.CompletedTask; }
        public Task DeleteAsync(string name, CancellationToken token = default) { Values.TryRemove(name, out _); return Task.CompletedTask; }
    }

    private sealed class Harness : HttpMessageHandler
    {
        private readonly RSA _rsa = RSA.Create(2048);
        private Task _callback = Task.CompletedTask;
        private int _registrations, _codes;
        private readonly Dictionary<string, (string Client, Dictionary<string, string> Parameters)> _attempts = new();
        internal MemorySecrets Secrets { get; } = new();
        internal HttpClient Http { get; }
        internal ChatGptAccountService Service { get; }
        internal List<Dictionary<string, string>> Authorizations { get; } = [];
        internal List<Dictionary<string, string>> TokenForms { get; } = [];
        internal List<Dictionary<string, string>> RevocationForms { get; } = [];
        internal string GrantedScopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
        internal string Subject = "test-user";
        internal string? CallbackFailure, IdentityFailure, RefreshFailure;
        internal int InitialLifetime = 3600;
        internal bool RevocationUnavailable;

        internal Harness()
        {
            Http = new(this, disposeHandler: false);
            Service = new(Secrets, OpenBrowser);
        }
        internal async Task<ChatGptSignInResult> SignInAsync(string? id = null, bool consent = false)
        {
            try { return await Service.SignInAsync(Http, id, consent); }
            finally { await _callback; }
        }
        private void OpenBrowser(Uri uri)
        {
            Assert.Equal("auth.openai.com", uri.Host);
            Assert.Equal("/api/accounts/authorize", uri.AbsolutePath);
            var parameters = Parse(uri.Query);
            Authorizations.Add(parameters);
            var client = parameters["client_id"] == "dynamic_agent_client" ? "oaiapp_test_" + ++_registrations : parameters["client_id"];
            var code = "code-" + ++_codes;
            _attempts[code] = (client, parameters);
            var callback = new Dictionary<string, string> { ["code"] = code, ["state"] = parameters["state"], ["client_id"] = client };
            if (CallbackFailure == "invalid_state") callback["state"] = "wrong-state";
            if (CallbackFailure == "missing_client") callback.Remove("client_id");
            if (CallbackFailure == "access_denied") callback["error"] = "access_denied";
            var query = string.Join('&', callback.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
            if (CallbackFailure == "duplicate_state") query += "&state=duplicate";
            _callback = SendCallbackAsync(parameters["redirect_uri"] + "?" + query);
        }
        private static async Task SendCallbackAsync(string uri)
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
            try { using var response = await http.GetAsync(uri); Assert.Equal(HttpStatusCode.OK, response.StatusCode); }
            catch (HttpRequestException) { /* A rejected duplicate callback can close before the test browser gets a response. */ }
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("auth.openai.com", request.RequestUri!.Host);
            switch (request.RequestUri.AbsolutePath)
            {
                case "/api/accounts/oauth/token":
                    var fields = Parse(await request.Content!.ReadAsStringAsync(token)); TokenForms.Add(fields);
                    if (fields["grant_type"] == "refresh_token")
                    {
                        await Task.Delay(20, token);
                        if (RefreshFailure is { } failure) return Json(failure == "temporary" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest, new { error = failure });
                        return Json(HttpStatusCode.OK, new { access_token = "test-access-refreshed", refresh_token = "test-refresh-rotated",
                            token_type = "Bearer", expires_in = 3600, scope = GrantedScopes });
                    }
                    var attempt = _attempts[fields["code"]];
                    Assert.Equal(attempt.Client, fields["client_id"]);
                    return Json(HttpStatusCode.OK, new { access_token = "test-access-" + _codes, refresh_token = "test-refresh-" + _codes,
                        id_token = IdentityToken(attempt.Client, attempt.Parameters["nonce"]), token_type = "Bearer", expires_in = InitialLifetime, scope = GrantedScopes });
                case "/.well-known/jwks.json":
                    var key = _rsa.ExportParameters(false);
                    return Json(HttpStatusCode.OK, new { keys = new[] { new { kty = "RSA", use = "sig", kid = "test-key", alg = "RS256", n = B64(key.Modulus!), e = B64(key.Exponent!) } } });
                case "/.well-known/openid-configuration":
                    return Json(HttpStatusCode.OK, new { revocation_endpoint = "https://auth.openai.com/api/accounts/oauth/revoke" });
                case "/api/accounts/oauth/revoke":
                    RevocationForms.Add(Parse(await request.Content!.ReadAsStringAsync(token)));
                    return new(RevocationUnavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent("") };
                default: throw new InvalidOperationException("Unexpected OAuth endpoint.");
            }
        }
        private string IdentityToken(string client, string nonce)
        {
            using var other = RSA.Create(2048);
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = IdentityFailure == "issuer" ? "https://untrusted.example" : "https://auth.openai.com",
                Audience = IdentityFailure == "audience" ? "another-client" : client,
                Claims = new Dictionary<string, object> { ["sub"] = Subject, ["email"] = "test@example.com", ["nonce"] = IdentityFailure == "nonce" ? "wrong-nonce" : nonce },
                NotBefore = DateTime.UtcNow.AddHours(-2), IssuedAt = DateTime.UtcNow.AddHours(-2),
                Expires = IdentityFailure == "expiry" ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddHours(1),
                SigningCredentials = new(new RsaSecurityKey(IdentityFailure == "signature" ? other : _rsa) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256),
            });
        }
        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing) { if (disposing) { Http.Dispose(); _rsa.Dispose(); } base.Dispose(disposing); }
    }
}
