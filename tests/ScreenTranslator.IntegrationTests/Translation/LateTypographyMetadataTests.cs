using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Translation;

public sealed class LateTypographyMetadataTests
{
    [Fact]
    public void CompletedTranslationAcceptsLateStyleWithoutRestoringAnEarlierTextPrefix()
    {
        var complete = new TranslatedBlock("p1", "HELLO", "你好，世界", new(10, 10, 80, 30));
        var model = new TranslationResultViewModel { IsBusy = true };
        model.ApplyStreamUpdate(new(complete.Translation) { ImageSegments = [complete] });
        model.IsBusy = false;
        var hint = new VisionTypographyHint(VisionFontFamily.Brush, Bold: true) { ColorHex = "#FF2040" };
        model.ApplyStreamUpdate(new("你") { IsLayoutUpdate = true, ImageSegments = [complete with
        { Translation = "你", TypographyHint = hint, GlyphSizeHint = 26, VisionInkBounds = new(12, 12, 74, 24) }] });
        var result = Assert.Single(model.Segments).ToBlock();
        Assert.Equal("你好，世界", result.Translation);
        Assert.Equal(hint, result.TypographyHint);
        Assert.Equal(26, result.GlyphSizeHint);
        Assert.Equal(new PixelRect(12, 12, 74, 24), result.VisionInkBounds);
    }
}
