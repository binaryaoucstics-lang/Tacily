using System.Net;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Core.Authentication;

namespace ScreenTranslator.Core.Tests.Authentication;

public sealed class ChatGptErrorTests
{
    [Theory]
    [InlineData("input\t")]
    [InlineData("input\0")]
    [InlineData(" input")]
    [InlineData("input\u2028")]
    public void Malformed_Metadata_Is_Not_Normalized_Into_Valid_Log_Identifiers(string value)
    {
        var error = new ChatGptException("Safe display text", value, requestId: value,
            parameter: value, responseShape: value, errorType: value);
        Assert.Null(error.Code); Assert.Null(error.RequestId); Assert.Null(error.Parameter);
        Assert.Null(error.ErrorType); Assert.Equal("unknown", error.ResponseShape);
    }
    [Theory]
    [InlineData("x-oai-request-id")]
    [InlineData("x-request-id")]
    [InlineData("openai-request-id")]
    public async Task Standard_Error_With_Null_Code_Preserves_Actual_Reason_And_Metadata(string header)
    {
        const string reason = "Response input messages must contain the word 'json' in some form to use 'text.format' of type 'json_object'.";
        using var response = Response(JsonSerializer.Serialize(new { error = new
            { message = reason, type = "invalid_request_error", code = (string?)null, param = "input" } }), HttpStatusCode.BadRequest, header);
        var error = await ChatGptException.FromResponseAsync(response, default);
        Assert.Null(error.Code);
        Assert.Equal(reason, error.ServerMessage);
        Assert.Equal("invalid_request_error", error.ErrorType);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal("input", error.Parameter);
        Assert.Equal("request-test", error.RequestId);
        Assert.Equal("error", error.ResponseShape);
        Assert.Contains(reason, error.Message);
        Assert.Contains("HTTP 400", error.Message);
        Assert.Contains("参数 input", error.Message);
    }

    [Theory]
    [InlineData("""{"detail":"Model is unavailable in this workspace"}""", "Model is unavailable in this workspace")]
    [InlineData("""{"detail":{"message":"Input validation failed","type":"invalid_request_error","param":"input"}}""", "Input validation failed")]
    [InlineData("""{"detail":[{"msg":"Field is required"},{"message":"Image cannot be decoded"}]}""", "Field is required; Image cannot be decoded")]
    public async Task Detail_Failures_Display_Their_Specific_Reason(string body, string reason)
    {
        using var response = Response(body, HttpStatusCode.Forbidden);
        var error = await ChatGptException.FromResponseAsync(response, default);
        Assert.Equal("detail", error.ResponseShape);
        Assert.Equal(reason, error.Detail);
        Assert.Contains(reason, error.Message);
        Assert.Contains("HTTP 403", error.Message);
        if (body.Contains("invalid_request_error"))
        {
            Assert.Equal("invalid_request_error", error.ErrorType);
            Assert.Equal("input", error.Parameter);
        }
    }

    [Fact]
    public async Task Official_Request_Id_Takes_Precedence_Over_Older_Headers()
    {
        using var response = Response("""{"error":{"message":"Bad input"}}""", HttpStatusCode.BadRequest);
        response.Headers.Add("x-oai-request-id", "official-id");
        response.Headers.Add("openai-request-id", "older-id");
        var error = await ChatGptException.FromResponseAsync(response, default);
        Assert.Equal("official-id", error.RequestId);
    }

