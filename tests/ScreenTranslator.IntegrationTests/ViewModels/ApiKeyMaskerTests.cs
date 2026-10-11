using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class ApiKeyMaskerTests
{
    [Fact]
    public void Mask_Reveals_Only_The_Last_Four_Characters()
    {
        Assert.Equal(
            "************abd4",
            ApiKeyMasker.Mask("sk-1234567890abd4"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("abcd", "****")]
    [InlineData("abc", "***")]
    public void Mask_Hides_Short_Or_Blank_Values(
        string value,
        string expected)
    {
        Assert.Equal(expected, ApiKeyMasker.Mask(value));
    }
}
