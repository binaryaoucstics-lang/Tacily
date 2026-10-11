using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ScreenTranslator.App.Services.Capture;

public sealed class FallbackScreenCaptureService : IScreenCaptureService
{
    private static readonly TimeSpan PrimaryTimeout = TimeSpan.FromSeconds(2);
    private readonly IScreenCaptureService _primary;
    private readonly IScreenCaptureService _fallback;

    public FallbackScreenCaptureService(
        IScreenCaptureService primary,
        IScreenCaptureService fallback)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public FallbackScreenCaptureService()
        : this(new WindowsGraphicsCaptureService(), new GdiScreenCaptureService())
    {
    }

    public bool IsAvailable => _primary.IsAvailable || _fallback.IsAvailable;

    public IReadOnlyList<ScreenMonitor> GetMonitors()
    {
        if (_primary.IsAvailable)
        {
            try
            {
                return _primary.GetMonitors();
            }
            catch (Exception exception) when (CanFallback(exception))
            {
                // Monitor discovery can fail with the same device/platform
                // errors as capture. Use the compatibility backend's view.
            }
        }

        return _fallback.GetMonitors();
    }

    public async Task<MonitorCapture> CaptureAsync(
        ScreenMonitor monitor,
        CancellationToken cancellationToken = default)
    {
        if (_primary.IsAvailable)
        {
            try
            {
                return await _primary
                    .CaptureAsync(monitor, cancellationToken)
                    .WaitAsync(PrimaryTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Primary timed out. Continue with the compatibility backend.
            }
            catch (Exception exception) when (CanFallback(exception))
            {
                // The primary backend is unavailable for this monitor/session.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await _fallback
            .CaptureAsync(monitor, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MonitorCapture>> CaptureAllAsync(
        CancellationToken cancellationToken = default)
    {
        var monitors = GetMonitors();
        var captures = new List<MonitorCapture>(monitors.Count);

        foreach (var monitor in monitors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            captures.Add(
                await CaptureAsync(monitor, cancellationToken).ConfigureAwait(false));
        }

        return captures;
    }

    private static bool CanFallback(Exception exception) =>
        exception is PlatformNotSupportedException
            or NotSupportedException
            or UnauthorizedAccessException
            or COMException
            or Win32Exception;
}
