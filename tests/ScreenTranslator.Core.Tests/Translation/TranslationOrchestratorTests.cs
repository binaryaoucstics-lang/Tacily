using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class TranslationOrchestratorTests
{
    [Fact]
    public async Task TranslateAsync_Reassociates_OutOfOrder_Response_In_Source_Order()
    {
        var provider = new QueueProvider(
            """{"blocks":[{"id":"b2","translation":"第二"},{"id":"b1","translation":"第一"}]}""");
        var orchestrator = Create(provider);

        var result = await orchestrator.TranslateAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(["b1", "b2"], result.Blocks.Select(block => block.Id));
        Assert.Equal(["第一", "第二"], result.Blocks.Select(block => block.Translation));
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task TranslateAsync_Retries_Format_Exactly_Once()
    {
        var invalid = """{"blocks":[{"id":"b1","translation":"只有一个"}]}""";
        var provider = new QueueProvider(
            invalid,
            """{"blocks":[{"id":"b1","translation":"第一"},{"id":"b2","translation":"第二"}]}""");
        var orchestrator = Create(provider);

        var result = await orchestrator.TranslateAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal(2, provider.CallCount);
        Assert.Contains("FORMAT_REPAIR", provider.Requests[1].Context, StringComparison.Ordinal);
        Assert.Contains(invalid, provider.Requests[1].Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsync_Stops_After_Second_Invalid_Response()
    {
        var provider = new QueueProvider("""{}""", """{"blocks":[]}""");
        var orchestrator = Create(provider);

        await Assert.ThrowsAsync<TranslationFormatException>(
            () => orchestrator.TranslateAsync(CreateRequest(), CancellationToken.None));

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task TranslateAsync_Does_Not_Repair_After_Cancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        var provider = new QueueProvider("""{}""")
        {
            AfterCall = _ => cancellationSource.Cancel(),
        };
        var orchestrator = Create(provider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orchestrator.TranslateAsync(CreateRequest(), cancellationSource.Token));

        Assert.Equal(1, provider.CallCount);
    }

    private static TranslationOrchestrator Create(ITranslationProvider provider) =>
        new(provider, new TranslationResponseValidator());

    private static TranslationRequest CreateRequest() =>
        new(
            "auto",
            "zh-CN",
            TranslationStyle.Natural,
            string.Empty,
            [
                new OcrBlock("b1", "First", 1, new PixelRect(0, 0, 100, 20), 0),
                new OcrBlock("b2", "Second", 1, new PixelRect(0, 30, 100, 20), 1),
            ]);

    private sealed class QueueProvider(params string[] responses) : ITranslationProvider
    {
        private readonly Queue<string> _responses = new(responses);

        public List<TranslationRequest> Requests { get; } = [];

        public Action<int>? AfterCall { get; init; }

        public int CallCount => Requests.Count;

        public Task<string> TranslateRawAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            AfterCall?.Invoke(Requests.Count);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
