using System.Runtime.InteropServices;

namespace ScreenTranslator.App.Services.Overlays;

public sealed class ForegroundWindowChangedEventArgs(IntPtr windowHandle)
    : EventArgs
{
    public IntPtr WindowHandle { get; } = windowHandle;
}

public sealed class ForegroundWindowMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const uint GaRoot = 2;

    private readonly WinEventDelegate _callback;
    private IntPtr _hook;
    private volatile bool _disposed;

    public ForegroundWindowMonitor()
    {
        _callback = OnWinEvent;
        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WineventOutofcontext);

        if (_hook == IntPtr.Zero)
        {
            _disposed = true;
            throw new InvalidOperationException("无法监听 Windows 前台窗口变化。");
        }
    }

    public event EventHandler<ForegroundWindowChangedEventArgs>? Changed;

    public static IntPtr CaptureForegroundRootWindow() =>
        NormalizeRootWindow(GetForegroundWindow());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var hook = _hook;
        _hook = IntPtr.Zero;
        if (hook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(hook);
        }
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
        if (_disposed)
        {
            return;
        }

        Changed?.Invoke(
            this,
            new ForegroundWindowChangedEventArgs(
                NormalizeRootWindow(windowHandle)));
    }

    private static IntPtr NormalizeRootWindow(IntPtr handle) =>
        handle == IntPtr.Zero
            ? IntPtr.Zero
            : GetAncestor(handle, GaRoot);

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventId,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(
        IntPtr windowHandle,
        uint flags);
}
