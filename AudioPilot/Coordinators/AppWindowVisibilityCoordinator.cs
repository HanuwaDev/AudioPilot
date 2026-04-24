using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    internal readonly record struct MinimizeWindowPlan(
        MinimizeAttemptResult AttemptResult,
        bool ShowBalloon,
        bool ConsumeFirstRunBalloon,
        bool ConsumeSaveBalloon);

    internal static class AppWindowVisibilityCoordinator
    {
        public static void ShowWindow(
            AppWindowStateCoordinator windowState,
            Action showWindowFrontAndCenter,
            Action refreshAvailableDeviceCollections,
            Action refreshDeviceCache,
            Func<Task> refreshMixerAsync,
            Func<Task> updateMuteFlagsAsync,
            ILogger logger,
            DateTime now)
        {
            windowState.RequestInteractiveShow();
            if (!windowState.IsStartupVisibilityResolved)
            {
                logger.Debug("AppViewModel", "window-show-deferred | reason=startup-visibility-pending");
                return;
            }

            windowState.MarkShown(now);
            showWindowFrontAndCenter();
            refreshAvailableDeviceCollections();
            refreshDeviceCache();
            _ = refreshMixerAsync();
            _ = updateMuteFlagsAsync();

        }

        public static void StartHiddenToTray(Action startHiddenToTray, ILogger logger)
        {
            startHiddenToTray();
            logger.Debug("AppViewModel", "window-start-hidden-to-tray");
        }

        public static MinimizeWindowPlan BuildMinimizePlan(
            AppWindowStateCoordinator windowState,
            bool showBalloonAfterSave,
            DateTime now)
        {
            MinimizeAttemptResult minimizeResult = windowState.TryBeginMinimize(now);
            bool showBalloonOnFirstRun = windowState.ShowBalloonOnFirstMinimize;
            bool showBalloon = showBalloonOnFirstRun || showBalloonAfterSave;

            return new MinimizeWindowPlan(
                minimizeResult,
                showBalloon,
                ConsumeFirstRunBalloon: minimizeResult == MinimizeAttemptResult.Started && showBalloonOnFirstRun,
                ConsumeSaveBalloon: minimizeResult == MinimizeAttemptResult.Started && showBalloonAfterSave);
        }

        public static void ApplyMinimizePlan(
            AppWindowStateCoordinator windowState,
            MinimizeWindowPlan plan,
            Action<Action, bool, string> minimizeToTray,
            Action clearSaveBalloon,
            ILogger logger)
        {
            if (plan.AttemptResult == MinimizeAttemptResult.Cooldown)
            {
                logger.Debug("AppViewModel", "minimize-to-tray-skipped | reason=show-cooldown");
                return;
            }

            if (plan.AttemptResult == MinimizeAttemptResult.AlreadyMinimizing)
            {
                logger.Debug("AppViewModel", "minimize-to-tray-skipped | reason=already-minimizing");
                return;
            }

            logger.Debug("AppViewModel", "minimize-to-tray-start");

            minimizeToTray(
                () =>
                {
                    logger.Debug("AppViewModel", "minimize-to-tray-hidden");
                    windowState.CompleteMinimize();
                },
                plan.ShowBalloon,
                AppConstants.Identity.DisplayName);

            if (plan.ConsumeFirstRunBalloon)
            {
                windowState.ShowBalloonOnFirstMinimize = false;
            }

            if (plan.ConsumeSaveBalloon)
            {
                clearSaveBalloon();
            }
        }
    }
}
