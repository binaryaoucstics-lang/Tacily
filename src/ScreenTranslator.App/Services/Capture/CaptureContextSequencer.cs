namespace ScreenTranslator.App.Services.Capture;

public static class CaptureContextSequencer
{
    public static async Task<T> CaptureAsync<T>(
        Action hideSurfaces,
        Func<Task> yieldUi,
        Func<T> captureContext)
    {
        ArgumentNullException.ThrowIfNull(hideSurfaces);
        ArgumentNullException.ThrowIfNull(yieldUi);
        ArgumentNullException.ThrowIfNull(captureContext);

        hideSurfaces();
        await yieldUi();
        return captureContext();
    }
}
