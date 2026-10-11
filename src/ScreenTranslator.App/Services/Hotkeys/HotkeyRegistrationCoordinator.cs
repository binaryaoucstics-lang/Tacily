using ScreenTranslator.Core.Hotkeys;

namespace ScreenTranslator.App.Services.Hotkeys;

public sealed class HotkeyRegistrationCoordinator
{
    private readonly IGlobalHotkeyService _service;

    public HotkeyRegistrationCoordinator(IGlobalHotkeyService service)
    {
        _service = service;
    }

    public HotkeyGesture CurrentGesture { get; private set; } =
        HotkeyGesture.Default;

    public bool IsEnabled { get; private set; }

    public HotkeyReplacementResult TryEnable(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        _service.Unregister();

        try
        {
            _service.Register(gesture);
            CurrentGesture = gesture;
            IsEnabled = true;
            return HotkeyReplacementResult.Success(gesture);
        }
        catch (HotkeyConflictException exception)
        {
            IsEnabled = false;
            return HotkeyReplacementResult.Failure(
                gesture,
                isEnabled: false,
                exception.Message);
        }
    }

    public HotkeyReplacementResult TryReplace(HotkeyGesture next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var previous = CurrentGesture;
        _service.Unregister();
        IsEnabled = false;

        try
        {
            _service.Register(next);
            CurrentGesture = next;
            IsEnabled = true;
            return HotkeyReplacementResult.Success(next);
        }
        catch (HotkeyConflictException conflict)
        {
            return Restore(previous, conflict.Message);
        }
    }

    public void Suspend()
    {
        _service.Unregister();
        IsEnabled = false;
    }

    public void Disable()
    {
        _service.Unregister();
        IsEnabled = false;
    }

    public HotkeyReplacementResult TryRestoreCurrent(string? failureMessage = null) =>
        Restore(
            CurrentGesture,
            failureMessage ?? "快捷键修改已取消。");

    private HotkeyReplacementResult Restore(
        HotkeyGesture previous,
        string failureMessage)
    {
        try
        {
            _service.Register(previous);
            CurrentGesture = previous;
            IsEnabled = true;
            return HotkeyReplacementResult.Failure(
                previous,
                isEnabled: true,
                failureMessage);
        }
        catch (HotkeyConflictException restoreConflict)
        {
            IsEnabled = false;
            return HotkeyReplacementResult.Failure(
                previous,
                isEnabled: false,
                $"{failureMessage} 原快捷键也无法恢复：{restoreConflict.Message}");
        }
    }
}

public sealed record HotkeyReplacementResult(
    bool Succeeded,
    bool IsEnabled,
    HotkeyGesture Gesture,
    string Message)
{
    public static HotkeyReplacementResult Success(HotkeyGesture gesture) =>
        new(true, true, gesture, "快捷键已更新");

    public static HotkeyReplacementResult Failure(
        HotkeyGesture gesture,
        bool isEnabled,
        string message) =>
        new(false, isEnabled, gesture, message);
}
