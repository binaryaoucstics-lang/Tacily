using System.Runtime.InteropServices;

namespace ScreenTranslator.App.Services.Overlays;

/// <summary>Observe scrolling/dragging without consuming or injecting any input.</summary>
public sealed class SourceContentInteractionMonitor : IDisposable
{
    private readonly nint _source;
    private readonly Func<nint> _overlay;
    private readonly Hook _mouse;
    private readonly Hook _keyboard;
    private nint _mouseHandle, _keyboardHandle;
    public bool PointerDown { get; private set; }
    public event Action? Changed;

    public SourceContentInteractionMonitor(nint source, Func<nint> overlay)
    {
        _source = source; _overlay = overlay; _mouse = Mouse; _keyboard = Keyboard;
        _mouseHandle = SetWindowsHookEx(14, _mouse, GetModuleHandle(null), 0);
        _keyboardHandle = SetWindowsHookEx(13, _keyboard, GetModuleHandle(null), 0);
    }

    private nint Mouse(int code, nint message, nint data)
    {
        if (code >= 0 && message == 0x202 && PointerDown) { PointerDown = false; Changed?.Invoke(); }
        if (code >= 0 && GetForegroundWindow() == _source)
        {
            var point = Marshal.PtrToStructure<MouseData>(data).Point;
            var window = GetAncestor(WindowFromPoint(point), 2);
            if (window == _source || window == _overlay())
            {
                if (message == 0x201) { PointerDown = true; Changed?.Invoke(); }
                else if (message == 0x202) { PointerDown = false; Changed?.Invoke(); }
                else if (message is 0x20A or 0x20E || PointerDown && message == 0x200) Changed?.Invoke();
            }
            else if (message == 0x202) PointerDown = false;
        }
        return CallNextHookEx(_mouseHandle, code, message, data);
    }

    private nint Keyboard(int code, nint message, nint data)
    {
        if (code >= 0 && message is 0x100 or 0x104 && GetForegroundWindow() == _source)
        {
            var key = Marshal.ReadInt32(data);
            if (key is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x09) Changed?.Invoke();
        }
        return CallNextHookEx(_keyboardHandle, code, message, data);
    }

    public void Dispose()
    {
        if (_mouseHandle != 0) { UnhookWindowsHookEx(_mouseHandle); _mouseHandle = 0; }
        if (_keyboardHandle != 0) { UnhookWindowsHookEx(_keyboardHandle); _keyboardHandle = 0; }
    }

    private delegate nint Hook(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public NativePoint Point; public uint MouseDataValue, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll")] private static extern nint SetWindowsHookEx(int hook, Hook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flag);
}
