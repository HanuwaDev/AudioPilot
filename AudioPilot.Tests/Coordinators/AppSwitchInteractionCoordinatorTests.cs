using AudioPilot.Coordinators;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSwitchInteractionCoordinatorTests
{
    [Fact]
    public async Task RegisterResumeHotkeysOnDispatcherAsync_MapsResult_AndRegistersRoutineHotkeys()
    {
        int dispatcherCalls = 0;
        int routineRegistrationCalls = 0;

        AppViewModel.ResumeHotkeyRegistrationResult result = await AppSwitchInteractionCoordinator.RegisterResumeHotkeysOnDispatcherAsync(
            callback =>
            {
                dispatcherCalls++;
                return Task.FromResult(callback());
            },
            () => new HotkeyRegistrationResult(
                ShowAppRegistered: true,
                MediaHotkeysRegistered: false,
                MuteHotkeysRegistered: true,
                ListenToInputRegistered: true,
                VolumeStepHotkeysRegistered: true,
                OutputSwitchRegistered: true,
                InputSwitchRegistered: false,
                OutputReverseSwitchRegistered: true,
                InputReverseSwitchRegistered: true),
            () => routineRegistrationCalls++);

        Assert.Equal(1, dispatcherCalls);
        Assert.Equal(1, routineRegistrationCalls);
        Assert.True(result.ShowAppRegistered);
        Assert.False(result.MediaHotkeysRegistered);
        Assert.True(result.VolumeStepHotkeysRegistered);
        Assert.False(result.InputSwitchRegistered);
        Assert.Equal(2, result.FailedCount);
    }

}
