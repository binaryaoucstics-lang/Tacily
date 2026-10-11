using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Capture;

public sealed class WholeImageSelectionTests
{
    [Fact]
    public async Task Whole_Image_Crop_Includes_Title_And_Distant_Paragraphs_But_Excludes_Neighbouring_Image()
    {
        var ocr = new Ocr([
            new OcrBlock("title", "Title", 1, new PixelRect(60, 40, 260, 20), 0),
            new OcrBlock("para1", "First", 1, new PixelRect(70, 180, 340, 30), 1),
            new OcrBlock("para2", "Last", 1, new PixelRect(70, 600, 330, 25), 2)]);
        var bitmap = new CapturedBitmap(1400, 1000, 5600, new byte[5600000]);
        var monitor = new ScreenMonitor(0, "test", new PixelRect(-1400, 100, 1400, 1000), new PixelRect(-1400, 100, 1400, 1000), 144, 144, false);
        var selected = await new SmartSelectionResolver(ocr).FromWholeImageAsync(new MonitorCapture(monitor, bitmap, null!),
            new PixelRect(-1300, 200, 600, 800), "auto", 42, CancellationToken.None);
        Assert.Equal(default, ocr.ReceivedSize);
        Assert.Equal(new PixelRect(-1300, 200, 600, 800), selected.Bounds);
        Assert.Null(selected.TextCropBounds);
        Assert.Empty(selected.ImageOcrLines);
        Assert.Null(selected.AccessibleText);
    }

    [Fact]
    public async Task Missing_Ocr_Language_Still_Uses_Entire_Image_For_Vlm()
    {
        var bitmap = new CapturedBitmap(600, 800, 2400, new byte[1920000]);
        var monitor = new ScreenMonitor(0, "test", new PixelRect(0, 0, 600, 800), new PixelRect(0, 0, 600, 800), 96, 96, true);
        var image = new PixelRect(20, 30, 500, 700);
        var selected = await new SmartSelectionResolver(new Ocr(null)).FromWholeImageAsync(new MonitorCapture(monitor, bitmap, null!), image, "auto", 42, CancellationToken.None);
        Assert.Equal(image, selected.Bounds);
    }

    private sealed class Ocr(IReadOnlyList<OcrBlock>? lines) : IOcrEngine
    {
        public PixelSize ReceivedSize { get; private set; }
        public Task<IReadOnlyList<OcrBlock>> RecognizeAsync(CapturedBitmap bitmap, string? languageTag, CancellationToken cancellationToken)
        { ReceivedSize = bitmap.Size; return lines is null ? throw new OcrLanguageUnavailableException(languageTag) : Task.FromResult(lines); }
    }
}
