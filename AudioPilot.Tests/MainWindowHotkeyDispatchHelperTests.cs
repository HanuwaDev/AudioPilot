using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests;

[Collection("MessageBoxServiceIsolation")]
public sealed class MainWindowHotkeyDispatchHelperTests
{
    [Fact]
    public void Dispatch_RunsActionOnDispatcher()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            int calls = 0;
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

            MainWindowHotkeyDispatchHelper.Dispatch(
                dispatcher,
                Logger.Instance,
                () => calls++,
                "dispatch-failed",
                nameof(Dispatch_RunsActionOnDispatcher));

            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.Equal(1, calls);
        });
    }

    [Fact]
    public void ExecuteAsync_ShowsError_WhenActionThrows()
    {
        var messages = new RecordingMessageBoxNative();
        MessageBoxService.SetNativeForTests(messages);

        try
        {
            TestExecutionGuards.RunOnSharedSta(() =>
            {
                MainWindowHotkeyDispatchHelper.ExecuteAsync(
                    () => throw new InvalidOperationException("boom"),
                    Logger.Instance,
                    Dispatcher.CurrentDispatcher,
                    "hotkey-failed",
                    "User-visible failure",
                    nameof(ExecuteAsync_ShowsError_WhenActionThrows))
                    .GetAwaiter()
                    .GetResult();

                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            });

            Assert.Contains(messages.ErrorMessages, call =>
                string.Equals(call.message, "User-visible failure", StringComparison.Ordinal));
        }
        finally
        {
            MessageBoxService.ResetNativeForTests();
        }
    }

    [Fact]
    public void InvokeAsync_CompletesAfterAsyncActionRunsOnDispatcher()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            bool completed = false;

            Task task = MainWindowHotkeyDispatchHelper.InvokeAsync(
                Dispatcher.CurrentDispatcher,
                Logger.Instance,
                async () =>
                {
                    await Task.Yield();
                    completed = true;
                },
                "hotkey-failed",
                nameof(InvokeAsync_CompletesAfterAsyncActionRunsOnDispatcher));

            TestPrivateAccess.RunTaskOnDispatcher(task);

            Assert.True(completed);
        });
    }

    [Fact]
    public void InvokeAsync_ReturnsCompletedTask_WhenDispatcherShutdownHasStarted()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeShutdown();

            Task task = MainWindowHotkeyDispatchHelper.InvokeAsync(
                dispatcher,
                Logger.Instance,
                static () => Task.CompletedTask,
                "hotkey-failed",
                nameof(InvokeAsync_ReturnsCompletedTask_WhenDispatcherShutdownHasStarted));

            Assert.True(task.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public void ExecuteAsync_DoesNotShowError_WhenDispatcherShutdownHasStarted()
    {
        var messages = new RecordingMessageBoxNative();
        MessageBoxService.SetNativeForTests(messages);

        try
        {
            TestExecutionGuards.RunIsolatedSta(() =>
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.InvokeShutdown();

                MainWindowHotkeyDispatchHelper.ExecuteAsync(
                    () => throw new InvalidOperationException("boom"),
                    Logger.Instance,
                    dispatcher,
                    "hotkey-failed",
                    "User-visible failure",
                    nameof(ExecuteAsync_DoesNotShowError_WhenDispatcherShutdownHasStarted))
                    .GetAwaiter()
                    .GetResult();
            });

            Assert.Empty(messages.ErrorMessages);
        }
        finally
        {
            MessageBoxService.ResetNativeForTests();
        }
    }
}
