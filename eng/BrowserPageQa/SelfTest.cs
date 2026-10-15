using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace BrowserPageQa;

/// <summary>Only this isolated entry point constructs synthetic providers; never used by live composition.</summary>
internal static class SelfTest
{
    private const string Source = "The entrance is blue and the garden is quiet.";

    public static async Task<int> RunAsync()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("complete-stream-and-cache", CompleteAsync),
            ("assessment-and-skip-without-provider", AssessmentAsync),
            ("native-candidate-boundaries", InvalidAsync),
            ("id-scoped-cancellation", CancellationAsync),
            ("completed-message-cancel-is-case-scoped-and-idempotent", CompletedCancellationAsync),
            ("completion-cancel-race-emits-one-terminal", CompletionRaceAsync),
            ("bounded-admission-and-shutdown", CapacityAsync),
            ("interrupted-stream-is-failure", InterruptedAsync),
            ("live-factory-failure-never-falls-back", NoFallbackAsync),
            ("read-only-settings-migration-and-corruption", SettingsAsync),
            ("running-preset-scope-resolution", RunningPresetAsync),
            ("verified-running-snapshot-rejects-drift", SnapshotAsync),
            ("explicit-saved-snapshot-selects-only-supported-preset", PinnedSnapshotAsync),
            ("continuation-reuses-document-assessment", ContinuationAsync),
            ("bounded-jsonl-and-eof-drain", InputAsync),
            ("broken-output-cannot-exit-successfully", TransportAsync),
            ("diagnostics-opt-in-concurrent-attribution-and-privacy", DiagnosticsAsync),
            ("diagnostics-bounded-failures-and-broken-sink", DiagnosticBoundsAsync),
            ("diagnostics-observe-numeric-transfer-and-recovery", DiagnosticTransferAsync),
            ("diagnostics-format-recovery-keeps-successful-groups", DiagnosticFormatRecoveryAsync),
        };
        var failed = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine(JsonSerializer.Serialize(new { type = "selfTest", live = false, test = test.Name, success = true }));
            }
            catch
            {
                failed++;
                Console.WriteLine(JsonSerializer.Serialize(new { type = "selfTest", live = false, test = test.Name,
                    success = false, code = "assertion_failed" }));
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { type = "selfTestSummary", live = false,
            success = failed == 0, passed = tests.Length - failed, failed }));
        return failed == 0 ? 0 : 1;
    }

    private static async Task CompleteAsync()
    {
        var output = new StringWriter(); var created = 0;
        await using var host = Host(output, _ => { created++; return new SyntheticProvider(FinishAsync); });
        await host.HandleAsync(Request("cap", "capabilities"));
        Check(created == 0 && Events(output).Single().GetProperty("live").GetBoolean() == false);
        await host.HandleAsync(Candidate("first")); await host.DrainAsync();
        var events = Events(output).Where(e => Id(e) == "first").ToArray();
        Check(events.All(e => e.GetProperty("caseId").GetString() == "case"));
        Check(events.First().GetProperty("accepted").GetBoolean());
        var packets = events.Where(e => Type(e) == "packet").Select(e => e.GetProperty("packet")).ToArray();
        Check(packets.All(p => p.GetProperty("requestId").GetString() == "r-first"
            && p.GetProperty("documentToken").GetString() == "document"
            && p.GetProperty("tabId").GetInt32() == 1 && p.GetProperty("browserWindowId").GetInt32() == 2
            && p.GetProperty("generation").GetInt64() == 3 && p.GetProperty("revision").GetInt64() == 4));
        Check(packets.Any(p => Type(p) == "pageAutoAssessment" && p.GetProperty("needsTranslation").GetBoolean()));
        var updates = packets.SelectMany(p => p.GetProperty("updates").EnumerateArray()).ToArray();
        Check(updates.Any(u => !u.GetProperty("complete").GetBoolean() && !u.GetProperty("failed").GetBoolean()));
        Check(updates.Any(u => u.GetProperty("id").GetString() == "node" && u.GetProperty("complete").GetBoolean()));
        Check(packets.Last().GetProperty("finished").GetBoolean());
        var terminal = Terminal(output, "first");
        Check(terminal.GetProperty("success").GetBoolean() && terminal.GetProperty("requestCount").GetInt32() == 1);
        await host.HandleAsync(Candidate("cached")); await host.DrainAsync();
        terminal = Terminal(output, "cached");
        Check(created == 1 && terminal.GetProperty("cacheHits").GetInt32() == 1
            && terminal.GetProperty("requestCount").GetInt32() == 0);
        await host.HandleAsync(Candidate("cached"));
        Check(Events(output).Last().GetProperty("code").GetString() == "duplicate_id");
    }

    private static async Task AssessmentAsync()
    {
        var output = new StringWriter(); var created = 0;
        await using var host = Host(output, _ => { created++; throw new Exception(); });
        await host.HandleAsync(Request("assess", "assess", new { samples = new[] { new { text = Source, kind = "body" } },
            statistics = new { totalCharacters = Source.Length, latinProseCharacters = 40 } }));
        Check(Type(Events(output).Single()) == "assessment" && Events(output).Single().GetProperty("needsTranslation").GetBoolean());
        await host.HandleAsync(Candidate("skip", "这是一段已经使用中文书写的正文，读者可以直接阅读而无需翻译。"));
        await host.DrainAsync();
        Check(created == 0 && Terminal(output, "skip").GetProperty("success").GetBoolean()
            && Terminal(output, "skip").GetProperty("requestCount").GetInt32() == 0);
    }

    private static async Task InvalidAsync()
    {
        var output = new StringWriter(); var loaded = 0;
        await using var host = new JsonlHost(false, () => { loaded++; throw new Exception(); }, _ => throw new Exception(), output);
        var good = Candidate("bad");
        var variants = new[]
        {
            good.Replace("\"frameId\":0", "\"frameId\":1"),
            good.Replace("\"type\":\"pageAutoCandidate\"", "\"type\":\"pageCandidate\""),
            good.Replace("\"tabId\":1", "\"tabId\":-1"),
            good.Replace("\"generation\":3", "\"generation\":9007199254740992"),
            good.Replace("\"node\"", "\"\""),
            good.Replace("\"revision\":4", "\"revision\":4,\"revision\":5"),
            good.Replace("\"blocks\":[", "\"blocks\":null,\"unused\":["),
            good.Replace("\"siteHost\":\"example.org\"", "\"siteHost\":\"HTTPS://example.org\""),
            good.Replace("\"text\":\"" + Source + "\"", "\"text\":null"),
        };
        for (var i = 0; i < variants.Length; i++)
            await host.HandleAsync(variants[i].Replace("\"id\":\"bad\"", "\"id\":\"bad" + i + "\""));
        await host.HandleAsync("{");
        await host.HandleAsync(Request("invalid-assess", "assess", new { samples = new[] { new { text = Source } },
            statistics = new { totalCharacters = 1, latinCharacters = 2 } }));
        Check(loaded == 0 && Events(output).Count == variants.Length + 2 && Events(output).All(e => Type(e) == "error"));
    }

    private static async Task CancellationAsync()
    {
        var output = new StringWriter(); var started = Signal(); var release = Signal(); var entered = 0; var ended = 0;
        var provider = new SyntheticProvider(async (request, progress, token) =>
        {
            progress.Report(Prefix(request));
            if (Interlocked.Increment(ref entered) == 2) started.TrySetResult();
            try { await release.Task.WaitAsync(token); return Final(request); }
            finally { Interlocked.Increment(ref ended); }
        });
        await using var host = Host(output, _ => provider);
        await host.HandleAsync(Candidate("cancel-one"));
        await host.HandleAsync(Candidate("keep-two", "The entrance is blue and this sentence is different."));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A mismatching case cannot cancel another case's request.
        await host.HandleAsync(Request("keep-two", "cancel").Replace("\"caseId\":\"case\"", "\"caseId\":\"other\""));
        await host.HandleAsync(Request("cancel-one", "cancel"));
        release.TrySetResult(); await host.DrainAsync();
        Check(ended == 2 && !Terminal(output, "cancel-one").GetProperty("success").GetBoolean()
            && Terminal(output, "keep-two").GetProperty("success").GetBoolean());
        Check(Events(output).Any(e => Id(e) == "cancel-one" && Type(e) == "error" && e.GetProperty("code").GetString() == "cancelled"));
        Check(Events(output).Where(e => Id(e) == "cancel-one" && Type(e) == "packet").Last()
            .GetProperty("packet").GetProperty("error").GetString() == "cancelled");
        var count = Events(output).Count;
        await host.HandleAsync(Request("cancel-one", "cancel"));
        Check(Events(output).Count == count); // Retrying cancellation cannot create a second terminal.
    }

    private static async Task CompletedCancellationAsync()
    {
        var output = new StringWriter();
        await using var host = Host(output, _ => new SyntheticProvider(FinishAsync));
        await host.HandleAsync(Candidate("completed")); await host.DrainAsync();
        var count = Events(output).Count;
        for (var i = 0; i < 3; i++) await host.HandleAsync(Request("completed", "cancel"));
        Check(Events(output).Count == count && Terminal(output, "completed").GetProperty("success").GetBoolean());
        await host.HandleAsync(Request("completed", "cancel").Replace("\"caseId\":\"case\"", "\"caseId\":\"CASE\""));
        await host.HandleAsync(Request("unknown", "cancel"));
        await host.HandleAsync(Request("cap", "capabilities"));
        await host.HandleAsync(Request("cap", "cancel"));
        await host.HandleAsync(Request("assess", "assess", new { samples = new[] { new { text = Source, kind = "body" } } }));
        await host.HandleAsync(Request("assess", "cancel"));
        await host.HandleAsync(Request("invalid", "message", new { message = new { type = "invalid" } }));
        await host.HandleAsync(Request("invalid", "cancel"));
        Check(Events(output).Count(e => Type(e) == "error" && e.GetProperty("code").GetString() == "unknown_request") == 5);
        await host.HandleAsync(Candidate("completed"));
        Check(Events(output).Last().GetProperty("code").GetString() == "duplicate_id");
        Check(Events(output).Count(e => Type(e) == "terminal") == 1);
    }

    private static async Task CompletionRaceAsync()
    {
        // Pause actual terminal output while the admitted work is still active.
        // Completion owns the result by this point; cancellation must neither
        // use a retired token source nor emit unknown_request/another terminal.
        var output = new PausedTerminalWriter();
        await using var host = Host(output, _ => new SyntheticProvider(FinishAsync));
        await host.HandleAsync(Candidate("race"));
        try
        {
            await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.HandleAsync(Request("race", "cancel"));
            host.CancelAll();
        }
        finally { output.Release.TrySetResult(); }
        await host.DrainAsync();
        await host.HandleAsync(Request("race", "cancel"));
        Check(Events(output).All(e => Type(e) != "error"));
        Check(Terminal(output, "race").GetProperty("success").GetBoolean());
        Check(Events(output).Count(e => Type(e) == "terminal") == 1);
    }

    private static async Task CapacityAsync()
    {
        var output = new StringWriter(); var started = Signal(); var entered = 0; var ended = 0;
        var provider = new SyntheticProvider(async (_, _, token) =>
        {
            if (Interlocked.Increment(ref entered) == JsonlHost.MaximumActiveRequests) started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return ""; }
            finally { Interlocked.Increment(ref ended); }
        });
        await using var host = Host(output, _ => provider);
        for (var i = 0; i <= JsonlHost.MaximumActiveRequests; i++) await host.HandleAsync(Candidate("capacity-" + i));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(Events(output).Any(e => Type(e) == "error" && e.GetProperty("code").GetString() == "busy"));
        await host.HandleAsync(Request("capacity-" + JsonlHost.MaximumActiveRequests, "cancel"));
        Check(Events(output).Last().GetProperty("code").GetString() == "unknown_request");
        host.CancelAll(); await host.DrainAsync();
        Check(entered == JsonlHost.MaximumActiveRequests && ended == entered
            && Events(output).Count(e => Type(e) == "terminal") == entered
            && Events(output).Where(e => Type(e) == "terminal").All(e => !e.GetProperty("success").GetBoolean()));
    }

    private static async Task InterruptedAsync()
    {
        var output = new StringWriter();
        await using var host = Host(output, _ => new SyntheticProvider((request, progress, _) =>
        {
            progress.Report(Prefix(request));
            throw new TranslationAuthenticationException("DO_NOT_LEAK account=user endpoint=https://secret proxy=private key=secret");
        }));
        await host.HandleAsync(Candidate("interrupted")); await host.DrainAsync();
        Check(!output.ToString().Contains("DO_NOT_LEAK") && !output.ToString().Contains("https://secret"));
        var terminal = Terminal(output, "interrupted");
        Check(!terminal.GetProperty("success").GetBoolean() && terminal.GetProperty("requestCount").GetInt32() == 1
            && terminal.GetProperty("failedCount").GetInt32() == 1);
        var updates = Events(output).Where(e => Type(e) == "packet").SelectMany(e => e.GetProperty("packet").GetProperty("updates").EnumerateArray());
        Check(updates.Any(u => u.GetProperty("failed").GetBoolean()) && !updates.Any(u => u.GetProperty("complete").GetBoolean()));
    }

    private static async Task NoFallbackAsync()
    {
        var output = new StringWriter(); var created = 0;
        // Exercise the same live host path with an injected rejecting factory, without IO or credentials.
        await using var host = new JsonlHost(true, () => Task.FromResult(new AppSettings()),
            _ => { created++; throw new TranslationConfigurationException("DO_NOT_LEAK"); }, output);
        await host.HandleAsync(Request("real-assess", "assess", new { samples = new[] { new { text = Source } } }));
        Check(created == 0);
        await host.HandleAsync(Candidate("real-failure")); await host.DrainAsync();
        Check(created == 1 && !Terminal(output, "real-failure").GetProperty("success").GetBoolean()
            && !output.ToString().Contains("DO_NOT_LEAK")
            && Events(output).Any(e => Type(e) == "error" && e.GetProperty("code").GetString() == "api_configuration"));
        // Internal live=true test events are captured in memory, never advertised as live evidence.
    }

    private static async Task SettingsAsync()
    {
        using var valid = new MemoryStream(Encoding.UTF8.GetBytes("{\"version\":1,\"targetLanguage\":\"ja\"}"));
        var settings = await LiveRuntime.ReadSettingsAsync(valid);
        Check(settings.Version == AppSettings.CurrentVersion && settings.TargetLanguage == "ja");
        foreach (var content in new[] { "{broken", "null" })
        {
            using var bad = new MemoryStream(Encoding.UTF8.GetBytes(content));
            var threw = false;
            try { await LiveRuntime.ReadSettingsAsync(bad); } catch (JsonException) { threw = true; }
            Check(threw && Encoding.UTF8.GetString(bad.ToArray()) == content);
        }
        var output = new StringWriter(); var created = 0;
        await using var host = new JsonlHost(false, () => throw new JsonException("DO_NOT_LEAK"),
            _ => { created++; throw new Exception(); }, output);
        await host.HandleAsync(Candidate("bad-settings")); await host.DrainAsync();
        Check(created == 0 && !Terminal(output, "bad-settings").GetProperty("success").GetBoolean()
            && !output.ToString().Contains("DO_NOT_LEAK"));
    }

    private static Task RunningPresetAsync()
    {
        var settings = new AppSettings();
        var id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var selected = settings with { SelectedTranslationPresetId = id, TranslationProviderPresets = [new()
            { Id = id, Provider = TranslationProviderKind.DeepSeek, Model = settings.DeepSeekModel, BaseUrl = settings.DeepSeekBaseUrl }] };
        var result = LiveRuntime.ResolveRunningPreset(settings, LiveRuntime.CacheScope(selected), ["invalid", id, id]);
        Check(TranslationProviderConfiguration.SecretName(result) == TranslationProviderConfiguration.PresetSecretName(id));
        Check(ReferenceEquals(settings, LiveRuntime.ResolveRunningPreset(settings, LiveRuntime.CacheScope(settings), [])));
        foreach (var scope in new[] { "invalid", new string('0', 64) })
        {
            var threw = false;
            try { LiveRuntime.ResolveRunningPreset(settings, scope, [id]); } catch (InvalidDataException) { threw = true; }
            Check(threw);
        }
        var mismatch = false;
        try { LiveRuntime.ResolveRunningPreset(settings with { TranslationProvider = TranslationProviderKind.OpenAI }, LiveRuntime.CacheScope(selected), [id]); }
        catch (InvalidDataException) { mismatch = true; }
        Check(mismatch);
        return Task.CompletedTask;
    }

    private static async Task ContinuationAsync()
    {
        var output = new StringWriter();
        await using var host = Host(output, _ => new SyntheticProvider(FinishAsync));
        await host.HandleAsync(Candidate("document-first")); await host.DrainAsync();
        var follow = JsonNode.Parse(Candidate("document-next").Replace(Source, Source + " The flowers are blooming."))!;
        follow["message"]!.AsObject().Remove("samples");
        follow["message"]!.AsObject().Remove("pageSummary");
        await host.HandleAsync(follow.ToJsonString()); await host.DrainAsync();
        var terminal = Terminal(output, "document-next");
        Check(terminal.GetProperty("success").GetBoolean() && terminal.GetProperty("requestCount").GetInt32() > 0);
        Check(Events(output).Where(e => Id(e) == "document-next" && Type(e) == "packet")
            .SelectMany(e => e.GetProperty("packet").GetProperty("updates").EnumerateArray())
            .Any(u => u.GetProperty("complete").GetBoolean()));
        follow["id"] = "unknown-document";
        follow["message"]!["requestId"] = "unknown-document";
        follow["message"]!["documentToken"] = "new-document-without-assessment";
        await host.HandleAsync(follow.ToJsonString()); await host.DrainAsync();
        Check(!Terminal(output, "unknown-document").GetProperty("success").GetBoolean());
    }

    private static Task SnapshotAsync()
    {
        var id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var settings = new AppSettings { SelectedTranslationPresetId = id, TranslationProviderPresets = [new()
            { Id = id, Provider = TranslationProviderKind.DeepSeek, Model = "deepseek-flash", BaseUrl = "https://api.deepseek.com" }] };
        var scope = new string('a', 64);
        var snapshot = JsonSerializer.SerializeToElement(new { schemaVersion = 1, runningCacheScope = scope, settings });
        Check(LiveRuntime.ReadVerifiedSnapshot(snapshot, scope, settings.TargetLanguage).SelectedTranslationPresetId == id);
        foreach (var (actualScope, target) in new[] { (new string('b', 64), settings.TargetLanguage), (scope, "ja") })
        {
            var threw = false;
            try { LiveRuntime.ReadVerifiedSnapshot(snapshot, actualScope, target); } catch (InvalidDataException) { threw = true; }
            Check(threw);
        }
        var unsupported = JsonSerializer.SerializeToElement(new { schemaVersion = 1, runningCacheScope = scope,
            settings = settings with { SelectedTranslationPresetId = "", TranslationProvider = TranslationProviderKind.OpenAI } });
        var rejected = false;
        try { LiveRuntime.ReadVerifiedSnapshot(unsupported, scope, settings.TargetLanguage); } catch (InvalidDataException) { rejected = true; }
        Check(rejected);
        return Task.CompletedTask;
    }

    private static Task PinnedSnapshotAsync()
    {
        var id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var settings = new AppSettings { SelectedTranslationPresetId = id, TranslationProviderPresets = [new()
            { Id = id, Provider = TranslationProviderKind.DeepSeek, Model = "deepseek-flash", BaseUrl = "https://api.deepseek.com" }] };
        JsonElement Snapshot(AppSettings value) => JsonSerializer.SerializeToElement(new
            { schemaVersion = 1, runningCacheScope = new string('a', 64), settings = value });
        var pinned = LiveRuntime.ReadPinnedSnapshot(Snapshot(settings));
        Check(pinned.SelectedTranslationPresetId == id && TranslationProviderConfiguration.SecretName(pinned) == "translation-preset-" + id);
        foreach (var candidate in new[] {
            settings with { SelectedTranslationPresetId = "" },
            settings with { TranslationProviderPresets = [settings.TranslationProviderPresets[0] with { Model = "deepseek-pro" }] },
            settings with { TranslationProviderPresets = [settings.TranslationProviderPresets[0] with { BaseUrl = "https://other.invalid" }] } })
        {
            var rejected = false;
            try { LiveRuntime.ReadPinnedSnapshot(Snapshot(candidate)); } catch (InvalidDataException) { rejected = true; }
            Check(rejected);
        }
        return Task.CompletedTask;
    }

    private static async Task InputAsync()
    {
        var output = new StringWriter();
        await using var host = Host(output, _ => new SyntheticProvider(FinishAsync));
        var input = new StringReader(new string('x', JsonlHost.MaximumLineCharacters + 1) + "\n"
            + Candidate("eof") + "\r\n" + Request("last", "capabilities"));
        await host.RunAsync(input);
        Check(Events(output).Any(e => Type(e) == "error" && e.GetProperty("code").GetString() == "line_too_large")
            && Terminal(output, "eof").GetProperty("success").GetBoolean()
            && Events(output).Any(e => Id(e) == "last" && Type(e) == "capabilities"));
    }

    private static async Task TransportAsync()
    {
        await using var host = Host(new BrokenWriter(), _ => new SyntheticProvider(FinishAsync));
        await host.HandleAsync(Candidate("broken-output"));
        var failed = false;
        try { await host.DrainAsync(); } catch (IOException) { failed = true; }
        Check(failed);
    }

    private static JsonlHost Host(TextWriter output, Func<AppSettings, ITranslationProvider> factory)
        => new(false, () => Task.FromResult(new AppSettings()), factory, output);

    private static async Task DiagnosticsAsync()
    {
        // Hold two real host jobs inside their providers so attribution is tested
        // across overlapping async continuations and smaller format retries.
        var output = new StringWriter(); var diagnostics = new StringWriter();
        var entered = 0; var both = Signal();
        var provider = new SyntheticProvider(async (request, _, token) =>
        {
            if (Interlocked.Increment(ref entered) >= 2) both.TrySetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return request.Blocks[0].Text.Contains("SOURCE_SECRET", StringComparison.Ordinal)
                ? "MODEL_SECRET sk-credential https://private.invalid" : Final(request);
        });
        await using (var host = new JsonlHost(false, () => Task.FromResult(new AppSettings { TranslationContext = "SETTINGS_SECRET" }),
            _ => provider, output, diagnostics))
        {
            await host.HandleAsync(Candidate("ID_SECRET", "SOURCE_SECRET The entrance is blue.").Replace("\"caseId\":\"case\"", "\"caseId\":\"CASE_SECRET\""));
            await host.HandleAsync(Candidate("success", Source + " The flowers bloom."));
            await host.DrainAsync();
        }
        Check(!Terminal(output, "ID_SECRET").GetProperty("success").GetBoolean());
        Check(Terminal(output, "success").GetProperty("success").GetBoolean());
        Check(Events(output).All(e => Type(e) != "diagnostic"));
        var records = Events(diagnostics);
        var failure = records.Where(e => e.GetProperty("idHash").GetString() == HashIdentity("ID_SECRET")).ToArray();
        var success = records.Where(e => e.GetProperty("idHash").GetString() == HashIdentity("success")).ToArray();
        Check(failure.Length > 0 && success.Length > 0 && failure.Length + success.Length == records.Count);
        Check(failure.All(e => e.GetProperty("caseIdHash").GetString() == HashIdentity("CASE_SECRET")));
        Check(success.All(e => e.GetProperty("caseIdHash").GetString() == HashIdentity("case")));
        Check(failure.Any(e => e.GetProperty("reason").GetString() == "format_failure"));
        Check(failure.Any(e => e.GetProperty("phase").GetString() == "format_retry"));
        Check(failure.Any(e => e.GetProperty("reason").GetString() == "result_mismatch"));
        Check(success.All(e => e.GetProperty("reason").GetString() is "successful_group" or "service_complete"));
        Check(!diagnostics.ToString().Contains("SECRET") && !diagnostics.ToString().Contains("entrance")
            && !diagnostics.ToString().Contains("credential") && !diagnostics.ToString().Contains("https"));
        Check(records.All(e => e.GetProperty("type").GetString() == "diagnostic" && e.GetProperty("requests").GetInt32() <= 32));

        // Same malformed input with no writer keeps default protocol and terminal.
        var plain = new StringWriter();
        await using var defaultHost = Host(plain, _ => new SyntheticProvider((_, _, _) => Task.FromResult("MODEL_SECRET malformed")));
        await defaultHost.HandleAsync(Candidate("default")); await defaultHost.DrainAsync();
        Check(Events(plain).All(e => Type(e) != "diagnostic") && !Terminal(plain, "default").GetProperty("success").GetBoolean());
        Check(Terminal(plain, "default").GetProperty("requestCount").GetInt32()
            == Terminal(output, "ID_SECRET").GetProperty("requestCount").GetInt32());
    }

    private static async Task DiagnosticBoundsAsync()
    {
        var output = new StringWriter(); var diagnostics = new StringWriter();
        var candidate = JsonNode.Parse(Candidate("bounded", "The demonstration took place in 1954."))!;
        candidate["message"]!["blocks"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 48)
            .Select(i => new { id = "fact" + i, text = "The demonstration took place in 1954." }));
        var provider = new SyntheticProvider((request, _, _) => Task.FromResult(JsonSerializer.Serialize(new
            { blocks = request.Blocks.Select(block => new { id = block.Id, translation = "MODEL_SECRET 演示于1955年举行。" }) })));
        await using (var host = new JsonlHost(false, () => Task.FromResult(new AppSettings()), _ => provider, output, diagnostics))
        {
            await host.HandleAsync(candidate.ToJsonString()); await host.DrainAsync();
        }
        Check(!Terminal(output, "bounded").GetProperty("success").GetBoolean()
            && Terminal(output, "bounded").GetProperty("failedCount").GetInt32() == 48);
        var records = Events(diagnostics);
        Check(records.Count <= 66 && records.Any(e => e.GetProperty("reason").GetString() == "output_limited")
            && records.Any(e => e.GetProperty("reason").GetString() == "retry_budget_exhausted"));
        Check(records.Any(e => e.GetProperty("reason").GetString() == "repeated_rejected_answer"));
        Check(records.Any(e => e.GetProperty("reason").GetString() == "result_mismatch"));
        Check(diagnostics.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).All(line => line.Length <= 384));
        Check(!diagnostics.ToString().Contains("SECRET") && !diagnostics.ToString().Contains("演示"));
        var brokenOutput = new StringWriter();
        await using var broken = new JsonlHost(false, () => Task.FromResult(new AppSettings()),
            _ => new SyntheticProvider(FinishAsync), brokenOutput, new BrokenDiagnosticWriter());
        await broken.HandleAsync(Candidate("broken-diagnostic")); await broken.DrainAsync();
        Check(Terminal(brokenOutput, "broken-diagnostic").GetProperty("success").GetBoolean());
    }

    private static string HashIdentity(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task DiagnosticTransferAsync()
    {
        var output = new StringWriter(); var diagnostics = new StringWriter(); var calls = 0;
        var candidate = JsonNode.Parse(Candidate("transfer"))!;
        candidate["message"]!["blocks"] = JsonSerializer.SerializeToNode(new[]
        {
            new { id = "receiver", text = "The demonstration", groupId = "inline" },
            new { id = "owner", text = "took place in 1954.", groupId = "inline" },
        });
        var provider = new SyntheticProvider((request, _, _) =>
        {
            var retry = Interlocked.Increment(ref calls) > 1;
            return Task.FromResult(JsonSerializer.Serialize(new { blocks = request.Blocks.Select(block => new
            {
                id = block.Id, translation = block.Text == "The demonstration"
                    ? retry ? "演示" : "1954年的演示" : retry ? "于1954年举行。" : "举行。",
            }) }));
        });
        await using var host = new JsonlHost(false, () => Task.FromResult(new AppSettings()), _ => provider, output, diagnostics);
        await host.HandleAsync(candidate.ToJsonString()); await host.DrainAsync();
        Check(Terminal(output, "transfer").GetProperty("success").GetBoolean() && calls == 3);
        var records = Events(diagnostics);
        Check(records.Any(e => e.GetProperty("reason").GetString() == "numeric_transfer"));
        Check(records.Any(e => e.GetProperty("phase").GetString() == "semantic_retry"));
        Check(records.Any(e => e.GetProperty("reason").GetString() == "successful_group"));
        Check(!diagnostics.ToString().Contains("demonstration") && !diagnostics.ToString().Contains("1954")
            && !diagnostics.ToString().Contains("演示"));
    }

    private sealed class BrokenDiagnosticWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("EXCEPTION_SECRET");
    }

    private static async Task DiagnosticFormatRecoveryAsync()
    {
        var output = new StringWriter(); var diagnostics = new StringWriter();
        var candidate = JsonNode.Parse(Candidate("format-recovery"))!;
        candidate["message"]!["blocks"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 6)
            .Select(i => new { id = "node" + i, text = Source + (char)('a' + i) }));
        var sizes = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var provider = new SyntheticProvider((request, _, _) =>
        {
            sizes.Enqueue(request.Blocks.Count);
            return Task.FromResult(request.Blocks.Count > 4 || request.Blocks[0].Text.EndsWith('e')
                ? "MODEL_SECRET malformed" : Final(request));
        });
        await using var host = new JsonlHost(false, () => Task.FromResult(new AppSettings()), _ => provider, output, diagnostics);
        await host.HandleAsync(candidate.ToJsonString()); await host.DrainAsync();
        var terminal = Terminal(output, "format-recovery");
        Check(!terminal.GetProperty("success").GetBoolean() && terminal.GetProperty("requestCount").GetInt32() == 3
            && terminal.GetProperty("failedCount").GetInt32() == 2 && sizes.SequenceEqual(new[] { 6, 4, 2 }));
        var complete = Events(output).Where(e => Type(e) == "packet")
            .SelectMany(e => e.GetProperty("packet").GetProperty("updates").EnumerateArray())
            .Where(e => e.GetProperty("complete").GetBoolean() && !e.GetProperty("failed").GetBoolean());
        Check(complete.Select(e => e.GetProperty("id").GetString()).Distinct().Count() == 4);
        var records = Events(diagnostics);
        Check(records.Any(e => e.GetProperty("reason").GetString() == "successful_group")
            && records.Any(e => e.GetProperty("phase").GetString() == "format_retry"));
        Check(!diagnostics.ToString().Contains("SECRET") && !diagnostics.ToString().Contains("entrance"));
    }

    private static string Candidate(string id, string source = Source) => Request(id, "message", new { message = new
    {
        type = "pageAutoCandidate", requestId = "r-" + id, documentToken = "document", tabId = 1,
        browserWindowId = 2, frameId = 0, generation = 3, revision = 4, documentLanguage = "en", siteHost = "example.org",
        blocks = new[] { new { id = "node", text = source } }, samples = new[] { new { text = source, kind = "body" } },
        pageSummary = new { },
    } });

    private static string Request(string id, string action, object? extra = null)
    {
        var values = new Dictionary<string, object?> { ["schemaVersion"] = 1, ["id"] = id, ["caseId"] = "case", ["action"] = action };
        if (extra is not null)
            foreach (var field in JsonSerializer.SerializeToElement(extra).EnumerateObject()) values.Add(field.Name, field.Value);
        return JsonSerializer.Serialize(values);
    }

    private static List<JsonElement> Events(StringWriter output) => output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => { using var doc = JsonDocument.Parse(line); return doc.RootElement.Clone(); }).ToList();
    private static string? Type(JsonElement value) => value.GetProperty("type").GetString();
    private static string? Id(JsonElement value) => value.GetProperty("id").GetString();
    private static JsonElement Terminal(StringWriter output, string id) => Events(output).Single(e => Id(e) == id && Type(e) == "terminal");
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException(); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TranslationStreamUpdate Prefix(TranslationRequest request) => new("入口")
        { ImageSegments = [new(request.Blocks[0].Id, request.Blocks[0].Text, "入口", default)] };
    private static string Final(TranslationRequest request) => JsonSerializer.Serialize(new
        { blocks = request.Blocks.Select(block => new { id = block.Id, translation = "入口是蓝色，花园十分安静。" }) });
    private static async Task<string> FinishAsync(TranslationRequest request, IProgress<TranslationStreamUpdate> progress, CancellationToken token)
    {
        Check(request.IsPageText && request.ThinkingOverride == ThinkingLevel.None);
        progress.Report(Prefix(request)); await Task.Delay(15, token); return Final(request);
    }
    private sealed class SyntheticProvider(Func<TranslationRequest, IProgress<TranslationStreamUpdate>, CancellationToken, Task<string>> run)
        : ITranslationProvider, IStreamingTranslationProvider
    {
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken token) => run(request, new EmptyProgress(), token);
        public Task<string> TranslateRawStreamingAsync(TranslationRequest request, IProgress<TranslationStreamUpdate> progress, CancellationToken token)
            => run(request, progress, token);
    }
    private sealed class EmptyProgress : IProgress<TranslationStreamUpdate> { public void Report(TranslationStreamUpdate value) { } }
    private sealed class BrokenWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(string? value) => throw new IOException("DO_NOT_LEAK");
    }
    private sealed class PausedTerminalWriter : StringWriter
    {
        public TaskCompletionSource Started { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public override async Task WriteLineAsync(string? value)
        {
            using var document = JsonDocument.Parse(value!);
            if (Type(document.RootElement) == "terminal")
            {
                Started.TrySetResult();
                await Release.Task;
            }
            await base.WriteLineAsync(value);
        }
    }
}
