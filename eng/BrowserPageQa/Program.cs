using System.Text;
using System.Text.Json;

namespace BrowserPageQa;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.SequenceEqual(new[] { "--self-test" })) return await SelfTest.RunAsync();
        var useRunningPreset = args.SequenceEqual(new[] { "--use-running-app-preset" });
        if (args.Length != 0 && !useRunningPreset)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, id = "", caseId = "", type = "error",
                code = "invalid_arguments", message = "Use live JSONL, --use-running-app-preset, or --self-test." }));
            return 2;
        }
        using var runtime = new LiveRuntime();
        await using var host = new JsonlHost(true, () => LiveRuntime.LoadSettingsAsync(useRunningPreset), runtime.CreateProvider, Console.Out);
        ConsoleCancelEventHandler stop = (_, e) => { e.Cancel = true; host.CancelAll(); };
        Console.CancelKeyPress += stop;
        try { await host.RunAsync(Console.In); return 0; }
        catch (Exception)
        {
            // Never write exception messages, paths, response bodies or configuration to either stream.
            Console.Error.WriteLine("BrowserPageQa: transport_failed");
            return 1;
        }
        finally { Console.CancelKeyPress -= stop; }
    }
}
