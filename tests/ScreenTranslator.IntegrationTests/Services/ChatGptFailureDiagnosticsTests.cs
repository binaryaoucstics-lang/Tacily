using System.Net;
using ScreenTranslator.App.Services;
using ScreenTranslator.Core.Authentication;

namespace ScreenTranslator.IntegrationTests.Services;

// Only Format is exercised: no logging, controller, account store, network or user files.
public sealed class ChatGptFailureDiagnosticsTests
{
    [Fact]
    public void Json_Input_Error_Preserves_Status_Parameter_Shape_And_RequestId_Without_Message()
    {
        var error = new ChatGptException(
            "Response input messages must contain the word 'json' in some form to use 'text.format' of type 'json_object'.",
            status: HttpStatusCode.BadRequest, requestId: "req_0123456789abcdef0123456789abcdef",
            parameter: "input", responseShape: "error");

        Assert.Equal("chatgpt_failure stage=translation status=400 code=unknown param=input shape=error request_id=req_0123456789abcdef0123456789abcdef",
            ChatGptFailureDiagnostics.Format("translation", error));
    }

    [Theory]
    [InlineData("translation")]
    [InlineData("connection_test")]
    [InlineData("initialize")]
    [InlineData("sign_in")]
    [InlineData("sign_out")]
    [InlineData("refresh_state")]
    public void Known_Stages_Identify_Failures_Without_Reading_Unknown_Exception_Properties(string stage)
    {
        var error = new SensitiveException();
        error.Data["access_token"] = "fake-access-token";
        Assert.Equal($"chatgpt_failure stage={stage} status=unknown code=unknown param=unknown shape=unknown request_id=unknown",
            ChatGptFailureDiagnostics.Format(stage, error));
    }

    [Fact]
    public void Unknown_Exception_Does_Not_Unwrap_Inner_Metadata_Or_Print_The_Type()
    {
        var inner = new ChatGptException("fake-refresh-token", "invalid_grant", HttpStatusCode.Unauthorized,
            "req_123", "input", "error");
        var result = ChatGptFailureDiagnostics.Format("sign_in", new InvalidOperationException("fake-account-email", inner));
        Assert.Equal("chatgpt_failure stage=sign_in status=unknown code=unknown param=unknown shape=unknown request_id=unknown", result);
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("invalid_refresh_token")]
    [InlineData("token_expired")]
    [InlineData("refresh_token_expired")]
    [InlineData("refresh_token_invalidated")]
    [InlineData("refresh_token_reused")]
    [InlineData("invalid_saved_registration")]
    [InlineData("invalid_callback")]
    [InlineData("invalid_state")]
    [InlineData("account_mismatch")]
    [InlineData("sign_in_timeout")]
    [InlineData("sign_in_required")]
    [InlineData("plan_permission_required")]
    [InlineData("chatpass_v2_scope_not_authorized")]
    [InlineData("subscription_sharing_user_not_eligible")]
    [InlineData("subscription_sharing_usage_limit_exceeded")]
    [InlineData("rate_limit_exceeded")]
    [InlineData("insufficient_quota")]
    public void Recognized_Error_Codes_Are_Metadata_Not_Credential_Values(string code)
    {
        var result = ChatGptFailureDiagnostics.Format("sign_in", new ChatGptException("do not log", code));
        Assert.Contains($" code={code} ", result);
        Assert.DoesNotContain("do not log", result);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("input[0]")]
    [InlineData("input[0].content[1].text")]
    [InlineData("input[123456].content[0].image_url")]
    [InlineData("text.format")]
    [InlineData("text.format.type")]
    [InlineData("text.format.schema")]
    [InlineData("reasoning.effort")]
    [InlineData("model")]
    [InlineData("max_output_tokens")]
    public void Protocol_Parameter_Paths_Remain_Useful(string parameter)
    {
        var result = ChatGptFailureDiagnostics.Format("connection_test", new ChatGptException("do not log", parameter: parameter));
        Assert.Contains($" param={parameter} ", result);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("detail")]
    [InlineData("non_json")]
    [InlineData("response_too_large")]
    [InlineData("response.failed")]
    [InlineData("response.incomplete")]
    [InlineData("sse_error")]
    public void Recognized_Shapes_Are_Preserved(string shape)
    {
        var result = ChatGptFailureDiagnostics.Format("translation", new ChatGptException("do not log", responseShape: shape));
        Assert.Contains($" shape={shape} ", result);
    }

    [Theory]
    [InlineData("req_123")]
    [InlineData("req_abcDEF0123456789")]
    [InlineData("req_0123456789abcdef0123456789abcdef")]
    [InlineData("123e4567-e89b-12d3-a456-426614174000")]
    [InlineData("123E4567-E89B-12D3-A456-426614174000")]
    public void Request_Id_Namespace_And_Canonical_Uuids_Can_Be_Correlated(string requestId)
    {
        var result = ChatGptFailureDiagnostics.Format("translation", new ChatGptException("do not log", requestId: requestId));
        Assert.EndsWith($" request_id={requestId}", result);
    }

