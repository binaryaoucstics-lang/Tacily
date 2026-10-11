using ScreenTranslator.App.Services.Capture;

namespace ScreenTranslator.IntegrationTests.Capture;

public sealed class CaptureContextSequencerTests
{
    [Fact]
    public async Task Hides_And_Yields_Before_Capturing_Context()
    {
        var calls = new List<string>();

        var result = await CaptureContextSequencer.CaptureAsync(
            () => calls.Add("hide"),
            () =>
            {
                calls.Add("yield");
                return Task.CompletedTask;
            },
            () =>
            {
                calls.Add("capture");
                return 42;
            });

        Assert.Equal(42, result);
        Assert.Equal(["hide", "yield", "capture"], calls);
    }
}
