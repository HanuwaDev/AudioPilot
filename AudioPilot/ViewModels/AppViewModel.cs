using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Diagnostics;

namespace AudioPilot.ViewModels
{
    internal readonly record struct ApplyEntryPreparation(double OverlayDurationSeconds, bool StartupChanged);
    internal readonly record struct ApplySettingsPreparation(
        Settings NewSettings,
        bool OutputRolesFallbackApplied,
        bool InputRolesFallbackApplied);
    internal readonly record struct ApplySettingsPreparationInput(
        string OutputReverseHotkey,
        bool OutputHotkeysEnabled,
        string InputReverseHotkey,
        bool InputHotkeysEnabled,
        IReadOnlyList<string> AdditionalStandaloneHotkeyKeys,
        bool OutputRoleMultimedia,
        bool OutputRoleCommunications,
        bool OutputRoleConsole,
        bool InputRoleMultimedia,
        bool InputRoleCommunications,
        bool InputRoleConsole,
        bool AutoSaveEnabled,
        bool RunAtStartup,
        string ShowAppHotkey,
        string ShowCurrentTrackHotkey,
        string PlayPauseHotkey,
        string NextTrackHotkey,
        string PreviousTrackHotkey,
        string MuteMicHotkey,
        string MuteSoundHotkey,
        string DeafenHotkey,
        string ListenToInputHotkey,
        string MasterVolumeUpHotkey,
        string MasterVolumeDownHotkey,
        string MicVolumeUpHotkey,
        string MicVolumeDownHotkey,
        int MasterVolumeStepPercent,
        int MicVolumeStepPercent,
        string ListenMonitorOutputDeviceId,
        string ListenMonitorOutputDeviceName,
        IReadOnlyList<CycleDevice> AvailableOutputDevices,
        bool PreserveAudioLevels,
        bool BluetoothReconnectEnabled,
        DeviceReferenceFileMode DeviceReferenceFileMode,
        bool OverlayEnabled,
        AppTheme Theme,
        string LogLevel,
        bool RedactLogContent,
        bool AutoScrollToMixerOnRestore,
        OverlayPosition OverlayPosition,
        double OverlayDurationSeconds);
    internal readonly record struct SaveSettingsPreparationInput(
        IReadOnlyList<CycleDevice> OutputCycleDevices,
        IReadOnlyList<CycleDevice> InputCycleDevices,
        IReadOnlyList<CycleDevice> AvailableOutputDevices,
        IReadOnlyList<CycleDevice> AvailableInputDevices,
        SaveEditState EditState,
        bool CanWriteOutput,
        bool CanWriteInput,
            IReadOnlyList<string> AdditionalStandaloneHotkeyKeys,
        string OutputReverseHotkey,
        string InputReverseHotkey,
        bool OutputHotkeysEnabled,
        bool InputHotkeysEnabled,
        bool RunAtStartup,
        bool PreserveAudioLevels,
        bool OverlayEnabled,
        OverlayPosition OverlayPosition,
        double OverlayDurationSeconds,
        AppTheme Theme,
        bool RedactLogContent);

    public partial class AppViewModel : INotifyPropertyChanged
    {
        private readonly SettingsService _settings;
        private readonly StartupService _startup;
        private readonly AudioDeviceService _audio;
        private readonly HotkeyService _hotkeys;
        private readonly AppShellService _shell;
        private readonly OverlayService _overlay;
        private readonly Logger _logger;
        private readonly Dispatcher _dispatcher;
        private readonly Func<MixerViewModel> _mixerFactory;
        private readonly Func<MixerViewModel> _inputMixerFactory;
        private readonly DeviceCacheHelper _deviceCache;
        private readonly AppCliOverlayCoordinator _cliOverlayCoordinator;
        private readonly AppWindowStateCoordinator _windowState = new();
        private readonly AppRefreshCoordinator _refreshCoordinator = new();
        private readonly AppHotkeyRegistrationCoordinator _hotkeyRegistrationCoordinator;
        private readonly AppSwitchCommandCoordinator _switchCoordinator;
        private readonly AppViewModelBackgroundWorkHelper _backgroundWorkHelper;
        private readonly ExecutionHistoryService _executionHistory;
        private BluetoothReconnectCoordinator? _routineBluetoothReconnectCoordinator;
        private readonly IProcessLifecycleMonitor _routineAppProcessMonitor;
        private readonly IRoutineProcessSnapshotProvider _routineProcessSnapshotProvider;
        private readonly DispatcherTimer _routineLastRunRefreshTimer;
        private readonly Lock _muteRefreshLock = new();
        private readonly Lock _settingsLock = new();
        private readonly SemaphoreSlim _settingsWriteSemaphore = new(1, 1);
        private readonly CancellationTokenSource _backgroundWorkCts = new();
        private readonly ConcurrentDictionary<int, Task> _backgroundTasks = new();
        private readonly List<IDisposable> _ownedCommands = [];
        private readonly List<(HotkeyViewModel Draft, PropertyChangedEventHandler Handler)> _hotkeyDraftHandlers = [];
        private readonly Lock _mixerInitializationLock = new();
        private readonly Lock _mixerRestoreQueueLock = new();
        private int _backgroundTaskId;
        private Task? _muteRefreshProcessorTask;
        private int _pendingMuteRefreshCount;
        private string _pendingMuteRefreshContext = "unspecified";
        private bool _hasPendingMuteRefresh;
        private bool _isWindowVisible;
        private int _cleanupStarted;
        private volatile bool _isCleaningUp;
        private MixerViewModel? _mixer;
        private MixerViewModel? _inputMixer;
        private bool _mixersConnected;
        private int _pendingMixerRestoreQueueCount;
        private TaskCompletionSource<object?> _mixerRestoreQueueIdleTcs = CreateCompletedMixerRestoreQueueSignal();
        private static readonly AudioSessionItem[] EmptyMixerSessions = [];