    [Theory]
    [InlineData("subscription_sharing_usage_limit_exceeded", "恢复翻译")]
    [InlineData("chatpass_v2_scope_not_authorized", "重新授权")]
    [InlineData("invalid_grant", "重新登录")]
    [InlineData("invalid_refresh_token", "重新登录")]
    public void Known_Recovery_Guidance_Is_Preserved_With_Server_Reason(string code, string guidance)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { error = new
            { code, message = "Workspace restriction", type = "permission_error", param = "model" } }));
        var error = ChatGptException.FromError(json.RootElement, HttpStatusCode.Forbidden, "request-test");
        Assert.Equal(code, error.Code);
        Assert.Contains(guidance, error.Message);
        Assert.Contains("Workspace restriction", error.Message);
        Assert.Contains("HTTP 403", error.Message);
        Assert.Equal(code == "subscription_sharing_usage_limit_exceeded", error.IsUsageLimit);
        Assert.Equal(code is "invalid_grant" or "invalid_refresh_token", error.IsTerminalRefreshError);
    }

    [Fact]
    public async Task OAuth_String_Error_Preserves_Error_Description()
    {
        using var response = Response("""{"error":"invalid_grant","error_description":"Refresh token was revoked"}""", HttpStatusCode.BadRequest);
        var error = await ChatGptException.FromResponseAsync(response, default);
        Assert.True(error.IsTerminalRefreshError);
        Assert.Equal("Refresh token was revoked", error.ServerMessage);
        Assert.Contains("重新登录", error.Message);
        Assert.Contains("Refresh token was revoked", error.Message);
    }

    [Theory]
    [InlineData("Gateway denied this route", "Gateway denied this route")]
    [InlineData("", "无法识别")]
    public async Task Non_Json_Error_Retains_Status_Request_Id_And_Safe_Reason(string body, string expected)
    {
        using var response = Response(body, HttpStatusCode.BadGateway, "x-oai-request-id");
        var error = await ChatGptException.FromResponseAsync(response, default);
        Assert.Equal("non_json", error.ResponseShape);
        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Equal("request-test", error.RequestId);
        Assert.Contains("HTTP 502", error.Message);
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("detail")]
    [InlineData("non_json")]
    [InlineData("sse.failed")]
    [InlineData("sse.error")]
    public async Task All_Error_Shapes_Remove_Fake_Credentials_Jwt_Keys_And_Control_Characters(string shape)
    {
        const string diagnostic = "Invalid input\r\nBearer bearer-fake; Basic basic-fake; sk-proj-fakeKey123; sess-fakeSession; "
            + "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJmYWtlIn0.fakeSignature; access_token=access-fake; "
            + "refresh_token='refresh-fake'; \"id_token\":\"id-fake\"; client_secret=client-fake; "
            + "api_key=api-fake; password=password-fake; credential=credential-fake; "
            + "https://user-fake:pass-fake@example.invalid/\u0000\u001b\u202E";
        var serviceError = new { message = diagnostic, detail = diagnostic, code = diagnostic, type = diagnostic, param = diagnostic };
        var body = shape switch
        {
            "detail" => JsonSerializer.Serialize(new { detail = diagnostic }),
            "non_json" => diagnostic,
            "sse.failed" => JsonSerializer.Serialize(new { type = "response.failed", response = new { error = serviceError } }),
            "sse.error" => JsonSerializer.Serialize(new { type = "error", error = serviceError }),
            _ => JsonSerializer.Serialize(new { error = serviceError }),
        };
        ChatGptException error;
        if (shape.StartsWith("sse"))
        {
            using var json = JsonDocument.Parse(body);
            error = ChatGptException.FromError(shape == "sse.failed" ? json.RootElement.GetProperty("response") : json.RootElement,
                HttpStatusCode.OK, diagnostic);
        }
        else
        {
            using var response = Response(body, HttpStatusCode.BadRequest);
            error = await ChatGptException.FromResponseAsync(response, default);
        }
        var exposed = string.Join(" ", new[] { error.Message, error.ServerMessage, error.Detail, error.Code,
            error.ErrorType, error.Parameter, error.RequestId, error.ResponseShape });
        foreach (var secret in new[] { "bearer-fake", "basic-fake", "sk-proj-fakeKey123", "sess-fakeSession", "eyJhbGci",
            "fakeSignature", "access-fake", "refresh-fake", "id-fake", "client-fake", "api-fake", "password-fake",
            "credential-fake", "user-fake", "pass-fake" }) Assert.DoesNotContain(secret, exposed);
        Assert.DoesNotContain(exposed, char.IsControl);
        Assert.DoesNotContain('\u202E', exposed);
        Assert.Contains("Invalid input", error.Message);
        Assert.Contains("[redacted]", error.Message);
    }

    [Fact]
    public void Display_Bound_Does_Not_Expose_Part_Of_A_Credential()
    {
        var reason = new string('a', 480) + " sk-" + new string('z', 10000);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { error = new { message = reason } }));
        var error = ChatGptException.FromError(json.RootElement, HttpStatusCode.BadRequest);
        Assert.True(error.ServerMessage!.Length <= 512);
        Assert.True(error.Message.Length <= 1200);
        Assert.DoesNotContain("sk-", error.Message);
        Assert.DoesNotContain("zz", error.Message);
        Assert.Contains("HTTP 400", error.Message);
    }

    [Fact]
    public async Task Oversized_Error_Stops_At_The_Read_Budget_With_Metadata()
    {
        using var response = Response(new string('x', 2 * 1024 * 1024 + 1), HttpStatusCode.BadRequest);
        var error = await ChatGptException.FromResponseAsync(response, default);
        Assert.Equal("response_too_large", error.ResponseShape);
        Assert.Contains("HTTP 400", error.Message);
        Assert.Equal("request-test", error.RequestId);
        Assert.True(error.Message.Length <= 1200);
    }

    [Fact]
    public async Task Unknown_Json_Shape_Still_Preserves_Http_Metadata()
    {
        using var response = Response("[]", HttpStatusCode.ServiceUnavailable);
        var error = await ChatGptException.FromResponseAsync(response, default);
        Assert.Equal("unknown", error.ResponseShape);
        Assert.Contains("HTTP 503", error.Message);
        Assert.Contains("服务暂不可用", error.Message);
        Assert.Equal("request-test", error.RequestId);
    }

    private static HttpResponseMessage Response(string body, HttpStatusCode status, string header = "x-request-id")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        response.Headers.Add(header, "request-test");
        return response;
    }
}
