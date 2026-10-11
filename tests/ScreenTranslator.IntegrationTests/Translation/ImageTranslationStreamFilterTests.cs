using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Translation;

public sealed class ImageTranslationStreamFilterTests
{
    private static TranslationRequest Request => new("auto", "zh-Hans", TranslationStyle.Natural, "", [])
    { IsDirectVision = true, ImagePng = [1], ImageSize = new(300, 200) };

    [Fact]
    public void PendingLocalEvidenceStillDisplaysVlmTextWithoutErasing()
    {
        var view = new TranslationResultViewModel { IsBusy = true, IsInPlaceEnabled = true };
        var filtered = ImageTranslationStreamFilter.Apply(new("译文已经返回")
        { ImageSegments = [new("p1", "H", "译文已经返回", new(10, 10, 100, 40))], StreamingTail = "" }, Request, [], false);
        view.ApplyStreamUpdate(filtered);
        Assert.Equal("译文已经返回", view.TranslatedText);
        Assert.Empty(view.Segments);
        Assert.Null(filtered.ImageSegments);
    }

    [Fact]
    public void ReliableNativeCaptionDoesNotOccupySegmentsAndForeignTranslationRemains()
    {
        var view = new TranslationResultViewModel { IsBusy = true, IsInPlaceEnabled = true };
        TranslatedBlock native = new("p1", "中", "已有中文", new(10, 10, 100, 40));
        TranslatedBlock foreign = new("p2", "H", "保留译文", new(10, 80, 100, 40));
        var update = new TranslationStreamUpdate("已有中文\n保留译文") { ImageSegments = [native, foreign], StreamingTail = "后续" };
        var evidence = new OcrBlock[] { new("n", "已有中文说明", .99, native.Bounds, 0),
            new("f", "Hello there", .99, foreign.Bounds, 1) };
        view.ApplyStreamUpdate(ImageTranslationStreamFilter.Apply(update, Request, evidence, true));
        Assert.Equal("保留译文\n后续", view.TranslatedText);
        Assert.Equal("p2", Assert.Single(view.Segments).Id);
    }

    [Fact]
    public void ProvenEmptyResultRetractsOnlyIneligibleInkAndText()
    {
        var view = new TranslationResultViewModel { IsBusy = true, IsInPlaceEnabled = true };
        var block = new TranslatedBlock("p1", "S", "不应翻译署名", new(10, 10, 100, 40));
        var update = new TranslationStreamUpdate(block.Translation) { ImageSegments = [block], StreamingTail = "" };
        view.ApplyStreamUpdate(ImageTranslationStreamFilter.Apply(update, Request,
            [new("credit", "Sarah Meyssonnier/Reuters", .99, block.Bounds, 0)], true));
        Assert.Empty(view.Segments);
        Assert.Equal("", view.TranslatedText);
    }
}
