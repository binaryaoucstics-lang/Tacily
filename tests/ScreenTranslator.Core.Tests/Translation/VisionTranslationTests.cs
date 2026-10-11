using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class VisionTranslationTests
{
    [Fact]
    public async Task Original_Text_Comes_From_Vision_Response_And_Format_Repair_Keeps_Image()
    {
        var provider = new ImageProvider();
        var orchestrator = new TranslationOrchestrator(provider, new TranslationResponseValidator());
        var result = await orchestrator.TranslateAsync(new TranslationRequest("auto", "zh-CN", TranslationStyle.Natural, "", [
            new OcrBlock("block-1", "incorrect OCR hint", 1, new PixelRect(0, 0, 200, 100), 0)]) { ImagePng = [1, 2, 3] }, CancellationToken.None);
        Assert.Equal("Hello world", Assert.Single(result.Blocks).SourceText);
        Assert.Equal("你好，世界", result.Blocks[0].Translation);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(provider.Requests[0].ImagePng, provider.Requests[1].ImagePng);
    }

    [Fact]
    public void Unreadable_Image_Does_Not_Produce_A_Fabricated_Translation()
    {
        Assert.Throws<TranslationNoTextException>(() => new TranslationResponseValidator().ParseSources(
            """{"blocks":[{"id":"block-1","sourceText":"","translation":""}]}""", ["block-1"]));
    }

    private sealed class ImageProvider : ITranslationProvider
    {
        public List<TranslationRequest> Requests { get; } = [];
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Requests.Count == 1
                ? """{"blocks":[{"id":"block-1","translation":"你好，世界"}]}"""
                : """{"blocks":[{"id":"block-1","sourceText":"Hello world","translation":"你好，世界"}]}""");
        }
    }
}