        public HotkeyViewModel Hotkey { get; }
        public HotkeyViewModel OutputReverseHotkey { get; }
        public HotkeyViewModel InputHotkey { get; }
        public HotkeyViewModel InputReverseHotkey { get; }
        public HotkeyViewModel SettingsShowAppHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsShowCurrentTrackHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsPlayPauseHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsNextTrackHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsPreviousTrackHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsMuteMicHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsMuteSoundHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsDeafenHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsListenToInputHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsMasterVolumeUpHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsMasterVolumeDownHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsMicVolumeUpHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsMicVolumeDownHotkeyDraftCapture { get; }
        public MixerViewModel Mixer
        {
            get
            {
                EnsureMixerInitialized(AudioMixerMode.Output);
                return _mixer!;
            }
        }

        public MixerViewModel InputMixer
        {
            get
            {
                EnsureMixerInitialized(AudioMixerMode.Input);
                return _inputMixer!;
            }
        }

        public MixerViewModel ActiveMixer => IsInputSettingsTab(SelectedSettingsTabIndex) ? InputMixer : Mixer;
        public IEnumerable<AudioSessionItem> ActiveMixerSessions => TryGetActiveMixer()?.Sessions ?? (IEnumerable<AudioSessionItem>)EmptyMixerSessions;
        public string ActiveMixerHeader => IsInputSettingsTab(SelectedSettingsTabIndex) ? "Recording Mixer" : "Volume Mixer";
        public ObservableCollection<string> AvailableOutputDeviceNames { get; } = [];
        public ObservableCollection<CycleDevice> OutputCycleDevices { get; } = [];
        public ObservableCollection<string> AvailableInputDeviceNames { get; } = [];
        public ObservableCollection<CycleDevice> InputCycleDevices { get; } = [];
        public ObservableCollection<CycleDevice> SettingsListenMonitorOutputDevices { get; } = [];

        public bool ShowBalloonAfterSave { get; private set; }

        public Settings? CurrentSettings
        {
            get
            {
                lock (_settingsLock)
                {
                    return _cachedSettings;
                }
            }
        }

        public Settings? Settings => CurrentSettings;

        private bool TryRunBackgroundWork(Func<CancellationToken, Task> operation, string operationName)
        {
            return _backgroundWorkHelper.TryQueue(
                _backgroundTasks,
                ref _backgroundTaskId,
                _backgroundWorkCts,
                operation,
                operationName);
        }

        private void RunBackgroundWork(Func<CancellationToken, Task> operation, string operationName)
        {
            _ = TryRunBackgroundWork(operation, operationName);
        }

        private static TaskCompletionSource<object?> CreateCompletedMixerRestoreQueueSignal()
        {
            var source = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.TrySetResult(null);
            return source;
        }

        private async Task ExecuteSettingsWriteAsync(Func<Task> operation)
        {
            bool lockAcquired = false;
            try
            {
                await _settingsWriteSemaphore.WaitAsync();
                lockAcquired = true;
                await operation();
            }
            finally
            {
                if (lockAcquired)
                {
                    _settingsWriteSemaphore.Release();
                }
            }
        }

        private async Task<TResult> ExecuteSettingsWriteAsync<TResult>(Func<Task<TResult>> operation)
        {
            bool lockAcquired = false;
            try
            {
                await _settingsWriteSemaphore.WaitAsync();
                lockAcquired = true;
                return await operation();
            }
            finally
            {
                if (lockAcquired)
                {
                    _settingsWriteSemaphore.Release();
                }
            }
        }

        private async Task InvokeOnDispatcherAsync(Action action, [CallerMemberName] string callerName = "")
        {
            ArgumentNullException.ThrowIfNull(action);

            if (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(_dispatcher))
            {
                return;
            }

            if (_dispatcher.CheckAccess())
            {
                action();
                return;
            }

            try
            {
                await _dispatcher.InvokeAsync(action).Task;
            }
            catch (InvalidOperationException ex) when (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(_dispatcher))
            {
                _logger.Warning("AppViewModel", "Skipping dispatcher action because shutdown is in progress", callerName, ex);
            }
        }

        private async Task<TResult> InvokeOnDispatcherAsync<TResult>(Func<TResult> action, TResult fallback, [CallerMemberName] string callerName = "")
        {
            ArgumentNullException.ThrowIfNull(action);

            if (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(_dispatcher))
            {
                return fallback;
            }

            if (_dispatcher.CheckAccess())
            {
                return action();
            }

            try
            {
                return await _dispatcher.InvokeAsync(action).Task;
            }
            catch (InvalidOperationException ex) when (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(_dispatcher))
            {
                _logger.Warning("AppViewModel", "Skipping dispatcher result action because shutdown is in progress", callerName, ex);
                return fallback;
            }
        }

        private async Task InvokeOnDispatcherAsync(Func<Task> action, [CallerMemberName] string callerName = "")
        {
            ArgumentNullException.ThrowIfNull(action);

            if (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(_dispatcher))
            {
                return;
            }

            if (_dispatcher.CheckAccess())
            {
                await action();
                return;
            }

            try
            {
                await _dispatcher.InvokeAsync(action).Task.Unwrap();
            }
            catch (InvalidOperationException ex) when (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(_dispatcher))
            {
                _logger.Warning("AppViewModel", "Skipping dispatcher async action because shutdown is in progress", callerName, ex);
            }
        }

        private async Task<TResult> InvokeOnDispatcherAsync<TResult>(Func<Task<TResult>> action, TResult fallback, [CallerMemberName] string callerName = "")
        {
            ArgumentNullException.ThrowIfNull(action);

            if (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(_dispatcher))
            {
                return fallback;
            }

            if (_dispatcher.CheckAccess())
            {
                return await action();
            }

            try
            {
                return await _dispatcher.InvokeAsync(action).Task.Unwrap();
            }
            catch (InvalidOperationException ex) when (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(_dispatcher))
            {
                _logger.Warning("AppViewModel", "Skipping dispatcher async action because shutdown is in progress", callerName, ex);
                return fallback;
            }
        }

        private static List<string> BuildRoleSelections(bool multimedia, bool communications, bool console)
        {
            List<string> roles = [];
            if (multimedia)
            {
                roles.Add("Multimedia");
            }

            if (communications)
            {
                roles.Add("Communications");
            }

            if (console)
            {
                roles.Add("Console");
            }

            return roles;
        }

