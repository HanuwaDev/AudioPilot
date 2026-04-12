using AudioPilot.Cli;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Cli;

public sealed class CliRoutineExecutionPolicyTests
{
    [Fact]
    public void TryResolveManualRunProcessId_WhenExecutableSnapshotMatches_UsesSharedSnapshotProvider()
    {
        var provider = new FakeRoutineProcessSnapshotProvider();
        provider.CaptureAllSnapshots.Add(new RoutineProcessSnapshot(
            24,
            @"C:\Apps\Spotify\Spotify.exe"));

        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Name = "Spotify",
            TriggerOnAppStart = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            OutputDeviceId = "out-1",
        };

        bool resolved = CliRoutineExecutionPolicy.TryResolveManualRunProcessId(
            routine,
            provider,
            out int? processId,
            out string? errorCode,
            out string? errorMessage);

        Assert.True(resolved);
        Assert.Equal(24, processId);
        Assert.Null(errorCode);
        Assert.Null(errorMessage);
        Assert.Equal(1, provider.CaptureAllCallCount);
        Assert.Equal(0, provider.TryCaptureCallCount);
        Assert.Equal(RoutineProcessSnapshotCaptureOptions.None, Assert.Single(provider.CaptureAllOptionsHistory));
    }

    [Fact]
    public void TryResolveManualRunProcessId_WhenPackagedSnapshotMatches_UsesSharedSnapshotProvider()
    {
        var provider = new FakeRoutineProcessSnapshotProvider();
        provider.CaptureAllSnapshots.Add(new RoutineProcessSnapshot(
            12,
            @"C:\Program Files\WindowsApps\Spotify\Spotify.exe",
            AppUserModelId: "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"));

        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Name = "Spotify",
            TriggerOnAppStart = true,
            TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
            OutputDeviceId = "out-1",
        };

        bool resolved = CliRoutineExecutionPolicy.TryResolveManualRunProcessId(
            routine,
            provider,
            out int? processId,
            out string? errorCode,
            out string? errorMessage);

        Assert.True(resolved);
        Assert.Equal(12, processId);
        Assert.Null(errorCode);
        Assert.Null(errorMessage);
        Assert.Equal(1, provider.CaptureAllCallCount);
        Assert.Equal(RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId, Assert.Single(provider.CaptureAllOptionsHistory));
    }

    [Fact]
    public void TryResolveManualRunProcessId_WhenNoSnapshotMatches_ReturnsExpectedFailure()
    {
        var provider = new FakeRoutineProcessSnapshotProvider();
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Name = "Spotify",
            TriggerOnAppStart = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            OutputDeviceId = "out-1",
        };

        bool resolved = CliRoutineExecutionPolicy.TryResolveManualRunProcessId(
            routine,
            provider,
            out int? processId,
            out string? errorCode,
            out string? errorMessage);

        Assert.False(resolved);
        Assert.Null(processId);
        Assert.Equal("routine-trigger-app-not-running", errorCode);
        Assert.Equal("Routine 'Spotify' requires the target application 'Spotify' to be running.", errorMessage);
        Assert.Equal(1, provider.CaptureAllCallCount);
        Assert.Equal(RoutineProcessSnapshotCaptureOptions.None, Assert.Single(provider.CaptureAllOptionsHistory));
    }
}
