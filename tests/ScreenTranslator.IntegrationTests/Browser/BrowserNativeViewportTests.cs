using System.Runtime.InteropServices;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserNativeViewportTests
{
    [Fact]
    public async Task NativeRenderWidgetWorksWithoutAnAccessibleDocument()
    {
        // Real Win32 child geometry, outside the desktop: no Chrome/AI/network or injected input.
        const string childClass = "Chrome_RenderWidgetHostHWND";
        WindowProcedure procedure = DefWindowProc;
        var module = GetModuleHandle(null);
        var registration = new WindowClass { ClassName = childClass, Procedure = procedure, Instance = module };
        var atom = RegisterClass(ref registration);
        Assert.NotEqual((ushort)0, atom);
        nint parent = 0;
        try
        {
            parent = CreateWindowEx(0x08000080, "STATIC", "", 0x90000000, -5000, -5000, 860, 780, 0, 0, module, 0);
            Assert.NotEqual(0, parent);
            var child = CreateWindowEx(0, childClass, "", 0x50000000, 10, 70, 840, 700, parent, 0, module, 0);
            Assert.NotEqual(0, child);
            var outer = AccessibleTextResolver.GetWindowBounds(parent)!.Value;
            var expected = AccessibleTextResolver.GetWindowBounds(child)!.Value;
            var actual = await BrowserViewportResolver.ResolveAsync(new(parent, outer, 96), expected.Center,
                new(expected.Width, expected.Height), 1, CancellationToken.None);
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (parent != 0) DestroyWindow(parent);
            UnregisterClass(childClass, module);
            GC.KeepAlive(procedure);
        }
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wparam, nint lparam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string title,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nint wparam, nint lparam);
}
