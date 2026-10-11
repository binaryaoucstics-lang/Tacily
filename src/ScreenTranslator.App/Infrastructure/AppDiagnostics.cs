using System.IO;

namespace ScreenTranslator.App.Infrastructure;

public static class AppDiagnostics
{
    private static readonly object Gate = new();
    public static string LogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GlobalAITranslator", "logs");
    public static void Write(string stage)
    {
        try
        {
            lock (Gate)
            {
                foreach (var directory in new[] { LogDirectory,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlobalAITranslator") })
                {
                    try
                    {
                        Directory.CreateDirectory(directory);
                        var path = Path.Combine(directory, "diagnostics.log");
                        if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                            File.Move(path, Path.Combine(directory, "diagnostics.previous.log"), overwrite: true);
                        File.AppendAllText(path, $"{DateTimeOffset.Now:O} {stage}\n");
                    }
                    catch (Exception) { }
                }
            }
        }
        catch (Exception) { /* A diagnostic failure must never interrupt translation. */ }
    }
}
