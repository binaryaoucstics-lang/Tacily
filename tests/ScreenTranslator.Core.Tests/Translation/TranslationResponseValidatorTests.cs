using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class TranslationResponseValidatorTests
{
    private readonly TranslationResponseValidator _validator = new();

    [Theory]
    [InlineData("""{"blocks":[{"id":"b1","translation":"甲"},{"id":"b1","translation":"乙"}]}""")]
    [InlineData("""{"blocks":[{"id":"unknown","translation":"甲"}]}""")]
    [InlineData("""{"blocks":[]}""")]
    [InlineData("""{"blocks":[{"id":"b1"}]}""")]
    [InlineData("""not-json""")]
    public void Validate_Rejects_Missing_Duplicate_Unknown_Or_Malformed_Ids(string json)
    {
        Assert.Throws<TranslationFormatException>(() => _validator.Parse(json, ["b1"]));
    }

    [Fact]
    public void Validate_Accepts_OutOfOrder_Blocks()
    {
        var result = _validator.Parse(
            """{"blocks":[{"id":"b2","translation":"乙"},{"id":"b1","translation":"甲"}]}""",
            ["b1", "b2"]);

        Assert.Equal("甲", result["b1"]);
        Assert.Equal("乙", result["b2"]);
    }
}
