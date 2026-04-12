using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineConflictHelperTests
{
    [Fact]
    public void BuildConflictSummaries_FlagsAppStartRoutines_WhenSameAppTargetsDifferentOutputs()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.AppStartup,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Name = "Headset",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.AppStartup,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);

        Assert.Equal(2, result.Count);
        Assert.Contains("different output targets", result["routine-1"], StringComparison.Ordinal);
        Assert.Contains("Application start for Spotify", result["routine-1"], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConflictSummaries_DoesNotFlagComplementaryAppStartRoutines()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.AppStartup,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Name = "Mic",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.AppStartup,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                InputDeviceId = "in-1",
                InputDeviceName = "Microphone",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildConflictSummaries_FlagsDeviceChangeRoutines_WhenOutputsDiffer()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Name = "Headset",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void BuildConflictSummaries_FlagsAudioPilotStartupRoutines_WhenOutputsDiffer()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.AudioPilotStartup,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Name = "Headset",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.AudioPilotStartup,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);

        Assert.Equal(2, result.Count);
        Assert.Contains("AudioPilot startup", result["routine-1"], StringComparison.Ordinal);
        Assert.Contains("different output targets", result["routine-2"], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConflictSummaries_FlagsSteamBigPictureRoutines_WhenOutputsDiffer()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.SteamBigPicture,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Name = "Headset",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.SteamBigPicture,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);

        Assert.Equal(2, result.Count);
        Assert.Contains("Steam Big Picture", result["routine-1"], StringComparison.Ordinal);
        Assert.Contains("different output targets", result["routine-2"], StringComparison.Ordinal);
    }
}
