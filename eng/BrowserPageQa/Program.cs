using System.Text;
using System.Text.Json;
using System.IO;

namespace BrowserPageQa;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.SequenceEqual(new[] { "--self-test" })) return await SelfTest.RunAsync();
        var useRunningPreset = args.SequenceEqual(new[] { "--use-running-app-preset" });
        var pinnedPreset = args.Length == 2 && args[0] == "--saved-preset-snapshot";
        var snapshotPath = args.Length == 2 && (args[0] == "--running-preset-snapshot" || pinnedPreset) ? args[1] : null;
        if (args.Length != 0 && !useRunningPreset && snapshotPath is null)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, id = "", caseId = "", type = "error",
                code = "invalid_arguments", message = "Use live JSONL, --use-running-app-preset, --running-preset-snapshot PATH, --saved-preset-snapshot PATH, or --self-test." }));
            return 2;
        }
        using var runtime = new LiveRuntime();
        await using var host = new JsonlHost(true, () => LiveRuntime.LoadSettingsAsync(useRunningPreset, snapshotPath, pinnedPreset), runtime.CreateProvider, Console.Out);
        // Console.In wraps a synchronous reader. Once an async continuation owns
        // its thread, waiting for the next line can delay queued translation work
        // until another input arrives. Use the underlying UTF-8 stream directly.
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
        ConsoleCancelEventHandler stop = (_, e) => { e.Cancel = true; host.CancelAll(); };
        Console.CancelKeyPress += stop;
        try { await host.RunAsync(input); return 0; }
        catch (Exception)
        {
            // Never write exception messages, paths, response bodies or configuration to either stream.
            Console.Error.WriteLine("BrowserPageQa: transport_failed");
            return 1;
        }
        finally { Console.CancelKeyPress -= stop; }
    }
}
