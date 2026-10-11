using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.IntegrationTests.Capture;

public sealed class CaptureSessionPolicyTests
{
    [Theory]
    [InlineData(CaptureMode.Single, false, false)]
    [InlineData(CaptureMode.Multiple, false, true)]
    [InlineData(CaptureMode.Single, true, true)]
    public void Queue_Is_Used_For_Multiple_Or_An_Existing_Multiple_Session(
        CaptureMode mode,
        bool multipleStarted,
        bool expected)
    {
        Assert.Equal(
            expected,
            CaptureSessionPolicy.ShouldUseQueue(mode, multipleStarted));
    }

    [Fact]
    public void Queue_Limit_Takes_Precedence_Over_Switching_To_Single()
    {
        var decision = CaptureSessionPolicy.DecideAfterEnqueue(
            CaptureMode.Single,
            pendingAtAcceptance: 5,
            capacity: 5);

        Assert.Equal(
            CaptureAcquisitionDecision.StopQueueFull,
            decision);
    }

    [Theory]
    [InlineData(CaptureMode.Multiple, 4, CaptureAcquisitionDecision.Continue)]
    [InlineData(CaptureMode.Single, 4, CaptureAcquisitionDecision.StopAfterCurrent)]
    public void Enqueue_Decision_Uses_Mode_Below_Capacity(
        CaptureMode mode,
        int pending,
        CaptureAcquisitionDecision expected)
    {
        Assert.Equal(
            expected,
            CaptureSessionPolicy.DecideAfterEnqueue(mode, pending, 5));
    }
}
