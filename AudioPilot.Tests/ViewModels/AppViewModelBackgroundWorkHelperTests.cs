using System.Collections.Concurrent;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelBackgroundWorkHelperTests
{
    [Fact]
    public void SnapshotPendingTasks_ReturnsCurrentTasks()
    {
        var backgroundTasks = new ConcurrentDictionary<int, Task>();
        backgroundTasks[1] = Task.CompletedTask;

        Task[] snapshot = AppViewModelBackgroundWorkHelper.SnapshotPendingTasks(backgroundTasks);

        Assert.Single(snapshot);
    }

    [Fact]
    public void Cancel_CancelsBackgroundToken()
    {
        using var backgroundWorkCts = new CancellationTokenSource();

        AppViewModelBackgroundWorkHelper.Cancel(backgroundWorkCts);

        Assert.True(backgroundWorkCts.IsCancellationRequested);
    }

    [Fact]
    public void DisposeResources_DisposesTokenAndClearsTasks()
    {
        var backgroundTasks = new ConcurrentDictionary<int, Task>();
        backgroundTasks[1] = Task.CompletedTask;
        var backgroundWorkCts = new CancellationTokenSource();

        AppViewModelBackgroundWorkHelper.DisposeResources(backgroundWorkCts, backgroundTasks);

        Assert.Empty(backgroundTasks);
    }

    [Fact]
    public async Task TryQueue_QueuesBackgroundOperation_WhenNotCleaningUp()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelBackgroundWorkHelperTests), "appvm-background-helper.log");
        var helper = new AppViewModelBackgroundWorkHelper(loggerScope.Logger, () => false);
        var backgroundTasks = new ConcurrentDictionary<int, Task>();
        int backgroundTaskId = 0;
        int calls = 0;

        bool queued = helper.TryQueue(
            backgroundTasks,
            ref backgroundTaskId,
            new CancellationTokenSource(),
            _ =>
            {
                calls++;
                return Task.CompletedTask;
            },
            "test-op");

        Assert.True(queued);
        Task completion = Task.WhenAll([.. backgroundTasks.Values]);
        await completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TryQueue_DoesNotLeaveCompletedTasksTracked_WhenOperationCompletesSynchronously()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelBackgroundWorkHelperTests), "appvm-background-helper-sync.log");
        var helper = new AppViewModelBackgroundWorkHelper(loggerScope.Logger, () => false);
        var backgroundTasks = new ConcurrentDictionary<int, Task>();
        using var backgroundWorkCts = new CancellationTokenSource();
        int backgroundTaskId = 0;

        for (int index = 0; index < 256; index++)
        {
            bool queued = helper.TryQueue(
                backgroundTasks,
                ref backgroundTaskId,
                backgroundWorkCts,
                static _ => Task.CompletedTask,
                $"sync-op-{index}");

            Assert.True(queued);
        }

        await TestExecutionGuards.WaitUntilAsync(
            () => backgroundTasks.IsEmpty,
            "Completed background tasks remained tracked past the allotted timeout.");
    }
}
