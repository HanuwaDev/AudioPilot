using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppRoutineRestoreCoordinatorTests
{
    [Fact]
    public async Task ExecuteRestoreAsync_Skips_WhenSessionHasNoSnapshot()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineRestoreCoordinatorTests), "routine-restore-skip.log", LogLevel.Info);
        var session = new AppViewModel.RoutineStatefulSession(
            "app-start:routine-1:321",
            "routine-1",
            "Desk",
            RoutineTriggerKind.AppStartup,
            activationSequence: 7,
            restorePreviousAudioOnDeactivate: true,
            restoreSnapshot: null,
            rootProcessId: 321);
        int outputCalls = 0;
        int inputCalls = 0;

        await AppRoutineRestoreCoordinator.ExecuteRestoreAsync(
            session,
            new RoutineRestoreDependencies(
                TryGetActivePlaybackCycleEntry: static (_, _) => null,
                GetDefaultPlaybackDeviceId: static () => null,
                SwitchOutputAsync: (_, _, _) =>
                {
                    outputCalls++;
                    return Task.CompletedTask;
                },
                TryGetActiveRecordingCycleEntry: static (_, _) => null,
                SwitchInputAsync: (_, _, _) =>
                {
                    inputCalls++;
                    return Task.CompletedTask;
                }),
            loggerScope.Logger);

        Assert.Equal(0, outputCalls);
        Assert.Equal(0, inputCalls);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_SwitchesOutputOnly_WhenCurrentOutputDiffers()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineRestoreCoordinatorTests), "routine-restore-output.log", LogLevel.Info);
        var session = BuildSession(new AppViewModel.RoutineAudioRestoreSnapshot("out-target", "Speakers", "", ""));
        string? switchedFrom = null;
        string? switchedTo = null;
        string? outputOpId = null;

        await AppRoutineRestoreCoordinator.ExecuteRestoreAsync(
            session,
            new RoutineRestoreDependencies(
                TryGetActivePlaybackCycleEntry: static (id, name) => new CycleDevice { Id = id, Name = name },
                GetDefaultPlaybackDeviceId: static () => "out-current",
                SwitchOutputAsync: (currentId, targetId, opId) =>
                {
                    switchedFrom = currentId;
                    switchedTo = targetId;
                    outputOpId = opId;
                    return Task.CompletedTask;
                },
                TryGetActiveRecordingCycleEntry: static (_, _) => null,
                SwitchInputAsync: static (_, _, _) => Task.CompletedTask),
            loggerScope.Logger);

        Assert.Equal("out-current", switchedFrom);
        Assert.Equal("out-target", switchedTo);
        Assert.Equal($"routine-stateful-restore-output:{session.SessionKey}", outputOpId);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_SkipsOutput_WhenCurrentOutputAlreadyMatches()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineRestoreCoordinatorTests), "routine-restore-output-skip.log", LogLevel.Info);
        var session = BuildSession(new AppViewModel.RoutineAudioRestoreSnapshot("out-target", "Speakers", "", ""));
        int outputCalls = 0;

        await AppRoutineRestoreCoordinator.ExecuteRestoreAsync(
            session,
            new RoutineRestoreDependencies(
                TryGetActivePlaybackCycleEntry: static (id, name) => new CycleDevice { Id = id, Name = name },
                GetDefaultPlaybackDeviceId: static () => "out-target",
                SwitchOutputAsync: (_, _, _) =>
                {
                    outputCalls++;
                    return Task.CompletedTask;
                },
                TryGetActiveRecordingCycleEntry: static (_, _) => null,
                SwitchInputAsync: static (_, _, _) => Task.CompletedTask),
            loggerScope.Logger);

        Assert.Equal(0, outputCalls);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_SwitchesInput_WhenResolvedTargetExists()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineRestoreCoordinatorTests), "routine-restore-input.log", LogLevel.Info);
        var session = BuildSession(new AppViewModel.RoutineAudioRestoreSnapshot("", "", "in-target", "Mic"));
        string? inputTargetId = null;
        string? inputTargetName = null;
        string? inputOpId = null;

        await AppRoutineRestoreCoordinator.ExecuteRestoreAsync(
            session,
            new RoutineRestoreDependencies(
                TryGetActivePlaybackCycleEntry: static (_, _) => null,
                GetDefaultPlaybackDeviceId: static () => null,
                SwitchOutputAsync: static (_, _, _) => Task.CompletedTask,
                TryGetActiveRecordingCycleEntry: static (id, name) => new CycleDevice { Id = id, Name = name },
                SwitchInputAsync: (targetId, targetName, opId) =>
                {
                    inputTargetId = targetId;
                    inputTargetName = targetName;
                    inputOpId = opId;
                    return Task.CompletedTask;
                }),
            loggerScope.Logger);

        Assert.Equal("in-target", inputTargetId);
        Assert.Equal("Mic", inputTargetName);
        Assert.Equal($"routine-stateful-restore-input:{session.SessionKey}", inputOpId);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_RestoresEndpointVolumes_WhenSnapshotHasCapturedLevels()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineRestoreCoordinatorTests), "routine-restore-volumes.log", LogLevel.Info);
        var session = BuildSession(new AppViewModel.RoutineAudioRestoreSnapshot(
            "out-target",
            "Speakers",
            "in-target",
            "Mic",
            PreviousOutputVolumePercent: 65f,
            PreviousOutputMuted: false,
            PreviousInputVolumePercent: 5f,
            PreviousInputMuted: true));
        float? restoredOutputPercent = null;
        bool? restoredOutputMute = null;
        string? restoredOutputOpId = null;
        float? restoredInputPercent = null;
        bool? restoredInputMute = null;
        string? restoredInputOpId = null;

        await AppRoutineRestoreCoordinator.ExecuteRestoreAsync(
            session,
            new RoutineRestoreDependencies(
                TryGetActivePlaybackCycleEntry: static (id, name) => new CycleDevice { Id = id, Name = name },
                GetDefaultPlaybackDeviceId: static () => "out-target",
                SwitchOutputAsync: static (_, _, _) => Task.CompletedTask,
                TryGetActiveRecordingCycleEntry: static (id, name) => new CycleDevice { Id = id, Name = name },
                SwitchInputAsync: static (_, _, _) => Task.CompletedTask,
                RestoreOutputVolumeAsync: (percent, muted, opId) =>
                {
                    restoredOutputPercent = percent;
                    restoredOutputMute = muted;
                    restoredOutputOpId = opId;
                    return Task.CompletedTask;
                },
                RestoreInputVolumeAsync: (percent, muted, opId) =>
                {
                    restoredInputPercent = percent;
                    restoredInputMute = muted;
                    restoredInputOpId = opId;
                    return Task.CompletedTask;
                }),
            loggerScope.Logger);

        Assert.Equal(65f, restoredOutputPercent);
        Assert.False(restoredOutputMute);
        Assert.Equal($"routine-stateful-restore-output-volume:{session.SessionKey}", restoredOutputOpId);
        Assert.Equal(5f, restoredInputPercent);
        Assert.True(restoredInputMute);
        Assert.Equal($"routine-stateful-restore-input-volume:{session.SessionKey}", restoredInputOpId);
    }

    private static AppViewModel.RoutineStatefulSession BuildSession(AppViewModel.RoutineAudioRestoreSnapshot snapshot)
    {
        return new AppViewModel.RoutineStatefulSession(
            "app-start:routine-1:321",
            "routine-1",
            "Desk",
            RoutineTriggerKind.AppStartup,
            activationSequence: 7,
            restorePreviousAudioOnDeactivate: true,
            restoreSnapshot: snapshot,
            rootProcessId: 321);
    }
}
