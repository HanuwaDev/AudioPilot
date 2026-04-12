using System.Text.RegularExpressions;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed partial class MainWindowLifecycleCoordinatorTests
{
    [Fact]
    public async Task AwaitShutdownStepAsync_LogsCorrelatedFailure_WhenStepFaults()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowLifecycleCoordinatorTests), "shutdown-step-failure.log", LogLevel.Trace);
        MainWindowLifecycleCoordinator coordinator = CreateCoordinator(loggerScope.Logger, shutdownStepTimeoutMs: 1000);

        await coordinator.AwaitShutdownStepAsync(
            Task.FromException(new InvalidOperationException("boom")),
            "app-viewmodel-cleanup",
            nameof(AwaitShutdownStepAsync_LogsCorrelatedFailure_WhenStepFaults),
            "shutdown:test-failure");

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains("shutdown-step-failed | step=app-viewmodel-cleanup opId=shutdown:test-failure", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AwaitShutdownStepAsync_LogsCorrelatedTimeoutAndLateFault_WhenStepFaultsAfterTimeout()
    {
        using var loggerScope = TestLoggerScope.CreateFileBacked(nameof(MainWindowLifecycleCoordinatorTests), "shutdown-step-timeout.log", LogLevel.Trace);
        MainWindowLifecycleCoordinator coordinator = CreateCoordinator(loggerScope.Logger, shutdownStepTimeoutMs: 50);
        TaskCompletionSource delayedFault = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await coordinator.AwaitShutdownStepAsync(
            delayedFault.Task,
            "audio-service-dispose",
            nameof(AwaitShutdownStepAsync_LogsCorrelatedTimeoutAndLateFault_WhenStepFaultsAfterTimeout),
            "shutdown:test-timeout");

        delayedFault.TrySetException(new InvalidOperationException("late-boom"));

        string logText = TestLogFileAssert.WaitForLogText(
            loggerScope.LogPath,
            2000,
            "shutdown-step-timeout | step=audio-service-dispose timeoutMs=50 opId=shutdown:test-timeout",
            "shutdown-step-faulted-after-timeout | step=audio-service-dispose opId=shutdown:test-timeout");
        loggerScope.Logger.Dispose();

        Assert.Contains("shutdown-step-timeout | step=audio-service-dispose timeoutMs=50 opId=shutdown:test-timeout", logText, StringComparison.Ordinal);
        Assert.Contains("shutdown-step-faulted-after-timeout | step=audio-service-dispose opId=shutdown:test-timeout", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteShutdownAsync_RunsShutdownSequenceInExpectedOrder()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowLifecycleCoordinatorTests), "shutdown-sequence.log", LogLevel.Trace);
        MainWindowLifecycleCoordinator coordinator = CreateCoordinator(loggerScope.Logger, shutdownStepTimeoutMs: 1000);
        List<string> calls = [];

        await coordinator.ExecuteShutdownAsync(
            new MainWindowShutdownDependencies(
                Preparation: new MainWindowShutdownPreparationDependencies(
                    CloseOwnedWindows: () => calls.Add("prepare-close-owned-windows"),
                    DetachGlobalExceptionHandlers: () => calls.Add("prepare-detach-global-exceptions"),
                    DetachAudioEventHandlers: () => calls.Add("prepare-detach-audio"),
                    StopHotplugRefreshDebounce: () => calls.Add("prepare-stop-hotplug-debounce"),
                    DetachSystemEventHandlers: () => calls.Add("prepare-detach-system-events"),
                    DetachWindowEventHandlers: () => calls.Add("prepare-detach-window-events")),
                UnwireHotkeys: () => calls.Add("unwire-hotkeys"),
                CleanupAppViewModelAsync: () =>
                {
                    calls.Add("cleanup-appvm");
                    return Task.CompletedTask;
                },
                DisposeHotkeyService: () => calls.Add("dispose-hotkeys"),
                DisposeRuntimeServicesAsync: () =>
                {
                    calls.Add("dispose-runtime");
                    return Task.CompletedTask;
                },
                DisposeOverlayService: () => calls.Add("dispose-overlay"),
                DisposeDeviceCache: () => calls.Add("dispose-device-cache"),
                DisposeCoreAudioExecutor: () => calls.Add("dispose-core-audio"),
                DisposeShell: () => calls.Add("dispose-shell"),
                DisposeSingleInstance: () => calls.Add("dispose-single-instance")),
            nameof(ExecuteShutdownAsync_RunsShutdownSequenceInExpectedOrder));

        Assert.Equal(
            [
                "prepare-close-owned-windows",
                "prepare-detach-global-exceptions",
                "prepare-detach-audio",
                "prepare-stop-hotplug-debounce",
                "prepare-detach-system-events",
                "prepare-detach-window-events",
                "unwire-hotkeys",
                "cleanup-appvm",
                "dispose-hotkeys",
                "dispose-runtime",
                "dispose-overlay",
                "dispose-device-cache",
                "dispose-core-audio",
                "dispose-shell",
                "dispose-single-instance"
            ],
            calls);
        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("shutdown-complete | opId=shutdown:", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteShutdownAsync_ContinuesAfterSynchronousStepsThrow()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowLifecycleCoordinatorTests), "shutdown-sync-failure.log", LogLevel.Trace);
        MainWindowLifecycleCoordinator coordinator = CreateCoordinator(loggerScope.Logger, shutdownStepTimeoutMs: 1000);
        List<string> calls = [];

        await coordinator.ExecuteShutdownAsync(
            new MainWindowShutdownDependencies(
                Preparation: new MainWindowShutdownPreparationDependencies(
                    CloseOwnedWindows: () =>
                    {
                        calls.Add("prepare-close-owned-windows");
                        throw new InvalidOperationException("close-owned-failed");
                    },
                    DetachGlobalExceptionHandlers: () => calls.Add("prepare-detach-global-exceptions"),
                    DetachAudioEventHandlers: () => calls.Add("prepare-detach-audio"),
                    StopHotplugRefreshDebounce: () => calls.Add("prepare-stop-hotplug-debounce"),
                    DetachSystemEventHandlers: () => calls.Add("prepare-detach-system-events"),
                    DetachWindowEventHandlers: () => calls.Add("prepare-detach-window-events")),
                UnwireHotkeys: () => calls.Add("unwire-hotkeys"),
                CleanupAppViewModelAsync: () =>
                {
                    calls.Add("cleanup-appvm");
                    return Task.CompletedTask;
                },
                DisposeHotkeyService: () =>
                {
                    calls.Add("dispose-hotkeys");
                    throw new InvalidOperationException("dispose-hotkeys-failed");
                },
                DisposeRuntimeServicesAsync: () =>
                {
                    calls.Add("dispose-runtime");
                    return Task.CompletedTask;
                },
                DisposeOverlayService: () => calls.Add("dispose-overlay"),
                DisposeDeviceCache: () => calls.Add("dispose-device-cache"),
                DisposeCoreAudioExecutor: () => calls.Add("dispose-core-audio"),
                DisposeShell: () => calls.Add("dispose-shell"),
                DisposeSingleInstance: () => calls.Add("dispose-single-instance")),
            nameof(ExecuteShutdownAsync_ContinuesAfterSynchronousStepsThrow));

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Equal(
            [
                "prepare-close-owned-windows",
                "prepare-detach-global-exceptions",
                "prepare-detach-audio",
                "prepare-stop-hotplug-debounce",
                "prepare-detach-system-events",
                "prepare-detach-window-events",
                "unwire-hotkeys",
                "cleanup-appvm",
                "dispose-hotkeys",
                "dispose-runtime",
                "dispose-overlay",
                "dispose-device-cache",
                "dispose-core-audio",
                "dispose-shell",
                "dispose-single-instance"
            ],
            calls);
        Assert.Contains("shutdown-step-failed | step=prepare-close-owned-windows opId=shutdown:", logText, StringComparison.Ordinal);
        Assert.Contains("shutdown-step-failed | step=dispose-hotkeys opId=shutdown:", logText, StringComparison.Ordinal);
        Assert.Contains("shutdown-complete | opId=shutdown:", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteShutdownAsync_ContinuesAfterOwnedWindowCloseFailure()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowLifecycleCoordinatorTests), "shutdown-owned-window-close-failure.log", LogLevel.Trace);
        MainWindowLifecycleCoordinator coordinator = CreateCoordinator(loggerScope.Logger, shutdownStepTimeoutMs: 1000);
        List<string> calls = [];

        await coordinator.ExecuteShutdownAsync(
            new MainWindowShutdownDependencies(
                Preparation: new MainWindowShutdownPreparationDependencies(
                    CloseOwnedWindows: () =>
                    {
                        calls.Add("prepare-close-owned-windows");
                        throw new Exception("owned-window-close-failed");
                    },
                    DetachGlobalExceptionHandlers: () => calls.Add("prepare-detach-global-exceptions"),
                    DetachAudioEventHandlers: () => calls.Add("prepare-detach-audio"),
                    StopHotplugRefreshDebounce: () => calls.Add("prepare-stop-hotplug-debounce"),
                    DetachSystemEventHandlers: () => calls.Add("prepare-detach-system-events"),
                    DetachWindowEventHandlers: () => calls.Add("prepare-detach-window-events")),
                UnwireHotkeys: () => calls.Add("unwire-hotkeys"),
                CleanupAppViewModelAsync: () =>
                {
                    calls.Add("cleanup-appvm");
                    return Task.CompletedTask;
                },
                DisposeHotkeyService: () => calls.Add("dispose-hotkeys"),
                DisposeRuntimeServicesAsync: () =>
                {
                    calls.Add("dispose-runtime");
                    return Task.CompletedTask;
                },
                DisposeOverlayService: () => calls.Add("dispose-overlay"),
                DisposeDeviceCache: () => calls.Add("dispose-device-cache"),
                DisposeCoreAudioExecutor: () => calls.Add("dispose-core-audio"),
                DisposeShell: () => calls.Add("dispose-shell"),
                DisposeSingleInstance: () => calls.Add("dispose-single-instance")),
            nameof(ExecuteShutdownAsync_ContinuesAfterOwnedWindowCloseFailure));

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains("shutdown-step-failed | step=prepare-close-owned-windows opId=shutdown:", logText, StringComparison.Ordinal);
        Assert.Contains("shutdown-complete | opId=shutdown:", logText, StringComparison.Ordinal);
        Assert.Equal(
            [
                "prepare-close-owned-windows",
                "prepare-detach-global-exceptions",
                "prepare-detach-audio",
                "prepare-stop-hotplug-debounce",
                "prepare-detach-system-events",
                "prepare-detach-window-events",
                "unwire-hotkeys",
                "cleanup-appvm",
                "dispose-hotkeys",
                "dispose-runtime",
                "dispose-overlay",
                "dispose-device-cache",
                "dispose-core-audio",
                "dispose-shell",
                "dispose-single-instance"
            ],
            calls);
    }

    [Fact]
    public void PrepareForShutdown_RunsPreparationStepsInExpectedOrder()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowLifecycleCoordinatorTests), "shutdown-prepare.log", LogLevel.Trace);
        MainWindowLifecycleCoordinator coordinator = CreateCoordinator(loggerScope.Logger, shutdownStepTimeoutMs: 1000);
        List<string> calls = [];

        MainWindowLifecycleCoordinator.PrepareForShutdown(
            new MainWindowShutdownPreparationDependencies(
                CloseOwnedWindows: () => calls.Add("close-owned-windows"),
                DetachGlobalExceptionHandlers: () => calls.Add("detach-global-exceptions"),
                DetachAudioEventHandlers: () => calls.Add("detach-audio"),
                StopHotplugRefreshDebounce: () => calls.Add("stop-hotplug-debounce"),
                DetachSystemEventHandlers: () => calls.Add("detach-system-events"),
                DetachWindowEventHandlers: () => calls.Add("detach-window-events")));

        Assert.Equal(
            [
                "close-owned-windows",
                "detach-global-exceptions",
                "detach-audio",
                "stop-hotplug-debounce",
                "detach-system-events",
                "detach-window-events"
            ],
            calls);
    }

    [Fact]
    public async Task ExecuteShutdownAsync_LogsSharedShutdownOpId_ForStartAndCompletion()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowLifecycleCoordinatorTests), "shutdown-opid.log", LogLevel.Trace);
        MainWindowLifecycleCoordinator coordinator = CreateCoordinator(loggerScope.Logger, shutdownStepTimeoutMs: 1000);

        await coordinator.ExecuteShutdownAsync(
            new MainWindowShutdownDependencies(
                Preparation: new MainWindowShutdownPreparationDependencies(
                    CloseOwnedWindows: static () => { },
                    DetachGlobalExceptionHandlers: static () => { },
                    DetachAudioEventHandlers: static () => { },
                    StopHotplugRefreshDebounce: static () => { },
                    DetachSystemEventHandlers: static () => { },
                    DetachWindowEventHandlers: static () => { }),
                UnwireHotkeys: static () => { },
                CleanupAppViewModelAsync: static () => Task.CompletedTask,
                DisposeHotkeyService: static () => { },
                DisposeRuntimeServicesAsync: null,
                DisposeOverlayService: static () => { },
                DisposeDeviceCache: static () => { },
                DisposeCoreAudioExecutor: static () => { },
                DisposeShell: static () => { },
                DisposeSingleInstance: static () => { }),
            nameof(ExecuteShutdownAsync_LogsSharedShutdownOpId_ForStartAndCompletion));

        string logText = loggerScope.DisposeAndReadLogText();

        Match opIdMatch = MyRegex().Match(logText);
        Assert.True(opIdMatch.Success, $"Expected shutdown opId in log.\nLog text:\n{logText}");
        string shutdownOpId = opIdMatch.Groups[1].Value;

        Assert.Contains($"shutdown-start | opId={shutdownOpId}", logText, StringComparison.Ordinal);
        Assert.Contains($"shutdown-complete | opId={shutdownOpId}", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowFatalErrorDialogOnce_WhenDispatcherInvokeFails_DoesNotThrow()
    {
        TestExecutionGuards.RunSta(() =>
        {
            using var loggerScope = new TestLoggerScope(nameof(MainWindowLifecycleCoordinatorTests), "fatal-dialog-failure.log", LogLevel.Trace);
            MainWindowLifecycleCoordinator coordinator = CreateCoordinator(loggerScope.Logger, shutdownStepTimeoutMs: 1000);
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeShutdown();

            Exception? exception = Record.Exception(() =>
                Task.Run(() => coordinator.ShowFatalErrorDialogOnce("fatal")).GetAwaiter().GetResult());

            Assert.Null(exception);
        });
    }

    private static MainWindowLifecycleCoordinator CreateCoordinator(Logger logger, int shutdownStepTimeoutMs)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        return new MainWindowLifecycleCoordinator(logger, dispatcher, shutdownStepTimeoutMs);
    }

    [GeneratedRegex(@"opId=(shutdown:[0-9a-f]{32})", RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
}