        private static ApplySettingsPreparation BuildApplyPreparation(Settings? cachedCopy, ApplySettingsPreparationInput input)
        {
            bool outputRolesFallbackApplied =
                !input.OutputRoleMultimedia &&
                !input.OutputRoleCommunications &&
                !input.OutputRoleConsole;

            bool inputRolesFallbackApplied =
                !input.InputRoleMultimedia &&
                !input.InputRoleCommunications &&
                !input.InputRoleConsole;

            Settings newSettings = AppSettingsWorkflowCoordinator.BuildAppliedSettings(
                cachedCopy,
                new ApplySettingsBuildInput(
                    input.OutputReverseHotkey,
                    input.OutputHotkeysEnabled,
                    input.InputReverseHotkey,
                    input.InputHotkeysEnabled,
                    input.AdditionalStandaloneHotkeyKeys,
                    BuildRoleSelections(input.OutputRoleMultimedia, input.OutputRoleCommunications, input.OutputRoleConsole),
                    BuildRoleSelections(input.InputRoleMultimedia, input.InputRoleCommunications, input.InputRoleConsole),
                    input.AutoSaveEnabled,
                    input.RunAtStartup,
                    input.ShowAppHotkey,
                    input.ShowCurrentTrackHotkey,
                    input.PlayPauseHotkey,
                    input.NextTrackHotkey,
                    input.PreviousTrackHotkey,
                    input.MuteMicHotkey,
                    input.MuteSoundHotkey,
                    input.DeafenHotkey,
                    input.ListenToInputHotkey,
                    input.MasterVolumeUpHotkey,
                    input.MasterVolumeDownHotkey,
                    input.MicVolumeUpHotkey,
                    input.MicVolumeDownHotkey,
                    input.MasterVolumeStepPercent,
                    input.MicVolumeStepPercent,
                    input.ListenMonitorOutputDeviceId,
                    input.ListenMonitorOutputDeviceName,
                    input.AvailableOutputDevices,
                    input.PreserveAudioLevels,
                    input.BluetoothReconnectEnabled,
                    input.DeviceReferenceFileMode,
                    input.OverlayEnabled,
                    input.Theme,
                    input.LogLevel,
                    input.RedactLogContent,
                    input.AutoScrollToMixerOnRestore,
                    input.OverlayPosition,
                    input.OverlayDurationSeconds));

            return new ApplySettingsPreparation(newSettings, outputRolesFallbackApplied, inputRolesFallbackApplied);
        }

        private static Settings BuildSavePreparation(Settings? cachedCopy, SaveSettingsPreparationInput input)
        {
            Settings newSettings = AppSettingsWorkflowCoordinator.BuildSavedSettings(
                cachedCopy,
                new SaveSettingsBuildInput(
                    input.OutputCycleDevices,
                    input.InputCycleDevices,
                    input.AvailableOutputDevices,
                    input.AvailableInputDevices,
                    input.EditState,
                    input.CanWriteOutput,
                    input.CanWriteInput,
                    input.AdditionalStandaloneHotkeyKeys,
                    input.OutputReverseHotkey,
                    input.InputReverseHotkey,
                    input.OutputHotkeysEnabled,
                    input.InputHotkeysEnabled,
                    input.RunAtStartup,
                    input.PreserveAudioLevels,
                    input.OverlayEnabled,
                    input.OverlayPosition,
                    input.OverlayDurationSeconds,
                    input.Theme,
                    input.RedactLogContent));

            return newSettings;
        }

        private static void ApplyPersistedAdvancedTuning(Settings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            AdvancedTuningSettings advancedTuning = settings.AdvancedTuning ?? new AdvancedTuningSettings();
            BluetoothReconnectRuntimeConfig.Apply(advancedTuning.BluetoothReconnect);

            SteamBigPictureAdvancedTuningSettings steamBigPicture = advancedTuning.SteamBigPicture ?? new SteamBigPictureAdvancedTuningSettings();
            RuntimeTuningConfig.SteamBigPictureMonitorDebounceMs = steamBigPicture.MonitorDebounceMs;
            RuntimeTuningConfig.SteamBigPictureConfirmationDelayMs = steamBigPicture.ConfirmationDelayMs;
        }

        private bool _runAtStartup;
        private bool _isInitializing;
        private bool _deafenBackingField;
        private bool _muteMicBackingField;
        private bool _muteSoundBackingField;
        private bool _preserveAudioLevelsBackingField;
        private bool _overlayEnabledBackingField = true;
        private OverlayPosition _overlayPositionBackingField = OverlayPosition.TopRight;
        private string _overlayDurationSecondsTextBackingField = AudioPilot.Constants.AppConstants.Timing.OverlayAutoHideSeconds.ToString("0.0");
        private bool _settingsAutoSaveEnabledDraft;
        private bool _settingsRunAtStartupDraft;
        private AppTheme _settingsThemeDraft = AppTheme.System;
        private bool _settingsPreserveAudioLevelsDraft = true;
        private bool _settingsAutoScrollToMixerOnRestoreDraft = true;
        private bool _settingsOverlayEnabledDraft = true;
        private bool _settingsBluetoothReconnectEnabledDraft = true;
        private DeviceReferenceFileMode _settingsDeviceReferenceFileModeDraft = DeviceReferenceFileMode.Off;
        private LogLevel _settingsLogLevelDraft = LogLevel.Info;
        private bool _settingsRedactLogContentDraft = true;
        private bool _settingsOutputRoleMultimediaDraft = true;
        private bool _settingsOutputRoleCommunicationsDraft = true;
        private bool _settingsOutputRoleConsoleDraft = true;
        private bool _settingsInputRoleMultimediaDraft = true;
        private bool _settingsInputRoleCommunicationsDraft = true;
        private bool _settingsInputRoleConsoleDraft = true;
        private OverlayPosition _settingsOverlayPositionDraft = OverlayPosition.TopRight;
        private string _settingsOverlayDurationSecondsDraft = AudioPilot.Constants.AppConstants.Timing.OverlayAutoHideSeconds.ToString("0.0");
        private string _settingsMasterVolumeStepPercentDraft = "5";
        private string _settingsMicVolumeStepPercentDraft = "5";
        private string _settingsListenMonitorOutputDeviceIdDraft = string.Empty;
        private string _settingsListenMonitorOutputDeviceNameDraft = string.Empty;
        private bool _settingsMasterVolumeControlsExpanded = true;
        private bool _settingsMicVolumeControlsExpanded;
        private bool _isApplyingSettings;
        private bool _outputHotkeysEnabledBackingField = true;
        private bool _inputHotkeysEnabledBackingField = true;
        private readonly HashSet<string> _hotkeyConflictKeys = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _autoSaveDebounceCts;
        private CancellationTokenSource? _startupDebounceCts;
        private CancellationTokenSource? _sessionRefreshDebounceCts;
        private CancellationTokenSource? _visibleMixerActivationRefreshDebounceCts;
        private CancellationTokenSource? _steamBigPictureDebounceCts;
        private CancellationTokenSource? _steamBigPictureConfirmationDebounceCts;
        private int _autoSaveSuppressionCount;
        private int _pendingOutputSessionCreatedSignals;
        private int _pendingInputSessionCreatedSignals;
        private int _pendingOutputSessionLifecycleSignals;
        private int _pendingInputSessionLifecycleSignals;
        private int _pendingShowWindowMixerRefreshSignals;
        private int _pendingSteamBigPictureSignals;
        private readonly Lock _deviceReferenceFingerprintLock = new();
        private string _lastDeviceReferenceFingerprint = string.Empty;
        private DateTime _lastSettingsWriteTime;
        private AudioMixerMode? _mixerSessionMonitoringMode;
        private bool _hasHandledWindowVisibilityChange;

