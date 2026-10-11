using System.Diagnostics;
using System.Text.Json;
using ScreenTranslator.Core.Browser;

if (args.Length != 2) throw new ArgumentException("Usage: PageRulesBench corpus.json report.json");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var corpus = JsonSerializer.Deserialize<Corpus>(await File.ReadAllTextAsync(args[0]), json)!;
var results = new List<object>();
foreach (var page in corpus.Pages.Where(page => page.Success))
{
    var cold = Stopwatch.StartNew();
    var decision = PageTranslationDecision.Inspect(page.Samples, "zh-CN", page.Statistics).RuleDecision;
    var coldMs = cold.Elapsed.TotalMilliseconds;
    var measurements = new List<double>();
    for (var repeat = 0; repeat < 100; repeat++)
    {
        var start = Stopwatch.GetTimestamp();
        _ = PageTranslationDecision.Inspect(page.Samples, "zh-CN", page.Statistics);
        measurements.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
    measurements.Sort();
    results.Add(new { page.Url, page.ScanMs, SampleCount = page.Samples.Count, decision.NeedsTranslation, decision.ReasonCode,
        decision.DetectedLanguage, page.ExpectedNeedsTranslation, Correct = decision.NeedsTranslation == page.ExpectedNeedsTranslation,
        ColdRuleMilliseconds = coldMs, MedianRuleMilliseconds = measurements[50], P95RuleMilliseconds = measurements[95] });
}
var report = new { Policy = PageTranslationDecision.PolicyVersion, Results = results,
    RequiresModel = false, ApiKeyRead = false, CloudCalls = 0 };
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(report, json));
Console.WriteLine(JsonSerializer.Serialize(report, json));
record Corpus(List<PageCase> Pages);
record PageCase(string Url, bool Success, double ScanMs, bool ExpectedNeedsTranslation,
    List<PageTextSample> Samples, PageTextStatistics? Statistics);
