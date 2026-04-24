using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Platform;
using AudioPilot.ViewModels;

namespace AudioPilot
{
    public partial class App : Application
    {
        private sealed class UiDispatcherUnavailableException(Exception? innerException = null)
            : InvalidOperationException("UI dispatcher is not available.", innerException);

        private sealed class AppViewModelCliRuntime(AppViewModel appVm) : ICliCommandRuntime
        {
            private readonly AppViewModel _appVm = appVm;

            private static bool IsUiDispatcherUnavailable(Dispatcher? dispatcher)
            {
                return MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(dispatcher);
            }

            private static UiDispatcherUnavailableException CreateDispatcherUnavailableException(Exception? innerException = null)
            {
                return new UiDispatcherUnavailableException(innerException);
            }

            private static T InvokeOnUi<T>(Func<T> action)
            {
                var dispatcher = Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return action();
                }

                if (dispatcher.CheckAccess())
                {
                    return action();
                }

                if (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException();
                }

                try
                {
                    return dispatcher.Invoke(action);
                }
                catch (InvalidOperationException ex) when (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException(ex);
                }
            }

            private static void InvokeOnUi(Action action)
            {
                var dispatcher = Current?.Dispatcher;
                if (dispatcher == null)
                {
                    action();
                    return;
                }

                if (dispatcher.CheckAccess())
                {
                    action();
                    return;
                }

                if (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException();
                }

                try
                {
                    dispatcher.Invoke(action);
                }
                catch (InvalidOperationException ex) when (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException(ex);
                }
            }

            private static Task<T> InvokeOnUiAsync<T>(Func<Task<T>> action)
            {
                var dispatcher = Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return action();
                }

                if (dispatcher.CheckAccess())
                {
                    return action();
                }

                if (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException();
                }

                try
                {
                    return dispatcher.InvokeAsync(action).Task.Unwrap();
                }
                catch (InvalidOperationException ex) when (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException(ex);
                }
            }

            private static Task InvokeOnUiAsync(Func<Task> action)
            {
                var dispatcher = Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return action();
                }

                if (dispatcher.CheckAccess())
                {
                    return action();
                }

                if (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException();
                }