    public static IEnumerable<object?[]> UnsafeFields()
    {
        foreach (var value in new string?[]
        {
            null, "", "sk-proj-FAKEKEY0123456789", "sk-FAKEKEY0123456789", "sess-FAKESESSION0123456789",
            "eyJhbGciOiJub25lIn0.eyJzdWIiOiJmYWtlIn0.signature", "eyJhbGciOiJub25lIn0",
            "FAKEOPAQUEACCESS0123456789abcdefgh", "fake_access_token", "fake_refresh_token", "fake_id_token",
            "Bearer_fake_token", "ghp_FAKE0123456789", "fake@example.invalid", "account_0123456789",
            "https://example.invalid/account", "input\nforged_log", "input\r\nforged_log", "input\t",
            "input\0", "input\u001b", "input\u2028", "input\u2029", " input", "input ", "账户", new string('a', 121),
            new string('a', 10000), "future_unverified_field",
        }) yield return [value];
    }

    [Theory]
    [MemberData(nameof(UnsafeFields))]
    public void Credential_Shapes_Control_Characters_And_Unknown_Fields_Fail_Closed_In_Every_Field(string? value)
    {
        var error = new ChatGptException("fake-body-and-translation", value, HttpStatusCode.BadRequest,
            value, value, value!);
        var result = ChatGptFailureDiagnostics.Format(value, error);
        Assert.Equal("chatgpt_failure stage=unknown status=400 code=unknown param=unknown shape=unknown request_id=unknown", result);
    }

    [Theory]
    [InlineData("input.fake_access_token")]
    [InlineData("text.format.schema.fake_secret")]
    [InlineData("input[1234567].content")]
    [InlineData("input[-1].content")]
    [InlineData("input[0][1]")]
    [InlineData("input..text")]
    [InlineData("input/text")]
    [InlineData("account.id")]
    [InlineData("email")]
    [InlineData("access_token")]
    public void Parameter_Paths_Reject_Arbitrary_Keys_And_Malformed_Indices(string parameter)
    {
        Assert.Contains(" param=unknown ", ChatGptFailureDiagnostics.Format("translation",
            new ChatGptException("do not log", parameter: parameter)));
    }

    [Theory]
    [InlineData("req_skFAKEKEY0123456789")]
    [InlineData("req_skFake123")]
    [InlineData("req_eyJfake")]
    [InlineData("req_tokenFake")]
    [InlineData("req_bearerFake")]
    [InlineData("req_eyJhbGciOiJub25lIn0")]
    [InlineData("req_accessFAKE0123456789")]
    [InlineData("req_refreshFAKE0123456789")]
    [InlineData("req_idtokenFAKE0123456789")]
    [InlineData("req_bearerFAKE0123456789")]
    [InlineData("req_ghpFAKE0123456789")]
    [InlineData("req_secretFAKE0123456789")]
    [InlineData("req_FAKEOPAQUE0123456789abcdefgh")]
    [InlineData("req_FAKE_ACCESS_TOKEN")]
    [InlineData("req_abc\n")]
    [InlineData("123e4567-e89b-12d3-a456-426614174000\n")]
    [InlineData("{123e4567-e89b-12d3-a456-426614174000}")]
    [InlineData("123e4567e89b12d3a456426614174000")]
    public void Request_Id_Rejects_Namespaced_Credentials_And_Noncanonical_Values(string requestId)
    {
        Assert.EndsWith(" request_id=unknown", ChatGptFailureDiagnostics.Format("translation",
            new ChatGptException("do not log", requestId: requestId)));
    }

    [Fact]
    public void Request_Id_Length_Bound_Is_Enforced()
    {
        var accepted = "req_" + new string('a', 64);
        var rejected = "req_" + new string('a', 65);
        Assert.EndsWith($" request_id={accepted}", ChatGptFailureDiagnostics.Format("translation", new ChatGptException("", requestId: accepted)));
        Assert.EndsWith(" request_id=unknown", ChatGptFailureDiagnostics.Format("translation", new ChatGptException("", requestId: rejected)));
    }

    [Theory]
    [InlineData(100, "100")]
    [InlineData(400, "400")]
    [InlineData(429, "429")]
    [InlineData(599, "599")]
    [InlineData(0, "unknown")]
    [InlineData(99, "unknown")]
    [InlineData(600, "unknown")]
    [InlineData(int.MaxValue, "unknown")]
    [InlineData(-1, "unknown")]
    public void Only_Http_Status_Numbers_Are_Emitted(int status, string expected)
    {
        var result = ChatGptFailureDiagnostics.Format("translation", new ChatGptException("", status: (HttpStatusCode)status));
        Assert.Contains($" status={expected} ", result);
    }

    [Fact]
    public void Missing_Error_Is_Formatted_Without_Exceptions_Or_Content()
    {
        Assert.Equal("chatgpt_failure stage=unknown status=unknown code=unknown param=unknown shape=unknown request_id=unknown",
            ChatGptFailureDiagnostics.Format(null, null));
    }

    private sealed class SensitiveException : Exception
    {
        public override string Message => throw new InvalidOperationException("Diagnostics read a sensitive message.");
        public override string ToString() => throw new InvalidOperationException("Diagnostics read sensitive exception details.");
    }
}
