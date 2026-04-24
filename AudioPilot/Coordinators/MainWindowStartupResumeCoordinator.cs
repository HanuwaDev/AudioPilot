using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;
using Microsoft.Win32;

namespace AudioPilot.Coordinators
{
    /// <summary>
    /// Contract for the view-model resume recovery pipeline that rehydrates audio, hotkeys, and related state
    /// after the system resumes.
    /// </summary>
    internal interface IResumeRecoveryHandler
    {
        Task RecoverAfterSystemResumeAsync(string? resumeOpId = null);
    }

    /// <summary>
    /// Aggregates the startup and resume hooks that the main window owns directly so the coordinator can stay
    /// focused on lifecycle orchestration rather than service lookup.
    /// </summary>
    internal readonly record struct MainWindowStartupResumeDependencies(
        Action RegisterNotificationClient,
        Func<bool> SettingsFileExists,
        Func<bool, Task> InitializeStartupAsync,
        Action CaptureInitialHotplugSnapshot);

    /// <summary>
    /// Coordinates one-time window startup initialization and serialized post-resume recovery scheduling.
    /// </summary>
    /// <remarks>
    /// Startup and resume are kept together because both paths are main-window lifecycle seams that must respect
    /// duplicate signals, shutdown races, and shared logging correlation ids.
    /// </remarks>
    internal sealed class MainWindowStartupResumeCoordinator : IDisposable
    {
        private readonly Logger _logger;
        private readonly IResumeRecoveryHandler _appVm;
        private readonly Action<string> _showStartupError;
        private readonly Action _shutdown;
        private readonly MainWindowStartupResumeDependencies _dependencies;
        private readonly Func<Func<Task>, Task> _queueResumeRecoveryWork;

        private bool _initialized;
        private volatile bool _disposed;
        private int _disposeStarted;
        private int _resumeRecoveryInProgress;
        private DateTime _lastResumeSignalUtc;

        internal MainWindowStartupResumeCoordinator(
            Logger logger,
            AudioDeviceService audioService,
            SettingsService settingsService,
            AppStartupCoordinator startupCoordinator,
            IResumeRecoveryHandler appVm,
            MainWindowHotplugOverlayCoordinator hotplugOverlayCoordinator,
            Action<string> showStartupError,
            Action shutdown)
            : this(
                logger,
                appVm,
                new MainWindowStartupResumeDependencies(
                    audioService.RegisterNotificationClient,
                    settingsService.SettingsFileExists,
                    startupCoordinator.InitializeAsync,
                    hotplugOverlayCoordinator.CaptureInitialSnapshot),
                static work => Task.Run(work),
                showStartupError,
                shutdown)
        {
        }

        internal MainWindowStartupResumeCoordinator(
            Logger logger,
            IResumeRecoveryHandler appVm,
            MainWindowStartupResumeDependencies dependencies,
            Func<Func<Task>, Task> queueResumeRecoveryWork,
            Action<string> showStartupError,
            Action shutdown)
        {
            _logger = logger;
            _appVm = appVm;
            _dependencies = dependencies;
            _queueResumeRecoveryWork = queueResumeRecoveryWork;
            _showStartupError = showStartupError;
            _shutdown = shutdown;
        }

        /// <summary>
        /// Performs one-time startup initialization for the main window, including device-notification
        /// registration, startup coordinator execution, and the initial hotplug snapshot capture.
        /// </summary>
        /// <remarks>
        /// Duplicate Loaded events are ignored. Notification registration failures are downgraded to warnings so
        /// fallback polling can continue, while startup initialization failures remain fatal and trigger shutdown.
        /// </remarks>
        public async Task HandleWindowLoadedAsync(string ownerMethodName)
        {
            var startupStopwatch = Stopwatch.StartNew();
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            try
            {
                _dependencies.RegisterNotificationClient();
            }
            catch (Exception ex)
            {
                _logger.Warning("MainWindow", "Failed to register device notification callback, will use fallback polling", ownerMethodName, ex);
            }

            double notificationClientMs = startupStopwatch.Elapsed.TotalMilliseconds;
            bool noSettingsFileExists = !_dependencies.SettingsFileExists();
            double settingsBootstrapMs = startupStopwatch.Elapsed.TotalMilliseconds - notificationClientMs;

            try
            {
                await _dependencies.InitializeStartupAsync(noSettingsFileExists);
            }
            catch (OperationCanceledException)
            {
                _logger.Warning("MainWindow", "Startup initialization was canceled", ownerMethodName);
                return;
            }
            catch (Exception ex)
            {
                _logger.Fatal("MainWindow", () => $"startup-initialization-failed | error={ex.GetType().Name}", ownerMethodName, ex);
                _showStartupError("Failed to initialize application services. The app will now close. Please check AudioPilot.log for details.");
                _shutdown();
                return;
            }

            startupStopwatch.Stop();
            if (_logger.IsEnabled(LogLevel.Info))
            {
                _logger.Info("MainWindow", () => $"startup-initialization-timing | notificationClientMs={notificationClientMs:F1} settingsBootstrapMs={settingsBootstrapMs:F1} totalMs={startupStopwatch.Elapsed.TotalMilliseconds:F1}");
            }

            try
            {
                _dependencies.CaptureInitialHotplugSnapshot();
            }
            catch (Exception ex)
            {
                _logger.Warning("MainWindow", () => $"startup-hotplug-snapshot-capture-failed | error={ex.GetType().Name}", ownerMethodName, ex);
            }
        }

        /// <summary>
        /// Responds to power-resume notifications by queueing at most one recovery pipeline at a time and tagging
        /// the run with a correlated resume operation id.
        /// </summary>
        /// <remarks>
        /// The coordinator suppresses duplicate resume signals that arrive within the cooldown window and skips
        /// queued work entirely once disposal has started.
        /// </remarks>
        public void HandlePowerModeChanged(PowerModeChangedEventArgs e, string ownerMethodName)
        {
            if (_disposed)
            {
                return;
            }

            if (e.Mode != PowerModes.Resume)
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            if ((nowUtc - _lastResumeSignalUtc).TotalSeconds < 1)
            {
                return;
            }

            _lastResumeSignalUtc = nowUtc;
            string resumeOpId = $"resume:{Guid.NewGuid():N}";
            _ = _queueResumeRecoveryWork(async () =>
            {
                if (_disposed)
                {
                    return;
                }

                if (Interlocked.Exchange(ref _resumeRecoveryInProgress, 1) == 1)
                {
                    _logger.Info("MainWindow", () => $"{AppConstants.Audio.LogEvents.ResumeRecovery.Skip} | opId={resumeOpId} reason=recovery-in-progress");
                    return;
                }

                try
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _logger.Info("MainWindow", () => $"power-resume-detected | opId={resumeOpId}");
                    await _appVm.RecoverAfterSystemResumeAsync(resumeOpId);
                }
                catch (Exception ex)
                {
                    if (!_disposed)
                    {
                        _logger.Warning("MainWindow", () => $"power-resume-recovery-failed | opId={resumeOpId}", ownerMethodName, ex);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _resumeRecoveryInProgress, 0);
                }
            });
        }

        /// <summary>
        /// Marks the coordinator disposed so future startup-resume work is ignored and queued recovery lambdas
        /// observe shutdown state before running.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            _disposed = true;
        }
    }
}
