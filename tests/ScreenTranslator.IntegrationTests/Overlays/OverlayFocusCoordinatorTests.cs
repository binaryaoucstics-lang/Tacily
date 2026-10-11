using ScreenTranslator.App.Services.Overlays;

namespace ScreenTranslator.IntegrationTests.Overlays;

public sealed class OverlayFocusCoordinatorTests
{
    [Fact]
    public void Foreground_Changes_Hide_And_Restore_All_Remaining_Overlays()
    {
        var first = new FakeTarget();
        var second = new FakeTarget();
        var coordinator = new OverlayFocusCoordinator(
            new IntPtr(100),
            [first, second]);

        coordinator.HandleForegroundChanged(new IntPtr(200));
        coordinator.Remove(first);
        coordinator.HandleForegroundChanged(new IntPtr(100));

        Assert.Equal([false], first.States);
        Assert.Equal([false, true], second.States);
        Assert.Equal(1, coordinator.Count);
    }

    [Fact]
    public void Constructor_Rejects_Missing_Source_Window()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new OverlayFocusCoordinator(IntPtr.Zero, []));

        Assert.Equal("sourceWindow", exception.ParamName);
    }

    [Fact]
    public void Multiple_Source_Groups_Hide_And_Restore_Independently()
    {
        var browser = new FakeTarget();
        var chat = new FakeTarget();
        var coordinator = new OverlayFocusCoordinator(
            new IntPtr(100),
            [browser]);
        coordinator.AddGroup(new IntPtr(200), [chat]);

        coordinator.HandleForegroundChanged(new IntPtr(100));
        coordinator.HandleForegroundChanged(new IntPtr(200));

        Assert.Equal([true, false], browser.States);
        Assert.Equal([false, true], chat.States);
        Assert.Equal(2, coordinator.Count);
    }

    private sealed class FakeTarget : IOverlayFocusTarget
    {
        public List<bool> States { get; } = [];

        public void SetSourceWindowActive(bool active) => States.Add(active);
    }
}
