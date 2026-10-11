using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Layout;

namespace ScreenTranslator.App.Services.Capture;

public sealed record TranslationCaptureWorkItem(
    ScreenMonitor Monitor,
    PixelRect AbsoluteSelection,
    CapturedBitmap Bitmap,
    CapturedBrowserWindow? CapturedBrowser,
    IntPtr SourceWindowHandle,
    AppSettings Settings)
{
    public string? AccessibleText { get; init; }
    public PixelRect? ImageBounds { get; init; }
    public IReadOnlyList<OcrBlock> ImageOcrLines { get; init; } = [];
    public PixelRect? TextCropBounds { get; init; }
    public bool IsManualSelection { get; init; }
    public PixelRect? SourceWindowBounds { get; init; }
    public SourceImageMapping? SourceImageMapping { get; init; }
    public Func<Task<SourceImageMapping?>>? RefreshSourceImageMapping { get; init; }
    public BrowserImageIdentity? BrowserImage {get;init;}
}
