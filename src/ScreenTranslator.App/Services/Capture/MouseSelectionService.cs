using System.ComponentModel;
using System.Runtime.InteropServices;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Capture;

/// <summary>Consumes only the selection click/drag, leaving the desktop rendering unchanged.</summary>
public sealed class MouseSelectionService : IDisposable
{
    private readonly TaskCompletionSource<PointerSelection> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PointerSelectionTracker _tracker = new();
    private readonly HookCallback _mouseCallback;
    private readonly HookCallback _keyboardCallback;
    private readonly CancellationTokenRegistration _cancellation;
    private nint _mouseHook;
    private nint _keyboardHook;
    private bool _cancelOnRightUp;

    public event Action<PixelRect>? DragChanged;
    public Task<PointerSelection> Selection => _completion.Task;

    public MouseSelectionService(CancellationToken token) : this(token, installHooks: true) { }

    internal MouseSelectionService(CancellationToken token, bool installHooks)
    {
        _mouseCallback = OnMouse;
        _keyboardCallback = OnKey;
        if (installHooks)
        {
            var module = GetModuleHandle(null);
            _mouseHook = SetWindowsHookEx(14, _mouseCallback, module, 0);
            if (_mouseHook == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法开始鼠标选区。");
            _keyboardHook = SetWindowsHookEx(13, _keyboardCallback, module, 0);
            if (_keyboardHook == nint.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = nint.Zero;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听取消选区。");
            }
        }
        _cancellation = token.Register(() => _completion.TrySetCanceled(token));
    }

    public static PixelPoint CursorPosition
    {
        get
        {
            if (!GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new PixelPoint(point.X, point.Y);
        }
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code < 0 || _completion.Task.IsCompleted) return CallNextHookEx(_mouseHook, code, message, data);
        var pointData = Marshal.PtrToStructure<MouseData>(data);
        var point = new PixelPoint(pointData.Point.X, pointData.Point.Y);
        var action = (int)message;
        var isOurWindow = !_tracker.IsSelecting && (action == 0x0201 || action == 0x0204)
            && IsOurWindow(pointData.Point);
        return HandleMouseMessage(action, point, isOurWindow)
            ? new nint(1) : CallNextHookEx(_mouseHook, code, message, data);
    }

    internal bool HandleMouseMessage(int action, PixelPoint point, bool isOurWindow)
    {
        if (_completion.Task.IsCompleted) return false;
        if (action == 0x0200 && _tracker.IsSelecting)
        {
            _tracker.Move(point);
            DragChanged?.Invoke(_tracker.Bounds);
            // Let Windows move the actual cursor. Blocking WM_MOUSEMOVE here stalls cursor feedback.
            // Consuming down/up is sufficient to prevent the underlying app from receiving a drag.
            return false;
        }
        if (action == 0x0202 && _tracker.IsSelecting)
        {
            _completion.TrySetResult(_tracker.Complete(point));
            return true;
        }
        if (!_tracker.IsSelecting && isOurWindow) return false;
        if (action == 0x0201)
        {
            _tracker.Begin(point);
            return true;
        }
        if (action == 0x0204) { _cancelOnRightUp = true; return true; }
        if (action == 0x0205 && _cancelOnRightUp)
        {
            _completion.TrySetCanceled();
            return true;
        }
        return false;
    }

    private nint OnKey(int code, nint message, nint data)
    {
        if (code >= 0 && !_completion.Task.IsCompleted)
        {
            var key = Marshal.ReadInt32(data);
            if (key == 0x1B && (message == 0x0100 || message == 0x0104))
            {
                _completion.TrySetCanceled();
                return new nint(1);
            }
        }
        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private static bool IsOurWindow(NativePoint point)
    {
        GetWindowThreadProcessId(WindowFromPoint(point), out var process);
        return process == Environment.ProcessId;
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        if (_mouseHook != nint.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = nint.Zero; }
        if (_keyboardHook != nint.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = nint.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public NativePoint Point; public uint Mouse; public uint Flags; public uint Time; public nuint Extra; }
    private delegate nint HookCallback(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
