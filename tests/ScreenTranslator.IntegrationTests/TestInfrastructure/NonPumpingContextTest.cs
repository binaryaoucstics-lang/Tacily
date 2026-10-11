using System.Runtime.ExceptionServices;

namespace ScreenTranslator.IntegrationTests.TestInfrastructure;

public static class NonPumpingContextTest
{
    public static void Run(Action action, TimeSpan timeout)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new NonPumpingSynchronizationContext());
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(timeout))
        {
            throw new TimeoutException(
                "The operation deadlocked while the UI synchronization context was not pumping.");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            // Intentionally do not pump posted continuations.
        }
    }
}
