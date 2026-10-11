using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Ocr;

public sealed class WindowsOcrEngineTests
{
    [Fact]
    public async Task RecognizeAsync_HonorsAlreadyCancelledToken()
    {
        var engine = new WindowsOcrEngine();
        var bitmap = new CapturedBitmap(1, 1, 4, new byte[4]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.RecognizeAsync(bitmap, null, cancellation.Token));
    }

    [Fact]
    public async Task RecognizeAsync_ReportsInvalidLanguageAsUnavailable()
    {
        var engine = new WindowsOcrEngine();
        var bitmap = new CapturedBitmap(1, 1, 4, new byte[4]);

        var exception = await Assert.ThrowsAsync<OcrLanguageUnavailableException>(
            () => engine.RecognizeAsync(bitmap, "not_a_valid_bcp47_tag!", default));

        Assert.Equal("not_a_valid_bcp47_tag!", exception.LanguageTag);
        Assert.DoesNotContain("\\", exception.Message);
    }
}
