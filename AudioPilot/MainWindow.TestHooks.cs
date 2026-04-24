using System.Windows;
using Microsoft.Win32;

namespace AudioPilot
{
    public partial class MainWindow
    {
        internal bool AreSystemEventHandlersRegisteredForTests() => _systemEventHandlersRegistered;

        internal void RegisterSystemEventHandlersForTests() => RegisterSystemEventHandlers();

        internal void DetachSystemEventHandlersForTests() => DetachSystemEventHandlers();

        internal void OnUserPreferenceChangedForTests(UserPreferenceChangedEventArgs e) => OnUserPreferenceChanged(this, e);

        internal void OnPowerModeChangedForTests(PowerModeChangedEventArgs e) => OnPowerModeChanged(this, e);

        internal void TrayMenuShowClickForTests(object sender, RoutedEventArgs e) => TrayMenu_Show_Click(sender, e);

        internal void TrayMenuSettingsClickForTests(object sender, RoutedEventArgs e) => TrayMenu_Settings_Click(sender, e);

        internal void TrayMenuHideClickForTests(object sender, RoutedEventArgs e) => TrayMenu_Hide_Click(sender, e);

        internal void TrayMenuExitClickForTests(object sender, RoutedEventArgs e) => TrayMenu_Exit_Click(sender, e);

        internal void TaskbarIconTrayMouseDoubleClickForTests(object sender, RoutedEventArgs e) => TaskbarIcon_TrayMouseDoubleClick(sender, e);

        internal void ResetMainContentScrollToTopForTests() => ResetMainContentScrollToTop();
    }
}
