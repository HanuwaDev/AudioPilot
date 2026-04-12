using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using AudioPilot.Helpers;
using AudioPilot.ViewModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace AudioPilot.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum RoutineTriggerKind
    {
        Hotkey,
        AppStartup,
        AudioPilotStartup,
        SteamBigPicture,
        DeviceChange,
    }

    public enum RoutineLastRunState
    {
        Never,
        Succeeded,
        Failed,
        WaitingForApp,
        Skipped,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum RoutineTimingPreset
    {
        Automatic,
        Fast,
        Balanced,
        Reliable,
        Custom,
    }

    public sealed class AudioRoutine : INotifyPropertyChanged
    {
        public readonly record struct RoutineTimingProfile(
            int ExecutionDelayMs,
            int CooldownSeconds,
            int TriggerAppStableForMs);

        private string _id = Guid.NewGuid().ToString("N");
        private string _name = string.Empty;
        private bool _enabled = true;
        private int _displayOrder = 1;
        private string _outputDeviceId = string.Empty;
        private string _outputDeviceName = string.Empty;
        private string _inputDeviceId = string.Empty;
        private string _inputDeviceName = string.Empty;
        private int? _masterVolumePercent;
        private int? _micVolumePercent;
        private string _hotkey = string.Empty;
        private RoutineTriggerKind _triggerKind;
        private string _triggerAppPath = string.Empty;
        private bool _switchOutputPerApp;
        private bool _showInTrayMenu;
        private bool _restorePreviousAudioOnDeactivate;
        private bool _enforceTargetsOnDeviceChange;
        private int _executionDelayMs;
        private int _cooldownSeconds;
        private int _triggerAppStableForMs;
        private bool _hasConflict;
        private string _conflictSummary = string.Empty;
        private HotkeyWarningKind _hotkeyWarningKind;
        private string _hotkeyWarningSummary = string.Empty;
        private DateTimeOffset? _lastRunUtc;
        private RoutineLastRunState _lastRunState;
        private string _lastRunDetail = string.Empty;

        public string Id
        {
            get => _id;
            set => SetField(ref _id, value);
        }

        public string Name
        {
            get => _name;
            set
            {
                if (!SetField(ref _name, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(TargetSummary));
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (!SetField(ref _enabled, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(HasLastRunBadge));
            }
        }

        [JsonIgnore]
        public int DisplayOrder
        {
            get => _displayOrder;
            set
            {
                if (!SetField(ref _displayOrder, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(DisplayName));
            }
        }

        public string OutputDeviceId
        {
            get => _outputDeviceId;
            set
            {
                if (!SetField(ref _outputDeviceId, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasOutputTarget));
                OnPropertyChanged(nameof(TargetKindBadgeText));
                OnPropertyChanged(nameof(TargetSummary));
            }
        }

        public string OutputDeviceName
        {
            get => _outputDeviceName;
            set
            {
                if (!SetField(ref _outputDeviceName, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(TargetSummary));
            }
        }

        public string InputDeviceId
        {
            get => _inputDeviceId;
            set
            {
                if (!SetField(ref _inputDeviceId, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasInputTarget));
                OnPropertyChanged(nameof(TargetKindBadgeText));
                OnPropertyChanged(nameof(TargetSummary));
            }
        }

        public string InputDeviceName
        {
            get => _inputDeviceName;
            set
            {
                if (!SetField(ref _inputDeviceName, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(TargetSummary));
            }
        }

        public int? MasterVolumePercent
        {
            get => _masterVolumePercent;
            set
            {
                int? normalized = value.HasValue ? Math.Clamp(value.Value, 0, 100) : null;
                if (!SetField(ref _masterVolumePercent, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasMasterVolumeTarget));
                OnPropertyChanged(nameof(HasVolumeTarget));
                OnPropertyChanged(nameof(TargetKindBadgeText));
                OnPropertyChanged(nameof(TargetSummary));
            }
        }

        public int? MicVolumePercent
        {
            get => _micVolumePercent;
            set
            {
                int? normalized = value.HasValue ? Math.Clamp(value.Value, 0, 100) : null;
                if (!SetField(ref _micVolumePercent, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasMicVolumeTarget));
                OnPropertyChanged(nameof(HasVolumeTarget));
                OnPropertyChanged(nameof(TargetKindBadgeText));
                OnPropertyChanged(nameof(TargetSummary));
            }
        }

        public string Hotkey
        {
            get => _hotkey;
            set
            {
                if (!SetField(ref _hotkey, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(TriggerSummary));
            }
        }

        public RoutineTriggerKind TriggerKind
        {
            get => _triggerKind;
            set
            {
                if (!SetField(ref _triggerKind, value))
                {
                    return;
                }

                NormalizeTriggerDependentState();

                if (_triggerKind is not RoutineTriggerKind.AppStartup and not RoutineTriggerKind.SteamBigPicture)
                {
                    if (_restorePreviousAudioOnDeactivate)
                    {
                        _restorePreviousAudioOnDeactivate = false;
                        OnPropertyChanged(nameof(RestorePreviousAudioOnDeactivate));
                    }
                }

                OnPropertyChanged(nameof(TriggerOnAppStart));
                OnPropertyChanged(nameof(HasAppStartTrigger));
                OnPropertyChanged(nameof(HasAudioPilotStartupTrigger));
                OnPropertyChanged(nameof(HasSteamBigPictureTrigger));
                OnPropertyChanged(nameof(HasDeviceChangeTrigger));
                OnPropertyChanged(nameof(IsStatefulTrigger));
                OnPropertyChanged(nameof(HasTimingOptions));
                OnPropertyChanged(nameof(TimingSummary));
                OnPropertyChanged(nameof(TriggerSummary));
            }
        }

        /// <summary>
        /// Clears trigger-dependent fields that are no longer valid for the current trigger kind.
        /// </summary>
        /// <remarks>
        /// Trigger kind is the canonical owner of these dependent settings so persisted config, editor state,
        /// and future migrations converge on one routine shape.
        /// </remarks>
        private static bool UsesFixedAutomaticTiming(RoutineTriggerKind triggerKind)
        {
            return triggerKind is RoutineTriggerKind.DeviceChange or RoutineTriggerKind.SteamBigPicture;
        }

        private void NormalizeTriggerDependentState()
        {
            if (_triggerKind != RoutineTriggerKind.AppStartup && !string.IsNullOrWhiteSpace(_triggerAppPath))
            {
                _triggerAppPath = string.Empty;
                OnPropertyChanged(nameof(TriggerAppPath));
            }

            if (_triggerKind != RoutineTriggerKind.AppStartup && _switchOutputPerApp)
            {
                _switchOutputPerApp = false;
                OnPropertyChanged(nameof(SwitchOutputPerApp));
            }

            if (_triggerKind != RoutineTriggerKind.DeviceChange && _enforceTargetsOnDeviceChange)
            {
                _enforceTargetsOnDeviceChange = false;
                OnPropertyChanged(nameof(EnforceTargetsOnDeviceChange));
            }

            if (_triggerKind != RoutineTriggerKind.AppStartup && _triggerAppStableForMs != 0)
            {
                _triggerAppStableForMs = 0;
                OnPropertyChanged(nameof(TriggerAppStableForMs));
            }

            if (_triggerKind != RoutineTriggerKind.Hotkey)
            {
                if (_showInTrayMenu)
                {
                    _showInTrayMenu = false;
                    OnPropertyChanged(nameof(ShowInTrayMenu));
                    OnPropertyChanged(nameof(HasTrayMenuTrigger));
                }

                if (_triggerKind == RoutineTriggerKind.AudioPilotStartup && _executionDelayMs != 0)
                {
                    _executionDelayMs = 0;
                    OnPropertyChanged(nameof(ExecutionDelayMs));
                }

                if (_triggerKind == RoutineTriggerKind.AudioPilotStartup && _cooldownSeconds != 0)
                {
                    _cooldownSeconds = 0;
                    OnPropertyChanged(nameof(CooldownSeconds));
                }

                if (UsesFixedAutomaticTiming(_triggerKind))
                {
                    RoutineTimingProfile profile = GetRecommendedTimingProfile(_triggerKind, RoutineTimingPreset.Automatic);
                    if (_executionDelayMs != profile.ExecutionDelayMs)
                    {
                        _executionDelayMs = profile.ExecutionDelayMs;
                        OnPropertyChanged(nameof(ExecutionDelayMs));
                    }

                    if (_cooldownSeconds != profile.CooldownSeconds)
                    {
                        _cooldownSeconds = profile.CooldownSeconds;
                        OnPropertyChanged(nameof(CooldownSeconds));
                    }
                }
            }
        }

        [JsonIgnore]
        public bool TriggerOnAppStart
        {
            get => TriggerKind == RoutineTriggerKind.AppStartup;
            set
            {
                if (value)
                {
                    TriggerKind = RoutineTriggerKind.AppStartup;
                    return;
                }

                if (TriggerKind == RoutineTriggerKind.AppStartup)
                {
                    TriggerKind = RoutineTriggerKind.Hotkey;
                }
            }
        }

        public string TriggerAppPath
        {
            get => _triggerAppPath;
            set
            {
                string normalized = TriggerKind == RoutineTriggerKind.AppStartup
                    ? RoutineTriggerPathHelper.NormalizeTriggerTarget(value)
                    : string.Empty;
                if (!SetField(ref _triggerAppPath, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(TriggerSummary));
            }
        }

        public bool SwitchOutputPerApp
        {
            get => _switchOutputPerApp;
            set
            {
                bool normalized = value && TriggerKind == RoutineTriggerKind.AppStartup;
                if (!SetField(ref _switchOutputPerApp, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(TriggerSummary));
            }
        }

        public bool RestorePreviousAudioOnDeactivate
        {
            get => _restorePreviousAudioOnDeactivate;
            set
            {
                bool normalized = value && IsStatefulTrigger;
                if (!SetField(ref _restorePreviousAudioOnDeactivate, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(TriggerSummary));
            }
        }

        public bool EnforceTargetsOnDeviceChange
        {
            get => _enforceTargetsOnDeviceChange;
            set
            {
                bool normalized = value;
                if (!SetField(ref _enforceTargetsOnDeviceChange, normalized))
                {
                    return;
                }
            }
        }

        public int ExecutionDelayMs
        {
            get => _executionDelayMs;
            set
            {
                int normalized = TriggerKind == RoutineTriggerKind.AudioPilotStartup
                    ? 0
                    : UsesFixedAutomaticTiming(TriggerKind)
                        ? GetRecommendedTimingProfile(TriggerKind, RoutineTimingPreset.Automatic).ExecutionDelayMs
                        : Math.Max(0, value);
                if (!SetField(ref _executionDelayMs, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasTimingOptions));
                OnPropertyChanged(nameof(TimingSummary));
            }
        }

        public int CooldownSeconds
        {
            get => _cooldownSeconds;
            set
            {
                int normalized = TriggerKind == RoutineTriggerKind.AudioPilotStartup
                    ? 0
                    : UsesFixedAutomaticTiming(TriggerKind)
                        ? GetRecommendedTimingProfile(TriggerKind, RoutineTimingPreset.Automatic).CooldownSeconds
                        : Math.Max(0, value);
                if (!SetField(ref _cooldownSeconds, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasTimingOptions));
                OnPropertyChanged(nameof(TimingSummary));
            }
        }

        public int TriggerAppStableForMs
        {
            get => _triggerAppStableForMs;
            set
            {
                int normalized = TriggerKind == RoutineTriggerKind.AppStartup
                    ? Math.Max(0, value)
                    : 0;
                if (!SetField(ref _triggerAppStableForMs, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasTimingOptions));
                OnPropertyChanged(nameof(TimingSummary));
            }
        }

        public bool ShowInTrayMenu
        {
            get => _showInTrayMenu;
            set
            {
                bool normalized = TriggerKind == RoutineTriggerKind.Hotkey && value;
                if (!SetField(ref _showInTrayMenu, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(TriggerSummary));
                OnPropertyChanged(nameof(HasTrayMenuTrigger));
            }
        }

        [JsonIgnore]
        public bool HasOutputTarget => !string.IsNullOrWhiteSpace(OutputDeviceId);

        [JsonIgnore]
        public bool HasInputTarget => !string.IsNullOrWhiteSpace(InputDeviceId);

        [JsonIgnore]
        public bool HasMasterVolumeTarget => MasterVolumePercent.HasValue;

        [JsonIgnore]
        public bool HasMicVolumeTarget => MicVolumePercent.HasValue;

        [JsonIgnore]
        public bool HasVolumeTarget => HasMasterVolumeTarget || HasMicVolumeTarget;

        [JsonIgnore]
        public bool HasExecutionTarget => HasOutputTarget || HasInputTarget || HasVolumeTarget;

        [JsonIgnore]
        public bool HasAppStartTrigger => TriggerOnAppStart && !string.IsNullOrWhiteSpace(TriggerAppPath);

        [JsonIgnore]
        public bool HasAudioPilotStartupTrigger => TriggerKind == RoutineTriggerKind.AudioPilotStartup;

        [JsonIgnore]
        public bool HasSteamBigPictureTrigger => TriggerKind == RoutineTriggerKind.SteamBigPicture;

        [JsonIgnore]
        public bool HasDeviceChangeTrigger => TriggerKind == RoutineTriggerKind.DeviceChange;

        [JsonIgnore]
        public bool IsStatefulTrigger => TriggerKind is RoutineTriggerKind.AppStartup or RoutineTriggerKind.SteamBigPicture;

        [JsonIgnore]
        public bool HasTrayMenuTrigger => ShowInTrayMenu;

        [JsonIgnore]
        public bool HasTimingOptions =>
            ExecutionDelayMs > 0 ||
            CooldownSeconds > 0 ||
            (TriggerKind == RoutineTriggerKind.AppStartup && TriggerAppStableForMs > 0);

        [JsonIgnore]
        public RoutineTimingPreset TimingPreset => InferTimingPreset(TriggerKind, ExecutionDelayMs, CooldownSeconds, TriggerAppStableForMs);

        [JsonIgnore]
        public string TimingPresetLabel => GetTimingPresetLabel(TimingPreset);

        [JsonIgnore]
        public string DisplayName => $"{DisplayOrder}. {Name}";

        [JsonIgnore]
        public string StatusLabel => Enabled ? "Enabled" : "Disabled";

        [JsonIgnore]
        public bool HasConflict
        {
            get => _hasConflict;
            set => SetField(ref _hasConflict, value);
        }

        [JsonIgnore]
        public string ConflictSummary
        {
            get => _conflictSummary;
            set => SetField(ref _conflictSummary, value ?? string.Empty);
        }

        [JsonIgnore]
        public HotkeyWarningKind HotkeyWarningKind
        {
            get => _hotkeyWarningKind;
            set
            {
                if (!SetField(ref _hotkeyWarningKind, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasHotkeyWarning));
            }
        }

        [JsonIgnore]
        public bool HasHotkeyWarning => HotkeyWarningKind != HotkeyWarningKind.None;

        [JsonIgnore]
        public string HotkeyWarningSummary
        {
            get => _hotkeyWarningSummary;
            set => SetField(ref _hotkeyWarningSummary, value ?? string.Empty);
        }

        [JsonIgnore]
        public DateTimeOffset? LastRunUtc
        {
            get => _lastRunUtc;
            set
            {
                if (!SetField(ref _lastRunUtc, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(LastRunStatusText));
            }
        }

        [JsonIgnore]
        public RoutineLastRunState LastRunState
        {
            get => _lastRunState;
            set
            {
                if (!SetField(ref _lastRunState, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(LastRunStatusText));
                OnPropertyChanged(nameof(HasLastRunBadge));
                OnPropertyChanged(nameof(LastRunBadgeText));
            }
        }

        [JsonIgnore]
        public string LastRunDetail
        {
            get => _lastRunDetail;
            set
            {
                if (!SetField(ref _lastRunDetail, value ?? string.Empty))
                {
                    return;
                }

                OnPropertyChanged(nameof(LastRunStatusText));
            }
        }

        [JsonIgnore]
        public string LastRunStatusText => BuildLastRunStatusText(DateTimeOffset.UtcNow);

        [JsonIgnore]
        public bool HasLastRunBadge => Enabled && LastRunState != RoutineLastRunState.Never;

        [JsonIgnore]
        public string LastRunBadgeText => LastRunState switch
        {
            RoutineLastRunState.Succeeded => "Ran",
            RoutineLastRunState.Failed => "Failed",
            RoutineLastRunState.WaitingForApp => "Waiting",
            RoutineLastRunState.Skipped => "Skipped",
            _ => string.Empty,
        };

        [JsonIgnore]
        public string TriggerSummary
        {
            get
            {
                var triggers = new List<string>();

                if (HasAppStartTrigger)
                {
                    triggers.Add($"Application start: {RoutineTriggerPathHelper.GetTriggerDisplayName(TriggerAppPath)}");

                    if (SwitchOutputPerApp && (HasOutputTarget || HasInputTarget))
                    {
                        triggers.Add("Application audio only");
                    }
                }
                else if (HasAudioPilotStartupTrigger)
                {
                    triggers.Add("AudioPilot startup");
                }
                else if (HasSteamBigPictureTrigger)
                {
                    triggers.Add("Steam Big Picture");
                }
                else if (HasDeviceChangeTrigger)
                {
                    triggers.Add("Device change");
                }
                else if (!string.IsNullOrWhiteSpace(Hotkey))
                {
                    triggers.Add($"Hotkey: {Hotkey}");
                }

                if (RestorePreviousAudioOnDeactivate && IsStatefulTrigger)
                {
                    triggers.Add("Restore on exit");
                }

                if (HasTrayMenuTrigger)
                {
                    triggers.Add("Tray menu");
                }

                return triggers.Count > 0
                    ? string.Join(" | ", triggers)
                    : "No triggers configured";
            }
        }

        [JsonIgnore]
        public string TimingSummary
        {
            get
            {
                var items = new List<string>();

                if (ExecutionDelayMs > 0)
                {
                    items.Add($"Delay: {ExecutionDelayMs.ToString(CultureInfo.InvariantCulture)} ms");
                }

                if (CooldownSeconds > 0)
                {
                    items.Add($"Cooldown: {CooldownSeconds.ToString(CultureInfo.InvariantCulture)} s");
                }

                if (TriggerKind == RoutineTriggerKind.AppStartup && TriggerAppStableForMs > 0)
                {
                    items.Add($"App stable: {TriggerAppStableForMs.ToString(CultureInfo.InvariantCulture)} ms");
                }

                return items.Count > 0
                    ? string.Join(" | ", items)
                    : "No timing controls configured";
            }
        }

        public static RoutineTimingProfile GetRecommendedTimingProfile(RoutineTriggerKind triggerKind, RoutineTimingPreset preset)
        {
            if (preset == RoutineTimingPreset.Custom)
            {
                return new RoutineTimingProfile(0, 0, 0);
            }

            return triggerKind switch
            {
                RoutineTriggerKind.AppStartup => preset switch
                {
                    RoutineTimingPreset.Automatic => new RoutineTimingProfile(200, 10, 600),
                    RoutineTimingPreset.Fast => new RoutineTimingProfile(50, 3, 250),
                    RoutineTimingPreset.Balanced => new RoutineTimingProfile(250, 15, 800),
                    RoutineTimingPreset.Reliable => new RoutineTimingProfile(500, 30, 1500),
                    _ => new RoutineTimingProfile(0, 0, 0),
                },
                RoutineTriggerKind.DeviceChange => preset switch
                {
                    RoutineTimingPreset.Automatic => new RoutineTimingProfile(150, 5, 0),
                    RoutineTimingPreset.Fast => new RoutineTimingProfile(0, 1, 0),
                    RoutineTimingPreset.Balanced => new RoutineTimingProfile(125, 10, 0),
                    RoutineTimingPreset.Reliable => new RoutineTimingProfile(250, 20, 0),
                    _ => new RoutineTimingProfile(0, 0, 0),
                },
                RoutineTriggerKind.SteamBigPicture => preset switch
                {
                    RoutineTimingPreset.Automatic => new RoutineTimingProfile(0, 5, 0),
                    RoutineTimingPreset.Fast => new RoutineTimingProfile(0, 1, 0),
                    RoutineTimingPreset.Balanced => new RoutineTimingProfile(125, 10, 0),
                    RoutineTimingPreset.Reliable => new RoutineTimingProfile(250, 20, 0),
                    _ => new RoutineTimingProfile(0, 0, 0),
                },
                _ => preset switch
                {
                    RoutineTimingPreset.Automatic => new RoutineTimingProfile(0, 0, 0),
                    RoutineTimingPreset.Fast => new RoutineTimingProfile(0, 1, 0),
                    RoutineTimingPreset.Balanced => new RoutineTimingProfile(125, 10, 0),
                    RoutineTimingPreset.Reliable => new RoutineTimingProfile(250, 20, 0),
                    _ => new RoutineTimingProfile(0, 0, 0),
                },
            };
        }

        public static RoutineTimingPreset InferTimingPreset(
            RoutineTriggerKind triggerKind,
            int executionDelayMs,
            int cooldownSeconds,
            int triggerAppStableForMs)
        {
            int normalizedDelay = Math.Max(0, executionDelayMs);
            int normalizedCooldown = Math.Max(0, cooldownSeconds);
            int normalizedAppStable = triggerKind == RoutineTriggerKind.AppStartup
                ? Math.Max(0, triggerAppStableForMs)
                : 0;

            foreach (RoutineTimingPreset preset in new[]
            {
                RoutineTimingPreset.Automatic,
                RoutineTimingPreset.Fast,
                RoutineTimingPreset.Balanced,
                RoutineTimingPreset.Reliable,
            })
            {
                RoutineTimingProfile profile = GetRecommendedTimingProfile(triggerKind, preset);
                if (profile.ExecutionDelayMs == normalizedDelay &&
                    profile.CooldownSeconds == normalizedCooldown &&
                    profile.TriggerAppStableForMs == normalizedAppStable)
                {
                    return preset;
                }
            }

            return RoutineTimingPreset.Custom;
        }

        public static string GetTimingPresetLabel(RoutineTimingPreset preset)
        {
            return preset switch
            {
                RoutineTimingPreset.Automatic => "Automatic",
                RoutineTimingPreset.Fast => "Fast",
                RoutineTimingPreset.Balanced => "Balanced",
                RoutineTimingPreset.Reliable => "Reliable",
                _ => "Custom",
            };
        }

        [JsonIgnore]
        public string TargetKindBadgeText => (HasOutputTarget, HasInputTarget, HasVolumeTarget) switch
        {
            (true, true, _) => "Out + In",
            (true, false, _) => "Out",
            (false, true, _) => "In",
            (false, false, true) => "Vol",
            _ => string.Empty,
        };

        [JsonIgnore]
        public string TargetSummary
        {
            get
            {
                var parts = new List<string>();

                if (HasOutputTarget)
                {
                    parts.Add($"Output: {OutputDeviceName}");
                }

                if (HasInputTarget)
                {
                    parts.Add($"Input: {InputDeviceName}");
                }

                if (HasMasterVolumeTarget)
                {
                    parts.Add($"Master: {MasterVolumePercent.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)}%");
                }

                if (HasMicVolumeTarget)
                {
                    parts.Add($"Microphone: {MicVolumePercent.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)}%");
                }

                return parts.Count > 0
                    ? string.Join(" | ", parts)
                    : string.Empty;
            }
        }

        public AudioRoutine Clone()
        {
            return new AudioRoutine
            {
                Id = Id,
                Name = Name,
                Enabled = Enabled,
                OutputDeviceId = OutputDeviceId,
                OutputDeviceName = OutputDeviceName,
                InputDeviceId = InputDeviceId,
                InputDeviceName = InputDeviceName,
                MasterVolumePercent = MasterVolumePercent,
                MicVolumePercent = MicVolumePercent,
                Hotkey = Hotkey,
                TriggerKind = TriggerKind,
                TriggerAppPath = TriggerAppPath,
                SwitchOutputPerApp = SwitchOutputPerApp,
                ShowInTrayMenu = ShowInTrayMenu,
                RestorePreviousAudioOnDeactivate = RestorePreviousAudioOnDeactivate,
                EnforceTargetsOnDeviceChange = EnforceTargetsOnDeviceChange,
                ExecutionDelayMs = ExecutionDelayMs,
                CooldownSeconds = CooldownSeconds,
                TriggerAppStableForMs = TriggerAppStableForMs,
                DisplayOrder = DisplayOrder,
                HotkeyWarningKind = HotkeyWarningKind,
                HotkeyWarningSummary = HotkeyWarningSummary,
            };
        }

        public void RefreshLastRunStatusText()
        {
            OnPropertyChanged(nameof(LastRunStatusText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private string BuildLastRunStatusText(DateTimeOffset now)
        {
            return LastRunState switch
            {
                RoutineLastRunState.WaitingForApp => "Last run: Waiting for app",
                RoutineLastRunState.Never => "Last run: Never",
                _ => BuildCompletedLastRunStatusText(now),
            };
        }

        private string BuildCompletedLastRunStatusText(DateTimeOffset now)
        {
            string relativeTime = LastRunUtc.HasValue
                ? FormatRelativeTime(now - LastRunUtc.Value)
                : "recently";
            string statusSuffix = LastRunState switch
            {
                RoutineLastRunState.Succeeded => string.Empty,
                RoutineLastRunState.Failed => "Failed",
                RoutineLastRunState.Skipped => string.IsNullOrWhiteSpace(LastRunDetail) ? "Skipped" : LastRunDetail,
                _ => string.IsNullOrWhiteSpace(LastRunDetail) ? string.Empty : LastRunDetail,
            };

            return string.IsNullOrWhiteSpace(statusSuffix)
                ? $"Last run: {relativeTime}"
                : $"Last run: {relativeTime}, {statusSuffix}";
        }

        private static string FormatRelativeTime(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            if (elapsed < TimeSpan.FromSeconds(5))
            {
                return "just now";
            }

            if (elapsed < TimeSpan.FromMinutes(1))
            {
                return $"{Math.Max(1, (int)Math.Floor(elapsed.TotalSeconds)).ToString(CultureInfo.InvariantCulture)}s ago";
            }

            if (elapsed < TimeSpan.FromHours(1))
            {
                return $"{Math.Max(1, (int)Math.Floor(elapsed.TotalMinutes)).ToString(CultureInfo.InvariantCulture)}m ago";
            }

            if (elapsed < TimeSpan.FromDays(1))
            {
                return $"{Math.Max(1, (int)Math.Floor(elapsed.TotalHours)).ToString(CultureInfo.InvariantCulture)}h ago";
            }

            return $"{Math.Max(1, (int)Math.Floor(elapsed.TotalDays)).ToString(CultureInfo.InvariantCulture)}d ago";
        }
    }
}
