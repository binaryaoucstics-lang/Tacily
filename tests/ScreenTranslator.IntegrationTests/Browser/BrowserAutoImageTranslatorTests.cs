using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.App.Services.Overlays;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;
using SkiaSharp;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserAutoImageTranslatorTests
{
    [Fact]
    public async Task ExplicitManualImageOverridesLocalNegativeAndStillStreamsAndCachesTheResult()
    {
        await using var browser = await AcknowledgingBrowser.ConnectAsync();
        var image = Letters(); var provider = new BeforeTokenProvider(); var cache = new ImageTranslationCache(); var factories=0;
        provider.Release.TrySetResult();
        await using var service = new BrowserAutoImageTranslator(cache, new Secrets(), providerFactory: _ => { factories++; return provider; },
            prepareAsync: (_, _) => Task.FromResult(new GroundedVisionDocument(
                [new("r1", new(25,25,150,40),default) { PreciseWords=true,TextBounds=[new(25,25,150,40)] }],null)),
            assessAsync: (_,_,_) => Task.FromResult(Evidence(false)),locateAsync: (_,_)=>Task.FromResult<IReadOnlyList<OcrBlock>>([]));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var result=await service.TranslateAsync(image,new(){ThinkingLevel=ThinkingLevel.None},browser.Overlay(image.Size),timeout.Token,manual:true);
        Assert.Equal(1,factories);Assert.Equal(1,result.TileCount);Assert.Equal(1,cache.Count);
        Assert.Contains(browser.Messages,p=>p.Type=="imageOverlayCommit");
        await service.TranslateAsync(image,new(){ThinkingLevel=ThinkingLevel.None},browser.Overlay(image.Size),timeout.Token,manual:true);
        Assert.Equal(1,factories);
    }
    [Fact]
    public async Task WaitingPhaseIsPublishedBeforeTheFirstProviderTokenAndFinalPhaseCompletes()
    {
        await using var browser = await AcknowledgingBrowser.ConnectAsync();
        var image = Letters(); var provider = new BeforeTokenProvider();
        await using var service = new BrowserAutoImageTranslator(new(), new Secrets(), providerFactory: _ => provider,
            prepareAsync: (_, _) => Task.FromResult(new GroundedVisionDocument(
                [new("r1", new(25, 25, 150, 40), default) { PreciseWords = true, TextBounds = [new(25, 25, 150, 40)] }], null)),
            locateAsync: (_, _) => Task.FromResult<IReadOnlyList<OcrBlock>>([]));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var job = service.TranslateAsync(image, new() { ThinkingLevel = ThinkingLevel.None }, browser.Overlay(image.Size), stop.Token);
        try {
            await provider.Started.Task.WaitAsync(stop.Token);
            Assert.Contains(browser.Messages, message => message.Type == "imageOverlayBegin" && message.Phase == "translating");
            Assert.False(browser.FirstCommit.Task.IsCompleted);
        } finally { provider.Release.TrySetResult(); }
        var result = await job; Assert.Equal(1, result.TileCount);
        Assert.Contains(browser.Messages, message => message.Type == "imageOverlayState" && message.Phase == "complete");
    }

    private sealed class BeforeTokenProvider : ITranslationProvider, IStreamingTranslationProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken token) => throw new InvalidOperationException();
        public async Task<string> TranslateRawStreamingAsync(TranslationRequest request, IProgress<TranslationStreamUpdate> progress, CancellationToken token)
        {
            Started.TrySetResult(); await Release.Task.WaitAsync(token);
            return """{"blocks":[{"id":"p1","regions":["r1"],"ink":[0,0,1000,1000],"translation":"你好，世界"}]}""";
        }
    }
    [Fact]
    public async Task MissingKeyFailsBeforeLoadingModelsOrPreparingAnImage()
    {
        var secrets = new Secrets();
        var preparationCount = 0;
        var cache = new ImageTranslationCache();
        await using var service = new BrowserAutoImageTranslator(cache, secrets,
            prepareAsync: (_, _) =>
            {
                preparationCount++;
                throw new InvalidOperationException("Preparation must not run without credentials.");
            });
        await using var server = new BrowserBridgeServer("ScreenTranslator.Auto.NoKey." + Guid.NewGuid().ToString("N"));
        var overlay = new BrowserOverlayTransport(server, new(Guid.NewGuid(), 1, "i", "d", new(400, 240)));

        await Assert.ThrowsAsync<TranslationAuthenticationException>(() =>
            service.TranslateAsync(Letters(), new(), overlay, CancellationToken.None));

        Assert.Equal(1, secrets.Reads);
        Assert.Equal(0, preparationCount);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task CancelledQueuedRequestDoesNotReadSecretsOrCreateAProvider()
    {
        var secrets = new Secrets();
        var providerCount = 0;
        await using var service = new BrowserAutoImageTranslator(new(), secrets,
            providerFactory: _ =>
            {
                providerCount++;
                throw new InvalidOperationException("Cancelled work must not create a provider.");
            });
        await using var server = new BrowserBridgeServer("ScreenTranslator.Auto.Cancelled." + Guid.NewGuid().ToString("N"));
        var overlay = new BrowserOverlayTransport(server, new(Guid.NewGuid(), 1, "i", "d", new(400, 240)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TranslateAsync(Letters(), new(), overlay, cancelled.Token));

        Assert.Equal(0, secrets.Reads);
        Assert.Equal(0, providerCount);
    }

    [Fact]
    public async Task CachedTilesAreRepublishedWithoutCredentialsModelsOrTranslation()
    {
        await using var browser = await AcknowledgingBrowser.ConnectAsync();
        var image = Letters();
        var settings = new AppSettings();
        var block = new TranslatedBlock("p1", "", "你好", new(25, 25, 150, 40));
        byte[] png = [1, 2, 3, 4];
        var tile = new RenderedImageTile(block.Id, block.Bounds, png, image, true) { Translation = block.Translation };
        var cache = new ImageTranslationCache();
        cache.Set(ImageTranslationCache.Key(image, settings), new(null, new([block]), [tile]));
        var secrets = new Secrets();
        await using var service = new BrowserAutoImageTranslator(cache, secrets,
            providerFactory: _ => throw new InvalidOperationException("Cache hit must not create a provider."),
            prepareAsync: (_, _) => throw new InvalidOperationException("Cache hit must not prepare layout."));

        var result = await service.TranslateAsync(image, settings, browser.Overlay(image.Size), CancellationToken.None);

        Assert.True(result.CacheHit);
        Assert.Equal(1, result.TileCount);
        Assert.Equal(0, secrets.Reads);
        Assert.Contains(browser.Messages, message => message.Type == "imageOverlayChunk"
            && Convert.FromBase64String(message.Data!).SequenceEqual(png));
        Assert.Contains(browser.Messages, message => message.Type == "imageOverlayBegin" && message.Phase == "cached");
        Assert.DoesNotContain(browser.Messages, message => message.Type == "imageOverlayState" && message.Enabled == false);
    }

    [Fact]
    public async Task CancellationBeforeBeginAcknowledgementStillHidesTheCreatedLayer()
    {
        await using var browser = await AcknowledgingBrowser.ConnectAsync(acknowledgeBegin: false);
        var overlay = browser.Overlay(new(400, 240));
        using var stop = new CancellationTokenSource();
        var publishing = overlay.PublishAsync([], true, stop.Token);
        await browser.FirstBegin.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishing);

        await overlay.DisposeAsync(retainCompleted: false);

        Assert.Contains(browser.Messages, message => message.Type == "imageOverlayState" && message.Enabled == false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealRenderingStreamsBeforeCompletionAndCancellationDoesNotCache(bool cancel)
    {
        await using var browser = await AcknowledgingBrowser.ConnectAsync();
        var image = Letters();
        var cache = new ImageTranslationCache();
        var provider = new PausedStreamingProvider();
        await using var service = new BrowserAutoImageTranslator(cache, new Secrets(),
            providerFactory: _ => provider,
            prepareAsync: (_, _) => Task.FromResult(new GroundedVisionDocument(
                [new("r1", new(25, 25, 150, 40), default)
                { PreciseWords = true, TextBounds = [new(25, 25, 150, 40)] }], null)),
            locateAsync: (_, _) => Task.FromResult<IReadOnlyList<OcrBlock>>([]));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var job = service.TranslateAsync(image, new(), browser.Overlay(image.Size), stop.Token);
        await browser.FirstCommit.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(job.IsCompleted);
        Assert.Equal(0, cache.Count);
        Assert.NotNull(provider.Request);
        Assert.True(provider.Request.IsDirectVision);
        Assert.NotNull(provider.Request.ImagePng);
        Assert.Empty(provider.Request.Blocks.Single().Text);

        if (cancel)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
            Assert.True(provider.Cancelled);
            Assert.Equal(0, cache.Count);
            Assert.Contains(browser.Messages, message => message.Type == "imageOverlayState" && message.Enabled == false);
        }
        else
        {
            provider.Finish.TrySetResult(true);
            var result = await job;
            Assert.False(result.CacheHit);
            Assert.Equal(1, result.TileCount);
            var saved = cache.Get(ImageTranslationCache.Key(image, new()));
            Assert.NotNull(saved);
            Assert.All(saved.Tiles, tile => Assert.True(tile.Optimized));
            Assert.Equal("你好，世界", Assert.Single(saved.Tiles).Translation);
            Assert.True(browser.Messages.Count(message => message.Type == "imageOverlayCommit") >= 2);
            Assert.DoesNotContain(browser.Messages, message => message.Type == "imageOverlayState" && message.Enabled == false);
        }
    }

    [Fact]
    public async Task CompleteIdentityDismissalCancelsStreamingWhileOldSessionCannotCancelIt()
    {
        await using var browser = await AcknowledgingBrowser.ConnectAsync();
        var image = Letters();
        var cache = new ImageTranslationCache();
        var provider = new PausedStreamingProvider();
        await using var service = new BrowserAutoImageTranslator(cache, new Secrets(),
            providerFactory: _ => provider,
            prepareAsync: (_, _) => Task.FromResult(new GroundedVisionDocument(
                [new("r1", new(25, 25, 150, 40), default)
                { PreciseWords = true, TextBounds = [new(25, 25, 150, 40)] }], null)),
            locateAsync: (_, _) => Task.FromResult<IReadOnlyList<OcrBlock>>([]));
        var overlay = browser.Overlay(image.Size);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var job = service.TranslateAsync(image, new(), overlay, deadline.Token);
        await browser.FirstCommit.Task.WaitAsync(deadline.Token);

        await browser.DismissAsync("old-session", deadline.Token);
        Assert.False(job.IsCompleted);
        Assert.False(provider.Cancelled);
        Assert.False(overlay.DismissalToken.IsCancellationRequested);

        await browser.DismissAsync(null, deadline.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
        Assert.True(provider.Cancelled);
        Assert.Equal(0, cache.Count);
        Assert.True(overlay.DismissalToken.IsCancellationRequested); // Still readable after service disposed the transport.
        Assert.Contains(browser.Messages, message => message.Type == "imageOverlayState" && message.Enabled == false);
    }

    [Fact]
    public async Task AdmittedImageScrollsOffscreenAndBackWithoutLosingRealPrefixOrRestartingProvider()
    {
        await using var browser = await AcknowledgingBrowser.ConnectAsync();
        var image = Letters(); var cache = new ImageTranslationCache(); var provider = new PausedStreamingProvider();
        var settings = new AppSettings { ThinkingLevel = ThinkingLevel.None };
        await using var service = new BrowserAutoImageTranslator(cache, new Secrets(),
            providerFactory: _ => provider,
            prepareAsync: (_, _) => Task.FromResult(new GroundedVisionDocument(
                [new("r1", new(25, 25, 150, 40), default)
                { PreciseWords = true, TextBounds = [new(25, 25, 150, 40)] }], null)),
            locateAsync: (_, _) => Task.FromResult<IReadOnlyList<OcrBlock>>([]));
        var candidate = new AutoImageCandidate(Guid.Empty, "request", "image-1", "document-1", 1, 7, 10, "preview");
        var reply = new BrowserImageReply("geometry", true, "image-1", "document-1", new(400, 240),
            new(0, 0, 800, 480), new(0, 0, 800, 480), 1, new(1280, 720), 0) { AutoImageAllowed = true };
        var current = reply;
        var offscreenSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returnedSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.True(BrowserAutoImageLifetimePolicy.CanStart(candidate, new(true, 1, 7, "document-1", reply), true));
        var job = BrowserAutoImageLifetimePolicy.RunAsync(_ =>
        {
            var geometry = Volatile.Read(ref current);
            if (geometry.Clip.Width == 0) offscreenSeen.TrySetResult();
            else if (offscreenSeen.Task.IsCompleted) returnedSeen.TrySetResult();
            return Task.FromResult(BrowserAutoImageLifetimePolicy.CanContinue(candidate, new(true, 1, 7, "document-1", geometry), true));
        }, token => service.TranslateAsync(image, settings, browser.Overlay(image.Size), token), deadline.Token, TimeSpan.FromMilliseconds(10));
        await browser.FirstCommit.Task.WaitAsync(deadline.Token);
        var prefixChunks = browser.Messages.Count(message => message.Type == "imageOverlayChunk");
        Assert.True(prefixChunks > 0);
        Volatile.Write(ref current, reply with { Clip = new(0, 0, 0, 0), Display = new(0, -2000, 800, 480) });
        await offscreenSeen.Task.WaitAsync(deadline.Token);
        Assert.False(job.IsCompleted); Assert.False(provider.Cancelled); Assert.Equal(1, provider.Calls);
        Assert.DoesNotContain(browser.Messages, message => message.Type == "imageOverlayState" && message.Enabled == false);
        Volatile.Write(ref current, reply); await returnedSeen.Task.WaitAsync(deadline.Token);
        Assert.Equal(1, provider.Calls); Assert.True(browser.Messages.Count(message => message.Type == "imageOverlayChunk") >= prefixChunks);
        provider.Finish.TrySetResult(true); var result = await job;
        Assert.Equal(1, result.TileCount); Assert.Equal(1, cache.Count); Assert.Equal(1, provider.Calls);
        Assert.False(provider.Cancelled);
        Assert.DoesNotContain(browser.Messages, message => message.Type == "imageOverlayState" && message.Enabled == false);
    }

    private static CapturedBitmap Letters()
    {
        using var bitmap = new SKBitmap(400, 240, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bitmap);
        using var ink = new SKPaint { Color = SKColors.Black };
        using var font = new SKFont(SKTypeface.Default, 30);
        canvas.Clear(SKColors.White);
        canvas.DrawText("HELLO", 30, 55, font, ink);
        var bytes = new byte[bitmap.Width * bitmap.Height * 4];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return new(bitmap.Width, bitmap.Height, bitmap.Width * 4, bytes);
    }



    [Fact]
    public async Task OriginalNegativeRunsAlongsideGroundingAndNeverCreatesAProviderOrOverlay()
    {
        await using var browser = await AcknowledgingBrowser.ConnectAsync();
        var image = Letters(); var cache = new ImageTranslationCache(); var factories = 0;
        var assessmentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var service = new BrowserAutoImageTranslator(cache, new Secrets(),
            providerFactory: _ => { Interlocked.Increment(ref factories); throw new InvalidOperationException("A local negative cannot create a provider."); },
            prepareAsync: async (original, token) =>
            {
                Assert.Same(image, original); preparationStarted.TrySetResult();
                await assessmentStarted.Task.WaitAsync(token);
                return new([], null);
            },
            assessAsync: async (original, target, token) =>
            {
                Assert.Same(image, original); Assert.Equal("zh-CN", target);
                assessmentStarted.TrySetResult(); await preparationStarted.Task.WaitAsync(token);
                return Evidence(false, new OcrBlock("credit", "Sarah Meyssonnier/Reuters", .99, new(25, 25, 150, 40), 0));
            });
        var result = await service.TranslateAsync(image, new AppSettings {  },
            browser.Overlay(image.Size), deadline.Token);
        Assert.Empty(result.Result.Blocks); Assert.Equal(0, result.TileCount); Assert.Equal(0, factories); Assert.Equal(0, cache.Count);
        Assert.Empty(browser.Messages);
    }



    [Fact]
    public async Task AllTargetLanguageOutputIsFilteredBeforeAnyRenderingOrCacheEntry()
    {
        await using var browser = await AcknowledgingBrowser.ConnectAsync();
        var image = Letters(); var cache = new ImageTranslationCache(); var provider = new TargetTextProvider();
        await using var service = new BrowserAutoImageTranslator(cache, new Secrets(),
            providerFactory: _ => provider,
            prepareAsync: (_, _) => Task.FromResult(new GroundedVisionDocument([], null)),
            assessAsync: (_, _, _) => Task.FromResult(Evidence(true,
                new("target", "我们已经到家了", .99, new(25, 25, 150, 40), 0),
                new("foreign", "PLEASE HELP ME ESCAPE", .99, new(25, 130, 300, 40), 1))));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var result = await service.TranslateAsync(image, new AppSettings { ThinkingLevel = ThinkingLevel.None },
            browser.Overlay(image.Size), deadline.Token);
        Assert.Equal(1, provider.Calls); Assert.Empty(result.Result.Blocks); Assert.Equal(0, result.TileCount); Assert.Equal(0, cache.Count);
        Assert.DoesNotContain(browser.Messages, message => message.Type is "imageOverlayChunk" or "imageOverlayCommit");
        Assert.Contains(browser.Messages, message => message.Type == "imageOverlayState" && message.Enabled == false);
    }

    private static ImageTranslationAssessment Evidence(bool needsTranslation, params OcrBlock[] recognized) =>
        new(needsTranslation, "Fixture original assessment", 1, "latin", [], 0)
        { ReasonCode = needsTranslation ? "foreign-readable-text" : "unreadable-or-noise", AllRecognizedText = recognized,
            RecognizedText = recognized, DetectedTextRegions = recognized.Select(line => new TextRegion(line.BoundsInCapturePixels, 1)).ToArray() };

    private sealed class TargetTextProvider : ITranslationProvider, IStreamingTranslationProvider
    {
        public int Calls { get; private set; }
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<string> TranslateRawStreamingAsync(TranslationRequest request, IProgress<TranslationStreamUpdate> progress, CancellationToken token)
        {
            Calls++; token.ThrowIfCancellationRequested();
            const string partial = """{"blocks":[{"id":"p1","box":[62,104,438,271],"area":[62,104,438,271],"flow":"h","translation":"我""";
            const string final = """{"blocks":[{"id":"p1","box":[62,104,438,271],"area":[62,104,438,271],"flow":"h","translation":"我们已经到家了"}]}""";
            progress.Report(new("我") { ImageSegments = DirectVisionTranslation.ReadStreaming(partial, request.ImageSize!.Value) });
            progress.Report(new("我们已经到家了") { ImageSegments = DirectVisionTranslation.ReadStreaming(final, request.ImageSize!.Value) });
            return Task.FromResult(final);
        }
    }

    private sealed class Secrets : ISecretStore
    {
        public int Reads { get; private set; }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult<string?>(null);
        }
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PausedStreamingProvider : ITranslationProvider, IStreamingTranslationProvider
    {
        public TaskCompletionSource<bool> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TranslationRequest? Request { get; private set; }
        public bool Cancelled { get; private set; }
        public int Calls { get; private set; }
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Automatic jobs must use the streaming provider.");
        public async Task<string> TranslateRawStreamingAsync(TranslationRequest request,
            IProgress<TranslationStreamUpdate> progress, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            const string partial = """{"blocks":[{"id":"p1","regions":["r1"],"ink":[0,0,1000,1000],"translation":"你""";
            progress.Report(new("你")
            {
                ImageSegments = DirectVisionTranslation.ReadStreaming(partial, request.ImageSize!.Value, request.VisionRegions),
            });
            try { await Finish.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            const string final = """{"blocks":[{"id":"p1","regions":["r1"],"ink":[0,0,1000,1000],"translation":"你好，世界"}]}""";
            progress.Report(new("你好，世界")
            {
                ImageSegments = DirectVisionTranslation.ReadStreaming(final, request.ImageSize!.Value, request.VisionRegions),
            });
            return final;
        }
    }

    private sealed record ReceivedMessage(string Type, bool? Enabled, string? Data, string? Phase);

    private sealed class AcknowledgingBrowser : IAsyncDisposable
    {
        private readonly BrowserBridgeServer _server;
        private readonly BrowserBridgeClient _client;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly Guid _connectionId;
        private readonly bool _acknowledgeBegin;
        private string? _beginIdentity;
        private readonly Task _receive;
        public ConcurrentQueue<ReceivedMessage> Messages { get; } = new();
        public TaskCompletionSource<bool> FirstCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FirstBegin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private AcknowledgingBrowser(BrowserBridgeServer server, BrowserBridgeClient client, Guid connectionId, bool acknowledgeBegin)
        {
            _server = server;
            _client = client;
            _connectionId = connectionId;
            _acknowledgeBegin = acknowledgeBegin;
            _receive = ReceiveAsync();
        }

        public static async Task<AcknowledgingBrowser> ConnectAsync(bool acknowledgeBegin = true)
        {
            var pipe = "ScreenTranslator.Auto.Tests." + Guid.NewGuid().ToString("N");
            var server = new BrowserBridgeServer(pipe);
            var opened = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.ConnectionOpened += (_, args) => opened.TrySetResult(args.ConnectionId);
            server.Start();
            var client = new BrowserBridgeClient(pipe);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(deadline.Token);
            return new(server, client, await opened.Task.WaitAsync(deadline.Token), acknowledgeBegin);
        }

        public BrowserOverlayTransport Overlay(PixelSize size) =>
            new(_server, new(_connectionId, 1, "image-1", "document-1", size));

        public async Task DismissAsync(string? sessionOverride, CancellationToken token)
        {
            using var begin = JsonDocument.Parse(Volatile.Read(ref _beginIdentity)!);
            var identity = begin.RootElement;
            var request = Guid.NewGuid().ToString("N");
            var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Received(object? _, BrowserBridgeMessageEventArgs args)
            {
                using var message = JsonDocument.Parse(args.Json);
                if(message.RootElement.TryGetProperty("requestId", out var id) && id.GetString() == request)
                    delivered.TrySetResult(true);
            }
            _server.MessageReceived += Received;
            try
            {
                await WriteAsync(JsonSerializer.Serialize(new
                {
                    type = "imageOverlayDismissed",
                    tabId = identity.GetProperty("tabId").GetInt32(),
                    imageId = identity.GetProperty("imageId").GetString(),
                    documentToken = identity.GetProperty("documentToken").GetString(),
                    sessionId = sessionOverride ?? identity.GetProperty("sessionId").GetString(),
                    requestId = request,
                }), token);
                // Registered after the transport: observing this event proves
                // its identity handler has processed the dismissal already.
                await delivered.Task.WaitAsync(token);
            }
            finally { _server.MessageReceived -= Received; }
        }

        private async Task WriteAsync(string json, CancellationToken token)
        {
            await _writeGate.WaitAsync(token);
            try { await _client.WriteAsync(json, token); }
            finally { _writeGate.Release(); }
        }

        private async Task ReceiveAsync()
        {
            try
            {
                while (await _client.ReadAsync(_stop.Token) is { } json)
                {
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;
                    var type = root.GetProperty("type").GetString()!;
                    Messages.Enqueue(new(type,
                        root.TryGetProperty("enabled", out var enabled) ? enabled.GetBoolean() : null,
                        root.TryGetProperty("data", out var data) ? data.GetString() : null,
                        root.TryGetProperty("phase", out var phase) ? phase.GetString() : null));
                    if (type == "imageOverlayBegin")
                    {
                        Volatile.Write(ref _beginIdentity, json);
                        FirstBegin.TrySetResult(true);
                        if (!_acknowledgeBegin) continue;
                    }
                    await WriteAsync(JsonSerializer.Serialize(new
                    {
                        type = "imageOverlayAck",
                        requestId = root.GetProperty("requestId").GetString(),
                        found = true,
                    }), _stop.Token);
                    if (type == "imageOverlayCommit") FirstCommit.TrySetResult(true);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _receive; } catch (OperationCanceledException) { }
            await _client.DisposeAsync();
            await _server.DisposeAsync();
            _stop.Dispose();
        }
    }
}
