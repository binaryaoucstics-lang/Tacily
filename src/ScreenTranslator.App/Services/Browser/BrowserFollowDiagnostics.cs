using System.Globalization;
using System.IO;

namespace ScreenTranslator.App.Services.Browser;

public static class BrowserFollowDiagnostics
{
    private const long MaximumLogBytes = 1024 * 1024;
    private static readonly object SyncRoot = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTranslator",
        "browser-follow.log");

    public static void Write(
        string eventName,
        params (string Key, object? Value)[] fields)
    {
        try
        {
            var values = fields.Select(field =>
                $"{Normalize(field.Key)}={Normalize(field.Value)}");
            var line = string.Join(
                '\t',
                new[]
                {
                    DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                    Normalize(eventName),
                }.Concat(values));

            lock (SyncRoot)
            {
                var directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var append = !File.Exists(LogPath)
                             || new FileInfo(LogPath).Length < MaximumLogBytes;
                if (append)
                {
                    File.AppendAllText(
                        LogPath,
                        line + Environment.NewLine,
                        System.Text.Encoding.UTF8);
                }
                else
                {
                    File.WriteAllText(
                        LogPath,
                        line + Environment.NewLine,
                        System.Text.Encoding.UTF8);
                }
            }
        }
        catch
        {
            // Diagnostics must never interrupt translation.
        }
    }

    private static string Normalize(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture)?
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
        ?? string.Empty;
}
