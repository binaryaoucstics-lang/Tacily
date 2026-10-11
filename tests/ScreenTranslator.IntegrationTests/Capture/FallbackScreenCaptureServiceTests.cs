using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Capture;

public sealed class FallbackScreenCaptureServiceTests
{
    [Fact]
    public async Task CaptureAsync_UsesFallback_WhenPrimaryIsUnavailable()
    {
        var primary = new FakeCaptureService(isAvailable: false);
        var fallback = new FakeCaptureService(isAvailable: true);
        var service = new FallbackScreenCaptureService(primary, fallback);

        var result = await service.CaptureAsync(TestMonitor);

        Assert.Equal(TestMonitor, result.Monitor);
        Assert.Equal(0, primary.CaptureCalls);
        Assert.Equal(1, fallback.CaptureCalls);
    }

    [Fact]
    public async Task CaptureAsync_UsesFallback_WhenPrimaryFailsWithSupportedError()
    {
        var primary = new FakeCaptureService(
            isAvailable: true,
            exception: new UnauthorizedAccessException("capture denied"));
        var fallback = new FakeCaptureService(isAvailable: true);
        var service = new FallbackScreenCaptureService(primary, fallback);

        await service.CaptureAsync(TestMonitor);

        Assert.Equal(1, primary.CaptureCalls);
        Assert.Equal(1, fallback.CaptureCalls);
    }

    [Fact]
    public async Task CaptureAsync_DoesNotFallback_WhenUserCancels()
    {
        var primary = new FakeCaptureService(isAvailable: true);
        var fallback = new FakeCaptureService(isAvailable: true);
        var service = new FallbackScreenCaptureService(primary, fallback);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CaptureAsync(TestMonitor, cancellation.Token));

        Assert.Equal(1, primary.CaptureCalls);
        Assert.Equal(0, fallback.CaptureCalls);
    }

    private static readonly ScreenMonitor TestMonitor = new(
        1,
        "test",
        new PixelRect(0, 0, 1, 1),
        new PixelRect(0, 0, 1, 1),
        96,
        96,
        true);

    private sealed class FakeCaptureService : IScreenCaptureService
    {
        private readonly Exception? _exception;

        public FakeCaptureService(bool isAvailable, Exception? exception = null)
        {
            IsAvailable = isAvailable;
            _exception = exception;
        }

        public bool IsAvailable { get; }

        public int CaptureCalls { get; private set; }

        public IReadOnlyList<ScreenMonitor> GetMonitors() => [TestMonitor];

        public Task<MonitorCapture> CaptureAsync(
            ScreenMonitor monitor,
            CancellationToken cancellationToken = default)
        {
            CaptureCalls++;
            cancellationToken.ThrowIfCancellationRequested();

            if (_exception is not null)
            {
                return Task.FromException<MonitorCapture>(_exception);
            }

            var bitmap = new CapturedBitmap(1, 1, 4, new byte[4]);
            return Task.FromResult(
                new MonitorCapture(
                    monitor,
                    bitmap,
                    SoftwareBitmapConverter.ToBitmapSource(bitmap)));
        }

        public async Task<IReadOnlyList<MonitorCapture>> CaptureAllAsync(
            CancellationToken cancellationToken = default) =>
            [await CaptureAsync(TestMonitor, cancellationToken)];
    }
}
