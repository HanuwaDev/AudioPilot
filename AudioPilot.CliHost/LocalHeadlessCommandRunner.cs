using System.Diagnostics;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Diagnostics;
using AudioPilot.Services.UI;
using AudioPilot.Services.UI.MediaOverlay;
using NAudio.CoreAudioApi;
using Newtonsoft.Json;

namespace AudioPilot.CliHost
{
    internal sealed class LocalHeadlessCommandRunner : IDisposable, ICliCommandRuntime
    {
        internal sealed record AudioOverrides(
            Func<bool?>? GetDefaultPlaybackMute = null,
            Func<bool?>? GetDefaultCaptureMute = null,
            Func<(string? DeviceId, string? DeviceName)>? GetDefaultPlaybackDeviceSnapshot = null,
            Func<(bool Success, float Percent, bool Muted)>? GetDefaultPlaybackVolume = null,
            Func<(bool Success, float Percent, bool Muted)>? GetDefaultCaptureVolume = null,
            Func<string, (bool Success, float Percent, bool Muted)>? GetPlaybackVolumeByDeviceId = null,
            Func<string, (bool Success, float Percent, bool Muted)>? GetCaptureVolumeByDeviceId = null,
            Func<bool, bool>? TrySetPlaybackMute = null,
            Func<bool, bool>? TrySetMicrophoneMute = null,
            Func<float, (bool Success, float Percent, bool Muted)>? TrySetPlaybackVolume = null,
            Func<float, (bool Success, float Percent, bool Muted)>? TrySetCaptureVolume = null,
            Func<string, float, (bool Success, float Percent, bool Muted)>? TrySetPlaybackVolumeByDeviceId = null,
            Func<string, float, (bool Success, float Percent, bool Muted)>? TrySetCaptureVolumeByDeviceId = null,
            Func<bool>? TryToggleListenToInput = null,
            Func<bool, bool>? TrySetListenToInput = null,
            Func<(bool? Enabled, string? MonitorTargetOutputDeviceName)>? GetListenStatusSnapshot = null,
            Func<List<CycleDevice>>? GetActiveOutputDeviceInfos = null,
            Func<List<CycleDevice>>? GetActiveInputDeviceInfos = null,
            Func<string?>? GetCurrentOutputDeviceId = null,
            Func<string?>? GetCurrentInputDeviceId = null,
            Func<string>? GetLogRootDirectory = null,
            Func<uint, string, string, string?, ProcessAudioDeviceSwitchResult>? SwitchApplicationOutputDeviceDetailedAsync = null,
            Func<uint, string, string, string?, ProcessAudioDeviceSwitchResult>? SwitchApplicationInputDeviceDetailedAsync = null,
            Func<string, string, bool, bool, bool, bool, string?, (bool Success, string? DeviceName)>? SwitchAudioDeviceAsync = null,
            Func<string, string, string?, (bool Success, string? DeviceName)>? SwitchInputDeviceToAsync = null,
            Func<bool>? HasDefaultInputDevice = null,
            Func<Action, IDisposable>? SubscribeDeviceStateChanged = null,
            Func<int, Task>? DelayAsync = null);

        internal static int ResolveBluetoothPostAttemptRecheckDelayMs()
        {
            return RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs;
        }

        private sealed class DelegateDisposable(Action dispose) : IDisposable
        {
            private Action? _dispose = dispose;

            public void Dispose()
            {
                Interlocked.Exchange(ref _dispose, null)?.Invoke();
            }
        }

        internal sealed record RuntimeServiceFactories(
            Func<SettingsService>? CreateSettingsService = null,
            Func<StartupService>? CreateStartupService = null,
            Func<AudioDeviceService>? CreateAudioService = null,
            Func<BluetoothReconnectCoordinator>? CreateBluetoothReconnectCoordinator = null,
            Func<IRoutineProcessSnapshotProvider>? CreateRoutineProcessSnapshotProvider = null);

        private sealed class LazyRuntimeServices : IDisposable
        {
            private readonly Lock _sync = new();
            private readonly Func<SettingsService> _createSettingsService;
            private readonly Func<StartupService> _createStartupService;
            private readonly Func<AudioDeviceService> _createAudioService;
            private readonly Func<BluetoothReconnectCoordinator> _createBluetoothReconnectCoordinator;
            private readonly Action? _disposeOwner;
            private int _disposeStarted;
            private SettingsService? _settingsService;
            private StartupService? _startupService;
            private AudioDeviceService? _audioService;
            private BluetoothReconnectCoordinator? _bluetoothReconnectCoordinator;

            private LazyRuntimeServices(
                Func<SettingsService> createSettingsService,
                Func<StartupService> createStartupService,
                Func<AudioDeviceService> createAudioService,
                Func<BluetoothReconnectCoordinator> createBluetoothReconnectCoordinator,
                Action? disposeOwner = null)
            {
                _createSettingsService = createSettingsService;
                _createStartupService = createStartupService;
                _createAudioService = createAudioService;
                _createBluetoothReconnectCoordinator = createBluetoothReconnectCoordinator;
                _disposeOwner = disposeOwner;
            }

            public static LazyRuntimeServices CreateDefault(RuntimeServiceFactories? factories = null)
            {
                return new LazyRuntimeServices(
                    factories?.CreateSettingsService ?? (() => new SettingsService()),
                    factories?.CreateStartupService ?? (() => new StartupService()),
                    factories?.CreateAudioService ?? (() => new AudioDeviceService()),
                    factories?.CreateBluetoothReconnectCoordinator ?? (() => new BluetoothReconnectCoordinator(new BluetoothReconnectService(), Logger.Instance)));
            }

            public static LazyRuntimeServices CreateFromBundle(AppRuntimeServiceBundle runtimeServices)
            {
                ArgumentNullException.ThrowIfNull(runtimeServices);
                return new LazyRuntimeServices(
                    () => runtimeServices.SettingsService,
                    () => runtimeServices.StartupService,
                    () => runtimeServices.AudioService,
                    () => runtimeServices.BluetoothReconnectCoordinator.Value,
                    runtimeServices.Dispose);
            }

            public SettingsService GetSettingsService() => GetOrCreate(ref _settingsService, _createSettingsService);
            public StartupService GetStartupService() => GetOrCreate(ref _startupService, _createStartupService);
            public AudioDeviceService GetAudioService() => GetOrCreate(ref _audioService, _createAudioService);
            public BluetoothReconnectCoordinator GetBluetoothReconnectCoordinator() => GetOrCreate(ref _bluetoothReconnectCoordinator, _createBluetoothReconnectCoordinator);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                {
                    return;
                }

                _disposeOwner?.Invoke();
                if (_disposeOwner == null)
                {
                    _audioService?.Dispose();
                }
                BluetoothAssociationEndpointSource.DisposeWatcherCache(Logger.Instance);
            }

            private TService GetOrCreate<TService>(ref TService? service, Func<TService> factory)
                where TService : class
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, typeof(LazyRuntimeServices));

