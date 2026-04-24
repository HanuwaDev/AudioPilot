using System.Windows;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    internal sealed class MainWindowVisibilityCoordinator(Logger logger)
    {
        private readonly Logger _logger = logger;
        private bool _wasMinimizedToTray;

        public void HandleWindowStateChanged(
            WindowState state,
            Action hideWindow,
            Action minimizeAppVm)
        {
            if (state != WindowState.Minimized)
            {
                return;
            }

            _wasMinimizedToTray = true;
            _logger.Debug("MainWindow", "main-window-minimize-to-tray-redirect");
            hideWindow();
            minimizeAppVm();
        }

        public void MarkPendingAutoScrollOnNextShow()
        {
            _wasMinimizedToTray = true;
        }

        public void HandleVisibleChanged(
            bool isVisible,
            Func<bool> isEditorTabActive,
            Func<bool> isAutoScrollEnabled,
            Action scheduleScroll)
        {
            if (!_wasMinimizedToTray || !isVisible)
            {
                return;
            }

            if (!isEditorTabActive())
            {
                _wasMinimizedToTray = false;
                return;
            }

            if (!isAutoScrollEnabled())
            {
                _wasMinimizedToTray = false;
                return;
            }

            scheduleScroll();
            _wasMinimizedToTray = false;
        }
    }
}
