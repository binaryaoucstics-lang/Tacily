using ScreenTranslator.App.Services.Hotkeys;
using ScreenTranslator.Core.Hotkeys;

namespace ScreenTranslator.IntegrationTests.Hotkeys;

public sealed class HotkeyRegistrationCoordinatorTests
{
    [Fact]
    public void Replace_Restores_Old_Gesture_When_New_Gesture_Conflicts()
    {
        var service = new FakeHotkeyService(failOn: ["Ctrl+Alt+G"]);
        var coordinator = new HotkeyRegistrationCoordinator(service);
        var previous = HotkeyGesture.Parse("Ctrl+Shift+G");
        Assert.True(coordinator.TryEnable(previous).Succeeded);

        var result = coordinator.TryReplace(HotkeyGesture.Parse("Ctrl+Alt+G"));

        Assert.False(result.Succeeded);
        Assert.True(result.IsEnabled);
        Assert.Equal("Ctrl+Shift+G", service.RegisteredGesture);
        Assert.Equal(previous, coordinator.CurrentGesture);
    }

    [Fact]
    public void Replace_Disables_Hotkey_When_New_And_Old_Gestures_Both_Fail()
    {
        var service = new FakeHotkeyService();
        var coordinator = new HotkeyRegistrationCoordinator(service);
        Assert.True(
            coordinator.TryEnable(
                HotkeyGesture.Parse("Ctrl+Shift+G")).Succeeded);
        service.FailOn.UnionWith(["Ctrl+Alt+G", "Ctrl+Shift+G"]);

        var result = coordinator.TryReplace(HotkeyGesture.Parse("Ctrl+Alt+G"));

        Assert.False(result.Succeeded);
        Assert.False(result.IsEnabled);
        Assert.Null(service.RegisteredGesture);
        Assert.Contains("无法恢复", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Suspend_And_Restore_Preserve_The_Saved_Gesture()
    {
        var service = new FakeHotkeyService();
        var coordinator = new HotkeyRegistrationCoordinator(service);
        var gesture = HotkeyGesture.Parse("Win+Shift+S");
        coordinator.TryEnable(gesture);

        coordinator.Suspend();
        var result = coordinator.TryRestoreCurrent();

        Assert.False(result.Succeeded);
        Assert.True(result.IsEnabled);
        Assert.Equal("Win+Shift+S", service.RegisteredGesture);
    }

    private sealed class FakeHotkeyService(
        IEnumerable<string>? failOn = null) : IGlobalHotkeyService
    {
        public HashSet<string> FailOn { get; } =
            new(failOn ?? [], StringComparer.Ordinal);

        public event EventHandler? CaptureRequested
        {
            add { }
            remove { }
        }

        public bool IsRegistered => RegisteredGesture is not null;

        public string? RegisteredGesture { get; private set; }

        public void Register(HotkeyGesture gesture)
        {
            var persisted = gesture.ToPersistedString();
            if (FailOn.Contains(persisted))
            {
                throw new HotkeyConflictException($"冲突：{persisted}");
            }

            RegisteredGesture = persisted;
        }

        public void Unregister() => RegisteredGesture = null;

        public void Dispose()
        {
        }
    }
}
