using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;

namespace AudioPilot.ViewModels
{
    internal static class AppViewModelCleanupDrainLogHelper
    {
        public static BackgroundTaskDrainLoggingCallbacks CreateCallbacks(ILogger logger, string cleanupOpId)
        {
            return BackgroundTaskHelper.CreateWarningDrainLoggingCallbacks(
                logger,
                "AppViewModel",
                pendingAfterInitial => BuildInitialTimeoutMessage(cleanupOpId, pendingAfterInitial),
                () => BuildCompletedWithinGraceMessage(cleanupOpId),
                pendingAfterGrace => BuildForcedTimeoutMessage(cleanupOpId, pendingAfterGrace));
        }

        internal static string BuildInitialTimeoutMessage(string cleanupOpId, int pendingAfterInitial)
        {
            return $"{AppConstants.Audio.LogEvents.ViewModel.App.CleanupTimeout} | opId={cleanupOpId} stage=initial waitMs={AppConstants.Timing.CleanupWaitMs} pending={pendingAfterInitial}";
        }

        internal static string BuildCompletedWithinGraceMessage(string cleanupOpId)
        {
            return $"{AppConstants.Audio.LogEvents.ViewModel.App.CleanupCompleteAfterGrace} | opId={cleanupOpId} waitMs={AppConstants.Timing.CleanupWaitMs + AppConstants.Timing.CleanupGraceExtensionMs}";
        }

        internal static string BuildForcedTimeoutMessage(string cleanupOpId, int pendingAfterGrace)
        {
            return $"{AppConstants.Audio.LogEvents.ViewModel.App.CleanupTimeout} | opId={cleanupOpId} stage=forced waitMs={AppConstants.Timing.CleanupWaitMs + AppConstants.Timing.CleanupGraceExtensionMs} pending={pendingAfterGrace}";
        }
    }
}
