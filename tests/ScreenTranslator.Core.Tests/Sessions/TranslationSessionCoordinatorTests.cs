using ScreenTranslator.Core.Sessions;

namespace ScreenTranslator.Core.Tests.Sessions;

public sealed class TranslationSessionCoordinatorTests
{
    [Fact]
    public void Older_Session_Cannot_Publish_After_New_Session_Starts()
    {
        using var coordinator = new TranslationSessionCoordinator();
        var first = coordinator.Start();
        var second = coordinator.Start();

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.Equal(TranslationSessionState.Cancelled, first.State);
        Assert.False(coordinator.TryPublish(first.Id));
        Assert.True(coordinator.TryPublish(second.Id));
    }

    [Fact]
    public void State_Machine_Allows_Only_Forward_Processing_Transitions()
    {
        using var coordinator = new TranslationSessionCoordinator();
        var session = coordinator.Start();

        Assert.False(coordinator.TryTransition(session.Id, TranslationSessionState.Translating));
        Assert.True(coordinator.TryTransition(session.Id, TranslationSessionState.Ocr));
        Assert.True(coordinator.TryTransition(session.Id, TranslationSessionState.Translating));
        Assert.True(coordinator.TryTransition(session.Id, TranslationSessionState.Displayed));
        Assert.False(coordinator.TryTransition(session.Id, TranslationSessionState.Ocr));
        Assert.Equal(TranslationSessionState.Displayed, session.State);
    }

    [Fact]
    public void Cancelled_Session_Cannot_Publish()
    {
        using var coordinator = new TranslationSessionCoordinator();
        var session = coordinator.Start();

        Assert.True(coordinator.CancelCurrent());

        Assert.True(session.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.TryPublish(session.Id));
    }
}
