using System.Runtime.InteropServices;

namespace ScreenTranslator.App.Interop;

internal static class NativeMethods
{
    internal const int MonitorInfoPrimary = 0x00000001;
    internal const uint DibRgbColors = 0;
    internal const uint Srccopy = 0x00CC0020;
    internal const uint CaptureBlt = 0x40000000;
    internal const int BiRgb = 0;

    internal delegate bool MonitorEnumProc(
        nint monitor,
        nint monitorDc,
        ref Rect monitorRect,
        nint data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRect,
        MonitorEnumProc callback,
        nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateDIBSection(
        nint deviceContext,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out nint bits,
        nint section,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint SelectObject(nint deviceContext, nint graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(
        nint destinationDc,
        int destinationX,
        int destinationY,
        int width,
        int height,
        nint sourceDc,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(
        nint monitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect WorkArea;
        internal int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal int Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Colors;
    }

    internal enum MonitorDpiType
    {
        Effective = 0,
        Angular = 1,
        Raw = 2,
        Default = Effective,
    }
}
