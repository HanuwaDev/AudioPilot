using AudioPilot.Models;
using Newtonsoft.Json.Linq;

namespace AudioPilot.Tests.Models;

public sealed class AudioRoutineTests
{
    [Fact]
    public void TriggerSummary_IncludesAppAudioOnly_WhenConfigured()
    {
        var routine = new AudioRoutine
        {
            InputDeviceId = "in-1",
            InputDeviceName = "Mic",
            TriggerOnAppStart = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            ShowInTrayMenu = true,
        };

        Assert.Equal("Application start: Spotify | Application audio only", routine.TriggerSummary);
    }

    [Fact]
    public void Clone_PreservesSwitchOutputPerApp()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-1",
            Name = "Spotify",
            OutputDeviceId = "out-1",
            TriggerOnAppStart = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            MasterVolumePercent = 42,
        };

        AudioRoutine clone = routine.Clone();

        Assert.True(clone.SwitchOutputPerApp);
        Assert.Equal(42, clone.MasterVolumePercent);
    }

    [Fact]
    public void TargetSummary_IncludesConfiguredVolumeTargets()
    {
        var routine = new AudioRoutine
        {
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            MasterVolumePercent = 55,
            MicVolumePercent = 10,
        };

        Assert.Equal("Output: Speakers | Master: 55% | Microphone: 10%", routine.TargetSummary);
        Assert.Equal("Out", routine.TargetKindBadgeText);
    }

    [Fact]
    public void TargetKindBadgeText_UsesVolumeOnly_WhenOnlyVolumeTargetsConfigured()
    {
        var routine = new AudioRoutine
        {
            MasterVolumePercent = 30,
        };

        Assert.Equal("Vol", routine.TargetKindBadgeText);
    }

    [Fact]
    public void HasExecutionTarget_ReturnsTrue_WhenOnlyVolumeTargetsConfigured()
    {
        var routine = new AudioRoutine
        {
            MasterVolumePercent = 30,
        };

        Assert.True(routine.HasExecutionTarget);
    }

    [Fact]
    public void TriggerSummary_UsesPackagedAppDisplayName_WhenConfigured()
    {
        var routine = new AudioRoutine
        {
            TriggerOnAppStart = true,
            TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
            ShowInTrayMenu = true,
        };

        Assert.Equal("Application start: SpotifyAB SpotifyMusic", routine.TriggerSummary);
    }

    [Fact]
    public void TriggerOnAppStart_SetsTriggerKindToAppStartup()
    {
        var routine = new AudioRoutine
        {
            TriggerOnAppStart = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
        };

        Assert.Equal(RoutineTriggerKind.AppStartup, routine.TriggerKind);
        Assert.True(routine.HasAppStartTrigger);
    }

    [Fact]
    public void Serialize_DoesNotPersistTriggerOnAppStartCompatibilityProperty()
    {
        var routine = new AudioRoutine
        {
            TriggerOnAppStart = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
        };

        JObject json = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(routine));

        Assert.Equal(nameof(RoutineTriggerKind.AppStartup), json[nameof(AudioRoutine.TriggerKind)]?.Value<string>());
        Assert.Null(json[nameof(AudioRoutine.TriggerOnAppStart)]);
    }

    [Fact]
    public void TriggerSummary_UsesDeviceChangeTrigger_WhenConfigured()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.DeviceChange,
            ShowInTrayMenu = true,
        };

        Assert.Equal("Device change", routine.TriggerSummary);
    }

    [Fact]
    public void TriggerSummary_UsesTrayMenuOnlyForHotkeyRoutine()
    {
        var routine = new AudioRoutine
        {
            Hotkey = "Ctrl+Alt+R",
            TriggerKind = RoutineTriggerKind.Hotkey,
            ShowInTrayMenu = true,
        };

        Assert.Equal("Hotkey: Ctrl+Alt+R | Tray menu", routine.TriggerSummary);
    }

    [Fact]
    public void TriggerSummary_UsesAudioPilotStartupTrigger_WhenConfigured()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.AudioPilotStartup,
            ShowInTrayMenu = true,
        };

        Assert.Equal("AudioPilot startup", routine.TriggerSummary);
    }

    [Fact]
    public void TimingSummary_IncludesConfiguredDelayCooldownAndAppStableValues()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.AppStartup,
            ExecutionDelayMs = 250,
            CooldownSeconds = 12,
            TriggerAppStableForMs = 900,
        };

        Assert.Equal("Delay: 250 ms | Cooldown: 12 s | App stable: 900 ms", routine.TimingSummary);
    }

    [Fact]
    public void TimingPreset_InfersBalancedPreset_ForRecommendedAppStartupValues()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.AppStartup,
            ExecutionDelayMs = 250,
            CooldownSeconds = 15,
            TriggerAppStableForMs = 800,
        };

        Assert.Equal(RoutineTimingPreset.Balanced, routine.TimingPreset);
        Assert.Equal("Balanced", routine.TimingPresetLabel);
    }

    [Fact]
    public void TimingPreset_UsesAutomatic_ForFixedBehaviorDeviceChangeTrigger()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.DeviceChange,
            ExecutionDelayMs = 310,
            CooldownSeconds = 17,
        };

        Assert.Equal(150, routine.ExecutionDelayMs);
        Assert.Equal(5, routine.CooldownSeconds);
        Assert.Equal(RoutineTimingPreset.Automatic, routine.TimingPreset);
        Assert.Equal("Automatic", routine.TimingPresetLabel);
    }

    [Fact]
    public void TriggerKind_NormalizesSteamBigPictureTimingToAutomaticProfile()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.SteamBigPicture,
            ExecutionDelayMs = 250,
            CooldownSeconds = 20,
        };

        Assert.Equal(0, routine.ExecutionDelayMs);
        Assert.Equal(5, routine.CooldownSeconds);
        Assert.Equal(RoutineTimingPreset.Automatic, routine.TimingPreset);
    }

    [Fact]
    public void LastRunStatusText_ShowsFailureState()
    {
        var routine = new AudioRoutine
        {
            LastRunState = RoutineLastRunState.Failed,
            LastRunUtc = DateTimeOffset.UtcNow,
        };

        Assert.Contains("Last run:", routine.LastRunStatusText, StringComparison.Ordinal);
        Assert.Contains("Failed", routine.LastRunStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void TriggerKind_ClearsAppStableWait_WhenLeavingAppStartup()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.AppStartup,
            TriggerAppStableForMs = 800,
        };

        routine.TriggerKind = RoutineTriggerKind.Hotkey;

        Assert.Equal(0, routine.TriggerAppStableForMs);
    }

    [Fact]
    public void TriggerKind_ClearsAppStartOnlyFields_WhenSwitchingToAudioPilotStartup()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.AppStartup,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            ShowInTrayMenu = true,
            RestorePreviousAudioOnDeactivate = true,
            ExecutionDelayMs = 250,
            CooldownSeconds = 12,
            TriggerAppStableForMs = 800,
        };

        routine.TriggerKind = RoutineTriggerKind.AudioPilotStartup;

        Assert.Equal(string.Empty, routine.TriggerAppPath);
        Assert.False(routine.SwitchOutputPerApp);
        Assert.False(routine.ShowInTrayMenu);
        Assert.False(routine.RestorePreviousAudioOnDeactivate);
        Assert.Equal(0, routine.ExecutionDelayMs);
        Assert.Equal(0, routine.CooldownSeconds);
        Assert.Equal(0, routine.TriggerAppStableForMs);
        Assert.True(routine.HasAudioPilotStartupTrigger);
    }

    [Fact]
    public void TriggerKind_ClearsTrayMenu_WhenLeavingHotkey()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Hotkey,
            Hotkey = "Ctrl+Alt+R",
            ShowInTrayMenu = true,
        };

        routine.TriggerKind = RoutineTriggerKind.AppStartup;

        Assert.False(routine.ShowInTrayMenu);
    }
}
