using ScreenTranslator.Core.Sessions;

namespace ScreenTranslator.Core.Tests.Sessions;

public sealed class SequentialWorkQueueTests
{
    [Fact]
    public async Task Enqueue_Returns_The_Pending_Count_At_Acceptance()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new SequentialWorkQueue<int>(
            2,
            async (_, cancellationToken) =>
                await release.Task.WaitAsync(cancellationToken));

        var firstPending = await queue.EnqueueAsync(1);
        var secondPending = await queue.EnqueueAsync(2);

        Assert.Equal(1, firstPending);
        Assert.Equal(2, secondPending);

        release.TrySetResult();
        await queue.CompleteAsync();
    }

    [Fact]
    public async Task Processes_Items_In_FIFO_Order()
    {
        var processed = new List<int>();
        await using var queue = new SequentialWorkQueue<int>(
            capacity: 5,
            async (item, _) =>
            {
                processed.Add(item);
                await Task.Yield();
            });

        await queue.EnqueueAsync(1);
        await queue.EnqueueAsync(2);
        await queue.EnqueueAsync(3);
        await queue.CompleteAsync();

        Assert.Equal([1, 2, 3], processed);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Continues_After_One_Item_Fails()
    {
        var processed = new List<int>();
        var failures = new List<WorkItemFailedEventArgs<int>>();
        await using var queue = new SequentialWorkQueue<int>(
            capacity: 5,
            (item, _) =>
            {
                if (item == 2)
                {
                    throw new InvalidOperationException("bad item");
                }

                processed.Add(item);
                return Task.CompletedTask;
            });
        queue.ItemFailed += (_, args) => failures.Add(args);

        await queue.EnqueueAsync(1);
        await queue.EnqueueAsync(2);
        await queue.EnqueueAsync(3);
        await queue.CompleteAsync();

        Assert.Equal([1, 3], processed);
        var failure = Assert.Single(failures);
        Assert.Equal(2, failure.Item);
        Assert.Equal("bad item", failure.Exception.Message);
    }

    [Fact]
    public async Task Sixth_Item_Waits_Until_Capacity_Is_Available()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new SequentialWorkQueue<int>(
            capacity: 5,
            async (item, cancellationToken) =>
            {
                if (item == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
            });

        for (var item = 1; item <= 5; item++)
        {
            await queue.EnqueueAsync(item);
        }

        await firstStarted.Task;
        var sixth = queue.EnqueueAsync(6).AsTask();
        Assert.False(sixth.IsCompleted);

        releaseFirst.TrySetResult();
        await sixth;
        await queue.CompleteAsync();

        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Rejects_Non_Positive_Capacity()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new SequentialWorkQueue<int>(
                0,
                (_, _) => Task.CompletedTask));

        Assert.Equal("capacity", exception.ParamName);
        await Task.CompletedTask;
    }
}
