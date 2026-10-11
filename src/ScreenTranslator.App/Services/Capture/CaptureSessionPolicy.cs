using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.App.Services.Capture;

public enum CaptureAcquisitionDecision
{
    Continue,
    StopAfterCurrent,
    StopQueueFull,
}

public static class CaptureSessionPolicy
{
    public static bool ShouldUseQueue(
        CaptureMode completedMode,
        bool multipleStarted) =>
        completedMode == CaptureMode.Multiple || multipleStarted;

    public static CaptureAcquisitionDecision DecideAfterEnqueue(
        CaptureMode completedMode,
        int pendingAtAcceptance,
        int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (pendingAtAcceptance >= capacity)
        {
            return CaptureAcquisitionDecision.StopQueueFull;
        }

        return completedMode == CaptureMode.Single
            ? CaptureAcquisitionDecision.StopAfterCurrent
            : CaptureAcquisitionDecision.Continue;
    }
}