        private string GetSettingsPath() => _settings.GetSettingsPath();

        public bool Deafen
        {
            get => _deafenBackingField;
            set
            {
                if (_deafenBackingField == value) return;

                MuteStateChangePlan plan = AppMuteStateCoordinator.ResolveDeafenChange(value);
                _deafenBackingField = plan.NewDeafen;

                RunBackgroundWork(_ =>
                {
                    try
                    {
                        ComThreadingHelper.RunOnCoreAudioThread(() =>
                        {
                            _audio.SetMicrophoneMute(plan.DeviceMuteMicrophone);
                            _audio.SetPlaybackMute(plan.DeviceMutePlayback);
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("AppViewModel", () => $"mute-state-apply-failed | target=deafen error={ex.GetType().Name}", nameof(Deafen), ex);
                    }

                    return Task.CompletedTask;
                }, nameof(Deafen));

                _logger.Info("AppViewModel", () => plan.LogMessage);

                _muteMicBackingField = plan.NewMuteMic;
                _muteSoundBackingField = plan.NewMuteSound;

                foreach (string propertyName in plan.PropertyNamesToNotify)
                {
                    OnPropertyChanged(propertyName);
                }
            }
        }

        public bool MuteMic
        {
            get => _muteMicBackingField;
            set
            {
                if (_muteMicBackingField == value) return;
                MuteStateChangePlan plan = AppMuteStateCoordinator.ResolveMuteMicChange(value, _deafenBackingField, _muteSoundBackingField);
                _muteMicBackingField = plan.NewMuteMic;

                RunBackgroundWork(_ =>
                {
                    try
                    {
                        ComThreadingHelper.RunOnCoreAudioThread(() => _audio.SetMicrophoneMute(plan.DeviceMuteMicrophone));
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("AppViewModel", () => $"mute-state-apply-failed | target=mic error={ex.GetType().Name}", nameof(MuteMic), ex);
                    }

                    return Task.CompletedTask;
                }, nameof(MuteMic));

                _logger.Trace("AppViewModel", () => plan.LogMessage);
                foreach (string propertyName in plan.PropertyNamesToNotify)
                {
                    OnPropertyChanged(propertyName);
                }
            }
        }

        public bool MuteSound
        {
            get => _muteSoundBackingField;
            set
            {
                if (_muteSoundBackingField == value) return;
                MuteStateChangePlan plan = AppMuteStateCoordinator.ResolveMuteSoundChange(value, _deafenBackingField, _muteMicBackingField);
                _muteSoundBackingField = plan.NewMuteSound;

                RunBackgroundWork(_ =>
                {
                    try
                    {
                        ComThreadingHelper.RunOnCoreAudioThread(() => _audio.SetPlaybackMute(plan.DeviceMutePlayback));
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("AppViewModel", () => $"mute-state-apply-failed | target=playback error={ex.GetType().Name}", nameof(MuteSound), ex);
                    }

                    return Task.CompletedTask;
                }, nameof(MuteSound));

                _logger.Trace("AppViewModel", () => plan.LogMessage);
                foreach (string propertyName in plan.PropertyNamesToNotify)
                {
                    OnPropertyChanged(propertyName);
                }
            }
        }

        public bool PreserveAudioLevels
        {
            get => _preserveAudioLevelsBackingField;
            set
            {
                if (_preserveAudioLevelsBackingField == value)
                    return;

                _preserveAudioLevelsBackingField = value;
                OnPropertyChanged(nameof(PreserveAudioLevels));
            }
        }

        public OverlayPosition OverlayPosition
        {
            get => _overlayPositionBackingField;
            set
            {
                if (_overlayPositionBackingField == value)
                    return;

                _overlayPositionBackingField = value;
                OnPropertyChanged(nameof(OverlayPosition));
                ApplyOverlayDisplaySettings();
            }
        }

        public bool OverlayEnabled
        {
            get => _overlayEnabledBackingField;
            set
            {
                if (_overlayEnabledBackingField == value)
                    return;

                _overlayEnabledBackingField = value;
                OnPropertyChanged(nameof(OverlayEnabled));
                ApplyOverlayDisplaySettings();
            }
        }

        public string OverlayDurationSecondsText
        {
            get => _overlayDurationSecondsTextBackingField;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (string.Equals(_overlayDurationSecondsTextBackingField, normalized, StringComparison.Ordinal))
                    return;

                _overlayDurationSecondsTextBackingField = normalized;
                OnPropertyChanged(nameof(OverlayDurationSecondsText));
                ApplyOverlayDisplaySettings();
            }
        }

        public IEnumerable<OverlayPosition> AvailableOverlayPositions => _availableOverlayPositions;

        public bool SettingsRunAtStartupDraft
        {
            get => _settingsRunAtStartupDraft;
            set
            {
                if (_settingsRunAtStartupDraft == value)
                    return;

                _settingsRunAtStartupDraft = value;
                OnPropertyChanged(nameof(SettingsRunAtStartupDraft));
            }
        }

        public bool SettingsAutoSaveEnabledDraft
        {
            get => _settingsAutoSaveEnabledDraft;
            set
            {
                if (_settingsAutoSaveEnabledDraft == value)
                    return;

                _settingsAutoSaveEnabledDraft = value;
                OnPropertyChanged(nameof(SettingsAutoSaveEnabledDraft));
                OnPropertyChanged(nameof(IsAutoSaveActive));
                OnPropertyChanged(nameof(IsAutoSavePendingActivation));
            }
        }

        public bool IsAutoSaveActive => IsPersistedAutoSaveEnabled();

        public bool IsAutoSavePendingActivation =>
            SettingsAutoSaveEnabledDraft && !IsPersistedAutoSaveEnabled();

        public AppTheme SettingsThemeDraft
        {
            get => _settingsThemeDraft;
            set
            {
                if (_settingsThemeDraft == value)
                    return;

                _settingsThemeDraft = value;
                OnPropertyChanged(nameof(SettingsThemeDraft));
            }
        }

        public bool SettingsPreserveAudioLevelsDraft
        {
            get => _settingsPreserveAudioLevelsDraft;
            set
            {
                if (_settingsPreserveAudioLevelsDraft == value)
                    return;

                _settingsPreserveAudioLevelsDraft = value;
                OnPropertyChanged(nameof(SettingsPreserveAudioLevelsDraft));
            }
        }

        public bool SettingsAutoScrollToMixerOnRestoreDraft
        {
            get => _settingsAutoScrollToMixerOnRestoreDraft;
            set
            {
                if (_settingsAutoScrollToMixerOnRestoreDraft == value)
                    return;

                _settingsAutoScrollToMixerOnRestoreDraft = value;
                OnPropertyChanged(nameof(SettingsAutoScrollToMixerOnRestoreDraft));
            }
        }

        public bool SettingsOverlayEnabledDraft
        {
            get => _settingsOverlayEnabledDraft;
            set
            {
                if (_settingsOverlayEnabledDraft == value)
                    return;

                _settingsOverlayEnabledDraft = value;
                OnPropertyChanged(nameof(SettingsOverlayEnabledDraft));
            }
        }

        public LogLevel SettingsLogLevelDraft
        {
            get => _settingsLogLevelDraft;
            set
            {
                if (_settingsLogLevelDraft == value)
                    return;

                _settingsLogLevelDraft = value;
                OnPropertyChanged(nameof(SettingsLogLevelDraft));
            }
        }

        public bool SettingsRedactLogContentDraft
        {
            get => _settingsRedactLogContentDraft;
            set
            {
                if (_settingsRedactLogContentDraft == value)
                    return;

                _settingsRedactLogContentDraft = value;
                OnPropertyChanged(nameof(SettingsRedactLogContentDraft));
            }
        }

        public bool SettingsBluetoothReconnectEnabledDraft
        {
            get => _settingsBluetoothReconnectEnabledDraft;
            set
            {
                if (_settingsBluetoothReconnectEnabledDraft == value)
                    return;

                _settingsBluetoothReconnectEnabledDraft = value;
                OnPropertyChanged(nameof(SettingsBluetoothReconnectEnabledDraft));
            }
        }

        public DeviceReferenceFileMode SettingsDeviceReferenceFileModeDraft
        {
            get => _settingsDeviceReferenceFileModeDraft;
            set
            {
                if (_settingsDeviceReferenceFileModeDraft == value)
                    return;

                _settingsDeviceReferenceFileModeDraft = value;
                OnPropertyChanged(nameof(SettingsDeviceReferenceFileModeDraft));
            }
        }

        public string SettingsPlayPauseHotkeyDraft
        {
            get => SettingsPlayPauseHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsPlayPauseHotkeyDraftCapture, value, nameof(SettingsPlayPauseHotkeyDraft));
        }

