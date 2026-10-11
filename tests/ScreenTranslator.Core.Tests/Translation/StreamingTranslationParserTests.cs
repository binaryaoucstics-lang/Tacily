using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class StreamingTranslationParserTests
{
    [Fact]
    public void Every_Json_Prefix_Only_Exposes_Decoded_Translation()
    {
        const string json = """{"blocks":[{"id":"block-1","sourceText":"Never show this as a translation: \"translation\": \"wrong\"","translation":"你好\n\"世界\" \uD83D\uDE00"}]}""";
        const string expected = "你好\n\"世界\" 😀";
        for (var length = 0; length <= json.Length; length++)
        {
            var visible = StreamingTranslationParser.ReadTranslation(json[..length]);
            Assert.StartsWith(visible, expected);
            Assert.DoesNotContain("Never show", visible, StringComparison.Ordinal);
            Assert.False(visible.Length > 0 && char.IsHighSurrogate(visible[^1]));
        }
        Assert.Equal(expected, StreamingTranslationParser.ReadTranslation(json));
    }

    [Fact]
    public void Multiple_Blocks_Stream_In_Order()
    {
        Assert.Equal("第一段\n第二", StreamingTranslationParser.ReadTranslation(
            """{"blocks":[{"id":"a","translation":"第一段"},{"id":"b","translation":"第二"""));
    }
}
