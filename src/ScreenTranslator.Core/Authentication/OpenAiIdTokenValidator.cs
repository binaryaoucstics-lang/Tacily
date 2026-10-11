using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ScreenTranslator.Core.Authentication;

internal sealed record OpenAiIdentity(string Issuer, string Subject, string? Email, string? Name);

internal static class OpenAiIdTokenValidator
{
    internal static async Task<OpenAiIdentity> ValidateAsync(HttpClient http, string idToken, string clientId,
        string? nonce, CancellationToken token)
    {
        using var response = await http.GetAsync(ChatGptProtocol.Issuer + "/.well-known/jwks.json", token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await ChatGptException.FromResponseAsync(response, token).ConfigureAwait(false);
        using var json = await ChatGptProtocol.ReadJsonAsync(response, token).ConfigureAwait(false);
        var keys = new JsonWebKeySet(json.RootElement.GetRawText()).GetSigningKeys();
        var handler = new JsonWebTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 64 * 1024 };
        var result = await handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = ChatGptProtocol.Issuer,
            ValidateAudience = true, ValidAudience = clientId,
            ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(30),
            RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        }).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt
            || !jwt.TryGetPayloadValue<string>("sub", out var subject) || string.IsNullOrWhiteSpace(subject)
            || nonce is not null && (!jwt.TryGetPayloadValue<string>("nonce", out var actualNonce) || actualNonce != nonce))
            throw new ChatGptException("无法验证 ChatGPT 登录身份，请重新登录。", "invalid_id_token");
        jwt.TryGetPayloadValue<string>("email", out var email);
        jwt.TryGetPayloadValue<string>("name", out var name);
        return new(jwt.Issuer, subject, email, name);
    }
}
