using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Browser;

public enum BrowserWindowChangeKind
{
    MovedOrResized,
    Minimized,
    Destroyed,
}

public sealed class BrowserWindowChangedEventArgs(
    BrowserWindowChangeKind kind,
    BrowserWindowSnapshot? snapshot) : EventArgs
{
    public BrowserWindowChangeKind Kind { get; } = kind;

    public BrowserWindowSnapshot? Snapshot { get; } = snapshot;
}

public sealed record BrowserWindowSnapshot(
    IntPtr Handle,
    PixelRect Bounds,
    uint Dpi);

public sealed record CapturedBrowserWindow(
    BrowserKind Browser,
    BrowserWindowSnapshot Snapshot);

public sealed class BrowserWindowEventMonitor : IDisposable
{
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;
    private const int ObjidWindow = 0;

    private readonly IntPtr _windowHandle;
    private readonly WinEventDelegate _callback;
    private readonly List<IntPtr> _hooks = [];
    private bool _disposed;

    public BrowserWindowEventMonitor(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("浏览器窗口句柄不能为空。", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _callback = OnWinEvent;
        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            throw new InvalidOperationException("浏览器窗口已经关闭。");
        }

        RegisterHook(EventObjectLocationChange, processId);
        RegisterHook(EventSystemMinimizeStart, processId);
        RegisterHook(EventObjectDestroy, processId);
    }

    public event EventHandler<BrowserWindowChangedEventArgs>? Changed;

    public static CapturedBrowserWindow? CaptureForegroundBrowser()
    {
        return CaptureBrowserWindow(GetForegroundWindow());
    }

    public static CapturedBrowserWindow? CaptureBrowserWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            return null;
        }

        BrowserKind browser;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            browser = process.ProcessName.ToLowerInvariant() switch
            {
                "chrome" => BrowserKind.Chrome,
                "msedge" => BrowserKind.Edge,
                _ => throw new InvalidOperationException(),
            };
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        var snapshot = GetSnapshot(windowHandle);
        return snapshot is null
            ? null
            : new CapturedBrowserWindow(browser, snapshot);
    }

    public BrowserWindowSnapshot? GetSnapshot() => GetSnapshot(_windowHandle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var hook in _hooks)
        {
            if (hook != IntPtr.Zero)
            {
                _ = UnhookWinEvent(hook);
            }
        }

        _hooks.Clear();
        _disposed = true;
    }

    private void RegisterHook(uint eventId, uint processId)
    {
        var hook = SetWinEventHook(
            eventId,
            eventId,
            IntPtr.Zero,
            _callback,
            processId,
            0,
            WineventOutofcontext | WineventSkipownprocess);
        if (hook == IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException(
                $"无法监听浏览器窗口事件 0x{eventId:X4}。");
        }

        _hooks.Add(hook);
    }

    private void OnWinEvent(
        IntPtr hook,
        uint eventId,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || windowHandle != _windowHandle)
        {
            return;
        }

        if (eventId == EventObjectLocationChange && objectId != ObjidWindow)
        {
            return;
        }

        var kind = eventId switch
        {
            EventSystemMinimizeStart => BrowserWindowChangeKind.Minimized,
            EventObjectDestroy => BrowserWindowChangeKind.Destroyed,
            _ => BrowserWindowChangeKind.MovedOrResized,
        };
        var snapshot = kind == BrowserWindowChangeKind.MovedOrResized
            ? GetSnapshot()
            : null;
        Changed?.Invoke(this, new BrowserWindowChangedEventArgs(kind, snapshot));
    }

    private static BrowserWindowSnapshot? GetSnapshot(IntPtr windowHandle)
    {
        if (IsIconic(windowHandle)
            || !GetWindowRect(windowHandle, out var bounds))
        {
            return null;
        }

        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var dpi = GetDpiForWindow(windowHandle);
        return new BrowserWindowSnapshot(
            windowHandle,
            new PixelRect(bounds.Left, bounds.Top, width, height),
            dpi == 0 ? 96u : dpi);
    }

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventId,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookModule,
        WinEventDelegate eventProcedure,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr windowHandle,
        out NativeRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);
}