                if (service != null)
                {
                    return service;
                }

                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposeStarted != 0, typeof(LazyRuntimeServices));
                    service ??= factory();
                    return service;
                }
            }
        }

        private readonly record struct RoutineSwitchExecutionResult(bool Success, string? DeviceName, string? FailureDetail = null);

        private readonly LazyRuntimeServices _runtimeServices;
        private readonly IRoutineProcessSnapshotProvider _routineProcessSnapshotProvider;
        private readonly AudioOverrides? _audioOverrides;
        private readonly ExecutionHistoryService _executionHistory = new();

        public LocalHeadlessCommandRunner()
            : this(LazyRuntimeServices.CreateDefault(), new RoutineProcessSnapshotProvider(Logger.Instance), null)
        {
        }

        internal LocalHeadlessCommandRunner(AppRuntimeServiceBundle runtimeServices, AudioOverrides? audioOverrides = null)
            : this(LazyRuntimeServices.CreateFromBundle(runtimeServices), new RoutineProcessSnapshotProvider(Logger.Instance), audioOverrides)
        {
        }

        internal LocalHeadlessCommandRunner(RuntimeServiceFactories runtimeServiceFactories, AudioOverrides? audioOverrides = null)
            : this(
                LazyRuntimeServices.CreateDefault(runtimeServiceFactories),
                runtimeServiceFactories.CreateRoutineProcessSnapshotProvider?.Invoke() ?? new RoutineProcessSnapshotProvider(Logger.Instance),
                audioOverrides)
        {
        }

        private LocalHeadlessCommandRunner(
            LazyRuntimeServices runtimeServices,
            IRoutineProcessSnapshotProvider routineProcessSnapshotProvider,
            AudioOverrides? audioOverrides)
        {
            _runtimeServices = runtimeServices;
            _routineProcessSnapshotProvider = routineProcessSnapshotProvider;
            _audioOverrides = audioOverrides;
        }

        private SettingsService SettingsService => _runtimeServices.GetSettingsService();
        private StartupService StartupService => _runtimeServices.GetStartupService();
        private AudioDeviceService AudioService => _runtimeServices.GetAudioService();
        private BluetoothReconnectCoordinator BluetoothReconnectCoordinator => _runtimeServices.GetBluetoothReconnectCoordinator();

        public void Dispose()
        {
            _runtimeServices.Dispose();
        }

        public Task<CliExecutionResult> ExecuteAsync(CliCommand command)
        {
            return CliCommandExecutor.ExecuteAsync(command, this);
        }

        public void ShowWindow()
        {
        }

        public void HideWindow()
        {
        }

        public void MediaPlayPause()
        {
            long started = Stopwatch.GetTimestamp();
            bool success = MediaKeyHelper.TryPressPlayPause();
            RecordCliActionHistory(
                ExecutionHistoryKind.Media,
                "media-play-pause",
                success,
                skipped: false,
                success ? "Dispatched Play/Pause media command." : "Failed to dispatch Play/Pause media command.",
                success ? null : "Media command send failed.",
                target: "PlayPause",
                diagCode: success ? "media-command-dispatched" : "media-command-send-failed",
                elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                details: new Dictionary<string, string>
                {
                    ["command"] = "PlayPause",
                    ["mode"] = "headless-send-input",
                });
        }

        public void MediaNextTrack()
        {
            long started = Stopwatch.GetTimestamp();
            bool success = MediaKeyHelper.TryPressNextTrack();
            RecordCliActionHistory(
                ExecutionHistoryKind.Media,
                "media-next-track",
                success,
                skipped: false,
                success ? "Dispatched Next Track media command." : "Failed to dispatch Next Track media command.",
                success ? null : "Media command send failed.",
                target: "NextTrack",
                diagCode: success ? "media-command-dispatched" : "media-command-send-failed",
                elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                details: new Dictionary<string, string>
                {
                    ["command"] = "NextTrack",
                    ["mode"] = "headless-send-input",
                });
        }

        public void MediaPreviousTrack()
        {
            long started = Stopwatch.GetTimestamp();
            bool success = MediaKeyHelper.TryPressPreviousTrack();
            RecordCliActionHistory(
                ExecutionHistoryKind.Media,
                "media-previous-track",
                success,
                skipped: false,
                success ? "Dispatched Previous Track media command." : "Failed to dispatch Previous Track media command.",
                success ? null : "Media command send failed.",
                target: "PreviousTrack",
                diagCode: success ? "media-command-dispatched" : "media-command-send-failed",
                elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                details: new Dictionary<string, string>
                {
                    ["command"] = "PreviousTrack",
                    ["mode"] = "headless-send-input",
                });
        }

        public string GetMediaStatus(bool jsonOutput, bool redactOutput)
        {
            MediaOverlaySessionSnapshot snapshot = new MediaOverlayCommandService()
                .GetCurrentMediaSnapshotAsync()
                .GetAwaiter()
                .GetResult();

            return CliOutputFormatter.FormatMediaStatus(snapshot, jsonOutput, redactOutput);
        }

        public bool ToggleMuteMic()
        {
            bool? current = TryGetDefaultCaptureMute();
            if (!current.HasValue)
            {
                return false;
            }

            return SetMuteMic(!current.Value);
        }

        public bool SetMuteMic(bool enabled)
        {
            try
            {
                bool success = SetMuteMicCore(enabled);
                RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "mute-mic-on" : "mute-mic-off", success, skipped: false, success ? $"Microphone mute {(enabled ? "enabled" : "disabled")}." : "Failed to set microphone mute.", success ? null : "Failed to set microphone mute.", target: "mic", enabled: success ? enabled : null);
                return success;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(SetMuteMic), "cli-mute-mic-failed", ex);
                RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "mute-mic-on" : "mute-mic-off", success: false, skipped: false, summary: "Failed to set microphone mute.", reason: "Failed to set microphone mute.", target: "mic");
                return false;
            }
        }

        public bool ToggleMuteSound()
        {
            bool? current = TryGetDefaultPlaybackMute();
            if (!current.HasValue)
            {
                return false;
            }

            return SetMuteSound(!current.Value);
        }

        public bool SetMuteSound(bool enabled)
        {
            try
            {
                bool success = SetMuteSoundCore(enabled);
                RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "mute-sound-on" : "mute-sound-off", success, skipped: false, success ? $"Playback mute {(enabled ? "enabled" : "disabled")}." : "Failed to set playback mute.", success ? null : "Failed to set playback mute.", target: "sound", enabled: success ? enabled : null);
                return success;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(SetMuteSound), "cli-mute-sound-failed", ex);
                RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "mute-sound-on" : "mute-sound-off", success: false, skipped: false, summary: "Failed to set playback mute.", reason: "Failed to set playback mute.", target: "sound");
                return false;
            }
        }

        public bool ToggleDeafen()
        {
            bool? playbackMuted = TryGetDefaultPlaybackMute();
            bool? micMuted = TryGetDefaultCaptureMute();

            if (!playbackMuted.HasValue || !micMuted.HasValue)
            {
                return false;
            }

            bool next = !(playbackMuted.Value && micMuted.Value);
            return SetDeafen(next);
        }

        public bool SetDeafen(bool enabled)
        {
            bool success = SetMuteMicCore(enabled) && SetMuteSoundCore(enabled);
            RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "deafen-on" : "deafen-off", success, skipped: false, success ? $"Deafen {(enabled ? "enabled" : "disabled")}." : "Failed to set deafen.", success ? null : "Failed to set deafen.", target: "deafen", enabled: success ? enabled : null);
            return success;
        }

        /// <summary>
        /// Toggles the Windows "Listen to this device" state for the current default input endpoint.
        /// </summary>
        public bool ToggleListenToInput()
        {
            try
            {
                if (_audioOverrides?.TryToggleListenToInput != null)
                {
                    return _audioOverrides.TryToggleListenToInput();
                }

                return AudioService.TryToggleCurrentInputListenState(out _, out _);
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(ToggleListenToInput), "cli-listen-toggle-failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Sets the Windows "Listen to this device" state for the current default input endpoint.
        /// </summary>
        /// <remarks>
        /// This call is idempotent for CLI use: if the state is already applied, the operation still succeeds.
        /// </remarks>
        public bool SetListenToInput(bool enabled)
        {
            try
            {
                if (_audioOverrides?.TrySetListenToInput != null)
                {
                    return _audioOverrides.TrySetListenToInput(enabled);
                }

                return AudioService.TrySetCurrentInputListenState(enabled, out _, out _);
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(SetListenToInput), "cli-listen-set-failed", ex);
                return false;
            }
        }

        public (bool Success, string Output) GetVolume(bool playback, string? deviceId, bool jsonOutput)
        {
            string kind = playback ? "master" : "mic";
            string normalizedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? string.Empty : deviceId.Trim();
            if (!TryResolveCliVolumeDevice(playback, normalizedDeviceId, out string? resolvedDeviceId, out string? selectorError))
            {
                return (false, CliOutputFormatter.FormatVolumeError(kind, "volume-get-failed", selectorError ?? "Failed to read volume.", jsonOutput, normalizedDeviceId));
            }

            string unavailableMessage = playback
                ? string.IsNullOrWhiteSpace(resolvedDeviceId) ? "No default playback device is available." : $"Playback device '{resolvedDeviceId}' is not available."
                : string.IsNullOrWhiteSpace(resolvedDeviceId) ? "No default recording device is available." : $"Recording device '{resolvedDeviceId}' is not available.";
            string readFailureMessage = playback ? "Failed to read playback volume." : "Failed to read recording volume.";

            if (!TryGetEndpointVolume(playback, resolvedDeviceId, out float percent, out bool muted))
            {
                return (false, CliOutputFormatter.FormatVolumeError(kind, "volume-get-failed", HasDefaultEndpoint(playback, resolvedDeviceId) ? readFailureMessage : unavailableMessage, jsonOutput, resolvedDeviceId));
            }

            return (true, CliOutputFormatter.FormatVolumeResult(kind, percent, muted, jsonOutput, "volume-get-success", resolvedDeviceId));
        }

        public (bool Success, string Output) SetVolume(bool playback, string? deviceId, float percent, bool jsonOutput)
        {
            string kind = playback ? "master" : "mic";
            string normalizedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? string.Empty : deviceId.Trim();
            if (!TryResolveCliVolumeDevice(playback, normalizedDeviceId, out string? resolvedDeviceId, out string? selectorError))
            {
                return (false, CliOutputFormatter.FormatVolumeError(kind, "volume-set-failed", selectorError ?? "Failed to set volume.", jsonOutput, normalizedDeviceId));
            }

            string unavailableMessage = playback
                ? string.IsNullOrWhiteSpace(resolvedDeviceId) ? "No default playback device is available." : $"Playback device '{resolvedDeviceId}' is not available."
                : string.IsNullOrWhiteSpace(resolvedDeviceId) ? "No default recording device is available." : $"Recording device '{resolvedDeviceId}' is not available.";
            string writeFailureMessage = playback ? "Failed to set playback volume." : "Failed to set recording volume.";

            if (!TrySetEndpointVolume(playback, resolvedDeviceId, percent, out float appliedPercent, out bool muted))
            {
                return (false, CliOutputFormatter.FormatVolumeError(kind, "volume-set-failed", HasDefaultEndpoint(playback, resolvedDeviceId) ? writeFailureMessage : unavailableMessage, jsonOutput, resolvedDeviceId));
            }

            return (true, CliOutputFormatter.FormatVolumeResult(kind, appliedPercent, muted, jsonOutput, "volume-set-success", resolvedDeviceId));
        }

        public string GetRoutineList(bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            return CliOutputFormatter.FormatRoutineList(CloneRoutines(settings.Routines?.Items ?? []), jsonOutput, redactOutput);
        }

        public async Task<CliExecutionResult> RunRoutineAsync(string routineSelector, bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            List<AudioRoutine> routines = CloneRoutines(settings.Routines?.Items ?? []);
            CliRoutineResolutionResult resolution = CliRoutineResolver.Resolve(routines, routineSelector);
            if (resolution.Status != CliRoutineResolutionStatus.Success || resolution.Routine == null)
            {
                return BuildRoutineErrorResult(5, resolution.ErrorCode, resolution.Message, jsonOutput, redactOutput: redactOutput);
            }

            AudioRoutine routine = resolution.Routine;
            if (!routine.Enabled)
            {
                RecordRoutineHistory(routine, success: false, skipped: true, outputDeviceName: null, inputDeviceName: null, reason: "Routine is disabled.", outputSucceeded: null, inputSucceeded: null);
                return BuildRoutineErrorResult(5, "routine-disabled", $"Routine '{routine.Name}' is disabled.", jsonOutput, routine, redactOutput: redactOutput);
            }

            if (string.IsNullOrWhiteSpace(routine.OutputDeviceId) && string.IsNullOrWhiteSpace(routine.InputDeviceId))
            {
                RecordRoutineHistory(routine, success: false, skipped: true, outputDeviceName: null, inputDeviceName: null, reason: "Routine has no configured targets.", outputSucceeded: null, inputSucceeded: null);
                return BuildRoutineErrorResult(5, "routine-has-no-targets", $"Routine '{routine.Name}' has no configured targets.", jsonOutput, routine, redactOutput: redactOutput);
            }

            if (!CliRoutineExecutionPolicy.TryResolveManualRunProcessId(routine, _routineProcessSnapshotProvider, out int? processId, out string? errorCode, out string? errorMessage))
            {
                RecordRoutineHistory(routine, success: false, skipped: true, outputDeviceName: null, inputDeviceName: null, reason: errorMessage, outputSucceeded: null, inputSucceeded: null);
                return BuildRoutineErrorResult(
                    5,
                    errorCode ?? "routine-trigger-app-not-running",
                    errorMessage ?? $"Routine '{routine.Name}' requires the target application to be running.",
                    jsonOutput,
                    routine,
                    CliRoutineExecutionPolicy.GetTriggerApplicationDisplayName(routine.TriggerAppPath),
                    requiresRunningTriggerProcess: true,
                    redactOutput: redactOutput);
            }

            string? outputDeviceName = null;
            bool outputFailed = false;
            bool? outputSucceeded = null;
            string? outputFailureDetail = null;
            if (!string.IsNullOrWhiteSpace(routine.OutputDeviceId))
            {
                RoutineSwitchExecutionResult outputResult = await ExecuteRoutineOutputSwitchAsync(routine, settings, processId);
                outputFailed = !outputResult.Success;
                outputSucceeded = outputResult.Success;
                outputFailureDetail = outputResult.FailureDetail;

                if (outputResult.Success)
                {
                    outputDeviceName = outputResult.DeviceName;
                }
            }

            string? inputDeviceName = null;
            bool inputFailed = false;
            bool? inputSucceeded = null;
            string? inputFailureDetail = null;
            if (!string.IsNullOrWhiteSpace(routine.InputDeviceId))
            {
                RoutineSwitchExecutionResult inputResult = await ExecuteRoutineInputSwitchAsync(routine, processId);
                inputFailed = !inputResult.Success;
                inputSucceeded = inputResult.Success;
                inputFailureDetail = inputResult.FailureDetail;

                if (inputResult.Success)
                {
                    inputDeviceName = inputResult.DeviceName;
                }
            }

            if (outputFailed || inputFailed)
            {
                RecordRoutineHistory(routine, success: false, skipped: false, outputDeviceName, inputDeviceName, outputFailureDetail ?? inputFailureDetail ?? "Routine execution failed.", outputSucceeded, inputSucceeded);
                return BuildRoutineErrorResult(
                    3,
                    "routine-run-failed",
                    $"Failed to run routine '{routine.Name}'.",
                    jsonOutput,
                    routine,
                    outputSucceeded: outputSucceeded,
                    appliedOutputDeviceName: outputSucceeded == true ? outputDeviceName ?? routine.OutputDeviceName : null,
                    outputFailureDetail: outputSucceeded == false ? outputFailureDetail : null,
                    inputSucceeded: inputSucceeded,
                    appliedInputDeviceName: inputSucceeded == true ? inputDeviceName ?? routine.InputDeviceName : null,
                    inputFailureDetail: inputSucceeded == false ? inputFailureDetail : null,
                    redactOutput: redactOutput);
            }

            RecordRoutineHistory(routine, success: true, skipped: false, outputDeviceName, inputDeviceName, reason: null, outputSucceeded, inputSucceeded);

            return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineRunResult(routine, outputDeviceName, inputDeviceName, jsonOutput, redactOutput));
        }

        public CliExecutionResult SetRoutineEnabled(string routineSelector, bool enabled, bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            CliRoutineResolutionResult resolution = CliRoutineResolver.Resolve(settings.Routines?.Items ?? [], routineSelector);
            if (resolution.Status != CliRoutineResolutionStatus.Success || resolution.Routine == null)
            {
                return BuildRoutineErrorResult(5, resolution.ErrorCode, resolution.Message, jsonOutput, redactOutput: redactOutput);
            }

            AudioRoutine routine = resolution.Routine;
            bool updated = routine.Enabled != enabled;
            routine.Enabled = enabled;

            try
            {
                SettingsService.SaveSettings(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineStateChange(routine, enabled, updated, jsonOutput, redactOutput));
            }
            catch
            {
                return BuildRoutineErrorResult(3, "routine-update-failed", $"Failed to update routine '{routine.Name}'.", jsonOutput, routine, redactOutput: redactOutput);
            }
        }

        public CliExecutionResult CreateRoutine(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            if (!TryLoadRoutineDraft(path, allowAnyPath, out string? fullPath, out AudioRoutine? draft, out CliExecutionResult errorResult, jsonOutput))
            {
                return errorResult;
            }

            Settings settings = SettingsService.LoadSettings();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Create(settings, draft!);
            if (!mutation.Success)
            {
                return BuildRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                SettingsService.SaveSettings(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Created", jsonOutput, redactOutput));
            }
            catch
            {
                return BuildRoutineMutationError(3, "routine-create-failed", $"Failed to create routine from {CliOutputFormatter.FormatPath(fullPath!, redactOutput)}.", jsonOutput);
            }
        }

        public CliExecutionResult UpdateRoutine(string routineSelector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            if (!TryLoadRoutineDraft(path, allowAnyPath, out string? fullPath, out AudioRoutine? draft, out CliExecutionResult errorResult, jsonOutput))
            {
                return errorResult;
            }

            Settings settings = SettingsService.LoadSettings();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Update(settings, routineSelector, draft!);
            if (!mutation.Success)
            {
                return BuildRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                SettingsService.SaveSettings(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Updated", jsonOutput, redactOutput));
            }
            catch
            {
                return BuildRoutineMutationError(3, "routine-update-failed", $"Failed to update routine from {CliOutputFormatter.FormatPath(fullPath!, redactOutput)}.", jsonOutput);
            }
        }

        public CliExecutionResult DeleteRoutine(string routineSelector, bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Delete(settings, routineSelector);
            if (!mutation.Success)
            {
                return BuildRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                SettingsService.SaveSettings(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Deleted", jsonOutput, redactOutput));
            }
            catch
            {
                return BuildRoutineMutationError(3, "routine-delete-failed", "Failed to delete routine.", jsonOutput);
            }
        }

        public CliExecutionResult ImportRoutines(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            if (!TryLoadRoutineCollection(path, allowAnyPath, out string? fullPath, out List<AudioRoutine>? routines, out CliExecutionResult errorResult, jsonOutput))
            {
                return errorResult;
            }

            Settings settings = SettingsService.LoadSettings();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Import(settings, routines!, replaceImport);
            if (!mutation.Success)
            {
                return BuildRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                SettingsService.SaveSettings(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineImportResult(mutation.ImportedCount, replaceImport, jsonOutput));
            }
            catch
            {
                return BuildRoutineMutationError(3, "routine-import-failed", $"Failed to import routines from {CliOutputFormatter.FormatPath(fullPath!, redactOutput)}.", jsonOutput);
            }
        }

        public string GetListenStatus(bool jsonOutput, bool redactOutput)
        {
            (bool? Enabled, string? MonitorTargetOutputDeviceName)? statusSnapshot = _audioOverrides?.GetListenStatusSnapshot?.Invoke();

            bool? currentInputListenEnabled = statusSnapshot?.Enabled;
            if (!currentInputListenEnabled.HasValue && AudioService.TryGetCurrentInputListenState(out bool enabled, out _))
            {
                currentInputListenEnabled = enabled;
            }

            string? listenMonitorTargetOutputDeviceName = statusSnapshot?.MonitorTargetOutputDeviceName;
            if (listenMonitorTargetOutputDeviceName == null)
            {
                AudioService.TryGetCurrentInputListenTargetOutputDeviceName(out listenMonitorTargetOutputDeviceName, out _);
            }

            return CliOutputFormatter.FormatListenStatus(currentInputListenEnabled, listenMonitorTargetOutputDeviceName, jsonOutput, redactOutput);
        }

        public string GetMuteStatus(string target, bool jsonOutput)
        {
            bool enabled = target switch
            {
                "mic" => TryGetDefaultCaptureMute() ?? false,
                "sound" => TryGetDefaultPlaybackMute() ?? false,
                "deafen" => (TryGetDefaultPlaybackMute() ?? false) && (TryGetDefaultCaptureMute() ?? false),
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown mute target."),
            };

            return CliOutputFormatter.FormatMuteStatus(target, enabled, jsonOutput);
        }

        public Task RefreshAsync()
        {
            return Task.CompletedTask;
        }

        public bool SetStartupEnabled(bool enabled)
        {
            try
            {
                if (enabled)
                {
                    StartupService.AddToStartup();
                }
                else
                {
                    StartupService.RemoveFromStartup();
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool OpenStartupSettings()
        {
            return false;
        }

        public string GetStartupStatus(bool jsonOutput)
        {
            return CliOutputFormatter.FormatStartupStatus(StartupService.IsInStartupWithValidPath(), jsonOutput);
        }

        public string GetStatus(bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(
                settings,
                GetActiveOutputDeviceInfos(),
                GetActiveInputDeviceInfos());

            List<string> warnings =
            [
                .. diagnostics.Warnings.Select(FormatDiagnostic)
            ];

            return CliOutputFormatter.FormatStatusSnapshot(
                StartupService.IsInStartupWithValidPath(),
                GetActiveOutputDeviceInfos().Count,
                GetActiveInputDeviceInfos().Count,
                settings.DeviceSwitching.Output.CycleDevices.Count,
                settings.DeviceSwitching.Input.CycleDevices.Count,
                AudioService.TryGetCurrentInputListenState(out bool listenEnabled, out _) ? listenEnabled : null,
                AudioService.TryGetCurrentInputListenTargetOutputDeviceName(out string? monitorTargetOutputDeviceName, out _)
                    ? monitorTargetOutputDeviceName
                    : null,
                warnings,
                jsonOutput,
                settings.Miscellaneous.BluetoothReconnectEnabled,
                BluetoothReconnectRuntimeConfig.MaxAttempts,
                BluetoothReconnectRuntimeConfig.AttemptTimeoutMs,
                BluetoothReconnectRuntimeConfig.CooldownMs,
                BluetoothReconnectRuntimeConfig.OnlyLikelyBluetoothEndpoints,
                RuntimeTuningConfig.OutputSwitchDebounceMs,
                RuntimeTuningConfig.InputSwitchDebounceMs,
                RuntimeTuningConfig.SwitchRetryDelayMs,
                RuntimeTuningConfig.SwitchRetryMaxDelayMs,
                RuntimeTuningConfig.SwitchMaxRetries,
                RuntimeTuningConfig.HotplugRefreshDebounceMs,
                RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs,
                RuntimeTuningConfig.MixerSessionRefreshDebounceMs,
                RuntimeTuningConfig.MixerSnapshotCacheInteractiveMs,
                RuntimeTuningConfig.MixerSnapshotCacheBackgroundMs,
                RuntimeTuningConfig.ResumeHotkeyRetryDelayMs,
                RuntimeTuningConfig.MixerDiagnosticsSummaryWindowSeconds,
                RuntimeTuningConfig.MixerCacheWindowDiagnosticsLogEveryNRefreshes,
                RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs,
                RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitThreshold,
                RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitOpenMs,
                redactOutput: redactOutput);
        }

        public string GetDiagnosticsStatus(bool jsonOutput, bool showPaths, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            LogFileInventory logInventory = LogArchiveExportService.GetInventory(GetLogRootDirectory());

            string settingsPath = SettingsService.GetSettingsPath();
            string? settingsDirectory = Path.GetDirectoryName(settingsPath);
            string settingsBackupDirectory = Path.Combine(settingsDirectory ?? string.Empty, AppConstants.Files.BackupFolderName);
            List<string> settingsBackupFiles = Directory.Exists(settingsBackupDirectory)
                ? [.. Directory.GetFiles(settingsBackupDirectory, AppConstants.Files.SettingsFileName + ".bak*").OrderBy(path => path, StringComparer.OrdinalIgnoreCase)]
                : [];

            return CliOutputFormatter.FormatDiagnosticsStatus(
                logInventory.LogFilePath,
                logInventory.LogFileExists,
                logInventory.LogFileBytes,
                logInventory.LogBackupDirectory,
                logInventory.LogBackupFiles,
                AppConstants.Files.LogBackupRetentionCount,
                AppConstants.Logging.LogBackupMaxAgeDays,
                settingsPath,
                settingsBackupDirectory,
                settingsBackupFiles,
                AppConstants.Files.SettingsBackupRetentionCount,
                jsonOutput,
                settings.Miscellaneous.BluetoothReconnectEnabled,
                BluetoothReconnectRuntimeConfig.MaxAttempts,
                BluetoothReconnectRuntimeConfig.AttemptTimeoutMs,
                BluetoothReconnectRuntimeConfig.CooldownMs,
                BluetoothReconnectRuntimeConfig.OnlyLikelyBluetoothEndpoints,
                RuntimeTuningConfig.OutputSwitchDebounceMs,
                RuntimeTuningConfig.InputSwitchDebounceMs,
                RuntimeTuningConfig.SwitchRetryDelayMs,
                RuntimeTuningConfig.SwitchRetryMaxDelayMs,
                RuntimeTuningConfig.SwitchMaxRetries,
                RuntimeTuningConfig.HotplugRefreshDebounceMs,
                RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs,
                RuntimeTuningConfig.MixerSessionRefreshDebounceMs,
                RuntimeTuningConfig.MixerSnapshotCacheInteractiveMs,
                RuntimeTuningConfig.MixerSnapshotCacheBackgroundMs,
                RuntimeTuningConfig.ResumeHotkeyRetryDelayMs,
                RuntimeTuningConfig.MixerDiagnosticsSummaryWindowSeconds,
                RuntimeTuningConfig.MixerCacheWindowDiagnosticsLogEveryNRefreshes,
                RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs,
                RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitThreshold,
                RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitOpenMs,
                includeSensitivePaths: showPaths,
                redactOutput: redactOutput);
        }

        public (bool Success, string Output) ExportLogs(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput, bool redactOutput)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, SettingsService.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:diagnostics-export-logs-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    const string message = "Only .zip log exports are supported.";
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-invalid-path", Error = message }))
                        : (false, $"[diag-code:diagnostics-export-logs-invalid-path] {message}");
                }

                LogArchiveExportResult result = LogArchiveExportService.ExportLogs(GetLogRootDirectory(), fullPath);
                return (true, CliOutputFormatter.FormatLogExportResult(result, detailLevel, jsonOutput, redactOutput));
            }
            catch (InvalidOperationException ex)
            {
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-unavailable", Error = ex.Message }))
                    : (false, $"[diag-code:diagnostics-export-logs-unavailable] {ex.Message}");
            }
            catch
            {
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-failed", Error = "Failed to export log archive." }))
                    : (false, "[diag-code:diagnostics-export-logs-failed] Failed to export log archive.");
            }
        }

        public (bool Success, string Output) ExportDiagnosticBundle(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool includeSensitive, bool jsonOutput)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, SettingsService.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-bundle-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:diagnostics-export-bundle-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    const string message = "Only .zip diagnostic bundle exports are supported.";
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-bundle-invalid-path", Error = message }))
                        : (false, $"[diag-code:diagnostics-export-bundle-invalid-path] {message}");
                }

                bool redactBundleOutput = !includeSensitive;
                DiagnosticBundlePayloads payloads = new(
                    StatusJson: GetDiagnosticsStatus(jsonOutput: true, showPaths: includeSensitive, redactOutput: redactBundleOutput),
                    HistoryJson: GetDiagnosticsHistory(jsonOutput: true, limit: 100, type: null, redactOutput: redactBundleOutput),
                    MediaStatusJson: GetDiagnosticBundleMediaStatusJson(redactBundleOutput),
                    ConfigValidationJson: GetConfigValidation(jsonOutput: true, redactOutput: redactBundleOutput).Output);

                DiagnosticBundleExportResult result = DiagnosticBundleExportService.ExportBundle(
                    GetLogRootDirectory(),
                    fullPath,
                    payloads,
                    includeSensitive);
                return (true, CliOutputFormatter.FormatDiagnosticBundleExportResult(result, detailLevel, jsonOutput));
            }
            catch
            {
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-bundle-failed", Error = "Failed to export diagnostic bundle." }))
                    : (false, "[diag-code:diagnostics-export-bundle-failed] Failed to export diagnostic bundle.");
            }
        }

        private static string GetDiagnosticBundleMediaStatusJson(bool redactOutput)
        {
            try
            {
                MediaOverlaySessionSnapshot snapshot = new MediaOverlayCommandService()
                    .GetCurrentMediaSnapshotAsync()
                    .GetAwaiter()
                    .GetResult();
                return CliOutputFormatter.FormatMediaStatus(snapshot, jsonOutput: true, redactOutput);
            }
            catch (Exception ex)
            {
                return CliOutputFormatter.SerializeCliJson(new
                {
                    Available = false,
                    DiagCode = "diagnostics-bundle-media-status-unavailable",
                    Error = ex.GetType().Name,
                });
            }
        }

        public (bool Success, string Output) ResetPerAppAudioRouting(bool jsonOutput)
        {
            try
            {
                PerAppAudioRoutingResetResult result = AudioService.ResetAllPerAppAudioRouting();
                return (result.Success, CliOutputFormatter.FormatPerAppAudioResetResult(result, jsonOutput));
            }
            catch
            {
                PerAppAudioRoutingResetResult failed = new(Success: false, HadAssignments: false);
                return (false, CliOutputFormatter.FormatPerAppAudioResetResult(failed, jsonOutput));
            }
        }

        public string GetDeviceList(bool output, bool jsonOutput, bool redactOutput)
        {
            string kind = output ? "output" : "input";
            var devices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            return CliOutputFormatter.FormatDeviceList(kind, devices, jsonOutput, redactOutput);
        }

        public (bool Found, string Output) GetDevice(bool output, string selector, bool jsonOutput, bool redactOutput)
        {
            string kind = output ? "output" : "input";
            List<CycleDevice> devices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            CliDeviceSelectorResolution resolution = CliDeviceSelectorResolver.ResolveExact(devices, selector);
            if (resolution.Success && resolution.Device != null)
            {
                return (true, CliOutputFormatter.FormatDeviceGetResult(kind, resolution.Device, jsonOutput, redactOutput));
            }

            string message = resolution.Ambiguous
                ? CliDeviceSelectorResolver.BuildAmbiguousMessage(kind, resolution.Selector, resolution.Matches)
                : CliDeviceSelectorResolver.BuildNotFoundMessage(kind, resolution.Selector);
            return (false, CliOutputFormatter.FormatDeviceGetError(kind, resolution.Ambiguous ? "device-selector-ambiguous" : "device-not-found", message, jsonOutput, redactOutput));
        }

        public (bool Found, string Output) FindDevices(bool output, string query, bool jsonOutput, bool redactOutput)
        {
            string kind = output ? "output" : "input";
            List<CycleDevice> devices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            IReadOnlyList<CycleDevice> matches = CliDeviceSelectorResolver.FindMatches(devices, query);
            return (matches.Count > 0, CliOutputFormatter.FormatDeviceFindResult(kind, query, matches, jsonOutput, redactOutput));
        }

        public string GetCycle(bool output, bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            string kind = output ? "output" : "input";
            IReadOnlyList<CycleDevice> cycle = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
            return CliOutputFormatter.FormatCycleList(kind, cycle, jsonOutput, redactOutput);
        }

        public (bool IsValid, string Output) GetCycleValidation(bool output, bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            string kind = output ? "output" : "input";
            IReadOnlyList<CycleDevice> cycleDevices = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            var result = SettingsValidationService.ValidateCycle(cycleDevices, activeDevices);
            string formatted = CliOutputFormatter.FormatCycleValidation(kind, result.DuplicateDeviceNames, result.DisconnectedDeviceNames, jsonOutput, redactOutput);
            return (result.IsValid, formatted);
        }

        public (bool CanSwitch, string Output) GetCycleTest(bool output, bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            string kind = output ? "output" : "input";
            IReadOnlyList<CycleDevice> cycleDevices = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            var preflight = SettingsValidationService.EvaluateCycleSwitchPreflight(
                cycleDevices,
                activeDevices,
                hasDefaultInputDevice: HasDefaultInputDevice(),
                output);

            return (
                preflight.CanSwitch,
                CliOutputFormatter.FormatCycleTest(
                    kind,
                    preflight.ConfiguredCount,
                    preflight.ConnectedConfiguredCount,
                    preflight.HasDefaultInputDevice,
                    preflight.Reasons,
                    jsonOutput,
                    redactOutput));
        }

        public (bool Success, string Output) AddCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput)
        {
            return UpdateCycle(output, "add", deviceId, null, jsonOutput, redactOutput);
        }

        public (bool Success, string Output) RemoveCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput)
        {
            return UpdateCycle(output, "remove", deviceId, null, jsonOutput, redactOutput);
        }

        public (bool Success, string Output) ReorderCycle(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput)
        {
            return UpdateCycle(output, "reorder", null, deviceIds, jsonOutput, redactOutput);
        }

        public (bool Found, string? Value, string? Error) GetConfig(string key)
        {
            Settings settings = SettingsService.LoadSettings();
            bool found = CliConfigManager.TryGet(settings, key, out string value, out string? error);
            return (found, found ? value : null, error);
        }

        public string GetConfigList(bool jsonOutput)
        {
            return CliOutputFormatter.FormatSupportedKeyList("config", CliConfigManager.GetKnownKeys(), jsonOutput);
        }

        public (bool Updated, string? Error) SetConfig(string key, string value)
        {
            Settings settings = SettingsService.LoadSettings();
            if (!CliConfigManager.TrySet(settings, key, value, out string? error))
            {
                return (false, error);
            }

            try
            {
                SettingsService.SaveSettings(settings);

                if (string.Equals(key, "run-at-startup", StringComparison.OrdinalIgnoreCase))
                {
                    _ = SetStartupEnabled(settings.RunAtStartup);
                }

                return (true, null);
            }
            catch
            {
                return (false, "Failed to persist config value.");
            }
        }

        public (bool Found, string? Value, string? Error) GetRuntime(string key)
        {
            bool found = CliRuntimeManager.TryGet(key, out string value, out string? error);
            return (found, found ? value : null, error);
        }

        public string GetRuntimeList(bool jsonOutput)
        {
            return CliOutputFormatter.FormatSupportedKeyList("runtime", CliRuntimeManager.GetKnownKeys(), jsonOutput);
        }

        public (bool Updated, string? Error) SetRuntime(string key, string value)
        {
            return CliRuntimeManager.TrySet(key, value, out string? error)
                ? (true, null)
                : (false, error);
        }

        public string GetDiagnosticsHistory(bool jsonOutput, int? limit, string? type, bool redactOutput)
        {
            return CliOutputFormatter.FormatExecutionHistory(
                _executionHistory.GetEntries(limit, TryParseExecutionHistoryKind(type)),
                jsonOutput,
                redactOutput);
        }

        public (bool Found, string Output) GetDiagnosticsHistoryDetail(string opId, bool jsonOutput, bool redactOutput)
        {
            ExecutionHistoryEntry? entry = _executionHistory.GetEntry(opId);
            return entry == null
                ? (false, CliOutputFormatter.FormatExecutionHistoryNotFound(opId, jsonOutput))
                : (true, CliOutputFormatter.FormatExecutionHistoryDetail(entry, jsonOutput, redactOutput));
        }

        public (bool IsValid, string Output) GetConfigValidation(bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(
                settings,
                GetActiveOutputDeviceInfos(),
                GetActiveInputDeviceInfos());

            List<string> warnings =
            [
                .. diagnostics.Warnings.Select(FormatDiagnostic)
            ];

            return (
                warnings.Count == 0,
                CliOutputFormatter.FormatConfigValidation(warnings, jsonOutput, redactOutput));
        }

        public (bool Success, string Output) ExportConfig(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            try
            {
                Settings settings = SettingsService.LoadSettings();
                if (!CliPathPolicy.TryResolveConfigPath(path, SettingsService.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-export-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:config-export-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                SettingsTransferService.ExportSettings(settings, fullPath);
                return jsonOutput
                    ? (true, CliOutputFormatter.SerializeCliJson(new { Success = true, ExportPath = CliOutputFormatter.FormatPath(fullPath, redactOutput), DiagCode = "config-export-success" }))
                    : (true, $"[diag-code:config-export-success] Exported config to {CliOutputFormatter.FormatPath(fullPath, redactOutput)}.");
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "cli-config-export-failed", nameof(ExportConfig), ex);
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-export-failed", Error = "Failed to export config." }))
                    : (false, "[diag-code:config-export-failed] Failed to export config.");
            }
        }

        public (bool Success, string Output) ExportRoutines(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            try
            {
                Settings settings = SettingsService.LoadSettings();
                if (!CliPathPolicy.TryResolveConfigPath(path, SettingsService.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "routine-export-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:routine-export-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                List<AudioRoutine> routines = CloneRoutines(settings.Routines?.Items ?? []);
                string payload = JsonConvert.SerializeObject(new
                {
                    SchemaVersion = Settings.CurrentSchemaVersion,
                    Routines = routines,
                }, Formatting.Indented);

                File.WriteAllText(fullPath, payload);
                return (true, CliOutputFormatter.FormatRoutineExportResult(fullPath, routines.Count, jsonOutput, redactOutput));
            }
            catch
            {
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "routine-export-failed", Error = "Failed to export routines." }))
                    : (false, "[diag-code:routine-export-failed] Failed to export routines.");
            }
        }

        public (bool Success, string Output) ImportConfig(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, SettingsService.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-import-path-blocked", Error = pathError ?? "Import path is not allowed." }))
                        : (false, $"[diag-code:config-import-path-blocked] {pathError ?? "Import path is not allowed."}");
                }

                if (!File.Exists(fullPath))
                {
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-import-file-missing", Path = CliOutputFormatter.FormatPath(fullPath, redactOutput) }))
                        : (false, $"[diag-code:config-import-file-missing] Import file not found: {CliOutputFormatter.FormatPath(fullPath, redactOutput)}");
                }

                Settings current = SettingsService.LoadSettings();
                string importJson = SettingsTransferService.ReadImportText(fullPath, SettingsService.ReadTextFileWithSettingsLock);
                Settings imported = SettingsTransferService.ParseImportedSettings(importJson, current, replaceImport);
                SettingsService.SaveSettings(imported);

                return jsonOutput
                    ? (true, CliOutputFormatter.SerializeCliJson(new { Success = true, Mode = replaceImport ? "replace" : "merge", DiagCode = "config-import-success", Path = CliOutputFormatter.FormatPath(fullPath, redactOutput) }))
                    : (true, $"[diag-code:config-import-success] Imported config from {CliOutputFormatter.FormatPath(fullPath, redactOutput)} using {(replaceImport ? "replace" : "merge")} mode.");
            }
            catch (InvalidDataException ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "cli-config-import-failed", nameof(ImportConfig), ex);
                return BuildConfigImportFailure("config-import-invalid-data", ex.Message, jsonOutput);
            }
            catch (JsonException ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "cli-config-import-failed", nameof(ImportConfig), ex);
                return BuildConfigImportFailure("config-import-invalid-json", "Imported config is not valid JSON.", jsonOutput);
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "cli-config-import-failed", nameof(ImportConfig), ex);
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-import-failed", Error = "Failed to import config." }))
                    : (false, "[diag-code:config-import-failed] Failed to import config.");
            }
        }

        private static (bool Success, string Output) BuildConfigImportFailure(string diagCode, string message, bool jsonOutput)
        {
            return jsonOutput
                ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = diagCode, Error = message }))
                : (false, $"[diag-code:{diagCode}] {message}");
        }

        public string GetNetworkList(bool jsonOutput)
        {
            try
            {
                HashSet<string> wifiNetworks = NativeWifiScanner.GetAvailableSsids(logger: Logger.Instance);
                HashSet<string> nlmNetworks = Coordinators.NetworkTriggerCoordinator.GetAvailableNetworkNames();
                HashSet<string> allNetworks = [.. wifiNetworks, .. nlmNetworks];
                List<string> sortedNetworks = [.. allNetworks.Order()];

                if (jsonOutput)
                {
                    return CliOutputFormatter.SerializeCliJson(new
                    {
                        Success = true,
                        Networks = sortedNetworks
                    });
                }

                if (sortedNetworks.Count == 0)
                {
                    return "No networks detected.";
                }

                return string.Join("\n", sortedNetworks);
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "cli-network-list-failed", nameof(GetNetworkList), ex);

                if (jsonOutput)
                {
                    return CliOutputFormatter.SerializeCliJson(new
                    {
                        Success = false,
                        DiagCode = "network-list-failed",
                        Error = "Failed to scan networks."
                    });
                }

                return "[diag-code:network-list-failed] Failed to scan networks.";
            }
        }

        public (bool CanSwitch, string Output) PreviewSwitch(bool output, bool reverse, bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            var cycleDevices = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            string kind = output ? "output" : "input";

            var preflight = SettingsValidationService.EvaluateCycleSwitchPreflight(
                cycleDevices,
                activeDevices,
                hasDefaultInputDevice: HasDefaultInputDevice(),
                output);

            if (!preflight.CanSwitch)
            {
                return (false, CliOutputFormatter.FormatCycleTest(kind, preflight.ConfiguredCount, preflight.ConnectedConfiguredCount, preflight.HasDefaultInputDevice, preflight.Reasons, jsonOutput, redactOutput));
            }

            string? currentId = GetCurrentDeviceId(output);
            int currentIndex = cycleDevices.ToList().FindIndex(device => string.Equals(device.Id, currentId, StringComparison.OrdinalIgnoreCase));
            int targetIndex = ResolveCycleTargetIndex(currentIndex, cycleDevices.Count, reverse);
            string targetId = cycleDevices[targetIndex].Id;
            string targetName = cycleDevices[targetIndex].Name;

            if (jsonOutput)
            {
                return (true, CliOutputFormatter.SerializeCliJson(new { Kind = kind, DryRun = true, CurrentDeviceId = currentId, TargetDeviceId = targetId, TargetDeviceName = CliOutputFormatter.FormatDeviceName(targetName, redactOutput), DiagCode = "switch-dry-run" }));
            }

            return (true, $"[diag-code:switch-dry-run] {kind} switch would target '{CliOutputFormatter.FormatDeviceName(targetName, redactOutput)}' ({targetId}).");
        }

        public string? GetCurrentDeviceId(bool output)
        {
            try
            {
                if (output)
                {
                    if (_audioOverrides?.GetCurrentOutputDeviceId != null)
                    {
                        return _audioOverrides.GetCurrentOutputDeviceId();
                    }

                    using var device = AudioService.GetDefaultPlaybackDevice();
                    return device?.ID;
                }

                if (_audioOverrides?.GetCurrentInputDeviceId != null)
                {
                    return _audioOverrides.GetCurrentInputDeviceId();
                }

                using var input = AudioService.GetDefaultRecordingDevice();
                return input?.ID;
            }
            catch
            {
                return null;
            }
        }

        public async Task<(bool Found, string Output)> WaitForDeviceAsync(string deviceId, int timeoutMs, bool outputOnly, bool inputOnly, bool jsonOutput, bool redactOutput)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool found = await WaitForConditionAsync(
                () => IsDeviceAvailable(deviceId, outputOnly, inputOnly),
                timeoutMs,
                RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs,
                usePollingFallbackWhenOverridesPresent: true);

            if (found)
            {
                return jsonOutput
                    ? (true, CliOutputFormatter.SerializeCliJson(new { DeviceId = deviceId, Found = true, Scope = outputOnly ? "output" : inputOnly ? "input" : "any", ElapsedMs = sw.ElapsedMilliseconds, DiagCode = "wait-device-found" }))
                    : (true, $"[diag-code:wait-device-found] Device '{deviceId}' is available.");
            }

            return jsonOutput
                ? (false, CliOutputFormatter.SerializeCliJson(new { DeviceId = deviceId, Found = false, Scope = outputOnly ? "output" : inputOnly ? "input" : "any", TimeoutMs = timeoutMs, ElapsedMs = sw.ElapsedMilliseconds, DiagCode = "wait-device-timeout" }))
                : (false, $"[diag-code:wait-device-timeout] Timed out waiting for device '{deviceId}' after {timeoutMs}ms.");
        }

        private static string FormatDiagnostic(SettingsDiagnostic warning)
        {
            return $"[diag-code:{warning.Code}] {warning.Message} {warning.SuggestedAction}";
        }

        private string GetLogRootDirectory()
        {
            return _audioOverrides?.GetLogRootDirectory?.Invoke() ?? AppDataPaths.GetWritableDataRoot();
        }

        private static CliExecutionResult BuildRoutineErrorResult(
            int exitCode,
            string errorCode,
            string message,
            bool jsonOutput,
            AudioRoutine? routine = null,
            string? triggerApplicationName = null,
            bool? requiresRunningTriggerProcess = null,
            bool? outputSucceeded = null,
            string? appliedOutputDeviceName = null,
            string? outputFailureDetail = null,
            bool? inputSucceeded = null,
            string? appliedInputDeviceName = null,
            string? inputFailureDetail = null,
            bool redactOutput = false)
        {
            return jsonOutput
                ? new CliExecutionResult(exitCode, CliOutputFormatter.FormatRoutineError(exitCode, errorCode, message, jsonOutput: true, routine, triggerApplicationName, requiresRunningTriggerProcess, outputSucceeded, appliedOutputDeviceName, outputFailureDetail, inputSucceeded, appliedInputDeviceName, inputFailureDetail, redactOutput))
                : new CliExecutionResult(exitCode, CliOutputFormatter.FormatRoutineError(exitCode, errorCode, message, jsonOutput: false, routine, triggerApplicationName, requiresRunningTriggerProcess, outputSucceeded, appliedOutputDeviceName, outputFailureDetail, inputSucceeded, appliedInputDeviceName, inputFailureDetail, redactOutput));
        }

        private static CliExecutionResult BuildRoutineMutationError(int exitCode, string errorCode, string message, bool jsonOutput)
        {
            return jsonOutput
                ? new CliExecutionResult(exitCode, CliCommandExecutor.BuildJsonErrorPayload(exitCode, errorCode, message))
                : new CliExecutionResult(exitCode, $"[diag-code:{errorCode}] {message}");
        }

        private bool TryLoadRoutineDraft(string path, bool allowAnyPath, out string? fullPath, out AudioRoutine? draft, out CliExecutionResult errorResult, bool jsonOutput)
        {
            if (!CliRoutineTransferHelper.TryLoadRoutineDraft(
                path,
                SettingsService.GetSettingsPath(),
                allowAnyPath,
                out fullPath,
                out draft,
                out string? errorCode,
                out string? errorMessage))
            {
                errorResult = BuildRoutineMutationError(5, errorCode ?? "routine-import-invalid", errorMessage ?? "Failed to load routine.", jsonOutput);
                return false;
            }

            errorResult = default;
            return true;
        }

        private bool TryLoadRoutineCollection(string path, bool allowAnyPath, out string? fullPath, out List<AudioRoutine>? routines, out CliExecutionResult errorResult, bool jsonOutput)
        {
            if (!CliRoutineTransferHelper.TryLoadRoutineCollection(
                path,
                SettingsService.GetSettingsPath(),
                allowAnyPath,
                out fullPath,
                out routines,
                out string? errorCode,
                out string? errorMessage))
            {
                errorResult = BuildRoutineMutationError(5, errorCode ?? "routine-import-invalid", errorMessage ?? "Failed to load routines.", jsonOutput);
                return false;
            }

            errorResult = default;
            return true;
        }

        private async Task<RoutineSwitchExecutionResult> ExecuteRoutineOutputSwitchAsync(AudioRoutine routine, Settings settings, int? processId)
        {
            await TryReconnectRoutineTargetAsync(
                routine.OutputDeviceId,
                routine.OutputDeviceName,
                BluetoothReconnectDeviceKind.Output,
                settings,
                configured => AudioService.TryGetActivePlaybackCycleEntry(configured.Id, configured.Name),
                opId: $"routine-output-reconnect:{routine.Id}");

            if (routine.SwitchOutputPerApp && processId is > 0)
            {
                string opId = $"routine-app-output:{routine.Id}:{processId.Value}";

                try
                {
                    ProcessAudioDeviceSwitchResult result = _audioOverrides?.SwitchApplicationOutputDeviceDetailedAsync != null
                        ? _audioOverrides.SwitchApplicationOutputDeviceDetailedAsync((uint)processId.Value, routine.OutputDeviceId, routine.OutputDeviceName, opId)
                        : await AudioService.SwitchApplicationOutputDeviceDetailedAsync(
                            (uint)processId.Value,
                            routine.OutputDeviceId,
                            routine.OutputDeviceName,
                            opId: opId);

                    bool switchApplied = result.Result == ProcessAudioRoutingResult.Applied;

                    return new RoutineSwitchExecutionResult(
                        switchApplied,
                        result.DeviceName,
                        switchApplied ? null : BuildPerAppRoutingFailureDetail("output", result.Result));
                }
                catch (Exception ex)
                {
                    Logger.Instance.Error("LocalHeadlessCommandRunner", "routine-output-switch-failed", nameof(ExecuteRoutineOutputSwitchAsync), ex);
                    return new RoutineSwitchExecutionResult(false, null, BuildRoutineSwitchExceptionDetail("output", ex));
                }
            }

            MMDevice? currentDefault = null;
            try
            {
                if (_audioOverrides?.GetDefaultPlaybackDeviceSnapshot != null)
                {
                    (string? currentDeviceId, string? currentDeviceName) = _audioOverrides.GetDefaultPlaybackDeviceSnapshot();
                    if (string.IsNullOrWhiteSpace(currentDeviceId))
                    {
                        return new RoutineSwitchExecutionResult(false, null, "No default output device is available.");
                    }

                    if (string.Equals(currentDeviceId, routine.OutputDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        return new RoutineSwitchExecutionResult(true, currentDeviceName);
                    }

                    bool currentMuteMic = TryGetDefaultCaptureMute() ?? false;
                    bool currentMuteSound = TryGetDefaultPlaybackMute() ?? false;
                    bool currentDeafen = currentMuteMic && currentMuteSound;
                    string directOutputOpId = $"routine-output:{routine.Id}";
                    (bool switchSucceeded, string? switchedDeviceName) = _audioOverrides.SwitchAudioDeviceAsync != null
                        ? _audioOverrides.SwitchAudioDeviceAsync(currentDeviceId, routine.OutputDeviceId, currentMuteMic, currentMuteSound, currentDeafen, settings.Miscellaneous.PreserveAudioLevels, directOutputOpId)
                    : await AudioService.SwitchAudioDeviceAsync(
                        currentDeviceId,
                        routine.OutputDeviceId,
                        currentMuteMic,
                        currentMuteSound,
                        currentDeafen,
                        settings.Miscellaneous.PreserveAudioLevels,
                        opId: directOutputOpId);

                    return new RoutineSwitchExecutionResult(switchSucceeded, switchedDeviceName, switchSucceeded ? null : "Failed to switch the default output device.");
                }

                currentDefault = AudioService.GetDefaultPlaybackDevice();
                if (currentDefault == null)
                {
                    return new RoutineSwitchExecutionResult(false, null, "No default output device is available.");
                }

                if (string.Equals(currentDefault.ID, routine.OutputDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return new RoutineSwitchExecutionResult(true, currentDefault.FriendlyName);
                }

                bool muteMic = TryGetDefaultCaptureMute() ?? false;
                bool muteSound = TryGetDefaultPlaybackMute() ?? false;
                bool deafen = muteMic && muteSound;

                string opId = $"routine-output:{routine.Id}";
                (bool success, string? deviceName) = _audioOverrides?.SwitchAudioDeviceAsync != null
                    ? _audioOverrides.SwitchAudioDeviceAsync(currentDefault.ID, routine.OutputDeviceId, muteMic, muteSound, deafen, settings.Miscellaneous.PreserveAudioLevels, opId)
                    : await AudioService.SwitchAudioDeviceAsync(
                        currentDefault.ID,
                        routine.OutputDeviceId,
                        muteMic,
                        muteSound,
                        deafen,
                        settings.Miscellaneous.PreserveAudioLevels,
                        opId: opId);

                return new RoutineSwitchExecutionResult(success, deviceName, success ? null : "Failed to switch the default output device.");
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "routine-output-switch-failed", nameof(ExecuteRoutineOutputSwitchAsync), ex);
                return new RoutineSwitchExecutionResult(false, null, BuildRoutineSwitchExceptionDetail("output", ex));
            }
            finally
            {
                DisposeForCleanup(currentDefault, nameof(ExecuteRoutineOutputSwitchAsync), "routine-output-current-default");
            }
        }

        private async Task<RoutineSwitchExecutionResult> ExecuteRoutineInputSwitchAsync(AudioRoutine routine, int? processId)
        {
            Settings settings = SettingsService.LoadSettings();
            await TryReconnectRoutineTargetAsync(
                routine.InputDeviceId,
                routine.InputDeviceName,
                BluetoothReconnectDeviceKind.Input,
                settings,
                configured => AudioService.TryGetActiveRecordingCycleEntry(configured.Id, configured.Name),
                opId: $"routine-input-reconnect:{routine.Id}");

            if (routine.SwitchOutputPerApp && processId is > 0)
            {
                string opId = $"routine-app-input:{routine.Id}:{processId.Value}";

                try
                {
                    ProcessAudioDeviceSwitchResult result = _audioOverrides?.SwitchApplicationInputDeviceDetailedAsync != null
                        ? _audioOverrides.SwitchApplicationInputDeviceDetailedAsync((uint)processId.Value, routine.InputDeviceId, routine.InputDeviceName, opId)
                        : await AudioService.SwitchApplicationInputDeviceDetailedAsync(
                            (uint)processId.Value,
                            routine.InputDeviceId,
                            routine.InputDeviceName,
                            opId: opId);

                    bool switchApplied = result.Result == ProcessAudioRoutingResult.Applied;

                    return new RoutineSwitchExecutionResult(
                        switchApplied,
                        result.DeviceName,
                        switchApplied ? null : BuildPerAppRoutingFailureDetail("input", result.Result));
                }
                catch (Exception ex)
                {
                    Logger.Instance.Error("LocalHeadlessCommandRunner", "routine-input-switch-failed", nameof(ExecuteRoutineInputSwitchAsync), ex);
                    return new RoutineSwitchExecutionResult(false, null, BuildRoutineSwitchExceptionDetail("input", ex));
                }
            }

            try
            {
                string directInputOpId = $"routine-input:{routine.Id}";
                (bool switchSucceeded, string? switchedDeviceName) = _audioOverrides?.SwitchInputDeviceToAsync != null
                    ? _audioOverrides.SwitchInputDeviceToAsync(routine.InputDeviceId, routine.InputDeviceName, directInputOpId)
                    : await AudioService.SwitchInputDeviceToAsync(
                        routine.InputDeviceId,
                        routine.InputDeviceName,
                        settings.Miscellaneous.PreserveAudioLevels,
                        showOverlay: null,
                        opId: directInputOpId);

                return new RoutineSwitchExecutionResult(switchSucceeded, switchedDeviceName, switchSucceeded ? null : "Failed to switch the default input device.");
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "routine-input-switch-failed", nameof(ExecuteRoutineInputSwitchAsync), ex);
                return new RoutineSwitchExecutionResult(false, null, BuildRoutineSwitchExceptionDetail("input", ex));
            }
        }

        private static string BuildPerAppRoutingFailureDetail(string flow, ProcessAudioRoutingResult result)
        {
            return result switch
            {
                ProcessAudioRoutingResult.DeferredNoAudio => $"Per-app {flow} routing is pending until the application produces audio.",
                _ => $"Failed to apply per-app {flow} routing.",
            };
        }

        private static string BuildRoutineSwitchExceptionDetail(string flow, Exception exception)
        {
            return $"{char.ToUpperInvariant(flow[0])}{flow[1..]} switch threw {exception.GetType().Name}.";
        }

        private async Task TryReconnectRoutineTargetAsync(
            string? deviceId,
            string? deviceName,
            BluetoothReconnectDeviceKind deviceKind,
            Settings settings,
            Func<CycleDevice, CycleDevice?> tryResolveActiveDevice,
            string opId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }

            var configuredDevice = new CycleDevice
            {
                Id = deviceId,
                Name = deviceName ?? string.Empty,
            };

            if (tryResolveActiveDevice(configuredDevice) != null)
            {
                return;
            }

            BluetoothReconnectAttemptResult reconnectResult = await BluetoothReconnectCoordinator.TryReconnectDetailedAsync(
                [configuredDevice],
                [],
                deviceKind,
                BluetoothReconnectOptions.FromSettings(settings),
                opId);

            if (reconnectResult.Attempted)
            {
                await WaitForConditionAsync(
                    () => tryResolveActiveDevice(configuredDevice) != null,
                    RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs,
                    RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs,
                    usePollingFallbackWhenOverridesPresent: true);
            }
        }

        private bool IsDeviceAvailable(string deviceId, bool outputOnly, bool inputOnly)
        {
            bool outputFound = (outputOnly || (!outputOnly && !inputOnly)) &&
                GetActiveOutputDeviceInfos().Any(device => string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            bool inputFound = (inputOnly || (!outputOnly && !inputOnly)) &&
                GetActiveInputDeviceInfos().Any(device => string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase));

            return outputFound || inputFound;
        }

        private async Task<bool> WaitForConditionAsync(
            Func<bool> condition,
            int timeoutMs,
            int pollingFallbackDelayMs,
            bool usePollingFallbackWhenOverridesPresent)
        {
            if (condition())
            {
                return true;
            }

            if (timeoutMs <= 0)
            {
                return false;
            }

            if (CanSubscribeToDeviceStateChanged())
            {
                var signal = new SemaphoreSlim(0);
                using IDisposable signalSubscription = SubscribeDeviceStateChanged(() =>
                {
                    try
                    {
                        signal.Release();
                    }
                    catch (SemaphoreFullException)
                    {
                    }
                });
                if (condition())
                {
                    return true;
                }

                var signalWaitStopwatch = System.Diagnostics.Stopwatch.StartNew();
                while (!condition())
                {
                    int remainingMs = timeoutMs - (int)signalWaitStopwatch.ElapsedMilliseconds;
                    if (remainingMs <= 0)
                    {
                        return false;
                    }

                    try
                    {
                        if (!await signal.WaitAsync(TimeSpan.FromMilliseconds(remainingMs)))
                        {
                            return false;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }

                return true;
            }

            if (_audioOverrides != null && !usePollingFallbackWhenOverridesPresent)
            {
                return false;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                if (condition())
                {
                    return true;
                }

                await DelayAsync(pollingFallbackDelayMs);
            }
            while (sw.ElapsedMilliseconds < timeoutMs);

            return condition();
        }

        private Task DelayAsync(int delayMs)
        {
            return _audioOverrides?.DelayAsync?.Invoke(delayMs) ?? Task.Delay(delayMs);
        }

        private bool CanSubscribeToDeviceStateChanged()
        {
            if (_audioOverrides?.SubscribeDeviceStateChanged != null)
            {
                return true;
            }

            if (_audioOverrides != null)
            {
                return false;
            }

            return true;
        }

        private IDisposable SubscribeDeviceStateChanged(Action onChanged)
        {
            if (_audioOverrides?.SubscribeDeviceStateChanged != null)
            {
                return _audioOverrides.SubscribeDeviceStateChanged(onChanged);
            }

            AudioService.DeviceStateChanged += onChanged;
            return new DelegateDisposable(() => AudioService.DeviceStateChanged -= onChanged);
        }

        private static List<AudioRoutine> CloneRoutines(IEnumerable<AudioRoutine>? routines)
        {
            if (routines == null)
            {
                return [];
            }

            var cloned = new List<AudioRoutine>();
            foreach (AudioRoutine? routine in routines)
            {
                if (routine == null)
                {
                    continue;
                }

                cloned.Add(routine.Clone());
            }

            return cloned;
        }

        private (bool Success, string Output) UpdateCycle(
            bool output,
            string action,
            string? deviceId,
            IReadOnlyList<string>? orderedDeviceIds,
            bool jsonOutput,
            bool redactOutput)
        {
            try
            {
                Settings settings = SettingsService.LoadSettings();
                List<CycleDevice> cycleDevices = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
                List<CycleDevice> activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
                string kind = output ? "output" : "input";
                string? deviceName = FindDeviceName(cycleDevices, activeDevices, deviceId);

                bool updated;
                string? error;
                switch (action)
                {
                    case "add":
                        if (!TryResolveCycleMutationSelector(activeDevices, kind, deviceId ?? string.Empty, out string resolvedAddDeviceId, out string? addSelectorError))
                        {
                            return (false, FormatCycleFailure(action, addSelectorError ?? "Failed to resolve device selector.", jsonOutput));
                        }

                        deviceId = resolvedAddDeviceId;
                        deviceName = FindDeviceName(cycleDevices, activeDevices, deviceId);
                        updated = CliCycleManager.TryAddDevice(cycleDevices, activeDevices, resolvedAddDeviceId, out _, out _, out string addMessage);
                        error = addMessage;
                        break;
                    case "remove":
                        if (!TryResolveCycleMutationSelector(cycleDevices, kind, deviceId ?? string.Empty, out string resolvedRemoveDeviceId, out string? removeSelectorError, selector => $"Device '{selector}' is not configured in the cycle."))
                        {
                            return (false, FormatCycleFailure(action, removeSelectorError ?? "Failed to resolve device selector.", jsonOutput));
                        }

                        deviceId = resolvedRemoveDeviceId;
                        deviceName = FindDeviceName(cycleDevices, activeDevices, deviceId);
                        updated = CliCycleManager.TryRemoveDevice(cycleDevices, resolvedRemoveDeviceId, out _, out _, out string removeMessage);
                        error = removeMessage;
                        break;
                    case "reorder":
                        updated = CliCycleManager.TryReorder(cycleDevices, orderedDeviceIds ?? [], out _, out string reorderMessage);
                        error = reorderMessage;
                        break;
                    default:
                        updated = false;
                        error = "Unsupported cycle action.";
                        break;
                }

                if (!updated)
                {
                    return (false, FormatCycleFailure(action, error ?? "Failed to update cycle.", jsonOutput));
                }

                SettingsService.SaveSettings(settings);
                return (true, CliOutputFormatter.FormatCycleMutationResult(kind, action, GetCycleDiagCode(action), cycleDevices, deviceId, deviceName, jsonOutput, redactOutput));
            }
            catch
            {
                return (false, FormatCycleFailure(action, "Failed to persist cycle changes.", jsonOutput));
            }
        }

        private static string? FindDeviceName(List<CycleDevice> cycleDevices, List<CycleDevice> activeDevices, string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return null;
            }

            for (int index = 0; index < cycleDevices.Count; index++)
            {
                if (string.Equals(cycleDevices[index].Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return cycleDevices[index].Name;
                }
            }

            for (int index = 0; index < activeDevices.Count; index++)
            {
                if (string.Equals(activeDevices[index].Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return activeDevices[index].Name;
                }
            }

            return null;
        }

        private bool TryResolveCliVolumeDevice(bool playback, string selectorSpec, out string? resolvedDeviceId, out string? error)
        {
            resolvedDeviceId = null;
            error = null;

            CliDeviceSelectorQuery query = CliDeviceSelectorResolver.Decode(selectorSpec);
            if (string.IsNullOrWhiteSpace(query.Value))
            {
                return true;
            }

            string kind = playback ? "output" : "input";
            List<CycleDevice> devices = playback ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            CliDeviceSelectorResolution resolution = CliDeviceSelectorResolver.ResolveExact(devices, selectorSpec);
            if (resolution.Success && resolution.Device != null)
            {
                resolvedDeviceId = resolution.Device.Id;
                return true;
            }

            if (query.Kind == CliDeviceSelectorKind.ExactId)
            {
                resolvedDeviceId = query.Value;
                return true;
            }

            error = resolution.Ambiguous
                ? CliDeviceSelectorResolver.BuildAmbiguousMessage(kind, resolution.Selector, resolution.Matches)
                : CliDeviceSelectorResolver.BuildNotFoundMessage(kind, resolution.Selector);
            return false;
        }

        private static bool TryResolveCycleMutationSelector(IReadOnlyList<CycleDevice> devices, string kind, string selectorSpec, out string resolvedDeviceId, out string? error, Func<string, string>? notFoundMessageFactory = null)
        {
            resolvedDeviceId = selectorSpec;
            error = null;

            CliDeviceSelectorResolution resolution = CliDeviceSelectorResolver.ResolveExact(devices, selectorSpec);
            if (resolution.Success && resolution.Device != null)
            {
                resolvedDeviceId = resolution.Device.Id;
                return true;
            }

            error = resolution.Ambiguous
                ? CliDeviceSelectorResolver.BuildAmbiguousMessage(kind, resolution.Selector, resolution.Matches)
                : notFoundMessageFactory?.Invoke(resolution.Selector) ?? CliDeviceSelectorResolver.BuildNotFoundMessage(kind, resolution.Selector);
            return false;
        }

        private static string GetCycleDiagCode(string action)
        {
            return action switch
            {
                "add" => "cycle-add-success",
                "remove" => "cycle-remove-success",
                "reorder" => "cycle-reorder-success",
                _ => "cycle-update-success",
            };
        }

        private static string FormatCycleFailure(string action, string message, bool jsonOutput)
        {
            string diagCode = action switch
            {
                "add" => "cycle-add-failed",
                "remove" => "cycle-remove-failed",
                "reorder" => "cycle-reorder-failed",
                _ => "cycle-update-failed",
            };

            return jsonOutput
                ? CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = diagCode, Error = message })
                : $"[diag-code:{diagCode}] {message}";
        }

        public async ValueTask<bool> SwitchOutputAsync(bool muteMic, bool muteSound, bool deafen, bool reverse)
        {
            string? beforeDeviceName = TryGetCurrentDefaultDeviceName(output: true);
            Settings settings = SettingsService.LoadSettings();
            List<CycleDevice> configuredCycle =
            [
                .. settings.DeviceSwitching.Output.CycleDevices
                    .Where(device => device != null && !string.IsNullOrWhiteSpace(device.Id))
                    .Select(device => new CycleDevice { Id = device.Id, Name = device.Name })
            ];

            if (configuredCycle.Count == 0)
            {
                return false;
            }

            List<MMDevice> activeDevices = [];
            MMDevice? currentDevice = null;

            try
            {
                activeDevices = [.. AudioService.GetActivePlaybackDevices().Cast<MMDevice>()];
                var activeById = activeDevices.ToDictionary(d => d.ID, d => d, StringComparer.OrdinalIgnoreCase);
                List<CycleDevice> connectedCycle =
                [
                    .. configuredCycle
                        .Where(d => activeById.ContainsKey(d.Id))
                        .Select(d => new CycleDevice { Id = d.Id, Name = activeById[d.Id].FriendlyName })
                ];

                List<CycleDevice> skippedCycle =
                [
                    .. configuredCycle
                        .Where(d => !activeById.ContainsKey(d.Id))
                        .Select(d => new CycleDevice { Id = d.Id, Name = d.Name })
                ];

                if (connectedCycle.Count <= 1)
                {
                    BluetoothReconnectOptions reconnectOptions = BluetoothReconnectOptions.FromSettings(settings);
                    BluetoothReconnectAttemptResult reconnectResult = await BluetoothReconnectCoordinator.TryReconnectDetailedAsync(
                        configuredCycle,
                        new HashSet<string>(activeById.Keys, StringComparer.OrdinalIgnoreCase),
                        BluetoothReconnectDeviceKind.Output,
                        reconnectOptions,
                        opId: "cli");

                    if (reconnectResult.Connected)
                    {
                        RefreshActivePlaybackDevices(ref activeDevices, ref activeById, ref connectedCycle, configuredCycle);
                        skippedCycle =
                        [
                            .. configuredCycle
                                .Where(d => !activeById.ContainsKey(d.Id))
                                .Select(d => new CycleDevice { Id = d.Id, Name = d.Name })
                        ];
                    }
                    else if (reconnectResult.Attempted)
                    {
                        await DelayAsync(ResolveBluetoothPostAttemptRecheckDelayMs());
                        RefreshActivePlaybackDevices(ref activeDevices, ref activeById, ref connectedCycle, configuredCycle);
                        skippedCycle =
                        [
                            .. configuredCycle
                                .Where(d => !activeById.ContainsKey(d.Id))
                                .Select(d => new CycleDevice { Id = d.Id, Name = d.Name })
                        ];
                    }
                }

                if (connectedCycle.Count <= 1)
                {
                    return false;
                }

                currentDevice = AudioService.GetDefaultPlaybackDevice();
                if (currentDevice == null)
                {
                    return false;
                }

                int currentIndex = connectedCycle.FindIndex(d => d.Id.Equals(currentDevice.ID, StringComparison.OrdinalIgnoreCase));
                int targetIndex = ResolveCycleTargetIndex(currentIndex, connectedCycle.Count, reverse);
                if (targetIndex < 0)
                {
                    return false;
                }

                (bool success, _) = await AudioService.SwitchAudioDeviceAsync(
                    currentDevice.ID,
                    connectedCycle[targetIndex].Id,
                    muteMic,
                    muteSound,
                    deafen,
                    settings.Miscellaneous.PreserveAudioLevels,
                    opId: "cli");

                string? afterDeviceName = TryGetCurrentDefaultDeviceName(output: true);
                RecordCliActionHistory(ExecutionHistoryKind.Switch, reverse ? "switch-output-reverse" : "switch-output", success, skipped: false, success ? $"Output switch completed{(string.IsNullOrWhiteSpace(afterDeviceName) ? string.Empty : $" to '{afterDeviceName}'")}." : "Output switch failed or was rejected.", success ? null : "Output switch failed or was rejected.", target: beforeDeviceName, outputDeviceName: afterDeviceName);
                return success;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(SwitchOutputAsync), "cli-switch-output-failed", ex);
                RecordCliActionHistory(ExecutionHistoryKind.Switch, reverse ? "switch-output-reverse" : "switch-output", success: false, skipped: false, summary: "Output switch failed or was rejected.", reason: "Output switch threw an exception.", target: beforeDeviceName);
                return false;
            }
            finally
            {
                DisposeForCleanup(currentDevice, nameof(SwitchOutputAsync), "switch-output-current-device");
                foreach (var device in activeDevices)
                {
                    DisposeForCleanup(device, nameof(SwitchOutputAsync), "switch-output-active-device");
                }
            }
        }

        public async ValueTask<bool> SwitchInputAsync(bool reverse)
        {
            string? beforeDeviceName = TryGetCurrentDefaultDeviceName(output: false);
            Settings settings = SettingsService.LoadSettings();
            List<CycleDevice> configuredCycle =
            [
                .. settings.DeviceSwitching.Input.CycleDevices
                    .Where(device => device != null && !string.IsNullOrWhiteSpace(device.Id))
                    .Select(device => new CycleDevice { Id = device.Id, Name = device.Name })
            ];

            if (configuredCycle.Count == 0)
            {
                return false;
            }

            List<MMDevice> activeDevices = [];
            MMDevice? currentDevice = null;

            try
            {
                activeDevices = [.. AudioService.GetActiveCaptureDevices().Cast<MMDevice>()];
                var activeById = activeDevices.ToDictionary(d => d.ID, d => d, StringComparer.OrdinalIgnoreCase);
                List<CycleDevice> connectedCycle =
                [
                    .. configuredCycle
                        .Where(d => activeById.ContainsKey(d.Id))
                        .Select(d => new CycleDevice { Id = d.Id, Name = activeById[d.Id].FriendlyName })
                ];

                List<CycleDevice> skippedCycle =
                [
                    .. configuredCycle
                        .Where(d => !activeById.ContainsKey(d.Id))
                        .Select(d => new CycleDevice { Id = d.Id, Name = d.Name })
                ];

                if (connectedCycle.Count <= 1)
                {
                    BluetoothReconnectOptions reconnectOptions = BluetoothReconnectOptions.FromSettings(settings);
                    BluetoothReconnectAttemptResult reconnectResult = await BluetoothReconnectCoordinator.TryReconnectDetailedAsync(
                        configuredCycle,
                        new HashSet<string>(activeById.Keys, StringComparer.OrdinalIgnoreCase),
                        BluetoothReconnectDeviceKind.Input,
                        reconnectOptions,
                        opId: "cli");

                    if (reconnectResult.Connected)
                    {
                        RefreshActiveCaptureDevices(ref activeDevices, ref activeById, ref connectedCycle, configuredCycle);
                        skippedCycle =
                        [
                            .. configuredCycle
                                .Where(d => !activeById.ContainsKey(d.Id))
                                .Select(d => new CycleDevice { Id = d.Id, Name = d.Name })
                        ];
                    }
                    else if (reconnectResult.Attempted)
                    {
                        await DelayAsync(ResolveBluetoothPostAttemptRecheckDelayMs());
                        RefreshActiveCaptureDevices(ref activeDevices, ref activeById, ref connectedCycle, configuredCycle);
                        skippedCycle =
                        [
                            .. configuredCycle
                                .Where(d => !activeById.ContainsKey(d.Id))
                                .Select(d => new CycleDevice { Id = d.Id, Name = d.Name })
                        ];
                    }
                }

                if (connectedCycle.Count <= 1)
                {
                    return false;
                }

                currentDevice = AudioService.GetDefaultRecordingDevice();
                if (currentDevice == null)
                {
                    return false;
                }

                int currentIndex = connectedCycle.FindIndex(d => d.Id.Equals(currentDevice.ID, StringComparison.OrdinalIgnoreCase));
                int targetIndex = ResolveCycleTargetIndex(currentIndex, connectedCycle.Count, reverse);
                if (targetIndex < 0)
                {
                    return false;
                }

                var targetDevice = connectedCycle[targetIndex];
                var (switchSuccess, _) = await AudioService.SwitchInputDeviceToAsync(targetDevice.Id, targetDevice.Name, settings.Miscellaneous.PreserveAudioLevels, showOverlay: null, opId: "cli");
                string? afterDeviceName = TryGetCurrentDefaultDeviceName(output: false);
                RecordCliActionHistory(ExecutionHistoryKind.Switch, reverse ? "switch-input-reverse" : "switch-input", switchSuccess, skipped: false, switchSuccess ? $"Input switch completed{(string.IsNullOrWhiteSpace(afterDeviceName) ? string.Empty : $" to '{afterDeviceName}'")}." : "Input switch failed or was rejected.", switchSuccess ? null : "Input switch failed or was rejected.", target: beforeDeviceName, inputDeviceName: afterDeviceName);
                return switchSuccess;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(SwitchInputAsync), "cli-switch-input-failed", ex);
                RecordCliActionHistory(ExecutionHistoryKind.Switch, reverse ? "switch-input-reverse" : "switch-input", success: false, skipped: false, summary: "Input switch failed or was rejected.", reason: "Input switch threw an exception.", target: beforeDeviceName);
                return false;
            }
            finally
            {
                DisposeForCleanup(currentDevice, nameof(SwitchInputAsync), "switch-input-current-device");
                foreach (var device in activeDevices)
                {
                    DisposeForCleanup(device, nameof(SwitchInputAsync), "switch-input-active-device");
                }
            }
        }

        private List<CycleDevice> GetActiveOutputDeviceInfos()
        {
            if (_audioOverrides?.GetActiveOutputDeviceInfos != null)
            {
                return _audioOverrides.GetActiveOutputDeviceInfos();
            }

            return GetActiveDeviceInfos(AudioService.GetActivePlaybackDevices);
        }

        private List<CycleDevice> GetActiveInputDeviceInfos()
        {
            if (_audioOverrides?.GetActiveInputDeviceInfos != null)
            {
                return _audioOverrides.GetActiveInputDeviceInfos();
            }

            return GetActiveDeviceInfos(AudioService.GetActiveCaptureDevices);
        }

        private static List<CycleDevice> GetActiveDeviceInfos(Func<MMDeviceCollection> getDevices)
        {
            MMDeviceCollection? devices = null;
            try
            {
                devices = getDevices();
                return
                [
                    .. devices.Cast<MMDevice>()
                        .Select(d => new CycleDevice { Id = d.ID, Name = d.FriendlyName })
                ];
            }
            finally
            {
                if (devices != null)
                {
                    foreach (var device in devices.Cast<MMDevice>())
                    {
                        DisposeForCleanup(device, nameof(GetActiveDeviceInfos), "active-device-info-collection");
                    }
                }
            }
        }

        private bool SetMuteMicCore(bool enabled)
        {
            if (_audioOverrides?.TrySetMicrophoneMute != null)
            {
                return _audioOverrides.TrySetMicrophoneMute(enabled);
            }

            AudioService.SetMicrophoneMute(enabled);
            return true;
        }

        private bool SetMuteSoundCore(bool enabled)
        {
            if (_audioOverrides?.TrySetPlaybackMute != null)
            {
                return _audioOverrides.TrySetPlaybackMute(enabled);
            }

            AudioService.SetPlaybackMute(enabled);
            return true;
        }

        private static ExecutionHistoryKind? TryParseExecutionHistoryKind(string? type)
        {
            return type switch
            {
                null or "" => null,
                "routine" => ExecutionHistoryKind.Routine,
                "switch" => ExecutionHistoryKind.Switch,
                "media" => ExecutionHistoryKind.Media,
                "mute" => ExecutionHistoryKind.Mute,
                _ => null,
            };
        }

        private void RecordCliActionHistory(ExecutionHistoryKind kind, string action, bool success, bool skipped, string summary, string? reason = null, string? target = null, string? outputDeviceName = null, string? inputDeviceName = null, bool? enabled = null, string? diagCode = null, double? elapsedMs = null, IReadOnlyDictionary<string, string>? details = null)
        {
            _executionHistory.Record(new ExecutionHistoryEntry(
                OpId: $"cli-{action}:{Guid.NewGuid():N}",
                TimestampUtc: DateTimeOffset.UtcNow,
                Kind: kind,
                Source: "cli",
                Action: action,
                Success: success,
                Skipped: skipped,
                Summary: summary,
                Reason: reason,
                OutputDeviceName: outputDeviceName,
                InputDeviceName: inputDeviceName,
                Target: target,
                Enabled: enabled,
                DiagCode: diagCode,
                ElapsedMs: elapsedMs,
                Details: details));
        }

        private void RecordRoutineHistory(AudioRoutine routine, bool success, bool skipped, string? outputDeviceName, string? inputDeviceName, string? reason, bool? outputSucceeded, bool? inputSucceeded)
        {
            _executionHistory.Record(new ExecutionHistoryEntry(
                OpId: $"cli-routine-run:{Guid.NewGuid():N}",
                TimestampUtc: DateTimeOffset.UtcNow,
                Kind: ExecutionHistoryKind.Routine,
                Source: "cli",
                Action: "routine-run",
                Success: success,
                Skipped: skipped,
                Summary: skipped ? $"Routine '{routine.Name}' skipped." : success ? $"Routine '{routine.Name}' completed." : $"Routine '{routine.Name}' failed.",
                Reason: reason,
                RoutineId: routine.Id,
                RoutineName: routine.Name,
                OutputDeviceName: outputDeviceName,
                InputDeviceName: inputDeviceName,
                Target: routine.TargetSummary,
                OutputSucceeded: outputSucceeded,
                InputSucceeded: inputSucceeded,
                DiagCode: skipped ? "routine-run-skipped" : success ? "routine-run-success" : IsPartialRoutineResult(outputSucceeded, inputSucceeded) ? "routine-run-partial" : "routine-run-failed",
                Details: new Dictionary<string, string>
                {
                    ["trigger"] = routine.TriggerKind.ToString(),
                    ["executionSource"] = "cli",
                }));
        }

        private static bool IsPartialRoutineResult(bool? outputSucceeded, bool? inputSucceeded)
        {
            return (outputSucceeded == true && inputSucceeded == false)
                || (outputSucceeded == false && inputSucceeded == true);
        }

        private string? TryGetCurrentDefaultDeviceName(bool output)
        {
            try
            {
                if (output && _audioOverrides?.GetDefaultPlaybackDeviceSnapshot != null)
                {
                    return _audioOverrides.GetDefaultPlaybackDeviceSnapshot().DeviceName;
                }

                using var device = output ? AudioService.GetDefaultPlaybackDevice() : AudioService.GetDefaultRecordingDevice();
                return device?.FriendlyName;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(GetCurrentDeviceId), output ? "cli-get-current-output-device-failed" : "cli-get-current-input-device-failed", ex);
                return null;
            }
        }

        private void RefreshActivePlaybackDevices(
            ref List<MMDevice> activeDevices,
            ref Dictionary<string, MMDevice> activeById,
            ref List<CycleDevice> connectedCycle,
            IReadOnlyList<CycleDevice> configuredCycle)
        {
            DisposeDevices(activeDevices);
            activeDevices = [.. AudioService.GetActivePlaybackDevices().Cast<MMDevice>()];
            Dictionary<string, MMDevice> updatedActiveById = activeDevices.ToDictionary(d => d.ID, d => d, StringComparer.OrdinalIgnoreCase);
            activeById = updatedActiveById;
            connectedCycle =
            [
                .. configuredCycle
                    .Where(d => updatedActiveById.ContainsKey(d.Id))
                    .Select(d => new CycleDevice { Id = d.Id, Name = updatedActiveById[d.Id].FriendlyName })
            ];
        }

        private void RefreshActiveCaptureDevices(
            ref List<MMDevice> activeDevices,
            ref Dictionary<string, MMDevice> activeById,
            ref List<CycleDevice> connectedCycle,
            IReadOnlyList<CycleDevice> configuredCycle)
        {
            DisposeDevices(activeDevices);
            activeDevices = [.. AudioService.GetActiveCaptureDevices().Cast<MMDevice>()];
            Dictionary<string, MMDevice> updatedActiveById = activeDevices.ToDictionary(d => d.ID, d => d, StringComparer.OrdinalIgnoreCase);
            activeById = updatedActiveById;
            connectedCycle =
            [
                .. configuredCycle
                    .Where(d => updatedActiveById.ContainsKey(d.Id))
                    .Select(d => new CycleDevice { Id = d.Id, Name = updatedActiveById[d.Id].FriendlyName })
            ];
        }

        private static void DisposeDevices(IEnumerable<MMDevice> activeDevices)
        {
            foreach (MMDevice device in activeDevices)
            {
                DisposeForCleanup(device, nameof(DisposeDevices), "active-device-refresh");
            }
        }

        internal static bool DisposeForCleanup(IDisposable? disposable, string operation, string disposalTarget, ILogger? logger = null)
        {
            if (disposable == null)
            {
                return true;
            }

            try
            {
                disposable.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                ILogger cleanupLogger = logger ?? Logger.Instance;
                if (cleanupLogger.IsEnabled(LogLevel.Trace))
                {
                    cleanupLogger.Trace(
                        "LocalHeadlessCommandRunner",
                        () => $"headless-cleanup-dispose-ignored | target={disposalTarget} exceptionType={ex.GetType().Name}",
                        operation);
                }

                return false;
            }
        }

        private bool HasDefaultInputDevice()
        {
            try
            {
                if (_audioOverrides?.HasDefaultInputDevice != null)
                {
                    return _audioOverrides.HasDefaultInputDevice();
                }

                using var device = AudioService.GetDefaultRecordingDevice();
                return device != null;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(HasDefaultInputDevice), "cli-default-input-device-check-failed", ex);
                return false;
            }
        }

        private bool HasDefaultEndpoint(bool playback, string? deviceId)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    if (playback)
                    {
                        using var targetDevice = AudioService.TryGetPlaybackDeviceById(deviceId);
                        return targetDevice != null;
                    }

                    using var targetInputDevice = AudioService.TryGetCaptureDeviceById(deviceId);
                    return targetInputDevice != null;
                }

                if (playback)
                {
                    using var device = AudioService.GetDefaultPlaybackDevice();
                    return device != null;
                }

                if (_audioOverrides?.HasDefaultInputDevice != null)
                {
                    return _audioOverrides.HasDefaultInputDevice();
                }

                using var inputDevice = AudioService.GetDefaultRecordingDevice();
                return inputDevice != null;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(HasDefaultEndpoint), playback ? "cli-default-playback-endpoint-check-failed" : "cli-default-capture-endpoint-check-failed", ex);
                return false;
            }
        }

        private bool TryGetEndpointVolume(bool playback, string? deviceId, out float percent, out bool muted)
        {
            percent = 0f;
            muted = false;

            try
            {
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    if (playback && _audioOverrides?.GetPlaybackVolumeByDeviceId != null)
                    {
                        var (Success, Percent, Muted) = _audioOverrides.GetPlaybackVolumeByDeviceId(deviceId);
                        percent = Percent;
                        muted = Muted;
                        return Success;
                    }

                    if (!playback && _audioOverrides?.GetCaptureVolumeByDeviceId != null)
                    {
                        var (Success, Percent, Muted) = _audioOverrides.GetCaptureVolumeByDeviceId(deviceId);
                        percent = Percent;
                        muted = Muted;
                        return Success;
                    }

                    if (playback)
                    {
                        using var targetDevice = AudioService.TryGetPlaybackDeviceById(deviceId);
                        return AppCliOverlayCoordinator.TryGetEndpointVolumeState(Logger.Instance, targetDevice, "cli-volume-get:master:device", out percent, out muted);
                    }

                    using var targetInputDevice = AudioService.TryGetCaptureDeviceById(deviceId);
                    return AppCliOverlayCoordinator.TryGetEndpointVolumeState(Logger.Instance, targetInputDevice, "cli-volume-get:mic:device", out percent, out muted);
                }

                if (playback && _audioOverrides?.GetDefaultPlaybackVolume != null)
                {
                    var (Success, Percent, Muted) = _audioOverrides.GetDefaultPlaybackVolume();
                    percent = Percent;
                    muted = Muted;
                    return Success;
                }

                if (!playback && _audioOverrides?.GetDefaultCaptureVolume != null)
                {
                    var (Success, Percent, Muted) = _audioOverrides.GetDefaultCaptureVolume();
                    percent = Percent;
                    muted = Muted;
                    return Success;
                }

                if (playback)
                {
                    using var device = AudioService.GetDefaultPlaybackDevice();
                    return AppCliOverlayCoordinator.TryGetEndpointVolumeState(Logger.Instance, device, "cli-volume-get:master", out percent, out muted);
                }

                using var inputDevice = AudioService.GetDefaultRecordingDevice();
                return AppCliOverlayCoordinator.TryGetEndpointVolumeState(Logger.Instance, inputDevice, "cli-volume-get:mic", out percent, out muted);
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(TryGetEndpointVolume), playback ? "cli-volume-get-playback-failed" : "cli-volume-get-capture-failed", ex);
                return false;
            }
        }

        private bool TrySetEndpointVolume(bool playback, string? deviceId, float targetPercent, out float appliedPercent, out bool muted)
        {
            appliedPercent = 0f;
            muted = false;

            try
            {
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    if (playback && _audioOverrides?.TrySetPlaybackVolumeByDeviceId != null)
                    {
                        var (Success, Percent, Muted) = _audioOverrides.TrySetPlaybackVolumeByDeviceId(deviceId, targetPercent);
                        appliedPercent = Percent;
                        muted = Muted;
                        return Success;
                    }

                    if (!playback && _audioOverrides?.TrySetCaptureVolumeByDeviceId != null)
                    {
                        var (Success, Percent, Muted) = _audioOverrides.TrySetCaptureVolumeByDeviceId(deviceId, targetPercent);
                        appliedPercent = Percent;
                        muted = Muted;
                        return Success;
                    }

                    if (playback)
                    {
                        using var targetDevice = AudioService.TryGetPlaybackDeviceById(deviceId);
                        if (!AppCliOverlayCoordinator.TryApplyEndpointVolume(Logger.Instance, targetDevice, targetPercent, "cli-volume-set:master:device", muteAtZero: true, unmuteAboveZero: true, out appliedPercent))
                        {
                            return false;
                        }

                        muted = appliedPercent <= 0f;
                        return true;
                    }

                    using var targetInputDevice = AudioService.TryGetCaptureDeviceById(deviceId);
                    if (!AppCliOverlayCoordinator.TryApplyEndpointVolume(Logger.Instance, targetInputDevice, targetPercent, "cli-volume-set:mic:device", muteAtZero: true, unmuteAboveZero: true, out appliedPercent))
                    {
                        return false;
                    }

                    muted = appliedPercent <= 0f;
                    return true;
                }

                if (playback && _audioOverrides?.TrySetPlaybackVolume != null)
                {
                    var (Success, Percent, Muted) = _audioOverrides.TrySetPlaybackVolume(targetPercent);
                    appliedPercent = Percent;
                    muted = Muted;
                    return Success;
                }

                if (!playback && _audioOverrides?.TrySetCaptureVolume != null)
                {
                    var (Success, Percent, Muted) = _audioOverrides.TrySetCaptureVolume(targetPercent);
                    appliedPercent = Percent;
                    muted = Muted;
                    return Success;
                }

                if (playback)
                {
                    using var device = AudioService.GetDefaultPlaybackDevice();
                    if (!AppCliOverlayCoordinator.TryApplyEndpointVolume(Logger.Instance, device, targetPercent, "cli-volume-set:master", muteAtZero: true, unmuteAboveZero: true, out appliedPercent))
                    {
                        return false;
                    }

                    muted = appliedPercent <= 0f;
                    return true;
                }

                using var inputDevice = AudioService.GetDefaultRecordingDevice();
                if (!AppCliOverlayCoordinator.TryApplyEndpointVolume(Logger.Instance, inputDevice, targetPercent, "cli-volume-set:mic", muteAtZero: true, unmuteAboveZero: true, out appliedPercent))
                {
                    return false;
                }

                muted = appliedPercent <= 0f;
                return true;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(TrySetEndpointVolume), playback ? "cli-volume-set-playback-failed" : "cli-volume-set-capture-failed", ex);
                return false;
            }
        }

        private bool? TryGetDefaultPlaybackMute()
        {
            try
            {
                if (_audioOverrides?.GetDefaultPlaybackMute != null)
                {
                    return _audioOverrides.GetDefaultPlaybackMute();
                }

                using var device = AudioService.GetDefaultPlaybackDevice();
                return device?.AudioEndpointVolume.Mute;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(TryGetDefaultPlaybackMute), "cli-default-playback-mute-read-failed", ex);
                return null;
            }
        }

        private bool? TryGetDefaultCaptureMute()
        {
            try
            {
                if (_audioOverrides?.GetDefaultCaptureMute != null)
                {
                    return _audioOverrides.GetDefaultCaptureMute();
                }

                using var device = AudioService.GetDefaultRecordingDevice();
                return device?.AudioEndpointVolume.Mute;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(TryGetDefaultCaptureMute), "cli-default-capture-mute-read-failed", ex);
                return null;
            }
        }

        private static void LogAudioOperationFailure(string operation, string eventName, Exception ex)
        {
            Logger.Instance?.Debug(
                "LocalHeadlessCommandRunner",
                () => $"{eventName} | exceptionType={ex.GetType().Name}",
                operation);
        }

        private static int ResolveCycleTargetIndex(int currentIndex, int cycleCount, bool reverse)
        {
            if (cycleCount <= 0)
            {
                return -1;
            }

            if (currentIndex < 0)
            {
                return reverse ? cycleCount - 1 : 0;
            }

            return reverse
                ? (currentIndex - 1 + cycleCount) % cycleCount
                : (currentIndex + 1) % cycleCount;
        }
    }
}
