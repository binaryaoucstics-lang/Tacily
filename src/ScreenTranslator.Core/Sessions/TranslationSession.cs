namespace ScreenTranslator.Core.Sessions;

public enum TranslationSessionState
{
    Idle,
    Selecting,
    Ocr,
    Translating,
    Displayed,
    Cancelled,
    Failed,
}

public sealed class TranslationSession
{
    private int _state;

    internal TranslationSession(Guid id, CancellationTokenSource cancellationSource)
    {
        Id = id;
        CancellationSource = cancellationSource;
        _state = (int)TranslationSessionState.Selecting;
    }

    public Guid Id { get; }

    public CancellationToken CancellationToken => CancellationSource.Token;

    public TranslationSessionState State =>
        (TranslationSessionState)Volatile.Read(ref _state);

    internal CancellationTokenSource CancellationSource { get; }

    internal void SetState(TranslationSessionState state) =>
        Volatile.Write(ref _state, (int)state);
}
