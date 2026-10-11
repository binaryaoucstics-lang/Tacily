using System.Globalization;
using System.Text.RegularExpressions;
using ScreenTranslator.App.Infrastructure;
using ScreenTranslator.Core.Authentication;

namespace ScreenTranslator.App.Services;

/// <summary>Logs request metadata only, without request/response text or credentials.</summary>
public static class ChatGptFailureDiagnostics
{
    // Parameter names are protocol vocabulary, never arbitrary server strings or schema keys.
    private static readonly Regex ParameterPath = new(
        @"\A(?:input(?:\[[0-9]{1,6}\])?(?:\.(?:role|content|type|text|image_url|detail)(?:\[[0-9]{1,6}\])?){0,6}|text(?:\.format(?:\.(?:type|name|schema|strict))?)?|reasoning(?:\.(?:effort|summary))?|model|instructions|stream|store|max_output_tokens|temperature|top_p|tools|tool_choice|parallel_tool_calls|previous_response_id|truncation|service_tier|include)\z",
        // The symbolic NonBacktracking engine in .NET 8 expands the bounded paths
        // beyond its automaton limit. Inputs are bounded before this timed match.
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex RequestId = new(@"\Areq_(?:[A-Za-z0-9]{1,16}|[A-Fa-f0-9]{17,64})\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Pure formatting; does not inspect messages, inner exceptions, data or user files.</summary>
    public static string Format(string? stage, Exception? error)
    {
        var chatGpt = error as ChatGptException;
        var status = (int?)chatGpt?.StatusCode;
        return $"chatgpt_failure stage={SafeStage(stage)} status={(status is >= 100 and <= 599 ? status.Value.ToString(CultureInfo.InvariantCulture) : "unknown")} code={SafeCode(chatGpt?.Code)} param={SafeParameter(chatGpt?.Parameter)} shape={SafeShape(chatGpt?.ResponseShape)} request_id={SafeRequestId(chatGpt?.RequestId)}";
    }

    public static void Write(string stage, Exception error)
    {
        // The shared translation entry also handles other providers. Preserve its OAuth-only scope.
        if (error is not ChatGptException && (stage is "translation" or "connection_test")) return;
        try { AppDiagnostics.Write(Format(stage, error)); }
        catch (Exception) { /* Diagnostics must not change translation or credential outcomes. */ }
    }

    private static string SafeStage(string? value) => value switch
    {
        "translation" or "connection_test" or "initialize" or "sign_in" or "sign_out" or "refresh_state" => value,
        _ => "unknown",
    };

    // A character regex alone admits opaque access tokens. Unknown codes must fail closed.
    private static string SafeCode(string? value) => value is not { Length: > 0 and <= 64 } ? "unknown" : value switch
    {
        "subscription_sharing_usage_limit_exceeded" or "subscription_sharing_user_not_eligible"
            or "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable"
            or "subscription_sharing_unsupported_capability" or "subscription_sharing_route_not_supported"
            or "chatpass_v2_scope_not_authorized" or "chatpass_v2_invalid_authorization_context"
            or "subscription_sharing_invalid_user"
            or "invalid_grant" or "invalid_refresh_token" or "token_expired" or "refresh_token_expired"
            or "refresh_token_invalidated" or "refresh_token_reused" or "invalid_client" or "access_denied"
            or "invalid_callback" or "invalid_state" or "missing_code" or "missing_id_token"
            or "account_mismatch" or "sign_in_timeout" or "sign_in_required" or "plan_permission_required"
            or "invalid_saved_registration" or "invalid_token_response" or "missing_scope"
            or "invalid_request" or "unauthorized_client" or "unsupported_response_type" or "invalid_scope"
            or "server_error" or "temporarily_unavailable" or "invalid_request_error" or "invalid_value"
            or "invalid_parameter" or "unsupported_parameter" or "model_not_found"
            or "rate_limit_exceeded" or "insufficient_quota" or "context_length_exceeded"
            or "content_policy_violation" or "invalid_api_key" or "billing_hard_limit_reached" => value,
        _ => "unknown",
    };

    private static string SafeShape(string? value) => value switch
    {
        "error" or "detail" or "non_json" or "response_too_large" or "response.failed" or "response.incomplete" or "sse_error" => value,
        _ => "unknown",
    };

    private static string SafeParameter(string? value)
    {
        if (value is not { Length: > 0 and <= 120 }) return "unknown";
        try { return ParameterPath.IsMatch(value) ? value : "unknown"; }
        catch (RegexMatchTimeoutException) { return "unknown"; }
    }

    private static string SafeRequestId(string? value)
    {
        if (value is not { Length: > 0 and <= 68 }) return "unknown";
        // Canonical UUIDs and bounded req_ identifiers only; long opaque values must be hexadecimal.
        if (Guid.TryParseExact(value, "D", out var id)
            && string.Equals(value, id.ToString("D"), StringComparison.OrdinalIgnoreCase)) return value;
        if (!RequestId.IsMatch(value)) return "unknown";
        var suffix = value.AsSpan(4);
        // Reject credential prefixes even when someone prepends the request-id namespace.
        foreach (var prefix in new[] { "eyJ", "sk", "sess", "ghp", "gho", "ghu", "ghs", "ghr", "github", "token", "secret", "access", "refresh", "idtoken", "bearer" })
            if (suffix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "unknown";
        return value;
    }
}
