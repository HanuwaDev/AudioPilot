using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal static class AudioDeviceServiceLifecycle
    {
        public static async Task<bool> DrainBackgroundTasksAsync(Task[] pendingTasks, Logger logger)
        {
            return await BackgroundTaskHelper.DrainWithGraceAndLoggingAsync(
                pendingTasks,
                AppConstants.Timing.CleanupWaitMs,
                AppConstants.Timing.CleanupGraceExtensionMs,
                BackgroundTaskHelper.CreateWarningDrainLoggingCallbacks(
                    logger,
                    "AudioDeviceService",
                    pendingAfterInitial => $"{AppConstants.Audio.LogEvents.Lifecycle.DisposeTimeout} | stage=initial waitMs={AppConstants.Timing.CleanupWaitMs} pending={pendingAfterInitial}",
                    () => $"{AppConstants.Audio.LogEvents.Lifecycle.DisposeCompleteAfterGrace} | waitMs={AppConstants.Timing.CleanupWaitMs + AppConstants.Timing.CleanupGraceExtensionMs}",
                    pendingAfterGrace => $"{AppConstants.Audio.LogEvents.Lifecycle.DisposeTimeout} | stage=forced waitMs={AppConstants.Timing.CleanupWaitMs + AppConstants.Timing.CleanupGraceExtensionMs} pending={pendingAfterGrace}"));
        }
    }
}
