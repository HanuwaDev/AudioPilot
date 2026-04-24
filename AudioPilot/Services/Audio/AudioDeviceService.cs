using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NDeviceState = NAudio.CoreAudioApi.DeviceState;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Services.Audio
{
    internal interface IProcessAudioRouter
    {
        ProcessAudioRoutingResult TrySetProcessDevice(uint processId, DataFlow flow, string targetDeviceId, IReadOnlyList<NRole> roles);
        ProcessAudioRoutingResult TryClearProcessDevice(uint processId, DataFlow flow, IReadOnlyList<NRole> roles);
    }

    internal readonly record struct PerAppAudioRoutingResetResult(bool Success, bool HadAssignments);

    internal interface IPerAppAudioRoutingResetter
    {
        PerAppAudioRoutingResetResult TryResetAll();
    }

    internal readonly record struct ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult Result, string? DeviceName)
    {
        public bool Success => Result != ProcessAudioRoutingResult.Failed;
    }

    internal sealed class AudioPolicyProcessAudioRouter : IProcessAudioRouter
    {
        public ProcessAudioRoutingResult TrySetProcessDevice(uint processId, DataFlow flow, string targetDeviceId, IReadOnlyList<NRole> roles)
        {
            return AudioPolicyConfig.TrySetProcessDefaultDevice(processId, flow, roles, targetDeviceId);
        }

        public ProcessAudioRoutingResult TryClearProcessDevice(uint processId, DataFlow flow, IReadOnlyList<NRole> roles)
        {
            return AudioPolicyConfig.TryClearProcessDefaultDevice(processId, flow, roles);
        }
    }

    public partial class AudioDeviceService : IAudioDeviceEnumerator, IDisposable, IAsyncDisposable
    {
        internal static Action<bool>? SetMicrophoneMuteOverrideForTests { get; set; }
        internal static Action<bool>? SetPlaybackMuteOverrideForTests { get; set; }

        public event Action? DeviceStateChanged;
        public event Action<AudioMixerMode>? AudioSessionCreated;
        internal event Action<AudioSessionLifecycleSignal>? AudioSessionLifecycleChanged;

        private sealed class DeviceNotificationClient(
            Action<string, DataFlow, NRole> onDefaultDeviceChanged,
            Action onDeviceStateChanged,
            Func<bool> shouldIgnoreNotifications,
            Logger logger) : IMMNotificationClient
        {
            private readonly Action<string, DataFlow, NRole> _onDefaultDeviceChanged = onDefaultDeviceChanged;
            private readonly Action _onDeviceStateChanged = onDeviceStateChanged;
            private readonly Func<bool> _shouldIgnoreNotifications = shouldIgnoreNotifications;
            private readonly Logger _logger = logger;

            public void OnDefaultDeviceChanged(DataFlow flow, NRole role, string pwstrDefaultDeviceId)
            {
                if (_shouldIgnoreNotifications())
                {
                    return;
                }

                _logger.Trace("DeviceNotificationClient",
                    () => $"Default device changed: Flow={flow}, Role={role}");
                _onDefaultDeviceChanged?.Invoke(pwstrDefaultDeviceId, flow, role);
            }

            public void OnDeviceAdded(string pwstrDeviceId)
            {
                if (_shouldIgnoreNotifications())
                {
                    return;
                }

                _logger.Trace("DeviceNotificationClient", "Device added");
                _onDeviceStateChanged?.Invoke();
            }

            public void OnDeviceRemoved(string pwstrDeviceId)
            {
                if (_shouldIgnoreNotifications())
                {
                    return;
                }

                _logger.Trace("DeviceNotificationClient", "Device removed");
                _onDeviceStateChanged?.Invoke();
            }

            public void OnDeviceStateChanged(string pwstrDeviceId, NDeviceState dwNewState)
            {
                if (_shouldIgnoreNotifications())
                {
                    return;
                }

                if (dwNewState == NDeviceState.Active ||
                    dwNewState == NDeviceState.NotPresent ||
                    dwNewState == NDeviceState.Unplugged)
                {
                    _logger.Trace("DeviceNotificationClient",
                        () => $"Device state changed: State={dwNewState}");
                    _onDeviceStateChanged?.Invoke();
                }
            }

            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
            {
            }
        }

        private readonly MMDeviceEnumerator _enumerator;
        private readonly Logger _logger;
        private readonly Func<DeviceCacheHelper?> _deviceCacheAccessor;
        private readonly ReaderWriterLockSlim _enumeratorLock = new();
        private readonly DeviceNotificationClient _notificationClient;
        private readonly SwitchExecutionCoordinator _switchExecutionCoordinator = new();
        private readonly object _notificationRegistrationSync = new();
        private readonly TimeSpan _logCooldown = TimeSpan.FromMilliseconds(AppConstants.Timing.LogCooldownMs);
        private const int DeferredProcessOutputLogEvery = 10;
        private static readonly NRole[] DefaultOutputRoles =
        [
            NRole.Multimedia,
            NRole.Communications,
            NRole.Console
        ];
        private static readonly NRole[] DefaultInputRoles =
        [
            NRole.Console,
            NRole.Communications,
            NRole.Multimedia
        ];
        private readonly AudioRoleConfiguration _roleConfiguration = new(DefaultOutputRoles, DefaultInputRoles);
        private readonly AudioDeviceRoleConfigurationHelper _roleConfigurationHelper;
        private readonly ConcurrentDictionary<string, DateTime> _lastLogTime = new();
        private readonly ConcurrentDictionary<string, int> _appProcessAudioDeferredLogCounts = new(StringComparer.OrdinalIgnoreCase);

        private readonly AudioSessionService _sessionService;
        private readonly VolumeControlService _volumeService;
        private readonly AudioDeviceSessionProcessResolver _sessionProcessResolver;
        private readonly AudioDeviceSessionVolumeRestoreHelper _sessionVolumeRestoreHelper;
        private readonly AudioDeviceListenStateHelper _listenStateHelper;
        private readonly AudioDeviceEndpointQueryHelper _endpointQueryHelper;
        private readonly AudioDeviceSessionMonitoringCoordinatorFacade _sessionMonitoringFacade;
        private readonly AudioDeviceProcessRoutingHelper _processRoutingHelper;
        private readonly IInputListenPropertyWriter _inputListenPropertyWriter;
        private readonly IInputListenPropertyReader _inputListenPropertyReader;
        private readonly IInputListenAudioDeviceResolver _inputListenDeviceResolver;
        private readonly IProcessAudioRouter _processAudioRouter;
        private readonly IPerAppAudioRoutingResetter _perAppAudioRoutingResetter;

        private volatile bool _isRegistered;
        private volatile bool _disposed;

        internal ConcurrentDictionary<int, Task> BackgroundTasksForTests => _backgroundTasks;
        private int _disposeStarted;

        private readonly SessionMonitorCoordinator _playbackSessionMonitorCoordinator;
        private readonly SessionMonitorCoordinator _recordingSessionMonitorCoordinator;
        private readonly AudioDeviceBackgroundWorkHelper _backgroundWorkHelper;
        private readonly AudioDeviceNotificationRegistrationHelper _notificationRegistrationHelper;
        private readonly AudioDeviceResumeRecoveryHelper _resumeRecoveryHelper;
        private readonly AudioDeviceResumeRecoveryCoordinator _resumeRecoveryCoordinator;
        private readonly Action _outputSwitchCompletionSessionMonitoringUpdate;
        private readonly CancellationTokenSource _backgroundWorkCts = new();
        private readonly ConcurrentDictionary<int, Task> _backgroundTasks = new();
        private int _backgroundTaskId;
        private readonly DeviceStateMetricsTracker _deviceStateMetricsTracker = new();
        private readonly TaskCompletionSource<bool> _disposeStartedCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _disposeCleanupBarrierCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<Task>? _sessionMonitoringDrainOverride;

        public AudioDeviceService()
            : this(new InputListenPropertyWriter(Logger.Instance))
        {
        }

        internal AudioDeviceService(
            IInputListenPropertyWriter inputListenPropertyWriter,
            IInputListenPropertyReader? inputListenPropertyReader = null,
            IInputListenAudioDeviceResolver? inputListenDeviceResolver = null,
            IProcessAudioRouter? processAudioRouter = null,
            IPerAppAudioRoutingResetter? perAppAudioRoutingResetter = null,
            Func<IAudioDeviceEnumerator, AudioSessionService>? audioSessionServiceFactory = null,
            Action? outputSwitchCompletionSessionMonitoringUpdate = null,
            Func<Task>? sessionMonitoringDrainOverride = null,
            Logger? logger = null,
            Func<DeviceCacheHelper?>? deviceCacheAccessor = null)
        {
            _enumerator = new MMDeviceEnumerator();
            _logger = logger ?? Logger.Instance;
            _deviceCacheAccessor = deviceCacheAccessor ?? AudioSessionService.ResolveDeviceCacheOrNull;
            _endpointQueryHelper = new AudioDeviceEndpointQueryHelper(
                _enumerator,
                _enumeratorLock,
                _logger,
                () => _disposed,
                GetConfiguredOutputRolesSnapshot,
                GetConfiguredInputRolesSnapshot,
                LogDebugOnce);
            _inputListenPropertyWriter = inputListenPropertyWriter;
            _inputListenPropertyReader = inputListenPropertyReader ?? new InputListenPropertyReader(_logger);
            _inputListenDeviceResolver = inputListenDeviceResolver ?? new InputListenAudioDeviceResolver(
                GetDefaultRecordingDevice,
                GetDefaultPlaybackDevice,
                TryGetPlaybackDeviceById,
                GetActivePlaybackCycleEntries);
            _listenStateHelper = new AudioDeviceListenStateHelper(
                _logger,
                _inputListenPropertyWriter,
                _inputListenPropertyReader,
                _inputListenDeviceResolver);
            _processAudioRouter = processAudioRouter ?? new AudioPolicyProcessAudioRouter();
            _processRoutingHelper = new AudioDeviceProcessRoutingHelper(_logger, _processAudioRouter, DeferredProcessOutputLogEvery);
            _perAppAudioRoutingResetter = perAppAudioRoutingResetter ?? new RegistryPerAppAudioRoutingResetter(_logger);
            _notificationClient = new DeviceNotificationClient(
                OnDeviceSwitchNotification,
                OnDeviceStateChange,
                () => _disposed,
                _logger);
            _sessionMonitoringDrainOverride = sessionMonitoringDrainOverride;

            Func<IAudioDeviceEnumerator, AudioSessionService> resolvedAudioSessionServiceFactory =
                audioSessionServiceFactory ?? (enumerator => new AudioSessionService(enumerator, _deviceCacheAccessor, _logger));

            _sessionService = resolvedAudioSessionServiceFactory(this);
            _volumeService = new VolumeControlService(
                this,
                _sessionService.GetCachedProcessInfo,
                _sessionService.IsCacheEntryExpired);
            _sessionProcessResolver = new AudioDeviceSessionProcessResolver(
                _logger,
                _sessionService.GetCachedProcessInfo,
                _sessionService.IsCacheEntryExpired);
            _sessionVolumeRestoreHelper = new AudioDeviceSessionVolumeRestoreHelper(_sessionProcessResolver);
            _playbackSessionMonitorCoordinator = new SessionMonitorCoordinator(
                _logger,
                AudioMixerMode.Output,
                GetActivePlaybackMonitorEndpoints,
                OnSessionCreated,
                OnEndpointVolumeChanged,
                NotifyAudioSessionLifecycleChanged,
                RunBackgroundWork,
                () => _disposed);
            _recordingSessionMonitorCoordinator = new SessionMonitorCoordinator(
                _logger,
                AudioMixerMode.Input,
                GetActiveCaptureMonitorEndpoints,
                OnSessionCreated,
                OnEndpointVolumeChanged,
                NotifyAudioSessionLifecycleChanged,
                RunBackgroundWork,
                () => _disposed);
            _backgroundWorkHelper = new AudioDeviceBackgroundWorkHelper(_logger, () => _disposed);
            _sessionMonitoringFacade = new AudioDeviceSessionMonitoringCoordinatorFacade(
                _logger,
                _sessionService,
                _playbackSessionMonitorCoordinator,
                _recordingSessionMonitorCoordinator,
                () => _disposed,
                NotifyAudioSessionLifecycleChanged);
            _notificationRegistrationHelper = new AudioDeviceNotificationRegistrationHelper(
                _logger,
                _notificationRegistrationSync,
                () => _isRegistered,
                value => _isRegistered = value,
                () => _enumerator.RegisterEndpointNotificationCallback(_notificationClient),
                () => _enumerator.UnregisterEndpointNotificationCallback(_notificationClient),
                () => _sessionMonitoringFacade.Update(),
                () => _sessionMonitoringFacade.Stop());
            _resumeRecoveryHelper = new AudioDeviceResumeRecoveryHelper(
                _logger,
                () => _disposed,
                () => _isRegistered,
                RegisterNotificationClient,
                () => _sessionMonitoringFacade.Update());
            _resumeRecoveryCoordinator = new AudioDeviceResumeRecoveryCoordinator(
                _logger,
                () => _disposed);
            _outputSwitchCompletionSessionMonitoringUpdate = outputSwitchCompletionSessionMonitoringUpdate ?? _sessionMonitoringFacade.Update;
            _roleConfigurationHelper = new AudioDeviceRoleConfigurationHelper(_roleConfiguration, _logger);

            _logger.Info("AudioDeviceService", "Service initialized with delegated services");
        }

        internal static bool IsOutputSwitchDebounced(DateTime now, DateTime lastOutputSwitchTime)
        {
            return SwitchExecutionCoordinator.IsOutputSwitchDebounced(now, lastOutputSwitchTime);
        }

        internal static bool IsInputSwitchDebounced(DateTime now, DateTime lastInputSwitchTime)
        {
            return SwitchExecutionCoordinator.IsInputSwitchDebounced(now, lastInputSwitchTime);
        }

        /// <summary>
        /// Determines whether a captured session-volume snapshot should be persisted for post-switch restoration.
        /// </summary>
        /// <remarks>
        /// Snapshot registration is intentionally strict to avoid stale restore attempts when preservation is disabled
        /// or when no usable snapshot was captured.
        /// </remarks>
        internal static bool ShouldRegisterPreserveSnapshot(bool preserveAudioLevels, SessionVolumeSnapshot? snapshot)
        {
            return preserveAudioLevels && snapshot != null;
        }

        internal bool TryEnterOutputSwitchGateForTests() => _switchExecutionCoordinator.TryEnterOutputForTests();
        internal void ExitOutputSwitchGateForTests() => _switchExecutionCoordinator.ExitOutputForTests();
        internal bool TryEnterInputSwitchGateForTests() => _switchExecutionCoordinator.TryEnterInputForTests();
        internal void ExitInputSwitchGateForTests() => _switchExecutionCoordinator.ExitInputForTests();
        internal DateTime LastOutputSwitchTimeForTests => _switchExecutionCoordinator.LastOutputSwitchTime;
        internal DateTime LastInputSwitchTimeForTests => _switchExecutionCoordinator.LastInputSwitchTime;
        internal bool IsResumeRecoveryWaitingOnSemaphoreForTests => _resumeRecoveryCoordinator.IsWaitingOnSemaphoreForTests;
        internal int ActiveResumeRecoveryCountForTests => _resumeRecoveryCoordinator.ActiveRecoveryCountForTests;
        internal SemaphoreSlim ResumeRecoverySemaphoreForTests => _resumeRecoveryCoordinator.SemaphoreForTests;
        internal Task WaitForDisposeStartedForTestsAsync() => _disposeStartedCompletionSource.Task;
        internal Task WaitForDisposeCleanupBarrierForTestsAsync() => _disposeCleanupBarrierCompletionSource.Task;
        internal Task WaitForResumeRecoveryDrainedForTestsAsync() => _resumeRecoveryCoordinator.WaitForActiveResumeRecoveryAsync();
        internal void SetResumeRecoveryStateForTests(TaskCompletionSource<bool> completionSource, int activeCount) => _resumeRecoveryCoordinator.SetStateForTests(completionSource, activeCount);
        internal void CompleteOutputSwitchAttemptForTests(bool outputSwitchSucceeded) => CompleteOutputSwitchAttempt(outputSwitchSucceeded);
        internal void SetLastSwitchTimesForTests(DateTime? outputLast, DateTime? inputLast)
        {
            _switchExecutionCoordinator.SetLastSwitchTimes(outputLast, inputLast);
        }

        internal void RaiseAudioSessionCreatedForTests(AudioMixerMode mixerMode = AudioMixerMode.Output)
        {
            NotifyAudioSessionCreated(mixerMode);
        }

        internal void RaiseAudioSessionLifecycleChangedForTests(AudioSessionLifecycleSignal signal)
        {
            NotifyAudioSessionLifecycleChanged(signal);
        }

        internal void RaiseDeviceStateChangedForTests()
        {
            OnDeviceStateChange();
        }

        internal void RaiseDefaultPlaybackDeviceChangedForTests()
        {
            OnDeviceSwitchNotification("test-device", DataFlow.Render, NRole.Multimedia);
        }

        internal int GetSessionMonitoringConsumerCountForTests(AudioMixerMode mixerMode)
        {
            return _sessionMonitoringFacade.GetConsumerCountForTests(mixerMode);
        }

        internal void InvalidateRecentMixerSnapshotState()
        {
            _sessionService.InvalidateRecentMixerSnapshotState();
        }

        public void UpdateRoleConfiguration(IEnumerable<string>? outputRoles, IEnumerable<string>? inputRoles)
        {
            _ = _roleConfigurationHelper.UpdateConfiguration(
                outputRoles,
                inputRoles,
                DefaultOutputRoles,
                DefaultInputRoles);
        }

        internal static NRole[] NormalizeConfiguredRoles(IEnumerable<string>? configuredRoles, IReadOnlyList<NRole> fallback)
        {
            return AudioRoleConfiguration.NormalizeConfiguredRoles(configuredRoles, fallback);
        }

        private NRole[] GetConfiguredOutputRolesSnapshot()
        {
            return _roleConfigurationHelper.GetOutputRolesSnapshot();
        }

        private NRole[] GetConfiguredInputRolesSnapshot()
        {
            return _roleConfigurationHelper.GetInputRolesSnapshot();
        }

        internal static NRole ResolveDetectionRole(IReadOnlyList<NRole> configuredRoles, NRole fallback)
        {
            return AudioRoleConfiguration.ResolveDetectionRole(configuredRoles, fallback);
        }

        private static void ApplyConfiguredRoles(string targetDeviceId, IReadOnlyList<NRole> roles)
        {
            AudioRoleConfiguration.ApplyConfiguredRoles(targetDeviceId, roles);
        }

        private void LogDebugOnce(string key, Func<string> messageFactory)
        {
            var now = DateTime.UtcNow;
            if (_lastLogTime.TryGetValue(key, out var lastTime) &&
                (now - lastTime) < _logCooldown)
                return;

            _lastLogTime[key] = now;
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.Debug("AudioDeviceService", messageFactory);
        }

        private void LogDebugOnce(string key, string message)
        {
            LogDebugOnce(key, () => message);
        }

        internal static bool ShouldLogEveryNthOccurrence(int occurrence, int every)
        {
            return occurrence <= 1 || every <= 1 || occurrence % every == 0;
        }

        private bool ShouldLogDeferredProcessAudio(string scope, string op, out int occurrence)
        {
            string key = $"{scope}-deferred:{op}";
            occurrence = _appProcessAudioDeferredLogCounts.AddOrUpdate(key, 1, static (_, current) => current + 1);
            return ShouldLogEveryNthOccurrence(occurrence, DeferredProcessOutputLogEvery);
        }

        private void ResetDeferredProcessAudioLogCount(string scope, string op)
        {
            string key = $"{scope}-deferred:{op}";
            _appProcessAudioDeferredLogCounts.TryRemove(key, out _);
        }

        private void RunBackgroundWork(Func<CancellationToken, Task> operation, string operationName)
        {
            _ = _backgroundWorkHelper.TryQueue(
                _backgroundTasks,
                ref _backgroundTaskId,
                _backgroundWorkCts,
                operation,
                operationName);
        }

        public async Task RecoverAfterSystemResumeAsync()
        {
            await _resumeRecoveryCoordinator.RecoverAfterSystemResumeAsync(
                _deviceCacheAccessor,
                _sessionService.InvalidateRecentMixerSnapshotState,
                _switchExecutionCoordinator.ResetSwitchTimes,
                QueueBestEffortResumeRecoveryWork,
                _backgroundWorkCts.Token);
        }

        private bool QueueBestEffortResumeRecoveryWork()
        {
            return _resumeRecoveryHelper.TryQueueBestEffortRecovery(
                _backgroundTasks,
                ref _backgroundTaskId,
                _backgroundWorkCts);
        }

        public void ApplyMuteSettings(bool muteMic, bool muteSound, bool deafen)
        {
            _volumeService.ApplyMuteSettings(muteMic, muteSound, deafen);
        }

        public void SetMicrophoneMute(bool mute)
        {
            if (SetMicrophoneMuteOverrideForTests is Action<bool> overrideAction)
            {
                overrideAction(mute);
                return;
            }

            _volumeService.SetMicrophoneMute(mute);
        }

        public void SetPlaybackMute(bool mute)
        {
            if (SetPlaybackMuteOverrideForTests is Action<bool> overrideAction)
            {
                overrideAction(mute);
                return;
            }

            _volumeService.SetPlaybackMute(mute);
        }

        internal static void ResetTestHooks()
        {
            SetMicrophoneMuteOverrideForTests = null;
            SetPlaybackMuteOverrideForTests = null;
        }

        /// <summary>
        /// Reads the Windows "Listen to this device" state for the current default recording endpoint.
        /// </summary>
        /// <param name="enabled">Resolved listen state when read succeeds.</param>
        /// <param name="error">Stable error code when the read fails.</param>
        /// <returns><c>true</c> when the listen state was read; otherwise <c>false</c>.</returns>
        public bool TryGetCurrentInputListenState(out bool enabled, out string? error)
        {
            return _listenStateHelper.TryGetCurrentInputListenState(out enabled, out error);
        }

        /// <summary>
        /// Resolves the playback endpoint currently configured as monitor target for the default input listen setting.
        /// </summary>
        /// <param name="targetOutputDeviceName">Friendly output-device name when available; otherwise <c>null</c>.</param>
        /// <param name="error">Stable error code when read/resolve fails.</param>
        /// <returns><c>true</c> when read succeeds (even if no target is configured); otherwise <c>false</c>.</returns>
        public bool TryGetCurrentInputListenTargetOutputDeviceName(out string? targetOutputDeviceName, out string? error)
        {
            return _listenStateHelper.TryGetCurrentInputListenTargetOutputDeviceName(out targetOutputDeviceName, out error);
        }

        /// <summary>
        /// Writes the Windows "Listen to this device" state for the current default recording endpoint.
        /// </summary>
        /// <param name="enabled">Target listen state.</param>
        /// <param name="changed">
        /// <c>true</c> when post-write verification confirms the requested state; otherwise <c>false</c>.
        /// </param>
        /// <param name="error">Stable error code when the write fails.</param>
        /// <returns><c>true</c> when the write path completed; otherwise <c>false</c>.</returns>
        public bool TrySetCurrentInputListenState(bool enabled, out bool changed, out string? error)
        {
            return TrySetCurrentInputListenState(enabled, string.Empty, out changed, out error);
        }

        public bool TrySetCurrentInputListenState(bool enabled, string? preferredRenderDeviceId, out bool changed, out string? error)
        {
            return _listenStateHelper.TrySetCurrentInputListenState(enabled, preferredRenderDeviceId, out changed, out error);
        }

        public bool TrySetCurrentInputListenState(bool enabled, string? preferredRenderDeviceId, string? preferredRenderDeviceName, out bool changed, out string? error)
        {
            return _listenStateHelper.TrySetCurrentInputListenState(enabled, preferredRenderDeviceId, preferredRenderDeviceName, out changed, out error);
        }

        /// <summary>
        /// Toggles the Windows "Listen to this device" state for the current default recording endpoint.
        /// </summary>
        /// <param name="enabled">Resulting listen state when toggle succeeds.</param>
        /// <param name="error">Stable error code when toggle fails.</param>
        /// <returns><c>true</c> when toggle and verification succeed; otherwise <c>false</c>.</returns>
        public bool TryToggleCurrentInputListenState(out bool enabled, out string? error)
        {
            return TryToggleCurrentInputListenState(string.Empty, out enabled, out error);
        }

        public bool TryToggleCurrentInputListenState(string? preferredRenderDeviceId, out bool enabled, out string? error)
        {
            return _listenStateHelper.TryToggleCurrentInputListenState(preferredRenderDeviceId, out enabled, out error);
        }

        public bool TryToggleCurrentInputListenState(string? preferredRenderDeviceId, string? preferredRenderDeviceName, out bool enabled, out string? error)
        {
            return _listenStateHelper.TryToggleCurrentInputListenState(preferredRenderDeviceId, preferredRenderDeviceName, out enabled, out error);
        }

        public MMDevice? TryGetPlaybackDeviceById(string deviceId)
        {
            return _endpointQueryHelper.TryGetDeviceById(deviceId);
        }

        public MMDevice? TryGetCaptureDeviceById(string deviceId)
        {
            return _endpointQueryHelper.TryGetDeviceById(deviceId);
        }

        internal static Dictionary<string, MMDevice> BuildDeviceLookup(IEnumerable<MMDevice> devices)
        {
            var lookup = new Dictionary<string, MMDevice>(StringComparer.OrdinalIgnoreCase);

            foreach (MMDevice device in devices)
            {
                if (device == null || string.IsNullOrWhiteSpace(device.ID))
                {
                    continue;
                }

                lookup[device.ID] = device;
            }

            return lookup;
        }

        /// <summary>
        /// Switches the default output endpoint between two configured devices with optional mute/deafen and
        /// session-volume preservation behavior.
        /// </summary>
        /// <remarks>
        /// The method uses debounce and semaphore gating to prevent switch storms, verifies role application, and
        /// performs post-switch operations (mute/deafen + optional volume restore) asynchronously.
        /// </remarks>
        public async ValueTask<(bool Success, string? DeviceName)> SwitchAudioDeviceAsync(
            string device1Id,
            string device2Id,
            bool muteMic,
            bool muteSound,
            bool deafen,
            bool preserveAudioLevels,
            bool restoreMasterVolume = true,
            bool restoreMicVolume = true,
            string? opId = null)
        {
            string op = string.IsNullOrWhiteSpace(opId) ? "none" : opId;
            Stopwatch switchStopwatch = Stopwatch.StartNew();

            if (_disposed)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.Warning("AudioDeviceService",
                        () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Failed} | opId={op} reason=service-disposed");
                return (false, null);
            }

            if (_sessionService != null)
                _ = _sessionService.StartCleanupTaskAsync();

            if (string.IsNullOrEmpty(device1Id) || string.IsNullOrEmpty(device2Id))
                throw new InvalidOperationException("Both devices must be configured");

            if (!await _switchExecutionCoordinator.TryEnterOutputAsync())
            {
                _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Skip} | opId={op} reason=in-progress");
                return (false, null);
            }

            bool outputSwitchSucceeded = false;

            try
            {
                var outputRoles = GetConfiguredOutputRolesSnapshot();
                var inputRoles = GetConfiguredInputRolesSnapshot();
                var outputDetectionRole = ResolveDetectionRole(outputRoles, NRole.Multimedia);
                var inputDetectionRole = ResolveDetectionRole(inputRoles, NRole.Console);
                bool snapshotDeferredToBackground = false;

                Task<SessionVolumeSnapshot>? snapshotTask = null;
                string targetDeviceId;
                string targetDeviceName;

                _enumeratorLock.EnterReadLock();
                try
                {
                    using MMDevice? targetDevice = _enumerator.GetDevice(device2Id);

                    if (targetDevice == null)
                    {
                        _logger.Info("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Skip} | opId={op} reason=target-unavailable");
                        return (false, null);
                    }

                    if (targetDevice.State != NDeviceState.Active)
                    {
                        _logger.Info("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Skip} | opId={op} reason=target-inactive state={targetDevice.State}");
                        return (false, null);
                    }

                    targetDeviceId = targetDevice.ID;
                    targetDeviceName = targetDevice.FriendlyName;
                }
                finally
                {
                    _enumeratorLock.ExitReadLock();
                }

                if (preserveAudioLevels)
                {
                    NRole snapshotOutputRole = outputDetectionRole;
                    NRole snapshotInputRole = inputDetectionRole;
                    snapshotTask = Task.Run(() =>
                    {
                        ComThreadingHelper.ThrowIfComInitializationFailed(nameof(SwitchAudioDeviceAsync));
                        return _volumeService.CaptureSessionVolumesWithLocalEnumerator(snapshotOutputRole, snapshotInputRole, includeRecordingVolume: false);
                    });
                }

                if (_logger.IsEnabled(LogLevel.Info))
                    _logger.Info("AudioDeviceService",
                        () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Start} | opId={op} muteMic={muteMic} muteSound={muteSound} deafen={deafen} preserveAudioLevels={preserveAudioLevels}");

                if (string.IsNullOrEmpty(targetDeviceId))
                {
                    if (_logger.IsEnabled(LogLevel.Error))
                        _logger.Error("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Failed} | opId={op} reason=target-id-empty");
                    return (false, null);
                }

                SessionVolumeSnapshot? snapshot = null;
                if (snapshotTask is { IsCompletedSuccessfully: true })
                {
                    snapshot = snapshotTask.Result;
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        int sessionCount = snapshot.ByPid.Count + snapshot.ByName.Count;
                        _logger.Debug("AudioDeviceService",
                            () => $"{AppConstants.Audio.LogEvents.OutputSwitch.SnapshotCaptured} | opId={op} sessionCount={sessionCount}");
                    }
                }

                double preRoleSwitchMs = switchStopwatch.Elapsed.TotalMilliseconds;
                bool snapshotReadyBeforeSwitch = snapshotTask?.IsCompletedSuccessfully ?? false;
                double roleSwitchStartMs = switchStopwatch.Elapsed.TotalMilliseconds;

                bool switched = await DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(
                    targetDeviceId,
                    outputRoles,
                    ApplyConfiguredRoles,
                    GetDefaultPlaybackDevice,
                    _logger,
                    op,
                    nameof(SwitchAudioDeviceAsync),
                    _backgroundWorkCts.Token);

                double roleSwitchDurationMs = switchStopwatch.Elapsed.TotalMilliseconds - roleSwitchStartMs;
                double setupDurationMs = preRoleSwitchMs;

                if (!switched)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.Debug(
                            "AudioDeviceService",
                            () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Phases} | opId={op} snapshotReadyBeforeSwitch={snapshotReadyBeforeSwitch} setupMs={setupDurationMs:F1} roleSwitchMs={roleSwitchDurationMs:F1} finalizeMs=0.0 totalMs={switchStopwatch.Elapsed.TotalMilliseconds:F1} result=verify-failed");
                    }

                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Failed} | opId={op} reason=verify-failed-after-retries");
                    return (false, null);
                }

                if (_logger.IsEnabled(LogLevel.Info))
                    _logger.Info("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Confirmed} | opId={op}");

                var capturedSnapshot = snapshot;
                var capturedSnapshotTask = snapshotTask;
                var capturedTargetDeviceId = targetDeviceId;
                var capturedPreserveAudioLevels = preserveAudioLevels;
                if (ShouldRegisterPreserveSnapshot(capturedPreserveAudioLevels, capturedSnapshot))
                {
                    _volumeService.RegisterPostSwitchSnapshot(capturedSnapshot!, capturedTargetDeviceId);
                }
                var capturedMuteMic = muteMic;
                var capturedMuteSound = muteSound;
                var capturedDeafen = deafen;
                var capturedInputDetectionRole = inputDetectionRole;

                RunBackgroundWork(async shutdownToken =>
                {
                    try
                    {
                        SessionVolumeSnapshot? snapshotForPost = capturedSnapshot;
                        bool preserveForPost = capturedPreserveAudioLevels;

                        if (preserveForPost && snapshotForPost == null && capturedSnapshotTask != null)
                        {
                            snapshotForPost = await capturedSnapshotTask;
                            _volumeService.RegisterPostSwitchSnapshot(snapshotForPost, capturedTargetDeviceId);
                        }

                        await PostSwitchCoordinator.ExecuteAsync(
                            () => _disposed,
                            _logger,
                            _volumeService,
                            op,
                            capturedTargetDeviceId,
                            capturedInputDetectionRole,
                            capturedMuteMic,
                            capturedMuteSound,
                            capturedDeafen,
                            preserveForPost,
                            restoreMasterVolume,
                            restoreMicVolume,
                            snapshotForPost,
                            shutdownToken);
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsEnabled(LogLevel.Warning))
                            _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.PostFailed} | opId={op}", nameof(SwitchAudioDeviceAsync), ex);
                    }
                }, nameof(SwitchAudioDeviceAsync));

                snapshotDeferredToBackground = preserveAudioLevels && capturedSnapshot == null && capturedSnapshotTask != null;
                double finalizeDurationMs = switchStopwatch.Elapsed.TotalMilliseconds - (preRoleSwitchMs + roleSwitchDurationMs);

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug(
                        "AudioDeviceService",
                        () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Phases} | opId={op} snapshotReadyBeforeSwitch={snapshotReadyBeforeSwitch} setupMs={setupDurationMs:F1} roleSwitchMs={roleSwitchDurationMs:F1} finalizeMs={finalizeDurationMs:F1} totalMs={switchStopwatch.Elapsed.TotalMilliseconds:F1} result=success snapshotDeferred={snapshotDeferredToBackground}");
                }

                if (_logger.IsEnabled(LogLevel.Info))
                    _logger.Info("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Success} | opId={op} target={LogPrivacy.Device(targetDeviceName)} durationMs={switchStopwatch.Elapsed.TotalMilliseconds:F1} preserveAudioLevels={preserveAudioLevels} snapshotDeferred={snapshotDeferredToBackground}");
                outputSwitchSucceeded = true;
                return (true, targetDeviceName);
            }
            catch (OperationCanceledException) when (_disposed || _backgroundWorkCts.IsCancellationRequested)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Skip} | opId={op} reason=shutdown-canceled");
                }

                return (false, null);
            }
            catch (COMException ex)
            {
                AudioDeviceHelper.LogComException(_logger, nameof(SwitchAudioDeviceAsync), ex);
                return (false, null);
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, nameof(SwitchAudioDeviceAsync), ex);
                return (false, null);
            }
            finally
            {
                CompleteOutputSwitchAttempt(outputSwitchSucceeded);
            }
        }

        private void CompleteOutputSwitchAttempt(bool outputSwitchSucceeded)
        {
            _switchExecutionCoordinator.ReleaseOutput();
            if (outputSwitchSucceeded)
            {
                _switchExecutionCoordinator.MarkOutputSwitchSuccess(DateTime.Now);
            }

            QueueOutputSwitchCompletionSessionMonitoringUpdate();
        }

        private void QueueOutputSwitchCompletionSessionMonitoringUpdate()
        {
            RunBackgroundWork(
                _ =>
                {
                    _outputSwitchCompletionSessionMonitoringUpdate();
                    return Task.CompletedTask;
                },
                nameof(UpdateSessionMonitoring));
        }

        internal ValueTask<ProcessAudioDeviceSwitchResult> SwitchApplicationOutputDeviceDetailedAsync(
            uint processId,
            string targetDeviceId,
            string targetDeviceName,
            string? opId = null)
        {
            _ = targetDeviceName;

            return SwitchApplicationDeviceDetailedAsync(
                processId,
                targetDeviceId,
                DataFlow.Render,
                TryGetPlaybackDeviceById,
                GetConfiguredOutputRolesSnapshot,
                "app-process-output",
                nameof(SwitchApplicationOutputDeviceAsync),
                opId);
        }

        public async ValueTask<(bool Success, string? DeviceName)> SwitchApplicationOutputDeviceAsync(
            uint processId,
            string targetDeviceId,
            string targetDeviceName,
            string? opId = null)
        {
            ProcessAudioDeviceSwitchResult result = await SwitchApplicationOutputDeviceDetailedAsync(processId, targetDeviceId, targetDeviceName, opId);
            return (result.Success, result.DeviceName);
        }

        internal ValueTask<ProcessAudioDeviceSwitchResult> SwitchApplicationInputDeviceDetailedAsync(
            uint processId,
            string targetDeviceId,
            string targetDeviceName,
            string? opId = null)
        {
            _ = targetDeviceName;

            return SwitchApplicationDeviceDetailedAsync(
                processId,
                targetDeviceId,
                DataFlow.Capture,
                TryGetCaptureDeviceById,
                GetConfiguredInputRolesSnapshot,
                "app-process-input",
                nameof(SwitchApplicationInputDeviceAsync),
                opId);
        }

        public async ValueTask<(bool Success, string? DeviceName)> SwitchApplicationInputDeviceAsync(
            uint processId,
            string targetDeviceId,
            string targetDeviceName,
            string? opId = null)
        {
            ProcessAudioDeviceSwitchResult result = await SwitchApplicationInputDeviceDetailedAsync(processId, targetDeviceId, targetDeviceName, opId);
            return (result.Success, result.DeviceName);
        }

        private ValueTask<ProcessAudioDeviceSwitchResult> SwitchApplicationDeviceDetailedAsync(
            uint processId,
            string targetDeviceId,
            DataFlow flow,
            Func<string, MMDevice?> resolveTargetDevice,
            Func<NRole[]> getRoles,
            string logScope,
            string operationName,
            string? opId)
        {
            string op = string.IsNullOrWhiteSpace(opId) ? "none" : opId;

            if (_disposed || processId == 0 || string.IsNullOrWhiteSpace(targetDeviceId))
            {
                return ValueTask.FromResult(new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.Failed, null));
            }

            MMDevice? targetDevice = null;
            try
            {
                targetDevice = resolveTargetDevice(targetDeviceId);
                if (targetDevice == null || targetDevice.State != NDeviceState.Active)
                {
                    _logger.Info("AudioDeviceService", () => $"{logScope}-skip | opId={op} reason=target-not-active targetId={LogPrivacy.Id(targetDeviceId)}");
                    return ValueTask.FromResult(new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.Failed, null));
                }

                ProcessAudioDeviceSwitchResult result = _processRoutingHelper.ApplyProcessDeviceRouting(
                    processId,
                    flow,
                    targetDevice.ID,
                    targetDevice.FriendlyName,
                    getRoles,
                    logScope,
                    operationName,
                    op,
                    (scope, currentOp) => ShouldLogDeferredProcessAudio(scope, currentOp, out int occurrence) ? occurrence : null,
                    ResetDeferredProcessAudioLogCount);

                return ValueTask.FromResult(result);
            }
            catch (Exception ex)
            {
                _logger.Error("AudioDeviceService", () => $"{logScope}-failed | opId={op}", operationName, ex);
                return ValueTask.FromResult(new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.Failed, null));
            }
            finally
            {
                targetDevice?.Dispose();
            }
        }

        internal bool TryResetApplicationDeviceRouting(uint processId, bool resetOutput, bool resetInput, string? opId = null)
        {
            string op = string.IsNullOrWhiteSpace(opId) ? "none" : opId;
            if (_disposed || processId == 0 || (!resetOutput && !resetInput))
            {
                return false;
            }

            bool success = true;

            if (resetOutput)
            {
                success &= TryResetApplicationDeviceRoutingFlow(processId, DataFlow.Render, GetConfiguredOutputRolesSnapshot(), "app-process-output-reset", op);
            }

            if (resetInput)
            {
                success &= TryResetApplicationDeviceRoutingFlow(processId, DataFlow.Capture, GetConfiguredInputRolesSnapshot(), "app-process-input-reset", op);
            }

            return success;
        }

        internal PerAppAudioRoutingResetResult ResetAllPerAppAudioRouting()
        {
            return _perAppAudioRoutingResetter.TryResetAll();
        }

        private bool TryResetApplicationDeviceRoutingFlow(uint processId, DataFlow flow, IReadOnlyList<NRole> roles, string logScope, string op)
        {
            return _processRoutingHelper.TryResetProcessDeviceRouting(processId, flow, roles, logScope, op, nameof(TryResetApplicationDeviceRouting));
        }

        public async ValueTask<(bool Success, string? DeviceName)> SwitchInputDeviceAsync(
            string device1Id,
            string device1Name,
            string device2Id,
            string device2Name,
            bool preserveAudioLevels,
            Action<OverlayDeviceKind, string, string>? showOverlay,
            string? opId = null)
        {
            string op = string.IsNullOrWhiteSpace(opId) ? "none" : opId;

            if (_disposed)
            {
                _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op} reason=service-disposed");
                return (false, null);
            }

            if (!await _switchExecutionCoordinator.TryEnterInputAsync())
            {
                _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Skip} | opId={op} reason=in-progress");
                return (false, null);
            }

            try
            {
                if (string.IsNullOrEmpty(device1Id) || string.IsNullOrEmpty(device2Id))
                {
                    _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op} reason=not-configured device1={LogPrivacy.Device(device1Name)} id1={LogPrivacy.Id(device1Id)} device2={LogPrivacy.Device(device2Name)} id2={LogPrivacy.Id(device2Id)}");
                    return (false, null);
                }

                List<MMDevice> captureDeviceList = [];
                MMDevice? currentDefault = null;

                Task<SessionVolumeSnapshot>? snapshotTask = null;
                if (preserveAudioLevels)
                {
                    snapshotTask = Task.Run(() =>
                    {
                        ComThreadingHelper.ThrowIfComInitializationFailed(nameof(SwitchInputDeviceAsync));
                        return _volumeService.CaptureSessionVolumesWithLocalEnumerator(NRole.Multimedia, NRole.Console, includeRecordingVolume: true);
                    });
                }

                try
                {
                    captureDeviceList = AudioDeviceCollectionHelper.MaterializeDevices(GetActiveCaptureDevices());
                    Dictionary<string, MMDevice> captureDeviceLookup = BuildDeviceLookup(captureDeviceList);

                    captureDeviceLookup.TryGetValue(device1Id, out MMDevice? device1);
                    captureDeviceLookup.TryGetValue(device2Id, out MMDevice? device2);

                    if (device1 == null || device2 == null)
                    {
                        string missing = device1 == null && device2 == null ? "both input devices" :
                                         device1 == null ? device1Name : device2Name;
                        _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op} reason=target-missing missing={LogPrivacy.Device(missing)}");
                        showOverlay?.Invoke(OverlayDeviceKind.Error, "Failed to switch input device", missing);
                        return (false, null);
                    }

                    currentDefault = GetDefaultRecordingDevice();
                    if (currentDefault == null)
                    {
                        _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op} reason=no-default-recording-device");
                        return (false, null);
                    }

                    var targetDevice = currentDefault.ID == device1.ID ? device2 : device1;
                    string targetId = targetDevice.ID;
                    string targetName = targetDevice.FriendlyName;

                    currentDefault.Dispose();
                    currentDefault = null;

                    var inputRoles = GetConfiguredInputRolesSnapshot();
                    bool success = await DeviceRoleSwitchEngine.TrySwitchInputRolesAsync(
                        targetId,
                        targetName,
                        inputRoles,
                        ApplyConfiguredRoles,
                        GetDefaultRecordingDevice,
                        _logger,
                        op,
                        nameof(SwitchInputDeviceAsync),
                        emitVerifyRetryWarning: true,
                        traceComRetry: false,
                        _backgroundWorkCts.Token);

                    if (success)
                    {
                        SessionVolumeSnapshot? snapshot = null;
                        if (snapshotTask is { IsCompletedSuccessfully: true })
                        {
                            snapshot = snapshotTask.Result;
                        }

                        var capturedSnapshot = snapshot;
                        var capturedTargetDeviceId = targetId;
                        var capturedPreserveAudioLevels = preserveAudioLevels;
                        if (ShouldRegisterPreserveSnapshot(capturedPreserveAudioLevels, capturedSnapshot))
                        {
                            _volumeService.RegisterPostSwitchSnapshot(capturedSnapshot!, capturedTargetDeviceId);
                        }

                        RunBackgroundWork(async shutdownToken =>
                        {
                            try
                            {
                                SessionVolumeSnapshot? snapshotForRestore = capturedSnapshot;
                                if (capturedPreserveAudioLevels && snapshotForRestore == null && snapshotTask != null)
                                {
                                    snapshotForRestore = await snapshotTask;
                                }

                                if (snapshotForRestore?.MicVolumePercent.HasValue == true)
                                {
                                    await _volumeService.ApplySessionVolumesSimpleAsync(
                                        snapshotForRestore,
                                        applyMasterVolume: false,
                                        applyMicVolume: true);
                                }
                            }
                            catch (Exception ex)
                            {
                                if (_logger.IsEnabled(LogLevel.Warning))
                                    _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.PostFailed} | opId={op}", nameof(SwitchInputDeviceAsync), ex);
                            }
                        }, nameof(SwitchInputDeviceAsync));

                        _logger.Info("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Success} | opId={op} target={LogPrivacy.Device(targetName)} preserveAudioLevels={preserveAudioLevels}");
                        showOverlay?.Invoke(OverlayDeviceKind.Input, "Switched input device", targetName);
                        _switchExecutionCoordinator.MarkInputSwitchSuccess(DateTime.Now);
                    }

                    if (!success)
                    {
                        _logger.Error("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op} reason=verify-failed-after-retries attempts={RuntimeTuningConfig.SwitchMaxRetries}");
                        showOverlay?.Invoke(OverlayDeviceKind.Error, "Input switch failed", "");
                    }

                    return (success, success ? targetName : null);
                }
                finally
                {
                    currentDefault?.Dispose();
                    foreach (var device in captureDeviceList)
                    {
                        try { device.Dispose(); }
                        catch (Exception disposeEx)
                        {
                            if (_logger.IsEnabled(LogLevel.Trace))
                            {
                                _logger.Trace("AudioDeviceService", () => $"Ignored dispose exception for capture device {LogPrivacy.Device(device?.FriendlyName)} ({LogPrivacy.Id(device?.ID)}): {disposeEx.GetType().Name}");
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_disposed || _backgroundWorkCts.IsCancellationRequested)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Skip} | opId={op} reason=shutdown-canceled");
                }

                return (false, null);
            }
            catch (Exception ex)
            {
                _logger.Error("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op}", nameof(SwitchInputDeviceAsync), ex);
                showOverlay?.Invoke(OverlayDeviceKind.Error, "Input switch failed", "");
                return (false, null);
            }
            finally
            {
                _switchExecutionCoordinator.ReleaseInput();
            }
        }

        public async ValueTask<(bool Success, string? DeviceName)> SwitchInputDeviceToAsync(
            string targetDeviceId,
            string targetDeviceName,
            bool preserveAudioLevels,
            Action<OverlayDeviceKind, string, string>? showOverlay,
            string? opId = null)
        {
            string op = string.IsNullOrWhiteSpace(opId) ? "none" : opId;

            if (_disposed)
            {
                _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op} reason=service-disposed");
                return (false, null);
            }

            if (!await _switchExecutionCoordinator.TryEnterInputAsync())
            {
                _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Skip} | opId={op} reason=in-progress");
                return (false, null);
            }

            try
            {
                if (string.IsNullOrEmpty(targetDeviceId))
                {
                    _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op} reason=target-empty");
                    return (false, null);
                }

                Task<SessionVolumeSnapshot>? snapshotTask = null;
                if (preserveAudioLevels)
                {
                    snapshotTask = Task.Run(() =>
                    {
                        ComThreadingHelper.ThrowIfComInitializationFailed(nameof(SwitchInputDeviceToAsync));
                        return _volumeService.CaptureSessionVolumesWithLocalEnumerator(NRole.Multimedia, NRole.Console, includeRecordingVolume: true);
                    });
                }

                MMDevice? currentDefault = null;
                List<MMDevice> captureDeviceList = [];

                try
                {
                    captureDeviceList = AudioDeviceCollectionHelper.MaterializeDevices(GetActiveCaptureDevices());
                    Dictionary<string, MMDevice> captureDeviceLookup = BuildDeviceLookup(captureDeviceList);

                    captureDeviceLookup.TryGetValue(targetDeviceId, out MMDevice? targetDevice);

                    if (targetDevice == null)
                    {
                        _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op} reason=target-not-active targetId={LogPrivacy.Id(targetDeviceId)}");
                        showOverlay?.Invoke(OverlayDeviceKind.Error, "Failed to switch input device", targetDeviceName);
                        return (false, null);
                    }

                    currentDefault = GetDefaultRecordingDevice();
                    if (currentDefault != null && currentDefault.ID == targetDevice.ID)
                    {
                        _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Skip} | opId={op} reason=already-target target={LogPrivacy.Device(targetDevice.FriendlyName)}");
                        showOverlay?.Invoke(OverlayDeviceKind.Input, "Switched input device", targetDevice.FriendlyName);
                        _switchExecutionCoordinator.MarkInputSwitchSuccess(DateTime.Now);
                        return (true, targetDevice.FriendlyName);
                    }

                    string targetName = targetDevice.FriendlyName;
                    var inputRoles = GetConfiguredInputRolesSnapshot();
                    bool success = await DeviceRoleSwitchEngine.TrySwitchInputRolesAsync(
                        targetDeviceId,
                        targetName,
                        inputRoles,
                        ApplyConfiguredRoles,
                        GetDefaultRecordingDevice,
                        _logger,
                        op,
                        nameof(SwitchInputDeviceToAsync),
                        emitVerifyRetryWarning: false,
                        traceComRetry: true,
                        _backgroundWorkCts.Token);

                    if (success)
                    {
                        SessionVolumeSnapshot? snapshot = null;
                        if (snapshotTask is { IsCompletedSuccessfully: true })
                        {
                            snapshot = snapshotTask.Result;
                        }

                        var capturedSnapshot = snapshot;
                        var capturedTargetDeviceId = targetDeviceId;
                        var capturedPreserveAudioLevels = preserveAudioLevels;
                        if (ShouldRegisterPreserveSnapshot(capturedPreserveAudioLevels, capturedSnapshot))
                        {
                            _volumeService.RegisterPostSwitchSnapshot(capturedSnapshot!, capturedTargetDeviceId);
                        }

                        RunBackgroundWork(async shutdownToken =>
                        {
                            try
                            {
                                SessionVolumeSnapshot? snapshotForRestore = capturedSnapshot;
                                if (capturedPreserveAudioLevels && snapshotForRestore == null && snapshotTask != null)
                                {
                                    snapshotForRestore = await snapshotTask;
                                }

                                if (snapshotForRestore?.MicVolumePercent.HasValue == true)
                                {
                                    await _volumeService.ApplySessionVolumesSimpleAsync(
                                        snapshotForRestore,
                                        applyMasterVolume: false,
                                        applyMicVolume: true);
                                }
                            }
                            catch (Exception ex)
                            {
                                if (_logger.IsEnabled(LogLevel.Warning))
                                    _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.PostFailed} | opId={op}", nameof(SwitchInputDeviceToAsync), ex);
                            }
                        }, nameof(SwitchInputDeviceToAsync));

                        _logger.Info("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Success} | opId={op} target={LogPrivacy.Device(targetName)} preserveAudioLevels={preserveAudioLevels}");
                        showOverlay?.Invoke(OverlayDeviceKind.Input, "Switched input device", targetName);
                        _switchExecutionCoordinator.MarkInputSwitchSuccess(DateTime.Now);
                        return (true, targetName);
                    }

                    _logger.Error("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op} reason=verify-failed-after-retries attempts={RuntimeTuningConfig.SwitchMaxRetries}");
                    showOverlay?.Invoke(OverlayDeviceKind.Error, "Failed to switch input device", "");
                    return (false, null);
                }
                finally
                {
                    currentDefault?.Dispose();
                    foreach (var device in captureDeviceList)
                    {
                        try { device.Dispose(); }
                        catch (Exception disposeEx)
                        {
                            if (_logger.IsEnabled(LogLevel.Trace))
                            {
                                _logger.Trace("AudioDeviceService", () => $"Ignored dispose exception for capture device {LogPrivacy.Device(device?.FriendlyName)} ({LogPrivacy.Id(device?.ID)}): {disposeEx.GetType().Name}");
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_disposed || _backgroundWorkCts.IsCancellationRequested)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Skip} | opId={op} reason=shutdown-canceled");
                }

                return (false, null);
            }
            catch (Exception ex)
            {
                _logger.Error("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op}", nameof(SwitchInputDeviceToAsync), ex);
                showOverlay?.Invoke(OverlayDeviceKind.Error, "Failed to switch input device", "");
                return (false, null);
            }
            finally
            {
                _switchExecutionCoordinator.ReleaseInput();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            _disposeStartedCompletionSource.TrySetResult(true);

            _disposed = true;

            if (_logger.IsEnabled(LogLevel.Info))
                _logger.Info("AudioDeviceService", "Disposing audio device service");

            bool backgroundTasksCompleted = true;
            Task[] pendingTasks = [];
            try
            {
                pendingTasks = AudioDeviceBackgroundWorkHelper.CancelAndSnapshotPendingTasks(_backgroundWorkCts, _backgroundTasks);
                _resumeRecoveryCoordinator.SignalShutdown();

                UnregisterNotificationClient();

                Task sessionMonitoringDrainTask = StopSessionMonitoringAndDrainAsync();

                if (!sessionMonitoringDrainTask.IsCompleted)
                {
                    pendingTasks = [.. pendingTasks, sessionMonitoringDrainTask];
                }

                Task resumeRecoveryDrainTask = _resumeRecoveryCoordinator.CreateBoundedDrainTask();
                if (!resumeRecoveryDrainTask.IsCompleted)
                {
                    pendingTasks = [.. pendingTasks, resumeRecoveryDrainTask];
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug(
                        "AudioDeviceService",
                        () => $"shutdown-dispose-summary | notificationClientUnregistered=true sessionMonitoringDrainPending={!sessionMonitoringDrainTask.IsCompleted} resumeRecoveryDrainPending={!resumeRecoveryDrainTask.IsCompleted} pendingTaskCount={pendingTasks.Length}");
                }

                backgroundTasksCompleted = await AudioDeviceServiceLifecycle.DrainBackgroundTasksAsync(pendingTasks, _logger);
            }
            catch
            {
                backgroundTasksCompleted = false;
            }
            finally
            {
                try
                {
                    AudioDeviceBackgroundWorkHelper.DisposeResources(_backgroundWorkCts, _backgroundTasks);
                }
                catch
                {
                }
            }

            if (!backgroundTasksCompleted && _logger.IsEnabled(LogLevel.Warning))
            {
                _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.Lifecycle.DisposeForced} | reason=cleanup-timeout inFlightBackgroundTasksPossible=true");
            }

            _sessionService?.Dispose();
            _volumeService?.Dispose();
            _disposeCleanupBarrierCompletionSource.TrySetResult(true);

            try
            {
                _enumerator.Dispose();
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, nameof(Dispose), ex);
            }

            _enumeratorLock.Dispose();
            _switchExecutionCoordinator.Dispose();
            _resumeRecoveryCoordinator.Dispose();
            _lastLogTime.Clear();

            AudioDeviceHelper.ClearCaches();
            GC.SuppressFinalize(this);
        }

        public void Dispose()
        {
            Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }
    }
}
