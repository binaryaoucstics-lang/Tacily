namespace ScreenTranslator.Core.Sessions;

public sealed class TranslationSessionCoordinator : IDisposable
{
    private readonly object _gate = new();
    private TranslationSession? _current;
    private bool _disposed;

    public TranslationSession? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public TranslationSession Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            CancelWithoutLock(_current);
            _current = new TranslationSession(Guid.NewGuid(), new CancellationTokenSource());
            return _current;
        }
    }

    public bool IsCurrent(Guid sessionId)
    {
        lock (_gate)
        {
            return IsCurrentWithoutLock(sessionId);
        }
    }

    public bool TryPublish(Guid sessionId)
    {
        lock (_gate)
        {
            return IsCurrentWithoutLock(sessionId);
        }
    }

    public bool TryTransition(Guid sessionId, TranslationSessionState nextState)
    {
        lock (_gate)
        {
            if (!IsCurrentWithoutLock(sessionId) || _current is null)
            {
                return false;
            }

            if (!CanTransition(_current.State, nextState))
            {
                return false;
            }

            _current.SetState(nextState);
            if (nextState == TranslationSessionState.Cancelled)
            {
                _current.CancellationSource.Cancel();
            }

            return true;
        }
    }

    public bool Cancel(Guid sessionId)
    {
        lock (_gate)
        {
            if (_current is null || _current.Id != sessionId)
            {
                return false;
            }

            CancelWithoutLock(_current);
            return true;
        }
    }

    public bool CancelCurrent()
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return false;
            }

            CancelWithoutLock(_current);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelWithoutLock(_current);
            _current?.CancellationSource.Dispose();
            _current = null;
        }
    }

    private bool IsCurrentWithoutLock(Guid sessionId) =>
        !_disposed
        && _current is not null
        && _current.Id == sessionId
        && !_current.CancellationToken.IsCancellationRequested
        && _current.State is not TranslationSessionState.Cancelled
            and not TranslationSessionState.Failed;

    private static void CancelWithoutLock(TranslationSession? session)
    {
        if (session is null || session.State == TranslationSessionState.Cancelled)
        {
            return;
        }

        session.SetState(TranslationSessionState.Cancelled);
        session.CancellationSource.Cancel();
    }

    private static bool CanTransition(
        TranslationSessionState current,
        TranslationSessionState next) =>
        next switch
        {
            TranslationSessionState.Cancelled or TranslationSessionState.Failed =>
                current is TranslationSessionState.Selecting
                    or TranslationSessionState.Ocr
                    or TranslationSessionState.Translating,
            TranslationSessionState.Ocr => current == TranslationSessionState.Selecting,
            TranslationSessionState.Translating => current == TranslationSessionState.Ocr,
            TranslationSessionState.Displayed => current == TranslationSessionState.Translating,
            _ => false,
        };
}
