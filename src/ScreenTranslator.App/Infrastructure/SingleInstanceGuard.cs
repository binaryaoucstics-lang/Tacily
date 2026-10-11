using System.Threading;

namespace ScreenTranslator.App.Infrastructure;

public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "GlobalAITranslator.Singleton.v1";
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _ownsMutex);
    }

    public bool IsPrimaryInstance => _ownsMutex;

    public void Dispose()
    {
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
