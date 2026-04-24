using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal static class AppSwitchInteractionCoordinator
    {
        public static async Task<AppViewModel.ResumeHotkeyRegistrationResult> RegisterResumeHotkeysOnDispatcherAsync(
            Func<Func<AppViewModel.ResumeHotkeyRegistrationResult>, Task<AppViewModel.ResumeHotkeyRegistrationResult>> executeOnDispatcherAsync,
            Func<HotkeyRegistrationResult> registerHotkeys,
            Action registerRoutineHotkeys)
        {
            return await executeOnDispatcherAsync(() =>
            {
                HotkeyRegistrationResult result = registerHotkeys();
                registerRoutineHotkeys();
                return MapResumeHotkeyRegistrationResult(result);
            });
        }

        public static AppViewModel.ResumeHotkeyRegistrationResult MapResumeHotkeyRegistrationResult(HotkeyRegistrationResult result)
        {
            return new AppViewModel.ResumeHotkeyRegistrationResult(
                result.ShowAppRegistered,
                result.MediaHotkeysRegistered,
                result.MuteHotkeysRegistered,
                result.ListenToInputRegistered,
                result.VolumeStepHotkeysRegistered,
                result.OutputSwitchRegistered,
                result.InputSwitchRegistered,
                result.OutputReverseSwitchRegistered,
                result.InputReverseSwitchRegistered);
        }

        public static bool FinalizeSwitch(bool switched, bool output, Action<bool> markSwitchOverlayShown)
        {
            if (!switched)
            {
                return false;
            }

            markSwitchOverlayShown(output);
            return true;
        }
    }
}
