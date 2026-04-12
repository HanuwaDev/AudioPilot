using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    internal enum RoutineEditorTriggerMode
    {
        Hotkey,
        AppStartup,
        AudioPilotStartup,
        SteamBigPicture,
        DeviceChange,
    }

    internal sealed class RoutineEditorViewModel : INotifyPropertyChanged
    {
        internal const int MaxRoutineNameLength = 25;
        private static readonly IReadOnlyList<string> TriggerModeLabels = ["Hotkey", "Application startup", "AudioPilot startup", "Device change", "Steam Big Picture"];
        private static readonly IReadOnlyList<string> TimingPresetLabels =
        [
            AudioRoutine.GetTimingPresetLabel(RoutineTimingPreset.Automatic),
            AudioRoutine.GetTimingPresetLabel(RoutineTimingPreset.Fast),
            AudioRoutine.GetTimingPresetLabel(RoutineTimingPreset.Balanced),
            AudioRoutine.GetTimingPresetLabel(RoutineTimingPreset.Reliable),
            AudioRoutine.GetTimingPresetLabel(RoutineTimingPreset.Custom),
        ];

        private readonly bool _enabled;
        private readonly string _existingId;
        private readonly int _existingDisplayOrder;
        private readonly bool _isEditingExistingRoutine;
        private readonly IReadOnlyList<string> _availableTriggerModes = TriggerModeLabels;
        private readonly IReadOnlyList<string> _availableTimingPresets = TimingPresetLabels;
        private readonly HashSet<string> _reservedHotkeyKeys;
        private readonly IReadOnlyList<string> _additionalStandaloneHotkeyKeys;
        private string _name = string.Empty;
        private int _selectedOutputIndex = -1;
        private int _selectedInputIndex = -1;
        private string _selectedTriggerMode = TriggerModeLabels[0];
        private string _triggerAppPath = string.Empty;
        private bool _switchOutputPerApp;
        private bool _showInTrayMenu;
        private bool _restorePreviousAudioOnDeactivate;
        private string _masterVolumePercentText = string.Empty;
        private string _micVolumePercentText = string.Empty;
        private bool _isVolumeTargetsExpanded;
        private string _executionDelayMsText = "0";
        private string _cooldownSecondsText = "0";
        private string _triggerAppStableForMsText = "0";
        private string _selectedTimingPreset = TimingPresetLabels[0];
        private string _customExecutionDelayMsText = "0";
        private string _customCooldownSecondsText = "0";
        private string _customTriggerAppStableForMsText = "0";
        private string _resolvedPackagedAppDisplayName = string.Empty;

        public RoutineEditorViewModel(
            IEnumerable<CycleDevice> outputDevices,
            IEnumerable<CycleDevice> inputDevices,
            AudioRoutine? existingRoutine = null,
            string? suggestedName = null,
            IEnumerable<string>? reservedHotkeyKeys = null,
            IEnumerable<string>? additionalStandaloneHotkeyKeys = null)
        {
            OutputDevices.Add(new CycleDevice { Id = string.Empty, Name = "No output device" });
            foreach (CycleDevice device in outputDevices)
            {
                OutputDevices.Add(new CycleDevice { Id = device.Id, Name = device.Name });
            }

            InputDevices.Add(new CycleDevice { Id = string.Empty, Name = "No input device" });
            foreach (CycleDevice device in inputDevices)
            {
                InputDevices.Add(new CycleDevice { Id = device.Id, Name = device.Name });
            }

            EditorHotkey = new HotkeyViewModel
            {
                BaseHoverText = "Required when trigger mode is Hotkey. Press the shortcut you want to assign, or press Delete to clear it.",
            };
            EditorHotkey.UpdateAdditionalStandaloneHotkeyKeys(additionalStandaloneHotkeyKeys);
            _additionalStandaloneHotkeyKeys = [.. HotkeyStandaloneKeyPolicy.Analyze(additionalStandaloneHotkeyKeys).EffectiveTokens];
            _reservedHotkeyKeys = reservedHotkeyKeys == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(reservedHotkeyKeys, StringComparer.OrdinalIgnoreCase);
            _enabled = existingRoutine?.Enabled ?? true;
            _existingId = existingRoutine?.Id ?? Guid.NewGuid().ToString("N");
            _existingDisplayOrder = existingRoutine?.DisplayOrder ?? 1;
            _isEditingExistingRoutine = existingRoutine != null;

            if (TryNormalizeHotkey(existingRoutine?.Hotkey, additionalStandaloneHotkeyKeys, out string existingHotkey))
            {
                _reservedHotkeyKeys.Remove(existingHotkey);
            }

            if (existingRoutine?.HasHotkeyWarning == true)
            {
                EditorHotkey.SetRegistrationWarning(existingRoutine.HotkeyWarningKind, existingRoutine.HotkeyWarningSummary);
            }

            if (existingRoutine == null)
            {
                Name = string.IsNullOrWhiteSpace(suggestedName)
                    ? "Routine 1"
                    : suggestedName.Trim();
                SelectedOutputIndex = 0;
                SelectedInputIndex = 0;
                return;
            }

            Name = existingRoutine.Name;
            SelectedOutputIndex = ResolveSelectedIndex(OutputDevices, existingRoutine.OutputDeviceId);
            SelectedInputIndex = ResolveSelectedIndex(InputDevices, existingRoutine.InputDeviceId);
            EditorHotkey.LoadFromString(existingRoutine.Hotkey);
            SelectedTriggerMode = existingRoutine.TriggerKind switch
            {
                RoutineTriggerKind.AppStartup => TriggerModeLabels[1],
                RoutineTriggerKind.AudioPilotStartup => TriggerModeLabels[2],
                RoutineTriggerKind.DeviceChange => TriggerModeLabels[3],
                RoutineTriggerKind.SteamBigPicture => TriggerModeLabels[4],
                _ when existingRoutine.EnforceTargetsOnDeviceChange => TriggerModeLabels[3],
                _ => TriggerModeLabels[0],
            };
            TriggerAppPath = existingRoutine.TriggerAppPath;
            SwitchOutputPerApp = existingRoutine.SwitchOutputPerApp;
            ShowInTrayMenu = existingRoutine.ShowInTrayMenu;
            RestorePreviousAudioOnDeactivate = existingRoutine.RestorePreviousAudioOnDeactivate;
            MasterVolumePercentText = FormatOptionalPercent(existingRoutine.MasterVolumePercent);
            MicVolumePercentText = FormatOptionalPercent(existingRoutine.MicVolumePercent);
            ExecutionDelayMsText = existingRoutine.ExecutionDelayMs.ToString(CultureInfo.InvariantCulture);
            CooldownSecondsText = existingRoutine.CooldownSeconds.ToString(CultureInfo.InvariantCulture);
            TriggerAppStableForMsText = existingRoutine.TriggerAppStableForMs.ToString(CultureInfo.InvariantCulture);
            RefreshVolumeTargetsExpansionState();

            if (existingRoutine.TimingPreset == RoutineTimingPreset.Custom)
            {
                RememberCustomTimingValues();
            }

            SelectedTimingPreset = existingRoutine.TimingPresetLabel;
            NormalizeTriggerSpecificEditorState();
        }

        public ObservableCollection<CycleDevice> OutputDevices { get; } = [];

        public ObservableCollection<CycleDevice> InputDevices { get; } = [];

        public HotkeyViewModel EditorHotkey { get; }

        public bool IsEditingExistingRoutine => _isEditingExistingRoutine;

        public string PrimaryActionLabel => IsEditingExistingRoutine ? "Update" : "Add";

        public bool CanSubmit => OutputDevices.Count > 1 || InputDevices.Count > 1;

        public string Name
        {
            get => _name;
            set
            {
                string normalized = NormalizeRoutineName(value);
                if (_name == normalized)
                {
                    return;
                }

                _name = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RoutineNameCharactersRemainingText));
            }
        }

        public string RoutineNameCharactersRemainingText
        {
            get
            {
                int remaining = Math.Max(0, MaxRoutineNameLength - Name.Length);
                return remaining == 1
                    ? "1 character remaining"
                    : $"{remaining} characters remaining";
            }
        }

        public int SelectedOutputIndex
        {
            get => _selectedOutputIndex;
            set
            {
                if (_selectedOutputIndex == value)
                {
                    return;
                }

                _selectedOutputIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSwitchOutputPerApp));
                if (!CanSwitchOutputPerApp)
                {
                    SwitchOutputPerApp = false;
                }
            }
        }

        public int SelectedInputIndex
        {
            get => _selectedInputIndex;
            set
            {
                if (_selectedInputIndex == value)
                {
                    return;
                }

                _selectedInputIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSwitchOutputPerApp));
                if (!CanSwitchOutputPerApp)
                {
                    SwitchOutputPerApp = false;
                }
            }
        }

        public IReadOnlyList<string> AvailableTriggerModes => _availableTriggerModes;

        public IReadOnlyList<string> AvailableTimingPresets => _availableTimingPresets;

        public string SelectedTriggerMode
        {
            get => _selectedTriggerMode;
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? TriggerModeLabels[0] : value.Trim();
                if (_selectedTriggerMode == normalized)
                {
                    return;
                }

                _selectedTriggerMode = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsHotkeyTriggerSelected));
                OnPropertyChanged(nameof(IsAppStartTriggerSelected));
                OnPropertyChanged(nameof(IsAudioPilotStartupTriggerSelected));
                OnPropertyChanged(nameof(IsSteamBigPictureTriggerSelected));
                OnPropertyChanged(nameof(IsDeviceChangeTriggerSelected));
                OnPropertyChanged(nameof(SupportsTimingControls));
                OnPropertyChanged(nameof(SupportsTrayMenuTrigger));
                OnPropertyChanged(nameof(IsStatefulTriggerSelected));
                OnPropertyChanged(nameof(CanSwitchOutputPerApp));
                OnPropertyChanged(nameof(HasResolvedTriggerAppTarget));
                OnPropertyChanged(nameof(ResolvedTriggerAppTargetText));
                if (!CanSwitchOutputPerApp)
                {
                    SwitchOutputPerApp = false;
                }

                if (!IsAppStartTriggerSelected)
                {
                    TriggerAppStableForMsText = "0";
                }

                if (!IsCustomTimingPresetSelected)
                {
                    ApplySelectedTimingPreset();
                }

                if (!IsStatefulTriggerSelected)
                {
                    RestorePreviousAudioOnDeactivate = false;
                }

                NormalizeTriggerSpecificEditorState();
            }
        }

        public bool IsHotkeyTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[0], StringComparison.Ordinal);

        public bool IsAppStartTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[1], StringComparison.Ordinal);

        public bool IsAudioPilotStartupTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[2], StringComparison.Ordinal);

        public bool IsDeviceChangeTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[3], StringComparison.Ordinal);

        public bool IsSteamBigPictureTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[4], StringComparison.Ordinal);

        public bool SupportsTimingControls => IsAppStartTriggerSelected;

        public bool SupportsTrayMenuTrigger => IsHotkeyTriggerSelected;

        public bool IsStatefulTriggerSelected => IsAppStartTriggerSelected || IsSteamBigPictureTriggerSelected;

        public bool CanSwitchOutputPerApp => IsAppStartTriggerSelected && (SelectedOutputIndex > 0 || SelectedInputIndex > 0);

        public bool CanSelectInputTarget => InputDevices.Count >= 0;

        public string MasterVolumePercentText
        {
            get => _masterVolumePercentText;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (_masterVolumePercentText == normalized)
                {
                    return;
                }

                _masterVolumePercentText = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasConfiguredVolumeTargets));
                RefreshVolumeTargetsExpansionState();
            }
        }

        public string MicVolumePercentText
        {
            get => _micVolumePercentText;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (_micVolumePercentText == normalized)
                {
                    return;
                }

                _micVolumePercentText = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasConfiguredVolumeTargets));
                RefreshVolumeTargetsExpansionState();
            }
        }

        public bool HasConfiguredVolumeTargets =>
            !string.IsNullOrWhiteSpace(MasterVolumePercentText) ||
            !string.IsNullOrWhiteSpace(MicVolumePercentText);

        public bool IsVolumeTargetsExpanded
        {
            get => _isVolumeTargetsExpanded;
            set
            {
                if (_isVolumeTargetsExpanded == value)
                {
                    return;
                }

                _isVolumeTargetsExpanded = value;
                OnPropertyChanged();
            }
        }

        public string SelectedTimingPreset
        {
            get => _selectedTimingPreset;
            set
            {
                string normalized = NormalizeTimingPresetLabel(value);
                if (_selectedTimingPreset == normalized)
                {
                    return;
                }

                bool wasCustom = IsCustomTimingPresetSelected;
                if (wasCustom)
                {
                    RememberCustomTimingValues();
                }

                _selectedTimingPreset = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCustomTimingPresetSelected));
                OnPropertyChanged(nameof(TimingPresetHelpText));

                if (IsCustomTimingPresetSelected)
                {
                    RestoreCustomTimingValues();
                }
                else
                {
                    ApplySelectedTimingPreset();
                }
            }
        }

        public bool IsCustomTimingPresetSelected => string.Equals(SelectedTimingPreset, AudioRoutine.GetTimingPresetLabel(RoutineTimingPreset.Custom), StringComparison.Ordinal);

        public string TimingPresetHelpText
        {
            get
            {
                RoutineTimingPreset preset = ParseTimingPresetLabel(SelectedTimingPreset);
                if (preset == RoutineTimingPreset.Custom)
                {
                    return IsAppStartTriggerSelected
                        ? "Timing controls help prevent startup routines from firing too early or repeating too often. Custom lets you set the delay and cooldown yourself, while startup settling stays managed internally."
                        : "Timing controls help prevent routines from firing too early or repeating too often. Custom lets you set the delay and cooldown yourself.";
                }

                AudioRoutine.RoutineTimingProfile profile = AudioRoutine.GetRecommendedTimingProfile(GetSelectedTriggerKind(), preset);
                var parts = new List<string>();
                if (profile.ExecutionDelayMs > 0)
                {
                    parts.Add($"delay {profile.ExecutionDelayMs} ms");
                }

                if (profile.CooldownSeconds > 0)
                {
                    parts.Add($"cooldown {profile.CooldownSeconds} s");
                }

                if (IsAppStartTriggerSelected && profile.TriggerAppStableForMs > 0)
                {
                    parts.Add("startup settling included");
                }

                string behaviorSummary = parts.Count > 0
                    ? string.Join(" | ", parts)
                    : "no extra waits";

                return $"Timing controls add a short delay before the routine runs and a cooldown before it can run again. {SelectedTimingPreset} uses {behaviorSummary}.";
            }
        }

        public string ExecutionDelayMsText
        {
            get => _executionDelayMsText;
            set
            {
                string normalized = value ?? string.Empty;
                if (_executionDelayMsText == normalized)
                {
                    return;
                }

                _executionDelayMsText = normalized;
                OnPropertyChanged();

                if (IsCustomTimingPresetSelected)
                {
                    _customExecutionDelayMsText = normalized;
                }
            }
        }

        public string CooldownSecondsText
        {
            get => _cooldownSecondsText;
            set
            {
                string normalized = value ?? string.Empty;
                if (_cooldownSecondsText == normalized)
                {
                    return;
                }

                _cooldownSecondsText = normalized;
                OnPropertyChanged();

                if (IsCustomTimingPresetSelected)
                {
                    _customCooldownSecondsText = normalized;
                }
            }
        }

        public string TriggerAppStableForMsText
        {
            get => _triggerAppStableForMsText;
            set
            {
                string normalized = value ?? string.Empty;
                if (_triggerAppStableForMsText == normalized)
                {
                    return;
                }

                _triggerAppStableForMsText = normalized;
                OnPropertyChanged();

                if (IsCustomTimingPresetSelected)
                {
                    _customTriggerAppStableForMsText = normalized;
                }
            }
        }

        public string TriggerAppPath
        {
            get => _triggerAppPath;
            set
            {
                string updatedValue = value ?? string.Empty;
                if (_triggerAppPath == updatedValue)
                {
                    return;
                }

                _triggerAppPath = updatedValue;
                _resolvedPackagedAppDisplayName = string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasResolvedTriggerAppTarget));
                OnPropertyChanged(nameof(ResolvedTriggerAppTargetText));
            }
        }

        public bool HasResolvedTriggerAppTarget =>
            IsAppStartTriggerSelected && !string.IsNullOrWhiteSpace(GetResolvedTriggerAppTargetDisplayName());

        public string ResolvedTriggerAppTargetText
        {
            get
            {
                string displayName = GetResolvedTriggerAppTargetDisplayName();
                return string.IsNullOrWhiteSpace(displayName)
                    ? string.Empty
                    : $"Resolved app: {displayName}";
            }
        }

        public bool SwitchOutputPerApp
        {
            get => _switchOutputPerApp;
            set
            {
                bool normalized = value && CanSwitchOutputPerApp;
                if (_switchOutputPerApp == normalized)
                {
                    return;
                }

                _switchOutputPerApp = normalized;
                OnPropertyChanged();
            }
        }

        public bool ShowInTrayMenu
        {
            get => _showInTrayMenu;
            set
            {
                if (_showInTrayMenu == value)
                {
                    return;
                }

                _showInTrayMenu = value;
                OnPropertyChanged();
            }
        }

        public bool RestorePreviousAudioOnDeactivate
        {
            get => _restorePreviousAudioOnDeactivate;
            set
            {
                bool normalized = value && IsStatefulTriggerSelected;
                if (_restorePreviousAudioOnDeactivate == normalized)
                {
                    return;
                }

                _restorePreviousAudioOnDeactivate = normalized;
                OnPropertyChanged();
            }
        }

        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                return "Routine name is required.";
            }

            if (Name.Length > MaxRoutineNameLength)
            {
                return $"Routine name must be {MaxRoutineNameLength} characters or fewer.";
            }

            bool hasOutput = SelectedOutputIndex > 0 && SelectedOutputIndex < OutputDevices.Count;
            bool hasInput = SelectedInputIndex > 0 && SelectedInputIndex < InputDevices.Count;
            if (!TryParseOptionalVolumePercent(MasterVolumePercentText, out _) || !TryParseOptionalVolumePercent(MicVolumePercentText, out _))
            {
                return "Volume targets must be whole numbers between 0 and 100.";
            }

            if (!hasOutput && !hasInput)
            {
                return "Choose an output device, an input device, or both.";
            }

            string hotkey = EditorHotkey.ToHotkeyString();
            if (IsHotkeyTriggerSelected && string.IsNullOrWhiteSpace(hotkey))
            {
                return "Routine hotkey is required.";
            }

            if (IsHotkeyTriggerSelected && TryNormalizeHotkey(hotkey, _additionalStandaloneHotkeyKeys, out string normalizedHotkey) && _reservedHotkeyKeys.Contains(normalizedHotkey))
            {
                return "Routine hotkey must be unique and cannot conflict with another app hotkey.";
            }

            if (IsAppStartTriggerSelected && !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(TriggerAppPath))
            {
                return "Application startup trigger requires a full .exe path or packaged app AUMID.";
            }

            if (SwitchOutputPerApp && !CanSwitchOutputPerApp)
            {
                return "Application audio routing requires an application startup trigger and at least one target.";
            }

            if (RestorePreviousAudioOnDeactivate && !IsStatefulTriggerSelected)
            {
                return "Restore-on-deactivate is only available for stateful routines.";
            }

            if (!TryParseNonNegativeInt(ExecutionDelayMsText, out _))
            {
                return "Delay execution must be a non-negative whole number of milliseconds.";
            }

            if (!TryParseNonNegativeInt(CooldownSecondsText, out _))
            {
                return "Cooldown must be a non-negative whole number of seconds.";
            }

            if (!TryParseNonNegativeInt(TriggerAppStableForMsText, out _))
            {
                return "Wait for app stability must be a non-negative whole number of milliseconds.";
            }

            if (!IsAppStartTriggerSelected && ParseNonNegativeIntOrZero(TriggerAppStableForMsText) > 0)
            {
                return "Wait-for-app stability is only available for application startup triggers.";
            }

            return null;
        }

        public AudioRoutine BuildRoutine()
        {
            CycleDevice? output = SelectedOutputIndex > 0 && SelectedOutputIndex < OutputDevices.Count
                ? OutputDevices[SelectedOutputIndex]
                : null;
            CycleDevice? input = SelectedInputIndex > 0 && SelectedInputIndex < InputDevices.Count
                ? InputDevices[SelectedInputIndex]
                : null;

            RoutineTriggerKind triggerKind = IsAppStartTriggerSelected
                ? RoutineTriggerKind.AppStartup
                : IsAudioPilotStartupTriggerSelected
                    ? RoutineTriggerKind.AudioPilotStartup
                : IsSteamBigPictureTriggerSelected
                    ? RoutineTriggerKind.SteamBigPicture
                    : IsDeviceChangeTriggerSelected
                        ? RoutineTriggerKind.DeviceChange
                    : RoutineTriggerKind.Hotkey;

            int? masterVolumePercent = TryParseOptionalVolumePercent(MasterVolumePercentText, out int? parsedMasterVolumePercent)
                ? parsedMasterVolumePercent
                : null;
            int? micVolumePercent = TryParseOptionalVolumePercent(MicVolumePercentText, out int? parsedMicVolumePercent)
                ? parsedMicVolumePercent
                : null;

            return new AudioRoutine
            {
                Id = _existingId,
                Name = Name,
                Enabled = _enabled,
                OutputDeviceId = output?.Id ?? string.Empty,
                OutputDeviceName = output?.Name ?? string.Empty,
                InputDeviceId = input?.Id ?? string.Empty,
                InputDeviceName = input?.Name ?? string.Empty,
                MasterVolumePercent = masterVolumePercent,
                MicVolumePercent = micVolumePercent,
                Hotkey = IsHotkeyTriggerSelected ? EditorHotkey.ToHotkeyString() : string.Empty,
                TriggerKind = triggerKind,
                TriggerAppPath = IsAppStartTriggerSelected ? RoutineTriggerPathHelper.NormalizeTriggerTarget(TriggerAppPath) : string.Empty,
                SwitchOutputPerApp = SwitchOutputPerApp,
                ShowInTrayMenu = SupportsTrayMenuTrigger && ShowInTrayMenu,
                RestorePreviousAudioOnDeactivate = RestorePreviousAudioOnDeactivate,
                EnforceTargetsOnDeviceChange = IsDeviceChangeTriggerSelected,
                ExecutionDelayMs = IsAudioPilotStartupTriggerSelected ? 0 : ParseNonNegativeIntOrZero(ExecutionDelayMsText),
                CooldownSeconds = IsAudioPilotStartupTriggerSelected ? 0 : ParseNonNegativeIntOrZero(CooldownSecondsText),
                TriggerAppStableForMs = IsAppStartTriggerSelected ? ParseNonNegativeIntOrZero(TriggerAppStableForMsText) : 0,
                DisplayOrder = _existingDisplayOrder,
            };
        }

        internal void ApplyExecutablePath(string path)
        {
            TriggerAppPath = RoutineTriggerPathHelper.NormalizeExecutablePath(path);
        }

        internal void ApplyPackagedAppId(string appUserModelId)
        {
            TriggerAppPath = RoutineTriggerPathHelper.NormalizeTriggerTarget(appUserModelId);
        }

        internal void SetResolvedPackagedAppDisplayName(string? displayName)
        {
            string normalized = displayName?.Trim() ?? string.Empty;
            if (_resolvedPackagedAppDisplayName == normalized)
            {
                return;
            }

            _resolvedPackagedAppDisplayName = normalized;
            OnPropertyChanged(nameof(HasResolvedTriggerAppTarget));
            OnPropertyChanged(nameof(ResolvedTriggerAppTargetText));
        }

        private static string NormalizeRoutineName(string? value)
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (normalized.Length <= MaxRoutineNameLength)
            {
                return normalized;
            }

            return normalized[..MaxRoutineNameLength];
        }

        private static int ResolveSelectedIndex(ObservableCollection<CycleDevice> devices, string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return 0;
            }

            for (int index = 0; index < devices.Count; index++)
            {
                if (string.Equals(devices[index].Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return 0;
        }

        private static bool TryNormalizeHotkey(string? rawHotkey, IEnumerable<string>? additionalStandaloneHotkeyKeys, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(rawHotkey))
            {
                return false;
            }

            var parser = new HotkeyViewModel();
            parser.UpdateAdditionalStandaloneHotkeyKeys(additionalStandaloneHotkeyKeys);
            if (!parser.LoadFromString(rawHotkey))
            {
                return false;
            }

            normalized = parser.ToHotkeyString();
            return !string.IsNullOrWhiteSpace(normalized);
        }

        private static bool TryParseNonNegativeInt(string? rawValue, out int parsed)
        {
            string normalized = rawValue?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
            {
                parsed = 0;
                return true;
            }

            if (!int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
            {
                return false;
            }

            return parsed >= 0;
        }

        private static int ParseNonNegativeIntOrZero(string? rawValue)
        {
            return TryParseNonNegativeInt(rawValue, out int parsed)
                ? parsed
                : 0;
        }

        private static string FormatOptionalPercent(int? value)
        {
            return value.HasValue
                ? value.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static bool TryParseOptionalVolumePercent(string? rawValue, out int? parsed)
        {
            string normalized = rawValue?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
            {
                parsed = null;
                return true;
            }

            if (!int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value is < 0 or > 100)
            {
                parsed = null;
                return false;
            }

            parsed = value;
            return true;
        }

        private RoutineTriggerKind GetSelectedTriggerKind()
        {
            return IsAppStartTriggerSelected
                ? RoutineTriggerKind.AppStartup
                : IsAudioPilotStartupTriggerSelected
                    ? RoutineTriggerKind.AudioPilotStartup
                : IsSteamBigPictureTriggerSelected
                    ? RoutineTriggerKind.SteamBigPicture
                    : IsDeviceChangeTriggerSelected
                        ? RoutineTriggerKind.DeviceChange
                        : RoutineTriggerKind.Hotkey;
        }

        private string GetResolvedTriggerAppTargetDisplayName()
        {
            if (!IsAppStartTriggerSelected || !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(TriggerAppPath))
            {
                return string.Empty;
            }

            if (RoutineTriggerPathHelper.LooksLikePackagedAppId(TriggerAppPath) && !string.IsNullOrWhiteSpace(_resolvedPackagedAppDisplayName))
            {
                return _resolvedPackagedAppDisplayName;
            }

            return RoutineTriggerPathHelper.GetTriggerDisplayName(TriggerAppPath);
        }

        private void ApplySelectedTimingPreset()
        {
            RoutineTimingPreset preset = ParseTimingPresetLabel(SelectedTimingPreset);
            if (preset == RoutineTimingPreset.Custom)
            {
                return;
            }

            AudioRoutine.RoutineTimingProfile profile = AudioRoutine.GetRecommendedTimingProfile(GetSelectedTriggerKind(), preset);
            ExecutionDelayMsText = profile.ExecutionDelayMs.ToString(CultureInfo.InvariantCulture);
            CooldownSecondsText = profile.CooldownSeconds.ToString(CultureInfo.InvariantCulture);
            TriggerAppStableForMsText = profile.TriggerAppStableForMs.ToString(CultureInfo.InvariantCulture);
        }

        private void RememberCustomTimingValues()
        {
            _customExecutionDelayMsText = ExecutionDelayMsText;
            _customCooldownSecondsText = CooldownSecondsText;
            _customTriggerAppStableForMsText = TriggerAppStableForMsText;
        }

        private void RestoreCustomTimingValues()
        {
            ExecutionDelayMsText = _customExecutionDelayMsText;
            CooldownSecondsText = _customCooldownSecondsText;
            TriggerAppStableForMsText = IsAppStartTriggerSelected
                ? _customTriggerAppStableForMsText
                : "0";
        }

        private bool UsesFixedAutomaticTiming => IsDeviceChangeTriggerSelected || IsSteamBigPictureTriggerSelected;

        private void SelectAutomaticTimingPreset()
        {
            if (string.Equals(_selectedTimingPreset, TimingPresetLabels[0], StringComparison.Ordinal))
            {
                return;
            }

            _selectedTimingPreset = TimingPresetLabels[0];
            OnPropertyChanged(nameof(SelectedTimingPreset));
            OnPropertyChanged(nameof(IsCustomTimingPresetSelected));
            OnPropertyChanged(nameof(TimingPresetHelpText));
        }

        private void NormalizeTriggerSpecificEditorState()
        {
            if (!SupportsTrayMenuTrigger)
            {
                ShowInTrayMenu = false;
            }

            if (UsesFixedAutomaticTiming)
            {
                SelectAutomaticTimingPreset();
                ApplySelectedTimingPreset();
                return;
            }

            if (IsAudioPilotStartupTriggerSelected)
            {
                SelectAutomaticTimingPreset();
                ExecutionDelayMsText = "0";
                CooldownSecondsText = "0";
                TriggerAppStableForMsText = "0";
            }
        }

        private void RefreshVolumeTargetsExpansionState()
        {
            IsVolumeTargetsExpanded = HasConfiguredVolumeTargets;
        }

        private static string NormalizeTimingPresetLabel(string? value)
        {
            string normalized = value?.Trim() ?? string.Empty;
            return TimingPresetLabels.Contains(normalized, StringComparer.Ordinal)
                ? normalized
                : TimingPresetLabels[0];
        }

        private static RoutineTimingPreset ParseTimingPresetLabel(string label)
        {
            foreach (RoutineTimingPreset preset in Enum.GetValues<RoutineTimingPreset>())
            {
                if (string.Equals(AudioRoutine.GetTimingPresetLabel(preset), label, StringComparison.Ordinal))
                {
                    return preset;
                }
            }

            return RoutineTimingPreset.Automatic;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