                try
                {
                    return dispatcher.InvokeAsync(action).Task.Unwrap();
                }
                catch (InvalidOperationException ex) when (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException(ex);
                }
            }

            public void ShowWindow() => InvokeOnUi(_appVm.ShowWindow);
            public void HideWindow() => InvokeOnUi(_appVm.MinimizeWindow);
            public void MediaPlayPause() => InvokeOnUi(() => _appVm.MediaPlayPauseFromCli());
            public void MediaNextTrack() => InvokeOnUi(() => _appVm.MediaNextTrackFromCli());
            public void MediaPreviousTrack() => InvokeOnUi(() => _appVm.MediaPreviousTrackFromCli());
            public string GetMediaStatus(bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.GetMediaStatusFromCliAsync(jsonOutput, redactOutput)).GetAwaiter().GetResult();
            public bool ToggleMuteMic() => InvokeOnUi(_appVm.ToggleMuteMicFromCli);
            public bool SetMuteMic(bool enabled) => InvokeOnUi(() => _appVm.SetMuteMicFromCli(enabled));
            public bool ToggleMuteSound() => InvokeOnUi(_appVm.ToggleMuteSoundFromCli);
            public bool SetMuteSound(bool enabled) => InvokeOnUi(() => _appVm.SetMuteSoundFromCli(enabled));
            public bool ToggleDeafen() => InvokeOnUi(_appVm.ToggleDeafenFromCli);
            public bool SetDeafen(bool enabled) => InvokeOnUi(() => _appVm.SetDeafenFromCli(enabled));
            public bool ToggleListenToInput() => InvokeOnUi(_appVm.ToggleListenToInputFromCli);
            public bool SetListenToInput(bool enabled) => InvokeOnUi(() => _appVm.SetListenToInputFromCli(enabled));
            public string GetMuteStatus(string target, bool jsonOutput) => InvokeOnUi(() => _appVm.GetMuteStatusFromCli(target, jsonOutput));
            public string GetListenStatus(bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetListenStatusFromCli(jsonOutput, redactOutput));
            public (bool Success, string Output) GetVolume(bool playback, string? deviceId, bool jsonOutput) => InvokeOnUi(() => _appVm.GetVolumeFromCli(playback, deviceId, jsonOutput));
            public (bool Success, string Output) SetVolume(bool playback, string? deviceId, float percent, bool jsonOutput) => InvokeOnUi(() => _appVm.SetVolumeFromCli(playback, deviceId, percent, jsonOutput));
            public string GetRoutineList(bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetRoutineListFromCli(jsonOutput, redactOutput));
            public Task<CliExecutionResult> RunRoutineAsync(string routineSelector, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.RunRoutineFromCliAsync(routineSelector, jsonOutput, redactOutput));
            public CliExecutionResult SetRoutineEnabled(string routineSelector, bool enabled, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.SetRoutineEnabledFromCli(routineSelector, enabled, jsonOutput, redactOutput));
            public CliExecutionResult CreateRoutine(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.CreateRoutineFromCli(path, allowAnyPath, jsonOutput, redactOutput));
            public CliExecutionResult UpdateRoutine(string routineSelector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.UpdateRoutineFromCli(routineSelector, path, allowAnyPath, jsonOutput, redactOutput));
            public CliExecutionResult DeleteRoutine(string routineSelector, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.DeleteRoutineFromCli(routineSelector, jsonOutput, redactOutput));
            public CliExecutionResult ImportRoutines(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ImportRoutinesFromCli(path, replaceImport, allowAnyPath, jsonOutput, redactOutput));
            public async ValueTask<bool> SwitchOutputAsync(bool muteMic, bool muteSound, bool deafen, bool reverse) => await InvokeOnUiAsync(async () => await _appVm.SwitchOutputFromCliAsync(muteMic, muteSound, deafen, reverse));
            public async ValueTask<bool> SwitchInputAsync(bool reverse) => await InvokeOnUiAsync(async () => await _appVm.SwitchInputFromCliAsync(reverse));
            public Task RefreshAsync() => InvokeOnUiAsync(_appVm.RefreshFromCliAsync);
            public bool SetStartupEnabled(bool enabled) => InvokeOnUi(() => _appVm.SetStartupEnabledFromCli(enabled));
            public bool OpenStartupSettings() => InvokeOnUi(_appVm.OpenStartupSettingsFromCli);
            public string GetStartupStatus(bool jsonOutput) => InvokeOnUi(() => _appVm.GetStartupStatusFromCli(jsonOutput));
            public string GetStatus(bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetStatusFromCli(jsonOutput, redactOutput));
            public string GetDiagnosticsStatus(bool jsonOutput, bool showPaths, bool redactOutput) => InvokeOnUi(() => _appVm.GetDiagnosticsStatusFromCli(jsonOutput, showPaths, redactOutput));
            public string GetDiagnosticsHistory(bool jsonOutput, int? limit, string? type, bool redactOutput) => InvokeOnUi(() => _appVm.GetDiagnosticsHistoryFromCli(jsonOutput, limit, type, redactOutput));
            public (bool Found, string Output) GetDiagnosticsHistoryDetail(string opId, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetDiagnosticsHistoryDetailFromCli(opId, jsonOutput, redactOutput));
            public (bool Success, string Output) ExportLogs(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ExportLogsFromCli(path, allowAnyPath, detailLevel, jsonOutput, redactOutput));
            public (bool Success, string Output) ExportDiagnosticBundle(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool includeSensitive, bool jsonOutput) => InvokeOnUi(() => _appVm.ExportDiagnosticBundleFromCli(path, allowAnyPath, detailLevel, includeSensitive, jsonOutput));
            public (bool Success, string Output) ResetPerAppAudioRouting(bool jsonOutput) => InvokeOnUi(() => _appVm.ResetPerAppAudioRoutingFromCli(jsonOutput));
            public string GetDeviceList(bool output, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetDeviceListFromCli(output, jsonOutput, redactOutput));
            public (bool Found, string Output) GetDevice(bool output, string selector, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetDeviceFromCli(output, selector, jsonOutput, redactOutput));
            public (bool Found, string Output) FindDevices(bool output, string query, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.FindDevicesFromCli(output, query, jsonOutput, redactOutput));
            public string GetCycle(bool output, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetCycleFromCli(output, jsonOutput, redactOutput));
            public (bool IsValid, string Output) GetCycleValidation(bool output, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetCycleValidationFromCli(output, jsonOutput, redactOutput));
            public (bool CanSwitch, string Output) GetCycleTest(bool output, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetCycleTestFromCli(output, jsonOutput, redactOutput));
            public (bool Success, string Output) AddCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.AddCycleDeviceFromCli(output, deviceId, jsonOutput, redactOutput));
            public (bool Success, string Output) RemoveCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.RemoveCycleDeviceFromCli(output, deviceId, jsonOutput, redactOutput));
            public (bool Success, string Output) ReorderCycle(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ReorderCycleFromCli(output, deviceIds, jsonOutput, redactOutput));
            public (bool CanSwitch, string Output) PreviewSwitch(bool output, bool reverse, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.PreviewSwitchFromCli(output, reverse, jsonOutput, redactOutput));
            public string? GetCurrentDeviceId(bool output) => InvokeOnUi(() => _appVm.GetCurrentDeviceIdFromCli(output));
            public Task<(bool Found, string Output)> WaitForDeviceAsync(string deviceId, int timeoutMs, bool outputOnly, bool inputOnly, bool jsonOutput, bool redactOutput) =>
                InvokeOnUiAsync(() => _appVm.WaitForDeviceFromCliAsync(deviceId, timeoutMs, outputOnly, inputOnly, jsonOutput));
            public (bool Found, string? Value, string? Error) GetConfig(string key) => InvokeOnUi(() => _appVm.GetConfigFromCli(key));
            public string GetConfigList(bool jsonOutput) => InvokeOnUi(() => CliOutputFormatter.FormatSupportedKeyList("config", CliConfigManager.GetKnownKeys(), jsonOutput));
            public (bool Updated, string? Error) SetConfig(string key, string value) => InvokeOnUi(() => _appVm.SetConfigFromCli(key, value));
            public (bool Found, string? Value, string? Error) GetRuntime(string key) => InvokeOnUi(() => AppViewModel.GetRuntimeFromCli(key));
            public string GetRuntimeList(bool jsonOutput) => InvokeOnUi(() => CliOutputFormatter.FormatSupportedKeyList("runtime", CliRuntimeManager.GetKnownKeys(), jsonOutput));
            public (bool Updated, string? Error) SetRuntime(string key, string value) => InvokeOnUi(() => AppViewModel.SetRuntimeFromCli(key, value));
            public (bool IsValid, string Output) GetConfigValidation(bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetConfigValidationFromCli(jsonOutput, redactOutput));
            public (bool Success, string Output) ExportRoutines(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ExportRoutinesFromCli(path, allowAnyPath, jsonOutput, redactOutput));
            public (bool Success, string Output) ExportConfig(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ExportConfigFromCli(path, allowAnyPath, jsonOutput, redactOutput));
            public (bool Success, string Output) ImportConfig(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ImportConfigFromCli(path, replaceImport, allowAnyPath, jsonOutput, redactOutput));
            public string GetNetworkList(bool jsonOutput) => InvokeOnUi(() => _appVm.GetNetworkListFromCli(jsonOutput));
        }

        private MainWindow? _mainWindow;
        private readonly Logger _logger = Logger.Instance;
        private bool _unobservedTaskExceptionHandlerRegistered;
        private SingleInstanceHelper? _singleInstanceWithLifetimeHandlers;

        protected override void OnStartup(StartupEventArgs e)
        {
            var startupStopwatch = Stopwatch.StartNew();
            base.OnStartup(e);
            AttachUnobservedTaskExceptionHandler();

            bool prefersJson = CliHostUtilities.PrefersJson(e.Args);
            string cliExecutableName = CliHostUtilities.ResolveCliExecutableName(typeof(App).Assembly.Location);

            if (!CliCommand.TryParse(e.Args, out CliCommand startupCommand, out string? cliError))
            {
                CliHostUtilities.WriteCliError(
                    Console.Error,
                    exitCode: 2,
                    errorCode: "invalid-usage",
                    message: cliError ?? "Invalid CLI usage.",
                    jsonOutput: prefersJson,
                    includeUsage: !prefersJson,
                    helpExecutablePathOrName: cliExecutableName);
                ShutdownWithCode(2);
                return;
            }

            if (startupCommand.Action == CliAction.Help)
            {
                Console.WriteLine(CliCommand.UsageText);
                ShutdownWithCode(0);
                return;
            }

            if (startupCommand.Action == CliAction.Version)
            {
                Console.WriteLine($"AudioPilot {GetType().Assembly.GetName().Version}");
                ShutdownWithCode(0);
                return;
            }

            if (!startupCommand.IsNoOpLaunch)
            {
                CliHostUtilities.WriteCliError(
                    Console.Error,
                    exitCode: 2,
                    errorCode: "cli-host-required",
                    message: $"Run CLI commands with {cliExecutableName}.",
                    jsonOutput: startupCommand.JsonOutput,
                    includeUsage: !startupCommand.JsonOutput,
                    helpExecutablePathOrName: cliExecutableName);
                ShutdownWithCode(2);
                return;
            }

            ApplicationSingleInstanceStartupState singleInstanceStartup = ApplicationBootstrapper.InitializeSingleInstance(showUserErrors: false);
            if (!singleInstanceStartup.AcquireResult.Acquired)
            {
                if (singleInstanceStartup.AcquireResult.ExistingHealthy)
                {
                    if (_logger.IsEnabled(LogLevel.Info))
                    {
                        _logger.Info("App", AppConstants.Audio.LogEvents.SingleInstance.ActivationHandoff);
                    }

                    ShutdownWithCode(0);
                    return;
                }

                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.Warning("App", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ExistingInstanceUnresponsive} | failureKind={singleInstanceStartup.AcquireResult.FailureKind}");
                }

                var recoveryCoordinator = new SingleInstanceStartupRecoveryCoordinator(
                    new SingleInstanceProcessRecoveryHelper(_logger),
                    _logger);
                SingleInstanceStartupRecoveryResult recoveryResult = recoveryCoordinator.Resolve(
                    () => singleInstanceStartup.Helper.TryAcquireDetailed(showUserErrors: false));

                if (!recoveryResult.ContinueStartup)
                {
                    ShutdownWithCode(recoveryResult.ExitCode);
                    return;
                }
            }

            var singleInstance = singleInstanceStartup.Helper;
            AttachSingleInstanceLifetimeHandlers(singleInstance);

            try
            {
                _mainWindow = new MainWindow();
                MainWindow = _mainWindow;

                WindowInteropHelper windowHelper = new(_mainWindow);
                _ = windowHelper.EnsureHandle();
                _ = _mainWindow.BootstrapStartupAsync(nameof(OnStartup));
            }
            catch (Exception ex)
            {
                _logger.Fatal("App", "Failed to initialize main window", nameof(OnStartup), ex);
                MessageBoxService.ShowError("AudioPilot failed to start. Please check AudioPilot.log for details.", DialogText.Captions.StartupError);
                ShutdownWithCode(3);
                return;
            }

            startupStopwatch.Stop();
            if (_logger.IsEnabled(LogLevel.Info))
            {
                _logger.Info("App", () => $"Startup pipeline reached hidden main window bootstrap in {startupStopwatch.Elapsed.TotalMilliseconds:F1}ms");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                DetachLifetimeEventHandlers();
            }
            catch (Exception ex)
            {
                TryLogLifecycleWarning("Failed to detach lifetime event handlers during app shutdown", nameof(OnExit), ex);
            }

            base.OnExit(e);
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                _logger.Error("App", "Unobserved task exception occurred", nameof(OnUnobservedTaskException), e.Exception);
            }
            catch (Exception ex)
            {
                TryLogLifecycleWarning("Failed to log unobserved task exception", nameof(OnUnobservedTaskException), ex);
            }

            try
            {
                e.SetObserved();
            }
            catch (Exception ex)
            {
                TryLogLifecycleWarning("Failed to mark unobserved task exception as observed", nameof(OnUnobservedTaskException), ex);
            }
        }

        private void TryLogLifecycleWarning(string message, string operation, Exception ex)
        {
            try
            {
                _logger.Warning("App", message, operation, ex);
            }
            catch (Exception loggingEx)
            {
                LifecycleFallbackDiagnostics.Write("App", message, operation, ex, loggingEx);
            }
        }

        private void AttachUnobservedTaskExceptionHandler()
        {
            if (_unobservedTaskExceptionHandlerRegistered)
            {
                return;
            }

            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            _unobservedTaskExceptionHandlerRegistered = true;
        }

        private void AttachSingleInstanceLifetimeHandlers(SingleInstanceHelper singleInstance)
        {
            ArgumentNullException.ThrowIfNull(singleInstance);

            if (ReferenceEquals(_singleInstanceWithLifetimeHandlers, singleInstance))
            {
                return;
            }

            if (_singleInstanceWithLifetimeHandlers != null)
            {
                _singleInstanceWithLifetimeHandlers.ActivationRequested -= OnActivationRequested;
                _singleInstanceWithLifetimeHandlers.CommandRequested -= OnCommandRequested;
            }

            singleInstance.ActivationRequested += OnActivationRequested;
            singleInstance.CommandRequested += OnCommandRequested;
            _singleInstanceWithLifetimeHandlers = singleInstance;
        }

        private void DetachLifetimeEventHandlers()
        {
            if (_unobservedTaskExceptionHandlerRegistered)
            {
                TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
                _unobservedTaskExceptionHandlerRegistered = false;
            }

            if (_singleInstanceWithLifetimeHandlers != null)
            {
                _singleInstanceWithLifetimeHandlers.ActivationRequested -= OnActivationRequested;
                _singleInstanceWithLifetimeHandlers.CommandRequested -= OnCommandRequested;
                _singleInstanceWithLifetimeHandlers = null;
            }
        }

        private void OnActivationRequested(object? sender, EventArgs e)
        {
            if (_mainWindow == null)
            {
                return;
            }

            Dispatcher dispatcher = _mainWindow.Dispatcher;
            if (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(dispatcher))
            {
                return;
            }

            try
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    _mainWindow.AppViewModel.ShowWindow();
                }));
            }
            catch (InvalidOperationException ex) when (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(dispatcher))
            {
                _logger.Warning("App", "Activation request ignored because UI dispatcher is shutting down", nameof(OnActivationRequested), ex);
            }
        }

        private async Task<SingleInstanceCommandResult> OnCommandRequested(string payload)
        {
            if (_mainWindow == null)
            {
                return new SingleInstanceCommandResult(3, ErrorCode: "ui-host-unavailable", ErrorMessage: "Main window is not available.", ProtocolVersion: 2);
            }

            if (!CliCommand.TryFromPipePayload(payload, out CliCommand command, out string? failureReason, out int? protocolVersion))
            {
                _logger.Warning("App", () => $"{AppConstants.Audio.LogEvents.App.CliForwardParseFailed} | reason={(failureReason ?? "invalid-payload")} protocolVersion={(protocolVersion?.ToString() ?? "unknown")}");
                return new SingleInstanceCommandResult(
                    6,
                    ErrorCode: "forwarded-protocol-mismatch",
                    ErrorMessage: "The running AudioPilot instance uses an incompatible CLI forwarding protocol.",
                    ProtocolVersion: 1);
            }

            string opId = Guid.NewGuid().ToString("N")[..8];
            var stopwatch = Stopwatch.StartNew();
            _logger.Info("App", () => $"{AppConstants.Audio.LogEvents.App.CliForwardStart} | opId={opId} action={command.Action}");

            var executionResult = await ExecuteCliCommandAsync(command);
            stopwatch.Stop();
            _logger.Info(
                "App",
                () => $"{AppConstants.Audio.LogEvents.App.CliForwardComplete} | opId={opId} action={command.Action} exitCode={executionResult.ExitCode} durationMs={stopwatch.Elapsed.TotalMilliseconds:F1}");
            return new SingleInstanceCommandResult(executionResult.ExitCode, executionResult.Output, ProtocolVersion: 1);
        }

        private async Task<CliExecutionResult> ExecuteCliCommandAsync(CliCommand command)
        {
            if (_mainWindow == null)
            {
                return CliCommandExecutor.BuildRuntimeUnavailableResult(command.JsonOutput);
            }

            if (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(_mainWindow.Dispatcher))
            {
                return CliCommandExecutor.BuildRuntimeUnavailableResult(command.JsonOutput);
            }

            try
            {
                return await CliCommandExecutor.ExecuteAsync(command, new AppViewModelCliRuntime(_mainWindow.AppViewModel));
            }
            catch (UiDispatcherUnavailableException)
            {
                return CliCommandExecutor.BuildRuntimeUnavailableResult(command.JsonOutput);
            }
            catch (Exception ex) when (command.Action == CliAction.Refresh)
            {
                _logger.Error("App", AppConstants.Audio.LogEvents.ViewModel.RefreshFailed, nameof(ExecuteCliCommandAsync), ex);
                return CliCommandExecutor.BuildExecutionFailureResult(7, "refresh-failed", "Refresh command failed.", command.JsonOutput);
            }
        }

        private void ShutdownWithCode(int exitCode)
        {
            Environment.ExitCode = exitCode;
            Shutdown(exitCode);
        }
    }

    internal readonly record struct ApplicationSingleInstanceStartupState(
        SingleInstanceHelper Helper,
        SingleInstanceAcquireResult AcquireResult);

    public static class ApplicationBootstrapper
    {
        private static SingleInstanceHelper? _singleInstance;

        internal static ApplicationSingleInstanceStartupState InitializeSingleInstance(
            string? payloadToExistingInstance = null,
            bool showUserErrors = true)
        {
            _singleInstance = new SingleInstanceHelper();
            return new ApplicationSingleInstanceStartupState(
                _singleInstance,
                _singleInstance.TryAcquireDetailed(payloadToExistingInstance, showUserErrors));
        }

        public static bool ShouldStart(string? payloadToExistingInstance = null)
        {
            return InitializeSingleInstance(payloadToExistingInstance).AcquireResult.Acquired;
        }

        public static SingleInstanceHelper GetSingleInstance()
        {
            return _singleInstance ?? throw new InvalidOperationException("SingleInstanceHelper not initialized");
        }
    }
}
