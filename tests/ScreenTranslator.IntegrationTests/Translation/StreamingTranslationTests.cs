using System.Net;
using System.Text;
using System.Text.Json;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests.Translation;

public sealed class StreamingTranslationTests
{
    [Fact]
    public async Task FullImageVisionStreamsBeforeItsPageAndLocalPositioningFinish()
    {
        var source = new GatedStream(
            Event(new { reasoning_content = "Never render reasoning" }) +
            Event(new { content = "{\"blocks\":[{\"id\":\"p1\",\"box\":[100,200,300,400],\"area\":[80,180,330,450],\"flow\":\"h\",\"translation\":\"你" }),
            Event(new { content = "好\"}]}" }) + "data: [DONE]\n\n");
        var handler = new Handler(source);
        var provider = new DeepSeekTranslationProvider(new HttpClient(handler), new Secret(), new DeepSeekOptions { ThinkingLevel = ThinkingLevel.None });
        var view = new TranslationResultViewModel { IsBusy = true };
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var layoutReady = new TaskCompletionSource<IReadOnlyList<OcrBlock>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var layout = new ScreenTranslator.Core.Layout.ParallelVisionLayout(new InlineProgress(update =>
        { view.ApplyStreamUpdate(update); if (update.Translation.Length > 0) observed.TrySetResult(); }), CancellationToken.None);
        var positioning = layout.LocateAsync(_ => layoutReady.Task);
        var request = Request() with { IsDirectVision = true, ImagePng = [137, 80, 78, 71], ImageTextCropPng = [99], ImageSize = new(1000, 1000) };
        var pending = new TranslationOrchestrator(provider, new TranslationResponseValidator()).TranslateAsync(request, CancellationToken.None, layout);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        Assert.False(positioning.IsCompleted);
        Assert.True(view.IsExpanded);
        Assert.Equal("你", view.TranslatedText);
        Assert.Equal("你", Assert.Single(view.Segments).Translation);
        Assert.Empty(view.Segments[0].SourceText);
        Assert.Equal(new PixelRect(100, 200, 200, 200), view.Segments[0].Bounds);
        Assert.Equal(new PixelRect(80, 180, 250, 270), view.Segments[0].LayoutBounds);
        Assert.Empty(view.StreamingTail);
        using (var body = JsonDocument.Parse(handler.Body))
        {
            var root = body.RootElement;
            Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
            var system = root.GetProperty("messages")[0].GetProperty("content").GetString();
            Assert.StartsWith(DirectVisionTranslation.Prompt, system);
            Assert.Contains("Omit already-target-language text, photo/copyright credits", system);
            var content = root.GetProperty("messages")[1].GetProperty("content");
            Assert.Equal(2, content.GetArrayLength());
            Assert.Equal("data:image/png;base64,iVBORw==", content[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.DoesNotContain("blocks", content[0].GetProperty("text").GetString());
            Assert.DoesNotContain("bbox", content[0].GetProperty("text").GetString());
        }
        source.Release.SetResult();
        var result = layout.Complete(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        view.IsBusy = false;
        view.SetSegments(result.Blocks);
        view.TranslatedText = "你好";
        layoutReady.SetResult([new("l1", "Hello world", 1, new(10, 20, 130, 40), 0)]);
        await positioning.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("你好", view.TranslatedText);
        Assert.Equal(new PixelRect(100, 200, 200, 200), Assert.Single(view.Segments).Bounds);
    }

    [Fact]
    public void QueuedPartialLayoutCannotReplaceCompletedTextOrReopenCollapsedPopup()
    {
        var view = new TranslationResultViewModel { IsBusy = false, IsExpanded = false, TranslatedText = "完整译文", SourceText = "Full source" };
        view.SetSegments([new("p1", "Full source", "完整译文", default)]);
        view.ApplyStreamUpdate(new("完") { IsLayoutUpdate = true, ImageSegments = [new("p1", "Full source", "完", new(1, 2, 30, 40))] });
        Assert.Equal("完整译文", view.TranslatedText);
        Assert.Equal("完整译文", view.Segments[0].Translation);
        Assert.Equal(new PixelRect(1, 2, 30, 40), view.Segments[0].Bounds);
        Assert.False(view.IsExpanded);
    }

    [Fact]
    public async Task MergedOcrDialogueStreamsFirstCharacterThenBecomesOneHoverableCard()
    {
        var source = new GatedStream(
            Event(new { content = "{\"blocks\":[{\"ids\":[\"p1\",\"p2\",\"p3\"],\"translation\":\"诶" }),
            Event(new { content = "？！真央被绑架了？！\"}]}" }) + "data: [DONE]\n\n");
        var handler = new Handler(source);
        var provider = new DeepSeekTranslationProvider(new HttpClient(handler), new Secret(), new DeepSeekOptions());
        var view = new TranslationResultViewModel { IsBusy = true };
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress(update => { view.ApplyStreamUpdate(update); if (update.Translation.Length > 0) observed.TrySetResult(); });
        var request = new TranslationRequest("auto", "zh-CN", TranslationStyle.Natural, "",
            [new("p1", "ええっ?!", 1, default, 0), new("p2", "真央くんが", 1, default, 1), new("p3", "誘拐された?!", 1, default, 2)])
            { IsOcrDocument = true, AtlasRegions = [new("p1", default, default), new("p2", default, default),
                new("p3", new(200, 100, 30, 150), default) { TextBounds = [new(200, 100, 30, 150)] }] };
        var pending = new TranslationOrchestrator(provider, new TranslationResponseValidator()).TranslateAsync(request, CancellationToken.None, progress);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(view.IsExpanded);
        Assert.Equal("诶", view.StreamingTail);
        Assert.Empty(view.Segments);
        Assert.False(pending.IsCompleted);
        source.Release.TrySetResult();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        var segment = Assert.Single(view.Segments);
        Assert.Equal(new[] { "p1", "p2", "p3" }, segment.SourceIds);
        Assert.Equal("ええっ?!\n真央くんが\n誘拐された?!", view.SourceText);
        Assert.Equal("诶？！真央被绑架了？！", view.TranslatedText);
        Assert.True(segment.Contains(210, 120));
        Assert.False(segment.Contains(10, 10));
        Assert.Equal(segment.SourceIds, result.Blocks[0].SourceIds);
        using var payload = JsonDocument.Parse(handler.Body);
        Assert.Equal(DeepSeekTranslationProvider.OcrGroupingPrompt, payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void Image_Paragraphs_Expose_Hoverable_Groups_While_Next_Paragraph_Still_Streams()
    {
        var model = new TranslationResultViewModel { IsBusy = true };
        model.ApplyStreamUpdate(new TranslationStreamUpdate("第一段\n第二段正在")
        {
            ImageSegments = [new TranslatedBlock("p1", "First", "第一段", new PixelRect(10, 20, 100, 30))],
        });
        Assert.True(model.IsExpanded);
        Assert.True(model.HasSegments);
        Assert.False(model.IsFlatTranslation);
        Assert.Equal("第二段正在", model.StreamingTail);
        Assert.Equal(new PixelRect(10, 20, 100, 30), model.Segments[0].Bounds);
        model.ApplyStreamUpdate(new TranslationStreamUpdate(""));
        Assert.False(model.HasSegments);
        Assert.Equal("", model.StreamingTail);
    }

    [Fact]
    public async Task First_Translated_Character_Expands_Popup_Before_Http_Stream_Finishes()
    {
        var source = new GatedStream(
            Event(new { reasoning_content = "Do not display private reasoning" }) +
            Event(new { content = "{\"blocks\":[{\"id\":\"block-1\",\"sourceText\":\"Hello\"," }) +
            Event(new { content = "\"translation\":\"你" }),
            Event(new { content = "好\"}]}" }) + "data: [DONE]\n\n");
        var handler = new Handler(source);
        var provider = new DeepSeekTranslationProvider(new HttpClient(handler), new Secret(), new DeepSeekOptions { ThinkingLevel = ThinkingLevel.Low });
        var viewModel = new TranslationResultViewModel { IsBusy = true };
        Assert.True(viewModel.IsCollapsed);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress(update => { viewModel.ApplyStreamUpdate(update); if (update.Translation.Length > 0) observed.TrySetResult(); });
        var request = Request() with { ImagePng = [137, 80, 78, 71] };
        var pending = new TranslationOrchestrator(provider, new TranslationResponseValidator()).TranslateAsync(request, CancellationToken.None, progress);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        Assert.True(viewModel.IsExpanded);
        Assert.Equal("你", viewModel.TranslatedText);
        Assert.DoesNotContain("reasoning", viewModel.TranslatedText, StringComparison.OrdinalIgnoreCase);
        using (var body = JsonDocument.Parse(handler.Body))
        {
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("low", body.RootElement.GetProperty("reasoning_effort").GetString());
        }
        source.Release.TrySetResult();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("你好", viewModel.TranslatedText);
        Assert.Equal("Hello", result.Blocks[0].SourceText);
    }

    [Fact]
    public async Task Closing_Cancels_A_Stream_Waiting_For_More_Bytes()
    {
        var source = new GatedStream(Event(new { content = "{\"blocks\":[" }), "");
        var provider = new DeepSeekTranslationProvider(new HttpClient(new Handler(source)), new Secret(), new DeepSeekOptions());
        using var cancellation = new CancellationTokenSource();
        var task = provider.TranslateRawStreamingAsync(Request(), new InlineProgress(_ => { }), cancellation.Token);
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void Empty_Update_Does_Not_Expand_And_Completed_View_Ignores_Late_Packets()
    {
        var model = new TranslationResultViewModel { IsBusy = true };
        model.ApplyStreamUpdate(new TranslationStreamUpdate(""));
        Assert.False(model.IsExpanded);
        model.ApplyStreamUpdate(new TranslationStreamUpdate("完整译文"));
        model.IsBusy = false;
        model.ApplyStreamUpdate(new TranslationStreamUpdate("过时片段"));
        Assert.Equal("完整译文", model.TranslatedText);
    }

    private static string Event(object delta) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta } } }) + "\n\n";
    private static TranslationRequest Request() => new("auto", "zh-CN", TranslationStyle.Natural, "",
        [new OcrBlock("block-1", "Hello", 1, new PixelRect(0, 0, 100, 40), 0)]);
    private sealed class InlineProgress(Action<TranslationStreamUpdate> action) : IProgress<TranslationStreamUpdate>
    { public void Report(TranslationStreamUpdate value) => action(value); }
    private sealed class Secret : ISecretStore
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>("synthetic-test-key");
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Handler(Stream stream) : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        }
    }
    private sealed class GatedStream(string first, string second) : Stream
    {
        private readonly byte[] _first = Encoding.UTF8.GetBytes(first);
        private readonly byte[] _second = Encoding.UTF8.GetBytes(second);
        private int _position;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _first.Length)
            {
                var length = Math.Min(buffer.Length, _first.Length - _position);
                _first.AsMemory(_position, length).CopyTo(buffer); _position += length; return length;
            }
            Waiting.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var offset = _position - _first.Length;
            var count = Math.Min(buffer.Length, _second.Length - offset);
            if (count <= 0) return 0;
            _second.AsMemory(offset, count).CopyTo(buffer); _position += count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => _first.Length + _second.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
