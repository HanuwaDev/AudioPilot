using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class RoutineEditorViewModelTests
{
    [Fact]
    public void PrimaryActionLabel_IsAdd_ForNewRoutine()
    {
        var viewModel = new RoutineEditorViewModel([], [], existingRoutine: null, suggestedName: "Routine 3");

        Assert.False(viewModel.IsEditingExistingRoutine);
        Assert.Equal("Add", viewModel.PrimaryActionLabel);
    }

    [Fact]
    public void PrimaryActionLabel_IsUpdate_ForExistingRoutine()
    {
        var viewModel = new RoutineEditorViewModel(
            [],
            [],
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Routine 1",
                Enabled = true,
                DisplayOrder = 1,
            });

        Assert.True(viewModel.IsEditingExistingRoutine);
        Assert.Equal("Update", viewModel.PrimaryActionLabel);
    }

    [Fact]
    public void Constructor_UsesSuggestedName_ForNewRoutine()
    {
        var viewModel = new RoutineEditorViewModel([], [], existingRoutine: null, suggestedName: "Routine 3");

        Assert.Equal("Routine 3", viewModel.Name);
        Assert.Equal(0, viewModel.SelectedOutputIndex);
        Assert.Equal(0, viewModel.SelectedInputIndex);
    }

    [Fact]
    public void Constructor_FallsBackToRoutineOne_WhenSuggestionMissing()
    {
        var viewModel = new RoutineEditorViewModel([], [], existingRoutine: null, suggestedName: null);

        Assert.Equal("Routine 1", viewModel.Name);
        Assert.Equal("Automatic", viewModel.SelectedTimingPreset);
    }

    [Fact]
    public void Name_TruncatesToTwentyFiveCharacters()
    {
        var viewModel = new RoutineEditorViewModel([], [], existingRoutine: null, suggestedName: null)
        {
            Name = "123456789012345678901234567890"
        };

        Assert.Equal("1234567890123456789012345", viewModel.Name);
    }

    [Fact]
    public void RoutineNameCharactersRemainingText_UpdatesAsNameChanges()
    {
        var viewModel = new RoutineEditorViewModel([], [], existingRoutine: null, suggestedName: null)
        {
            Name = "Routine"
        };

        Assert.Equal("18 characters remaining", viewModel.RoutineNameCharactersRemainingText);

        viewModel.Name = "AB";

        Assert.Equal("23 characters remaining", viewModel.RoutineNameCharactersRemainingText);
    }

    [Fact]
    public void BuildRoutine_ForNewRoutine_DefaultsEnabled_AndUsesSelectedTargets()
    {
        var outputDevices = new[]
        {
            new CycleDevice { Id = "out-1", Name = "Speakers" }
        };
        var inputDevices = new[]
        {
            new CycleDevice { Id = "in-1", Name = "Mic" }
        };

        var viewModel = new RoutineEditorViewModel(outputDevices, inputDevices, suggestedName: "Routine 2")
        {
            SelectedOutputIndex = 1,
            SelectedInputIndex = 1,
        };
        viewModel.EditorHotkey.LoadFromString("Ctrl+Alt+R");

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal("Routine 2", routine.Name);
        Assert.True(routine.Enabled);
        Assert.Equal("out-1", routine.OutputDeviceId);
        Assert.Equal("Speakers", routine.OutputDeviceName);
        Assert.Equal("in-1", routine.InputDeviceId);
        Assert.Equal("Mic", routine.InputDeviceName);
        Assert.Equal("Ctrl+Alt+R", routine.Hotkey);
    }

    [Fact]
    public void BuildRoutine_PersistsAppStartTrigger_ButClearsTrayMenu()
    {
        var viewModel = new RoutineEditorViewModel([new CycleDevice { Id = "out-1", Name = "Speakers" }], [], suggestedName: "Routine 2")
        {
            SelectedOutputIndex = 1,
            SelectedInputIndex = 0,
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            ShowInTrayMenu = true,
            RestorePreviousAudioOnDeactivate = true,
        };

        viewModel.EditorHotkey.LoadFromString(string.Empty);

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(RoutineTriggerKind.AppStartup, routine.TriggerKind);
        Assert.True(routine.TriggerOnAppStart);
        Assert.Equal(@"C:\Apps\Spotify\Spotify.exe", routine.TriggerAppPath);
        Assert.True(routine.SwitchOutputPerApp);
        Assert.False(routine.ShowInTrayMenu);
        Assert.True(routine.RestorePreviousAudioOnDeactivate);
        Assert.Equal(string.Empty, routine.Hotkey);
    }

    [Fact]
    public void BuildRoutine_PersistsVolumeTargets_WhenConfigured()
    {
        var viewModel = new RoutineEditorViewModel([new CycleDevice { Id = "out-1", Name = "Speakers" }], [], suggestedName: "Routine 2")
        {
            SelectedOutputIndex = 1,
            MasterVolumePercentText = "60",
            MicVolumePercentText = "15",
        };

        viewModel.EditorHotkey.LoadFromString("Ctrl+Alt+R");

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(60, routine.MasterVolumePercent);
        Assert.Equal(15, routine.MicVolumePercent);
    }

    [Fact]
    public void Validate_RequiresDeviceTarget_WhenOnlyVolumeTargetsAreConfigured()
    {
        var viewModel = new RoutineEditorViewModel([], [], suggestedName: "Routine 4")
        {
            MasterVolumePercentText = "35",
        };

        viewModel.EditorHotkey.LoadFromString("Ctrl+Alt+R");

        string? validation = viewModel.Validate();

        Assert.Equal("Choose an output device, an input device, or both.", validation);
    }

    [Fact]
    public void Validate_RejectsInvalidVolumeTarget()
    {
        var viewModel = new RoutineEditorViewModel([], [], suggestedName: "Routine 4")
        {
            MasterVolumePercentText = "150",
        };

        viewModel.EditorHotkey.LoadFromString("Ctrl+Alt+R");

        string? validation = viewModel.Validate();

        Assert.Equal("Volume targets must be whole numbers between 0 and 100.", validation);
    }

    [Fact]
    public void Constructor_ExpandsVolumeTargets_WhenExistingRoutineHasConfiguredLevels()
    {
        var viewModel = new RoutineEditorViewModel(
            [],
            [],
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Routine 1",
                Enabled = true,
                DisplayOrder = 1,
                MasterVolumePercent = 45,
            });

        Assert.True(viewModel.IsVolumeTargetsExpanded);
        Assert.Equal("45", viewModel.MasterVolumePercentText);
    }

    [Fact]
    public void SwitchOutputPerApp_IsCleared_WhenOutputTargetIsRemoved()
    {
        var viewModel = new RoutineEditorViewModel([new CycleDevice { Id = "out-1", Name = "Speakers" }], [], suggestedName: "Routine 2")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
        };

        viewModel.SelectedOutputIndex = 0;

        Assert.False(viewModel.SwitchOutputPerApp);
        Assert.False(viewModel.CanSwitchOutputPerApp);
    }

    [Fact]
    public void SwitchOutputPerApp_AllowsInputTarget()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [new CycleDevice { Id = "in-1", Name = "Microphone" }],
            suggestedName: "Routine 2")
        {
            SelectedOutputIndex = 1,
            SelectedInputIndex = 1,
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true
        };

        Assert.Equal(1, viewModel.SelectedInputIndex);
        Assert.True(viewModel.CanSelectInputTarget);
    }

    [Fact]
    public void SwitchOutputPerApp_SupportsInputOnlyTarget_WhenAppStartupSelected()
    {
        var viewModel = new RoutineEditorViewModel(
            [],
            [new CycleDevice { Id = "in-1", Name = "Microphone" }],
            suggestedName: "Routine 2")
        {
            SelectedOutputIndex = 0,
            SelectedInputIndex = 1,
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true
        };

        Assert.True(viewModel.SwitchOutputPerApp);
        Assert.True(viewModel.CanSwitchOutputPerApp);
    }

    [Fact]
    public void Validate_RequiresHotkey()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4")
        {
            SelectedOutputIndex = 1,
        };

        string? validation = viewModel.Validate();

        Assert.Equal("Routine hotkey is required.", validation);
    }

    [Fact]
    public void Validate_RequiresHotkey_WhenHotkeyTriggerSelectedEvenIfTrayEnabled()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4")
        {
            SelectedOutputIndex = 1,
            ShowInTrayMenu = true,
        };

        string? validation = viewModel.Validate();

        Assert.Equal("Routine hotkey is required.", validation);
    }

    [Fact]
    public void Validate_RejectsDuplicateRoutineHotkey()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4",
            reservedHotkeyKeys: ["Ctrl+Alt+R"])
        {
            SelectedOutputIndex = 1,
        };

        viewModel.EditorHotkey.LoadFromString("Ctrl+Alt+R");

        string? validation = viewModel.Validate();

        Assert.Equal("Routine hotkey must be unique and cannot conflict with another app hotkey.", validation);
    }

    [Fact]
    public void Validate_AllowsExistingRoutineToKeepItsCurrentHotkey()
    {
        var existingRoutine = new AudioRoutine
        {
            Id = "routine-1",
            Name = "Routine 1",
            Enabled = true,
            DisplayOrder = 1,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            Hotkey = "Ctrl+Alt+R",
        };

        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            existingRoutine,
            reservedHotkeyKeys: ["Ctrl+Alt+R", "Ctrl+Alt+P"]);

        string? validation = viewModel.Validate();

        Assert.Null(validation);
    }

    [Fact]
    public void Validate_RequiresFullExePathWhenAppStartTriggerEnabled()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = "spotify",
        };

        string? validation = viewModel.Validate();

        Assert.Equal("Application startup trigger requires a full .exe path or packaged app AUMID.", validation);
    }

    [Fact]
    public void Validate_AllowsPackagedAppAumid_WhenAppStartTriggerEnabled()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
        };

        string? validation = viewModel.Validate();

        Assert.Null(validation);
    }

    [Fact]
    public void ResolvedTriggerAppTargetText_UsesExecutableFileName_ForDesktopApp()
    {
        var viewModel = new RoutineEditorViewModel([], [], suggestedName: "Routine 4")
        {
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
        };

        Assert.True(viewModel.HasResolvedTriggerAppTarget);
        Assert.Equal("Resolved app: Spotify", viewModel.ResolvedTriggerAppTargetText);
    }

    [Fact]
    public void ResolvedTriggerAppTargetText_UsesResolvedPackagedAppDisplayName_WhenAvailable()
    {
        var viewModel = new RoutineEditorViewModel([], [], suggestedName: "Routine 4")
        {
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
        };

        viewModel.SetResolvedPackagedAppDisplayName("Spotify");

        Assert.True(viewModel.HasResolvedTriggerAppTarget);
        Assert.Equal("Resolved app: Spotify", viewModel.ResolvedTriggerAppTargetText);
    }

    [Fact]
    public void ResolvedTriggerAppTargetText_IsHidden_WhenNotAppStartupTrigger()
    {
        var viewModel = new RoutineEditorViewModel([], [], suggestedName: "Routine 4")
        {
            SelectedTriggerMode = "Hotkey",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
        };

        Assert.False(viewModel.HasResolvedTriggerAppTarget);
        Assert.Equal(string.Empty, viewModel.ResolvedTriggerAppTargetText);
    }

    [Fact]
    public void Validate_AllowsPerAppRouting_ForPackagedAppTarget()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
            SwitchOutputPerApp = true,
        };

        string? validation = viewModel.Validate();

        Assert.Null(validation);
    }

    [Fact]
    public void Validate_AllowsAppStartupTriggerWithoutHotkey()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
        };

        string? validation = viewModel.Validate();

        Assert.Null(validation);
    }

    [Fact]
    public void BuildRoutine_PreservesPackagedAppAumidTrigger()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 2")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
            ShowInTrayMenu = true,
        };

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", routine.TriggerAppPath);
        Assert.Equal(RoutineTriggerKind.AppStartup, routine.TriggerKind);
        Assert.True(routine.TriggerOnAppStart);
    }

    [Fact]
    public void Constructor_PreservesStatefulFlags_ForExistingAppStartRoutine()
    {
        var viewModel = new RoutineEditorViewModel(
            [new() { Id = "out-1", Name = "Speakers" }],
            [],
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Routine 1",
                Enabled = true,
                DisplayOrder = 1,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                TriggerKind = RoutineTriggerKind.AppStartup,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                RestorePreviousAudioOnDeactivate = true,
            });

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.True(routine.RestorePreviousAudioOnDeactivate);
    }

    [Fact]
    public void BuildRoutine_SupportsSteamBigPictureTrigger()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 2")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Steam Big Picture",
            RestorePreviousAudioOnDeactivate = true,
        };

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(RoutineTriggerKind.SteamBigPicture, routine.TriggerKind);
        Assert.False(routine.TriggerOnAppStart);
        Assert.Equal(string.Empty, routine.TriggerAppPath);
        Assert.True(routine.RestorePreviousAudioOnDeactivate);
        Assert.Equal(0, routine.ExecutionDelayMs);
        Assert.Equal(5, routine.CooldownSeconds);
    }

    [Fact]
    public void BuildRoutine_SupportsDeviceChangeTrigger()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 2")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Device change",
            ShowInTrayMenu = true,
        };

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(RoutineTriggerKind.DeviceChange, routine.TriggerKind);
        Assert.False(routine.TriggerOnAppStart);
        Assert.Equal(string.Empty, routine.TriggerAppPath);
        Assert.False(routine.RestorePreviousAudioOnDeactivate);
        Assert.True(routine.EnforceTargetsOnDeviceChange);
        Assert.Equal(150, routine.ExecutionDelayMs);
        Assert.Equal(5, routine.CooldownSeconds);
    }

    [Fact]
    public void BuildRoutine_SupportsAudioPilotStartupTrigger()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 2")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "AudioPilot startup",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            RestorePreviousAudioOnDeactivate = true,
            ShowInTrayMenu = true,
            SelectedTimingPreset = "Custom",
            ExecutionDelayMsText = "250",
            CooldownSecondsText = "12",
        };

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(RoutineTriggerKind.AudioPilotStartup, routine.TriggerKind);
        Assert.False(routine.TriggerOnAppStart);
        Assert.Equal(string.Empty, routine.TriggerAppPath);
        Assert.False(routine.SwitchOutputPerApp);
        Assert.False(routine.RestorePreviousAudioOnDeactivate);
        Assert.False(routine.ShowInTrayMenu);
        Assert.Equal(0, routine.ExecutionDelayMs);
        Assert.Equal(0, routine.CooldownSeconds);
    }

    [Fact]
    public void Validate_AllowsAudioPilotStartupTriggerWithoutAppPath()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "AudioPilot startup",
        };

        string? validation = viewModel.Validate();

        Assert.Null(validation);
    }

    [Fact]
    public void Constructor_PreservesAudioPilotStartupTrigger_ForExistingRoutine()
    {
        var viewModel = new RoutineEditorViewModel(
            [new() { Id = "out-1", Name = "Speakers" }],
            [],
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Routine 1",
                Enabled = true,
                DisplayOrder = 1,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                TriggerKind = RoutineTriggerKind.AudioPilotStartup,
                ShowInTrayMenu = true,
                ExecutionDelayMs = 125,
                CooldownSeconds = 10,
            });

        Assert.Equal("AudioPilot startup", viewModel.SelectedTriggerMode);
        Assert.True(viewModel.IsAudioPilotStartupTriggerSelected);
        Assert.False(viewModel.IsStatefulTriggerSelected);
        Assert.False(viewModel.SupportsTrayMenuTrigger);
        Assert.False(viewModel.SupportsTimingControls);
        Assert.False(viewModel.ShowInTrayMenu);
        Assert.Equal("0", viewModel.ExecutionDelayMsText);
        Assert.Equal("0", viewModel.CooldownSecondsText);
    }

    [Fact]
    public void AutomaticTriggerModes_DoNotSupportTrayMenu()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4")
        {
            SelectedOutputIndex = 1,
            ShowInTrayMenu = true,
            SelectedTriggerMode = "Application startup",
        };

        Assert.False(viewModel.SupportsTrayMenuTrigger);
        Assert.False(viewModel.ShowInTrayMenu);

        viewModel.SelectedTriggerMode = "Device change";
        Assert.False(viewModel.SupportsTrayMenuTrigger);

        viewModel.SelectedTriggerMode = "Steam Big Picture";
        Assert.False(viewModel.SupportsTrayMenuTrigger);
    }

    [Fact]
    public void FixedBehaviorTriggers_HideTimingControls_AndResetToAutomaticTiming()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SelectedTimingPreset = "Custom",
            ExecutionDelayMsText = "333",
            CooldownSecondsText = "7",
            TriggerAppStableForMsText = "999",
        };

        viewModel.SelectedTriggerMode = "Device change";

        Assert.False(viewModel.SupportsTimingControls);
        Assert.Equal("Automatic", viewModel.SelectedTimingPreset);
        Assert.Equal("150", viewModel.ExecutionDelayMsText);
        Assert.Equal("5", viewModel.CooldownSecondsText);
        Assert.Equal("0", viewModel.TriggerAppStableForMsText);

        viewModel.SelectedTriggerMode = "Steam Big Picture";

        Assert.False(viewModel.SupportsTimingControls);
        Assert.Equal("Automatic", viewModel.SelectedTimingPreset);
        Assert.Equal("0", viewModel.ExecutionDelayMsText);
        Assert.Equal("5", viewModel.CooldownSecondsText);
    }

    [Fact]
    public void SelectedTriggerMode_ClearsTrayMenuAndTiming_ForAudioPilotStartup()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4")
        {
            SelectedOutputIndex = 1,
            ShowInTrayMenu = true,
            SelectedTimingPreset = "Custom",
            ExecutionDelayMsText = "333",
            CooldownSecondsText = "7",
            SelectedTriggerMode = "AudioPilot startup",
        };

        Assert.False(viewModel.SupportsTrayMenuTrigger);
        Assert.False(viewModel.SupportsTimingControls);
        Assert.False(viewModel.ShowInTrayMenu);
        Assert.Equal("Automatic", viewModel.SelectedTimingPreset);
        Assert.Equal("0", viewModel.ExecutionDelayMsText);
        Assert.Equal("0", viewModel.CooldownSecondsText);
    }

    [Fact]
    public void HotkeyTrigger_HidesTimingControls_ButPreservesExistingTimingValues()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Routine 1",
                Enabled = true,
                DisplayOrder = 1,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                TriggerKind = RoutineTriggerKind.Hotkey,
                Hotkey = "Ctrl+Alt+R",
                ExecutionDelayMs = 125,
                CooldownSeconds = 10,
            });

        Assert.True(viewModel.IsHotkeyTriggerSelected);
        Assert.False(viewModel.SupportsTimingControls);
        Assert.Equal("125", viewModel.ExecutionDelayMsText);
        Assert.Equal("10", viewModel.CooldownSecondsText);

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(125, routine.ExecutionDelayMs);
        Assert.Equal(10, routine.CooldownSeconds);
    }

    [Fact]
    public void TriggerAppPath_PreservesTypedPeriodWhileEditing()
    {
        var viewModel = new RoutineEditorViewModel([], [], suggestedName: "Routine 4")
        {
            TriggerAppPath = @"C:\Apps\Spotify\Spotify."
        };

        Assert.Equal(@"C:\Apps\Spotify\Spotify.", viewModel.TriggerAppPath);
    }

    [Fact]
    public void BuildRoutine_PersistsTimingSettings()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 5")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SelectedTimingPreset = "Custom",
            ExecutionDelayMsText = "350",
            CooldownSecondsText = "9",
            TriggerAppStableForMsText = "1200",
        };

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(350, routine.ExecutionDelayMs);
        Assert.Equal(9, routine.CooldownSeconds);
        Assert.Equal(1200, routine.TriggerAppStableForMs);
    }

    [Fact]
    public void Validate_RejectsNegativeTimingValue()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SelectedTimingPreset = "Custom",
            ExecutionDelayMsText = "-1",
        };

        string? validation = viewModel.Validate();

        Assert.Equal("Delay execution must be a non-negative whole number of milliseconds.", validation);
    }

    [Fact]
    public void SelectedTriggerMode_ClearsAppStabilityWait_WhenLeavingAppStartup()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application startup",
            SelectedTimingPreset = "Custom",
            TriggerAppStableForMsText = "500",
        };

        viewModel.TriggerAppStableForMsText = "500";
        viewModel.SelectedTriggerMode = "Device change";

        Assert.Equal("0", viewModel.TriggerAppStableForMsText);
        Assert.Equal("Automatic", viewModel.SelectedTimingPreset);
        Assert.Equal("150", viewModel.ExecutionDelayMsText);
        Assert.Equal("5", viewModel.CooldownSecondsText);
        Assert.Null(viewModel.Validate());
    }

    [Fact]
    public void SelectedTimingPreset_AppliesRecommendedValues()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SelectedTimingPreset = "Reliable",
        };

        Assert.Equal("500", viewModel.ExecutionDelayMsText);
        Assert.Equal("30", viewModel.CooldownSecondsText);
        Assert.Equal("1500", viewModel.TriggerAppStableForMsText);
        Assert.Contains("Timing controls add a short delay before the routine runs", viewModel.TimingPresetHelpText, StringComparison.Ordinal);
        Assert.Contains("Reliable uses", viewModel.TimingPresetHelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedTimingPreset_RestoresPreviousCustomValues_WhenReturningToCustom()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application startup",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SelectedTimingPreset = "Custom",
            ExecutionDelayMsText = "333",
            CooldownSecondsText = "7",
            TriggerAppStableForMsText = "999",
        };

        viewModel.SelectedTimingPreset = "Reliable";
        viewModel.SelectedTimingPreset = "Custom";

        Assert.Equal("333", viewModel.ExecutionDelayMsText);
        Assert.Equal("7", viewModel.CooldownSecondsText);
        Assert.Equal("999", viewModel.TriggerAppStableForMsText);
    }

    [Fact]
    public void SelectedTimingPreset_ExistingCustomRoutineKeepsCustomValues_AfterPresetRoundTrip()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Routine 1",
                Enabled = true,
                DisplayOrder = 1,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                TriggerKind = RoutineTriggerKind.AppStartup,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                ExecutionDelayMs = 333,
                CooldownSeconds = 7,
                TriggerAppStableForMs = 999,
            })
        {
            SelectedTimingPreset = "Balanced"
        };
        viewModel.SelectedTimingPreset = "Custom";

        Assert.Equal("333", viewModel.ExecutionDelayMsText);
        Assert.Equal("7", viewModel.CooldownSecondsText);
        Assert.Equal("999", viewModel.TriggerAppStableForMsText);
    }

    [Fact]
    public void Constructor_InfersPresetFromExistingRoutineTiming()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Routine 1",
                Enabled = true,
                DisplayOrder = 1,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                TriggerKind = RoutineTriggerKind.AppStartup,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                ExecutionDelayMs = 250,
                CooldownSeconds = 15,
                TriggerAppStableForMs = 800,
            });

        Assert.Equal("Balanced", viewModel.SelectedTimingPreset);
        Assert.False(viewModel.IsCustomTimingPresetSelected);
    }

    [Fact]
    public void Constructor_UsesCustomPresetForNonStandardTiming()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Routine 1",
                Enabled = true,
                DisplayOrder = 1,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                TriggerKind = RoutineTriggerKind.AppStartup,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                ExecutionDelayMs = 333,
                CooldownSeconds = 7,
                TriggerAppStableForMs = 999,
            });

        Assert.Equal("Custom", viewModel.SelectedTimingPreset);
        Assert.True(viewModel.IsCustomTimingPresetSelected);
    }
}
