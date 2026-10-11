using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class ImageTranslationTests
{
    private const string Response = """{"blocks":[{"id":"title","sourceText":"Title","translation":"标题","bbox":[100,20,800,120]},{"id":"second","sourceText":"Paragraph","translation":"下一段","bbox":[120,500,900,800]}]}""";

    [Fact]
    public async Task Vlm_Can_Return_All_Image_Paragraphs_For_One_Placeholder_And_Repair_Keeps_Image()
    {
        var provider = new Provider("{}", Response);
        var request = new TranslationRequest("auto", "zh-CN", TranslationStyle.Natural, "",
            [new OcrBlock("block-1", "", 1, new PixelRect(0, 0, 2000, 1000), 0)])
            { ImagePng = [1, 2, 3], LocateImageText = true };
        var result = await new TranslationOrchestrator(provider, new TranslationResponseValidator()).TranslateAsync(request, CancellationToken.None);
        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal(new PixelRect(200, 20, 1400, 100), result.Blocks[0].Bounds);
        Assert.Equal(new PixelRect(240, 500, 1560, 300), result.Blocks[1].Bounds);
        Assert.Equal(request.ImagePng, provider.Requests[1].ImagePng);
        Assert.True(provider.Requests[1].LocateImageText);
    }

    [Theory]
    [InlineData("[-1,0,400,100]")]
    [InlineData("[0,0,1001,100]")]
    [InlineData("[400,0,200,100]")]
    [InlineData("[0,0,200,0]")]
    [InlineData("[0,0,200]")]
    [InlineData("[\"bad\",0,200,100]")]
    public void Invalid_Positions_Are_Rejected(string bounds)
    {
        var json = "{\"blocks\":[{\"id\":\"p1\",\"sourceText\":\"Hello\",\"translation\":\"你好\",\"bbox\":" + bounds + "}]}";
        Assert.Throws<TranslationFormatException>(() => new TranslationResponseValidator().ParseImageBlocks(json, new PixelSize(1000, 500)));
    }

    [Fact]
    public void Complete_Groups_Become_Available_During_Streaming_Without_Waiting_For_Last_Group()
    {
        var prefix = Response[..Response.IndexOf(",\"translation\":\"下一段", StringComparison.Ordinal)];
        var groups = ImageTranslationStreamParser.ReadCompletedBlocks(prefix, new PixelSize(2000, 1000));
        Assert.Single(groups);
        Assert.Equal("标题", groups[0].Translation);
        Assert.Equal(2, ImageTranslationStreamParser.ReadCompletedBlocks(Response, new PixelSize(2000, 1000)).Count);
    }

    [Fact]
    public void Mapping_Handles_Letterboxing_Negative_Monitor_Origins_And_Window_Movement()
    {
        var preview = ImageTextMapping.ToPreview(new PixelRect(200, 100, 400, 100), new PixelSize(1000, 500), new DipSize(300, 180));
        Assert.Equal(new DipRect(60, 45, 120, 30), preview);
        var source = ImageTextMapping.ToScreen(new PixelRect(200, 100, 400, 100), new PixelRect(-1600, 300, 1000, 500),
            new PixelRect(-1800, 100, 1600, 900), new PixelRect(-1700, 150, 1600, 900));
        Assert.Equal(new PixelRect(-1300, 450, 400, 100), source);
        Assert.Null(ImageTextMapping.ToScreen(default, default, new PixelRect(0, 0, 100, 100), new PixelRect(0, 0, 200, 100)));
    }

    [Fact]
    public void Ocr_Calibrates_Vlm_Position_Without_Changing_Translation_Or_Including_Unrelated_Text()
    {
        var segment = new TranslatedBlock("p1", "First line.\nSecond line!", "译文", new PixelRect(40, 190, 500, 100));
        var lines = new[] {
            new OcrBlock("a", "First line", 1, new PixelRect(44, 170, 200, 30), 0),
            new OcrBlock("b", "Second line", 1, new PixelRect(44, 210, 250, 30), 1),
            new OcrBlock("c", "Unrelated", 1, new PixelRect(44, 255, 200, 30), 2) };
        var calibrated = ImageTextPositionCalibrator.Calibrate([segment], lines, new PixelSize(1000, 650));
        Assert.Equal(new PixelRect(40, 166, 258, 78), calibrated[0].Bounds);
        Assert.Equal(segment.Translation, calibrated[0].Translation);
        Assert.Equal(segment.SourceText, calibrated[0].SourceText);
    }

    private sealed class Provider(params string[] responses) : ITranslationProvider
    {
        private readonly Queue<string> _responses = new(responses);
        public List<TranslationRequest> Requests { get; } = [];
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken cancellationToken)
        { Requests.Add(request); return Task.FromResult(_responses.Dequeue()); }
    }
}
