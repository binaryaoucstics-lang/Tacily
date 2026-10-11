namespace ScreenTranslator.App.Services.Capture;

public interface IScreenCaptureService
{
    bool IsAvailable { get; }

    IReadOnlyList<ScreenMonitor> GetMonitors();

    Task<MonitorCapture> CaptureAsync(
        ScreenMonitor monitor,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MonitorCapture>> CaptureAllAsync(
        CancellationToken cancellationToken = default);
}
