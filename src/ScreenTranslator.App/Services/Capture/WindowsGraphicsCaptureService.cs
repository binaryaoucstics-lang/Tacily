using ScreenTranslator.App.Interop;

namespace ScreenTranslator.App.Services.Capture;

/// <summary>
/// Windows Graphics Capture with one-frame D3D11 readback; compatibility capture remains the fallback.
/// </summary>
public sealed class WindowsGraphicsCaptureService : IScreenCaptureService
{
    public bool IsPlatformApiPresent => GraphicsCaptureItemInterop.IsApiPresent();

    public bool IsAvailable => IsPlatformApiPresent;

    public string UnavailabilityReason => IsPlatformApiPresent
        ? string.Empty
        : "当前 Windows 版本不支持 Windows.Graphics.Capture。";

    public IReadOnlyList<ScreenMonitor> GetMonitors() =>
        MonitorEnumerator.GetMonitors();

    public async Task<MonitorCapture> CaptureAsync(
        ScreenMonitor monitor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(!IsAvailable)throw new PlatformNotSupportedException(UnavailabilityReason);
        var image=await Task.Run(()=>GraphicsFrameReader.CaptureAsync(monitor.Handle,true,cancellationToken),cancellationToken).ConfigureAwait(false);
        return new(monitor,image,CapturedBitmapEncoder.Preview(CapturedBitmapEncoder.ToPng(image)));
    }

    public async Task<IReadOnlyList<MonitorCapture>> CaptureAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result=new List<MonitorCapture>();foreach(var monitor in GetMonitors())result.Add(await CaptureAsync(monitor,cancellationToken));return result;
    }
}
