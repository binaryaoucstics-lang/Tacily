using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class PageTextStreamingTests
{
    private static readonly OcrBlock[] Input =
    [new("a", "Original one", 1, default, 0), new("b", "Original two", 1, default, 1)];

    [Fact]
    public void EveryPrefixExposesOnlyMatchingDecodedTranslationWithoutHalfSurrogates()
    {
        const string json = """{"metadata":{"blocks":[{"id":"a","translation":"fake"}]},"blocks":[{"id":"b","sourceText":"\"translation\":\"wrong\"","translation":"第二\uD83D\uDE00"},{"id":"a","translation":"第一\n段"}]}""";
        for (var length = 0; length <= json.Length; length++)
        {
            foreach (var block in PageTextStreaming.ReadStreaming(json[..length], Input))
            {
                Assert.Equal(Input.Single(item => item.Id == block.Id).Text, block.SourceText);
                Assert.StartsWith(block.Translation, block.Id == "a" ? "第一\n段" : "第二😀");
                Assert.False(block.Translation.Length > 0 && char.IsHighSurrogate(block.Translation[^1]));
                Assert.Equal(default, block.Bounds);
            }
        }
        Assert.Equal(new[] { "a", "b" }, PageTextStreaming.ReadStreaming(json, Input).Select(block => block.Id));
    }

    [Fact]
    public void UnknownDuplicateAndNestedIdsNeverAttachToARealNode()
    {
        const string json = """{"blocks":[{"id":"not-a-node","translation":"oops"},{"id":"a","translation":"一"},{"id":"a","translation":"二"},{"metadata":{"id":"b","translation":"bad"}}]}""";
        Assert.Empty(PageTextStreaming.ReadStreaming(json, Input));
    }

    [Fact]
    public void IdMayFollowTranslationAndPartialStringIsNotMarkedFinal()
    {
        var reversed = PageTextStreaming.ReadStreaming("""{"blocks":[{"translation":"文本","id":"a"}]}""", Input);
        Assert.Equal("文本", Assert.Single(reversed).Translation);
        var partial = PageTextStreaming.ReadStreaming("""{"blocks":[{"id":"b","translation":"还在输""", Input);
        Assert.Equal("还在输", Assert.Single(partial).Translation);
        Assert.False(partial[0].IsComplete);
    }

    [Theory]
    [InlineData("{\"blocks\":[{\"id\":\"a\",\"translation\":\"一\"}]}")]
    [InlineData("{\"blocks\":[{\"id\":\"a\",\"translation\":\"一\"},{\"id\":\"a\",\"translation\":\"二\"}]}")]
    [InlineData("{\"blocks\":[{\"id\":\"a\",\"translation\":\"一\"},{\"id\":\"c\",\"translation\":\"二\"}]}")]
    [InlineData("{\"blocks\":[{\"id\":\"a\",\"translation\":\"一\",\"translation\":\"修正\"},{\"id\":\"b\",\"translation\":\"二\"}]}")]
    [InlineData("{\"blocks\":[{\"id\":\"a\",\"translation\":\"一\"},{\"id\":\"b\",\"translation\":\"   \"}]}")]
    public void FinalValidationRejectsMissingDuplicateUnknownAndEmptyBlocks(string json)
        => Assert.Throws<TranslationFormatException>(() => PageTextStreaming.ParseComplete(json, Input));

    [Fact]
    public void FinalValidationJoinsByIdRatherThanArrayOrder()
    {
        var final = PageTextStreaming.ParseComplete("""{"blocks":[{"id":"b","translation":"二"},{"id":"a","translation":"一"}]}""", Input);
        Assert.Equal(new[] { "一", "二" }, final.Select(block => block.Translation));
        Assert.All(final, block => Assert.True(block.IsComplete));
    }
}
