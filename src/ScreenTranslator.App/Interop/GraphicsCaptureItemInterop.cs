using System.Runtime.InteropServices;
using Windows.Foundation.Metadata;

namespace ScreenTranslator.App.Interop;

/// <summary>
/// Provides an honest platform capability check for Windows.Graphics.Capture.
/// Creating a capture item is only one part of a functioning capture backend;
/// callers must also check that a D3D frame pipeline is available.
/// </summary>
public static class GraphicsCaptureItemInterop
{
    private const string GraphicsCaptureSessionType =
        "Windows.Graphics.Capture.GraphicsCaptureSession";

    public static bool IsApiPresent()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            return false;
        }

        try
        {
            return ApiInformation.IsTypePresent(GraphicsCaptureSessionType);
        }
        catch (COMException)
        {
            return false;
        }
    }
}
