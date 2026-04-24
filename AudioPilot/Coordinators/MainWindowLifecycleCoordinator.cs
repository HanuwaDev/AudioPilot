using System.Windows.Threading;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    /// <summary>
    /// Ordered pre-shutdown detachments that stop new UI, audio, and system callbacks from entering the main
    /// window while teardown is already in progress.
    /// </summary>
    internal readonly record struct MainWindowShutdownPreparationDependencies(
        Action CloseOwnedWindows,
        Action DetachGlobalExceptionHandlers,
        Action DetachAudioEventHandlers,
        Action StopHotplugRefreshDebounce,
        Action DetachSystemEventHandlers,
        Action DetachWindowEventHandlers);

    /// <summary>
    /// Full shutdown dependency bundle for the main window, split between synchronous detach/dispose steps and
    /// asynchronous cleanup that may need timeout handling.
    /// </summary>
    internal readonly record struct MainWindowShutdownDependencies(
        MainWindowShutdownPreparationDependencies Preparation,
        Action UnwireHotkeys,
        Func<Task> CleanupAppViewModelAsync,
        Action DisposeHotkeyService,
        Func<Task>? DisposeRuntimeServicesAsync,
        Action DisposeOverlayService,
        Action DisposeDeviceCache,
        Action DisposeCoreAudioExecutor,
        Action DisposeShell,
        Action DisposeSingleInstance);

    internal sealed class MainWindowLifecycleCoordinator(Logger logger, Dispatcher dispatcher, int shutdownStepTimeoutMs)
    {
        private readonly Logger _logger = logger;
        private readonly Dispatcher _dispatcher = dispatcher;
        private readonly int _shutdownStepTimeoutMs = shutdownStepTimeoutMs;
        private int _fatalErrorDialogShown;

        /// <summary>
        /// Shows a fatal error dialog at most once for the current process.
        /// </summary>
        /// <remarks>
        /// The dialog is marshaled to the UI dispatcher when required. Failures while showing the dialog are
        /// suppressed because this method is intended for unrecoverable error paths.
        /// </remarks>
        public void ShowFatalErrorDialogOnce(string message)
        {
            if (Interlocked.Exchange(ref _fatalErrorDialogShown, 1) != 0)
            {
                return;
            }

            try
            {
                if (_dispatcher.CheckAccess())
                {
                    MessageBoxService.ShowError(message, DialogText.Captions.FatalError);
                }
                else
                {
                    _dispatcher.Invoke(() => MessageBoxService.ShowError(message, DialogText.Captions.FatalError));
                }
            }
            catch (Exception ex)
            {
                TryLogFatalDialogFailure(ex);
            }
        }

        private void TryLogFatalDialogFailure(Exception ex)
        {
            const string message = "Failed to show fatal error dialog";

            try
            {
                _logger.Warning("MainWindow", message, nameof(ShowFatalErrorDialogOnce), ex);
            }
            catch (Exception loggingEx)
            {
                LifecycleFallbackDiagnostics.Write("MainWindow", message, nameof(ShowFatalErrorDialogOnce), ex, loggingEx);
            }
        }

        /// <summary>
        /// Runs the synchronous front half of shutdown that detaches producers before later cleanup starts
        /// awaiting background work.
        /// </summary>
        public static void PrepareForShutdown(MainWindowShutdownPreparationDependencies dependencies)
        {
            dependencies.CloseOwnedWindows();
            dependencies.DetachGlobalExceptionHandlers();
            dependencies.DetachAudioEventHandlers();
            dependencies.StopHotplugRefreshDebounce();
            dependencies.DetachSystemEventHandlers();
            dependencies.DetachWindowEventHandlers();
        }

        private void ExecuteShutdownAction(Action action, string stepName, string ownerMethodName, string shutdownOpId)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.Warning("MainWindow", () => $"shutdown-step-failed | step={stepName} opId={shutdownOpId}", ownerMethodName, ex);
            }
        }

        private void ExecutePreparationSteps(MainWindowShutdownPreparationDependencies dependencies, string ownerMethodName, string shutdownOpId)
        {
            ExecuteShutdownAction(dependencies.CloseOwnedWindows, "prepare-close-owned-windows", ownerMethodName, shutdownOpId);
            ExecuteShutdownAction(dependencies.DetachGlobalExceptionHandlers, "prepare-detach-global-exceptions", ownerMethodName, shutdownOpId);
            ExecuteShutdownAction(dependencies.DetachAudioEventHandlers, "prepare-detach-audio", ownerMethodName, shutdownOpId);
            ExecuteShutdownAction(dependencies.StopHotplugRefreshDebounce, "prepare-stop-hotplug-debounce", ownerMethodName, shutdownOpId);
            ExecuteShutdownAction(dependencies.DetachSystemEventHandlers, "prepare-detach-system-events", ownerMethodName, shutdownOpId);
            ExecuteShutdownAction(dependencies.DetachWindowEventHandlers, "prepare-detach-window-events", ownerMethodName, shutdownOpId);
        }

        /// <summary>
        /// Awaits one shutdown step with timeout logging so a hung cleanup task cannot block later teardown.
        /// </summary>
        /// <remarks>
        /// Faults are logged and swallowed because shutdown should continue best-effort even after one subsystem
        /// fails to clean up correctly.
        /// </remarks>
        public async Task AwaitShutdownStepAsync(Task operation, string stepName, string ownerMethodName, string shutdownOpId)
        {
            Task completed = await Task.WhenAny(operation, Task.Delay(_shutdownStepTimeoutMs));
            if (completed == operation)
            {
                try
                {
                    await operation;
                }
                catch (Exception ex)
                {
                    _logger.Warning("MainWindow", () => $"shutdown-step-failed | step={stepName} opId={shutdownOpId}", ownerMethodName, ex);
                }

                return;
            }

            _logger.Warning("MainWindow", () => $"shutdown-step-timeout | step={stepName} timeoutMs={_shutdownStepTimeoutMs} opId={shutdownOpId}");

            _ = operation.ContinueWith(
                task =>
                {
                    if (task.Exception != null)
                    {
                        _logger.Warning("MainWindow", () => $"shutdown-step-faulted-after-timeout | step={stepName} opId={shutdownOpId}", ownerMethodName, task.Exception);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Executes the ordered main-window shutdown sequence and logs per-step failures without aborting cleanup.
        /// </summary>
        /// <remarks>
        /// Each asynchronous step is awaited through <see cref="AwaitShutdownStepAsync"/> so slow or faulted cleanup
        /// work is logged with the shared shutdown operation id while later cleanup steps continue to run.
        /// </remarks>
        public async Task ExecuteShutdownAsync(MainWindowShutdownDependencies dependencies, string ownerMethodName)
        {
            string shutdownOpId = $"shutdown:{Guid.NewGuid():N}";
            _logger.Info("MainWindow", () => $"shutdown-start | opId={shutdownOpId}");

            try
            {
                ExecutePreparationSteps(dependencies.Preparation, ownerMethodName, shutdownOpId);
                ExecuteShutdownAction(dependencies.UnwireHotkeys, "unwire-hotkeys", ownerMethodName, shutdownOpId);

                await AwaitShutdownStepAsync(
                    dependencies.CleanupAppViewModelAsync(),
                    "app-viewmodel-cleanup",
                    ownerMethodName,
                    shutdownOpId);

                ExecuteShutdownAction(dependencies.DisposeHotkeyService, "dispose-hotkeys", ownerMethodName, shutdownOpId);

                if (dependencies.DisposeRuntimeServicesAsync != null)
                {
                    await AwaitShutdownStepAsync(
                        dependencies.DisposeRuntimeServicesAsync(),
                        "audio-service-dispose",
                        ownerMethodName,
                        shutdownOpId);
                }

                ExecuteShutdownAction(dependencies.DisposeOverlayService, "dispose-overlay", ownerMethodName, shutdownOpId);
                ExecuteShutdownAction(dependencies.DisposeDeviceCache, "dispose-device-cache", ownerMethodName, shutdownOpId);
                ExecuteShutdownAction(dependencies.DisposeCoreAudioExecutor, "dispose-core-audio", ownerMethodName, shutdownOpId);
                ExecuteShutdownAction(dependencies.DisposeShell, "dispose-shell", ownerMethodName, shutdownOpId);
                ExecuteShutdownAction(dependencies.DisposeSingleInstance, "dispose-single-instance", ownerMethodName, shutdownOpId);

                _logger.Info("MainWindow", () => $"shutdown-complete | opId={shutdownOpId}");
            }
            catch (Exception ex)
            {
                _logger.Error("MainWindow", () => $"shutdown-failed | opId={shutdownOpId} error={ex.GetType().Name}", ownerMethodName, ex);
            }
        }
    }
}
