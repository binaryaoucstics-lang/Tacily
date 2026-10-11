using System.Text.RegularExpressions;
using ScreenTranslator.App.Infrastructure;
using ScreenTranslator.Core.Authentication;

namespace ScreenTranslator.App.Services;

/// <summary>Logs request metadata only, without request/response text or credentials.</summary>
public static class ChatGptFailureDiagnostics
{
    public static string Format(string stage, ChatGptException error) =>
        $"chatgpt_failure stage={Safe(stage)} status={(int?)error.StatusCode} code={Safe(error.Code)} param={Safe(error.Parameter)} shape={Safe(error.ResponseShape)} request_id={Safe(error.RequestId)}";

    public static void Write(string stage, Exception error)
    {
        if (error is ChatGptException chatGpt) AppDiagnostics.Write(Format(stage, chatGpt));
    }

    private static string Safe(string? value) => value is not null && value.Length <= 120
        && Regex.IsMatch(value, @"\A[A-Za-z0-9_.:/\[\]-]+\z", RegexOptions.CultureInvariant) ? value : "unknown";
}