        public string SettingsShowCurrentTrackHotkeyDraft
        {
            get => SettingsShowCurrentTrackHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsShowCurrentTrackHotkeyDraftCapture, value, nameof(SettingsShowCurrentTrackHotkeyDraft));
        }

        public string SettingsShowAppHotkeyDraft
        {
            get => SettingsShowAppHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsShowAppHotkeyDraftCapture, value, nameof(SettingsShowAppHotkeyDraft));
        }

        public string SettingsNextTrackHotkeyDraft
        {
            get => SettingsNextTrackHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsNextTrackHotkeyDraftCapture, value, nameof(SettingsNextTrackHotkeyDraft));
        }

        public string SettingsPreviousTrackHotkeyDraft
        {
            get => SettingsPreviousTrackHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsPreviousTrackHotkeyDraftCapture, value, nameof(SettingsPreviousTrackHotkeyDraft));
        }

        public string SettingsMuteMicHotkeyDraft
        {
            get => SettingsMuteMicHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsMuteMicHotkeyDraftCapture, value, nameof(SettingsMuteMicHotkeyDraft));
        }

        public string SettingsMuteSoundHotkeyDraft
        {
            get => SettingsMuteSoundHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsMuteSoundHotkeyDraftCapture, value, nameof(SettingsMuteSoundHotkeyDraft));
        }

        public string SettingsDeafenHotkeyDraft
        {
            get => SettingsDeafenHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsDeafenHotkeyDraftCapture, value, nameof(SettingsDeafenHotkeyDraft));
        }

        public string SettingsListenToInputHotkeyDraft
        {
            get => SettingsListenToInputHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsListenToInputHotkeyDraftCapture, value, nameof(SettingsListenToInputHotkeyDraft));
        }

        public string SettingsMasterVolumeUpHotkeyDraft
        {
            get => SettingsMasterVolumeUpHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsMasterVolumeUpHotkeyDraftCapture, value, nameof(SettingsMasterVolumeUpHotkeyDraft));
        }

        public string SettingsMasterVolumeDownHotkeyDraft
        {
            get => SettingsMasterVolumeDownHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsMasterVolumeDownHotkeyDraftCapture, value, nameof(SettingsMasterVolumeDownHotkeyDraft));
        }

        public string SettingsMicVolumeUpHotkeyDraft
        {
            get => SettingsMicVolumeUpHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsMicVolumeUpHotkeyDraftCapture, value, nameof(SettingsMicVolumeUpHotkeyDraft));
        }

        public string SettingsMicVolumeDownHotkeyDraft
        {
            get => SettingsMicVolumeDownHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsMicVolumeDownHotkeyDraftCapture, value, nameof(SettingsMicVolumeDownHotkeyDraft));
        }

        public bool SettingsMasterVolumeControlsExpanded
        {
            get => _settingsMasterVolumeControlsExpanded;
            set
            {
                if (_settingsMasterVolumeControlsExpanded == value)
                {
                    return;
                }

                _settingsMasterVolumeControlsExpanded = value;
                OnPropertyChanged(nameof(SettingsMasterVolumeControlsExpanded));
            }
        }

        public bool SettingsMicVolumeControlsExpanded
        {
            get => _settingsMicVolumeControlsExpanded;
            set
            {
                if (_settingsMicVolumeControlsExpanded == value)
                {
                    return;
                }

                _settingsMicVolumeControlsExpanded = value;
                OnPropertyChanged(nameof(SettingsMicVolumeControlsExpanded));
            }
        }

        public string SettingsMasterVolumeStepPercentDraft
        {
            get => _settingsMasterVolumeStepPercentDraft;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (string.Equals(_settingsMasterVolumeStepPercentDraft, normalized, StringComparison.Ordinal))
                    return;

                _settingsMasterVolumeStepPercentDraft = normalized;
                OnPropertyChanged(nameof(SettingsMasterVolumeStepPercentDraft));
            }
        }

        public string SettingsMicVolumeStepPercentDraft
        {
            get => _settingsMicVolumeStepPercentDraft;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (string.Equals(_settingsMicVolumeStepPercentDraft, normalized, StringComparison.Ordinal))
                    return;

                _settingsMicVolumeStepPercentDraft = normalized;
                OnPropertyChanged(nameof(SettingsMicVolumeStepPercentDraft));
            }
        }

        public string SettingsListenMonitorOutputDeviceIdDraft
        {
            get => _settingsListenMonitorOutputDeviceIdDraft;
            set => SetSettingsListenMonitorOutputDraft(value, null);
        }











        private void DetachOwnedEventHandlers()
        {
            foreach ((HotkeyViewModel Draft, PropertyChangedEventHandler Handler) in _hotkeyDraftHandlers)
            {
                Draft.PropertyChanged -= Handler;
            }

            _hotkeyDraftHandlers.Clear();

            OutputCycleDevices.CollectionChanged -= OnOutputCycleDevicesCollectionChanged;
            InputCycleDevices.CollectionChanged -= OnInputCycleDevicesCollectionChanged;
            Routines.CollectionChanged -= OnRoutinesCollectionChanged;
            DetachRoutinePropertyHandlers();

            _routineLastRunRefreshTimer.Stop();
            _routineLastRunRefreshTimer.Tick -= OnRoutineLastRunRefreshTimerTick;
        }

        public bool OutputHotkeyHasConflict => Hotkey.HasWarning;

        public bool OutputReverseHotkeyHasConflict => OutputReverseHotkey.HasWarning;

        public bool InputHotkeyHasConflict => InputHotkey.HasWarning;

        public bool InputReverseHotkeyHasConflict => InputReverseHotkey.HasWarning;

        public bool SettingsShowAppHotkeyHasConflict => SettingsShowAppHotkeyDraftCapture.HasWarning;

        public bool SettingsShowCurrentTrackHotkeyHasConflict => SettingsShowCurrentTrackHotkeyDraftCapture.HasWarning;

        public bool SettingsPlayPauseHotkeyHasConflict => SettingsPlayPauseHotkeyDraftCapture.HasWarning;

        public bool SettingsNextTrackHotkeyHasConflict => SettingsNextTrackHotkeyDraftCapture.HasWarning;

        public bool SettingsPreviousTrackHotkeyHasConflict => SettingsPreviousTrackHotkeyDraftCapture.HasWarning;

        public bool SettingsMuteMicHotkeyHasConflict => SettingsMuteMicHotkeyDraftCapture.HasWarning;

        public bool SettingsMuteSoundHotkeyHasConflict => SettingsMuteSoundHotkeyDraftCapture.HasWarning;

        public bool SettingsDeafenHotkeyHasConflict => SettingsDeafenHotkeyDraftCapture.HasWarning;

        public bool SettingsListenToInputHotkeyHasConflict => SettingsListenToInputHotkeyDraftCapture.HasWarning;

        public bool SettingsMasterVolumeUpHotkeyHasConflict => SettingsMasterVolumeUpHotkeyDraftCapture.HasWarning;

        public bool SettingsMasterVolumeDownHotkeyHasConflict => SettingsMasterVolumeDownHotkeyDraftCapture.HasWarning;

        public bool SettingsMicVolumeUpHotkeyHasConflict => SettingsMicVolumeUpHotkeyDraftCapture.HasWarning;

        public bool SettingsMicVolumeDownHotkeyHasConflict => SettingsMicVolumeDownHotkeyDraftCapture.HasWarning;








        public bool SettingsOutputRoleMultimediaDraft
        {
            get => _settingsOutputRoleMultimediaDraft;
            set
            {
                if (_settingsOutputRoleMultimediaDraft == value)
                    return;

                _settingsOutputRoleMultimediaDraft = value;
                OnPropertyChanged(nameof(SettingsOutputRoleMultimediaDraft));
            }
        }

        public bool SettingsOutputRoleCommunicationsDraft
        {
            get => _settingsOutputRoleCommunicationsDraft;
            set
            {
                if (_settingsOutputRoleCommunicationsDraft == value)
                    return;

                _settingsOutputRoleCommunicationsDraft = value;
                OnPropertyChanged(nameof(SettingsOutputRoleCommunicationsDraft));
            }
        }

        public bool SettingsOutputRoleConsoleDraft
        {
            get => _settingsOutputRoleConsoleDraft;
            set
            {
                if (_settingsOutputRoleConsoleDraft == value)
                    return;

                _settingsOutputRoleConsoleDraft = value;
                OnPropertyChanged(nameof(SettingsOutputRoleConsoleDraft));
            }
        }

        public bool SettingsInputRoleMultimediaDraft
        {
            get => _settingsInputRoleMultimediaDraft;
            set
            {
                if (_settingsInputRoleMultimediaDraft == value)
                    return;

                _settingsInputRoleMultimediaDraft = value;
                OnPropertyChanged(nameof(SettingsInputRoleMultimediaDraft));
            }
        }

        public bool SettingsInputRoleCommunicationsDraft
        {
            get => _settingsInputRoleCommunicationsDraft;
            set
            {
                if (_settingsInputRoleCommunicationsDraft == value)
                    return;

                _settingsInputRoleCommunicationsDraft = value;
                OnPropertyChanged(nameof(SettingsInputRoleCommunicationsDraft));
            }
        }

        public bool SettingsInputRoleConsoleDraft
        {
            get => _settingsInputRoleConsoleDraft;
            set
            {
                if (_settingsInputRoleConsoleDraft == value)
                    return;

                _settingsInputRoleConsoleDraft = value;
                OnPropertyChanged(nameof(SettingsInputRoleConsoleDraft));
            }
        }

        public OverlayPosition SettingsOverlayPositionDraft
        {
            get => _settingsOverlayPositionDraft;
            set
            {
                if (_settingsOverlayPositionDraft == value)
                    return;

                _settingsOverlayPositionDraft = value;
                OnPropertyChanged(nameof(SettingsOverlayPositionDraft));
            }
        }

        public string SettingsOverlayDurationSecondsDraft
        {
            get => _settingsOverlayDurationSecondsDraft;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (string.Equals(_settingsOverlayDurationSecondsDraft, normalized, StringComparison.Ordinal))
                    return;

                _settingsOverlayDurationSecondsDraft = normalized;
                OnPropertyChanged(nameof(SettingsOverlayDurationSecondsDraft));
            }
        }

        public bool IsRoutinesTabActive => SelectedSettingsTabIndex == 2;
        public bool IsSettingsTabActive => SelectedSettingsTabIndex == 3;
        public bool IsDeviceTabsActive => SelectedSettingsTabIndex is 0 or 1;
        public bool IsEditorTabsActive => IsDeviceTabsActive;

        public bool IsApplyingSettings
        {
            get => _isApplyingSettings;
            private set
            {
                if (_isApplyingSettings == value)
                    return;

                _isApplyingSettings = value;
                OnPropertyChanged(nameof(IsApplyingSettings));
            }
        }



        public bool OutputHotkeysEnabled
        {
            get => _outputHotkeysEnabledBackingField;
            set
            {
                if (_outputHotkeysEnabledBackingField == value)
                    return;

                _outputHotkeysEnabledBackingField = value;
                OnPropertyChanged(nameof(OutputHotkeysEnabled));
                ApplySwitchHotkeyRegistrationFromCurrentUiState();
            }
        }

        public bool InputHotkeysEnabled
        {
            get => _inputHotkeysEnabledBackingField;
            set
            {
                if (_inputHotkeysEnabledBackingField == value)
                    return;

                _inputHotkeysEnabledBackingField = value;
                OnPropertyChanged(nameof(InputHotkeysEnabled));
                ApplySwitchHotkeyRegistrationFromCurrentUiState();
            }
        }



        private AppTheme _themeBackingField;
        public AppTheme Theme
        {
            get => _themeBackingField;
            set
            {
                if (_themeBackingField == value) return;
                _themeBackingField = value;
                OnPropertyChanged(nameof(Theme));
                SyncMirroredSettingsDraftsFromLiveState(theme: value);

                Application? application = Application.Current;
                if (application == null)
                {
                    return;
                }

                WindowThemeResolver.ApplyApplicationMainWindowTheme(value);
            }
        }

        public IEnumerable<AppTheme> AvailableThemes => _availableThemes;
        public IEnumerable<LogLevel> AvailableLogLevels => _availableLogLevels;
        public IEnumerable<DeviceReferenceFileMode> AvailableDeviceReferenceFileModes => _availableDeviceReferenceFileModes;

        public bool IsSaving
        {
            get => _isSaving;
            private set
            {
                if (_isSaving == value) return;
                _isSaving = value;
                OnPropertyChanged(nameof(IsSaving));
            }
        }

        private volatile bool _isSaving;

        public ICommand RefreshDevicesCommand { get; }
        public ICommand SaveSettingsCommand { get; }
        public ICommand SaveCurrentContextCommand { get; }
        public ICommand ApplySettingsCommand { get; }
        public ICommand ImportSettingsCommand { get; }
        public ICommand ExportSettingsCommand { get; }
        public ICommand ResetPerAppAudioRoutingCommand { get; }
        public ICommand ShowCommand { get; }
        public ICommand MinimizeCommand { get; }
        public ICommand ExitCommand { get; }
        public ICommand ResetToDefaultsCommand { get; }
        public ICommand AddOutputCycleDeviceCommand { get; }
        public ICommand RemoveOutputCycleDeviceCommand { get; }
        public ICommand MoveOutputCycleDeviceUpCommand { get; }
        public ICommand MoveOutputCycleDeviceDownCommand { get; }
        public ICommand AddInputCycleDeviceCommand { get; }
        public ICommand RemoveInputCycleDeviceCommand { get; }
        public ICommand MoveInputCycleDeviceUpCommand { get; }
        public ICommand MoveInputCycleDeviceDownCommand { get; }
        public ICommand AddRoutineCommand { get; }
        public ICommand EditRoutineCommand { get; }
        public ICommand DuplicateRoutineCommand { get; }
        public ICommand CopyRoutineCommand { get; }
        public ICommand RemoveRoutineCommand { get; }
        public ICommand MoveRoutineUpCommand { get; }
        public ICommand MoveRoutineDownCommand { get; }
        public ICommand EnableSelectedRoutinesCommand { get; }
        public ICommand DisableSelectedRoutinesCommand { get; }
        public ICommand SaveRoutinesCommand { get; }
        public ICommand NextSettingsTabCommand { get; }

        private readonly List<string> _additionalStandaloneHotkeyKeys = [];
        private Settings? _cachedSettings;
        private readonly List<CycleDevice> _outputDevices = [];
        private int _selectedAvailableOutputIndex = -1;
        private int _selectedOutputCycleIndex = -1;
        private readonly List<CycleDevice> _inputDevices = [];
        private int _selectedAvailableInputIndex = -1;
        private int _selectedInputCycleIndex = -1;
        private long _suppressHotplugOutputConnectedUntilUtcTicks;
        private long _suppressHotplugInputConnectedUntilUtcTicks;
        private int _selectedSettingsTabIndex;
        private readonly AppTheme[] _availableThemes = Enum.GetValues<AppTheme>();
        private readonly LogLevel[] _availableLogLevels = Enum.GetValues<LogLevel>();
        private readonly OverlayPosition[] _availableOverlayPositions = Enum.GetValues<OverlayPosition>();
        private readonly DeviceReferenceFileMode[] _availableDeviceReferenceFileModes = Enum.GetValues<DeviceReferenceFileMode>();





































        internal string? GetTrayShowAppHotkey()
        {
            Settings? cachedCopy = GetCachedSettingsSnapshot();
            string configuredHotkey = cachedCopy?.Hotkeys.App.ShowApp?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(configuredHotkey))
            {
                return null;
            }

            HotkeyRegistrationOutcome outcome = _hotkeys.GetLastRegistrationOutcome(AppConstants.Hotkeys.ShowAppHotkeyId);
            return outcome.Kind is HotkeyRegistrationOutcomeKind.Registered or HotkeyRegistrationOutcomeKind.Fallback
                ? configuredHotkey
                : null;
        }


        public Task RefreshDevicesForHotplugAsync()
        {
            return RefreshDevicesAsync(
                promptOnPotentialOverwrite: false,
                refreshMixerWhenWindowHidden: false,
                checkSettingsFileChanges: false);
        }




        /// <summary>
        /// Handles session-created notifications and triggers a debounced mixer refresh when the window is visible.
        /// </summary>
        /// <remarks>
        /// The mixer is intentionally not refreshed while hidden/tray-minimized to avoid unnecessary background churn.
        /// </remarks>




        internal SessionCreatedMixerRefreshDrainResult DrainPendingMixerRefreshSignals(MixerRefreshTarget requestedTarget)
        {
            if (requestedTarget == MixerRefreshTarget.Output)
            {
                int outputTargetSignals = Interlocked.Exchange(ref _pendingOutputSessionCreatedSignals, 0)
                    + Interlocked.Exchange(ref _pendingOutputSessionLifecycleSignals, 0)
                    + Interlocked.Exchange(ref _pendingShowWindowMixerRefreshSignals, 0);
                return new SessionCreatedMixerRefreshDrainResult(outputTargetSignals, MixerRefreshTarget.Output);
            }

            if (requestedTarget == MixerRefreshTarget.Input)
            {
                int inputTargetSignals = Interlocked.Exchange(ref _pendingInputSessionCreatedSignals, 0)
                    + Interlocked.Exchange(ref _pendingInputSessionLifecycleSignals, 0)
                    + Interlocked.Exchange(ref _pendingShowWindowMixerRefreshSignals, 0);
                return new SessionCreatedMixerRefreshDrainResult(inputTargetSignals, MixerRefreshTarget.Input);
            }

            int outputSignals = Interlocked.Exchange(ref _pendingOutputSessionCreatedSignals, 0)
                + Interlocked.Exchange(ref _pendingOutputSessionLifecycleSignals, 0);
            int inputSignals = Interlocked.Exchange(ref _pendingInputSessionCreatedSignals, 0)
                + Interlocked.Exchange(ref _pendingInputSessionLifecycleSignals, 0);
            int showWindowSignals = Interlocked.Exchange(ref _pendingShowWindowMixerRefreshSignals, 0);
            int totalSignals = outputSignals + inputSignals + showWindowSignals;

            MixerRefreshTarget target = showWindowSignals > 0 || (outputSignals > 0 && inputSignals > 0)
                ? MixerRefreshTarget.Both
                : outputSignals > 0
                    ? MixerRefreshTarget.Output
                    : inputSignals > 0
                        ? MixerRefreshTarget.Input
                        : MixerRefreshTarget.Both;

            return new SessionCreatedMixerRefreshDrainResult(totalSignals, target);
        }

        internal static int DrainPendingMixerRefreshSignals(
            ref int pendingSessionCreatedSignals,
            ref int pendingSessionLifecycleSignals,
            ref int pendingShowWindowMixerRefreshSignals)
        {
            int totalSignals = 0;
            totalSignals += Interlocked.Exchange(ref pendingSessionCreatedSignals, 0);
            totalSignals += Interlocked.Exchange(ref pendingSessionLifecycleSignals, 0);
            totalSignals += Interlocked.Exchange(ref pendingShowWindowMixerRefreshSignals, 0);
            return totalSignals;
        }

        /// <summary>
        /// Atomically replaces the active session-refresh debounce token source.
        /// </summary>
        internal static CancellationTokenSource? SwapSessionRefreshDebounce(
            ref CancellationTokenSource? current,
            CancellationTokenSource next)
        {
            return Interlocked.Exchange(ref current, next);
        }

        internal static CancellationTokenSource? CancelAndDetachDebounce(ref CancellationTokenSource? current)
        {
            CancellationTokenSource? detached = Interlocked.Exchange(ref current, null);
            detached?.Cancel();
            return detached;
        }

        internal static void ReleaseOwnedDebounce(ref CancellationTokenSource? current, CancellationTokenSource ownedDebounce)
        {
            Interlocked.CompareExchange(ref current, null, ownedDebounce);
            ownedDebounce.Dispose();
        }

















































        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

            if (ShouldQueueAutoSaveForProperty(name))
            {
                QueueAutoSave(name);
            }
        }

        private static bool ShouldQueueAutoSaveForProperty(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            if (propertyName.EndsWith("Draft", StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(propertyName, nameof(Theme), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(RunAtStartup), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(OutputHotkeysEnabled), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(InputHotkeysEnabled), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(Hotkey), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(OutputReverseHotkey), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(InputHotkey), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(InputReverseHotkey), StringComparison.Ordinal);
        }





















        public bool IsRefreshing => _refreshCoordinator.IsRefreshing;
    }
}
