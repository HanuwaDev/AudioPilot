using AudioPilot.Coordinators;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppPostSaveCoordinatorTests
{
    [Fact]
    public void BuildMuteApplication_UsesDeafenAsSharedOverride()
    {
        PostSaveMuteApplication result = AppPostSaveCoordinator.BuildMuteApplication(
            currentDeafen: true,
            currentMuteMic: false,
            currentMuteSound: false);

        Assert.True(result.MuteMicrophone);
        Assert.True(result.MutePlayback);
    }

    [Fact]
    public async Task ApplyMuteStateAsync_SkipsDispatcher_WhenShutdownRequested()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        int dispatcherCalls = 0;

        await AppPostSaveCoordinator.ApplyMuteStateAsync(
            () =>
            {
                dispatcherCalls++;
                return Task.CompletedTask;
            },
            cts.Token);

        Assert.Equal(0, dispatcherCalls);
    }
}
