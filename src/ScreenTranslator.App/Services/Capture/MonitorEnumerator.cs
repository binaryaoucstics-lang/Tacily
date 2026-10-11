using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using ScreenTranslator.App.Interop;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Capture;

public static class MonitorEnumerator
{
    private const uint DefaultDpi = 96;

    public static IReadOnlyList<ScreenMonitor> GetMonitors()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("屏幕捕获仅支持 Windows。");
        }

        var monitors = new List<ScreenMonitor>();
        Exception? callbackException = null;
        NativeMethods.MonitorEnumProc callback = (
            nint monitor,
            nint _,
            ref NativeMethods.Rect __,
            nint ___) =>
        {
            try
            {
                monitors.Add(CreateMonitor(monitor));
                return true;
            }
            catch (Exception exception)
            {
                callbackException = exception;
                return false;
            }
        };

        var enumerated = NativeMethods.EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);

        if (callbackException is not null)
        {
            ExceptionDispatchInfo.Capture(callbackException).Throw();
        }

        if (!enumerated)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法枚举显示器。");
        }

        return monitors;
    }

    private static ScreenMonitor CreateMonitor(nint handle)
    {
        var info = new NativeMethods.MonitorInfoEx
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            DeviceName = string.Empty,
        };

        if (!NativeMethods.GetMonitorInfo(handle, ref info))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法读取显示器信息。");
        }

        var (dpiX, dpiY) = GetDpi(handle);
        return new ScreenMonitor(
            handle,
            info.DeviceName,
            ToPixelRect(info.Monitor),
            ToPixelRect(info.WorkArea),
            dpiX,
            dpiY,
            (info.Flags & NativeMethods.MonitorInfoPrimary) != 0);
    }

    private static (uint X, uint Y) GetDpi(nint monitor)
    {
        try
        {
            var result = NativeMethods.GetDpiForMonitor(
                monitor,
                NativeMethods.MonitorDpiType.Effective,
                out var dpiX,
                out var dpiY);

            return result >= 0 && dpiX > 0 && dpiY > 0
                ? (dpiX, dpiY)
                : (DefaultDpi, DefaultDpi);
        }
        catch (DllNotFoundException)
        {
            return (DefaultDpi, DefaultDpi);
        }
        catch (EntryPointNotFoundException)
        {
            return (DefaultDpi, DefaultDpi);
        }
    }

    private static PixelRect ToPixelRect(NativeMethods.Rect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
}
