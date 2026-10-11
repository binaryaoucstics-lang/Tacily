using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.App.Interop;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Capture;

public sealed class GdiScreenCaptureService : IScreenCaptureService
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public IReadOnlyList<ScreenMonitor> GetMonitors() =>
        MonitorEnumerator.GetMonitors();

    public async Task<IReadOnlyList<MonitorCapture>> CaptureAllAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<MonitorCapture>();
        foreach (var monitor in GetMonitors())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(await CaptureAsync(monitor, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<MonitorCapture> CaptureAsync(
        ScreenMonitor monitor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException("GDI 屏幕捕获仅支持 Windows。");
        }

        if (monitor.Bounds.Width <= 0 || monitor.Bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(monitor),
                "显示器边界必须具有正宽度和高度。");
        }

        return await Task
            .Run(() => CaptureCore(monitor, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private static MonitorCapture CaptureCore(
        ScreenMonitor monitor,
        CancellationToken cancellationToken)
    {
        var width = monitor.Bounds.Width;
        var height = monitor.Bounds.Height;
        var stride = checked(width * 4);
        var byteCount = checked(stride * height);

        nint screenDc = 0;
        nint memoryDc = 0;
        nint bitmap = 0;
        nint previousObject = 0;

        try
        {
            screenDc = NativeMethods.GetDC(0);
            if (screenDc == 0)
            {
                throw CreateCaptureException("无法获取桌面设备上下文。");
            }

            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == 0)
            {
                throw CreateCaptureException("无法创建设备上下文。");
            }

            var bitmapInfo = new NativeMethods.BitmapInfo
            {
                Header = new NativeMethods.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = NativeMethods.BiRgb,
                    SizeImage = (uint)byteCount,
                },
            };

            bitmap = NativeMethods.CreateDIBSection(
                screenDc,
                ref bitmapInfo,
                NativeMethods.DibRgbColors,
                out var bits,
                0,
                0);

            if (bitmap == 0 || bits == 0)
            {
                throw CreateCaptureException("无法创建屏幕截图位图。");
            }

            previousObject = NativeMethods.SelectObject(memoryDc, bitmap);
            if (previousObject == 0 || previousObject == -1)
            {
                throw CreateCaptureException("无法选择屏幕截图位图。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var copied = NativeMethods.BitBlt(
                memoryDc,
                0,
                0,
                width,
                height,
                screenDc,
                monitor.Bounds.X,
                monitor.Bounds.Y,
                NativeMethods.Srccopy | NativeMethods.CaptureBlt);

            if (!copied)
            {
                // CAPTUREBLT includes layered windows, but a few remote or
                // restricted display drivers reject that flag. A plain
                // SRCCOPY remains a useful compatibility fallback.
                copied = NativeMethods.BitBlt(
                    memoryDc,
                    0,
                    0,
                    width,
                    height,
                    screenDc,
                    monitor.Bounds.X,
                    monitor.Bounds.Y,
                    NativeMethods.Srccopy);
            }

            if (!copied)
            {
                throw CreateCaptureException("无法复制当前屏幕画面。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var pixels = GC.AllocateUninitializedArray<byte>(byteCount);
            Marshal.Copy(bits, pixels, 0, byteCount);
            // A 32-bit GDI DIB is BGRX: its fourth byte is undefined, not image alpha.
            // The desktop has already been composited, so every captured pixel is opaque.
            for (var alpha = 3; alpha < pixels.Length; alpha += 4) pixels[alpha] = byte.MaxValue;

            var preview = BitmapSource.Create(
                width,
                height,
                monitor.DpiX,
                monitor.DpiY,
                PixelFormats.Bgra32,
                palette: null,
                pixels,
                stride);
            preview.Freeze();

            var capturedBitmap = new CapturedBitmap(
                width,
                height,
                stride,
                pixels);

            return new MonitorCapture(monitor, capturedBitmap, preview);
        }
        finally
        {
            if (previousObject != 0 && memoryDc != 0)
            {
                NativeMethods.SelectObject(memoryDc, previousObject);
            }

            if (bitmap != 0)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != 0)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            if (screenDc != 0)
            {
                NativeMethods.ReleaseDC(0, screenDc);
            }
        }
    }

    private static Win32Exception CreateCaptureException(string message) =>
        new(Marshal.GetLastWin32Error(), message);
}
