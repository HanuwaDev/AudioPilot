using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;
using Hardcodet.Wpf.TaskbarNotification;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class MainWindowTrayMenuTests
{
    [Fact]
    public void ResolveNativeTrayAlignmentFlags_WhenCursorBelowWorkArea_UsesBottomAlign()
    {
        MainWindow.MONITORINFO monitorInfo = new()
        {
            rcMonitor = new MainWindow.RECT
            {
                left = 0,
                top = 0,
                right = 1920,
                bottom = 1080,
            },
            rcWork = new MainWindow.RECT
            {
                left = 0,
                top = 0,
                right = 1920,
                bottom = 1040,
            },
        };

        uint flags = MainWindow.ResolveNativeTrayAlignmentFlags(
            new MainWindow.POINT { x = 1800, y = 1075 },
            monitorInfo,
            estimatedMenuWidthPx: 220,
            estimatedMenuHeightPx: 120);

        Assert.Equal(0x0028u, flags);
    }

    [Fact]
    public void ResolveNativeTrayAlignmentFlags_WhenCursorInsideWorkArea_UsesLeftAlign()
    {
        MainWindow.MONITORINFO monitorInfo = new()
        {
            rcMonitor = new MainWindow.RECT
            {
                left = 0,
                top = 0,
                right = 1920,
                bottom = 1080,
            },
            rcWork = new MainWindow.RECT
            {
                left = 0,
                top = 0,
                right = 1920,
                bottom = 1040,
            },
        };

        uint flags = MainWindow.ResolveNativeTrayAlignmentFlags(
            new MainWindow.POINT { x = 400, y = 300 },
            monitorInfo,
            estimatedMenuWidthPx: 220,
            estimatedMenuHeightPx: 120);

        Assert.Equal(0u, flags);
    }

    [Fact]
    public void ResolveNativeTrayPopupPlacement_WhenIconIsBelowWorkArea_OpensAboveAndToLeftOfIconRightEdge()
    {
        MainWindow.NativeTrayPopupPlacement placement = MainWindow.ResolveNativeTrayPopupPlacement(
            new MainWindow.RECT
            {
                left = 1800,
                top = 1048,
                right = 1832,
                bottom = 1080,
            },
            new MainWindow.RECT
            {
                left = 0,
                top = 0,
                right = 1920,
                bottom = 1040,
            },
            estimatedMenuWidthPx: 220,
            estimatedMenuHeightPx: 120);

        Assert.Equal(1832, placement.X);
        Assert.Equal(1048, placement.Y);
        Assert.Equal(0x0028u, placement.AlignmentFlags);
    }

    [Fact]
    public void ResolveNativeTrayPopupPlacement_WhenIconIsNearTopLeft_OpensBelowAndToRight()
    {
        MainWindow.NativeTrayPopupPlacement placement = MainWindow.ResolveNativeTrayPopupPlacement(
            new MainWindow.RECT
            {
                left = 12,
                top = 12,
                right = 44,
                bottom = 44,
            },
            new MainWindow.RECT
            {
                left = 0,
                top = 0,
                right = 1920,
                bottom = 1040,
            },
            estimatedMenuWidthPx: 220,
            estimatedMenuHeightPx: 120);

        Assert.Equal(12, placement.X);
        Assert.Equal(44, placement.Y);
        Assert.Equal(0x0000u, placement.AlignmentFlags);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(2, false, false)]
    [InlineData(2, true, true)]
    public void ShouldShowNativeSwitchMenuItem_RequiresDevicesAndEnabledState(int cycleDeviceCount, bool hotkeysEnabled, bool expected)
    {
        bool actual = MainWindow.ShouldShowNativeSwitchMenuItem(cycleDeviceCount, hotkeysEnabled);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Show", null, "Show")]
    [InlineData("Show", "", "Show")]
    [InlineData("Show", "Ctrl+Alt+H", "Show\tCtrl+Alt+H")]
    public void FormatNativeMenuItemLabel_AppendsShortcutColumnOnlyWhenPresent(string text, string? shortcut, string expected)
    {
        string actual = MainWindow.FormatNativeMenuItemLabel(text, shortcut);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GetTrayShowAppHotkey_ReturnsConfiguredHotkey_WhenRegistered()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(new TestSettingsWorkspace(nameof(MainWindowTrayMenuTests)), Dispatcher.CurrentDispatcher);
            harness.SetCachedSettings(new Settings { Hotkeys = new HotkeysSettings { App = new HotkeysAppSettings { ShowApp = "Ctrl+Alt+H" } } });
            SetHotkeyOutcome(harness.Hotkeys, AppConstants.Hotkeys.ShowAppHotkeyId, new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.Registered));

            string? actual = harness.ViewModel.GetTrayShowAppHotkey();

            Assert.Equal("Ctrl+Alt+H", actual);
        });
    }

    [Fact]
    public void GetTrayShowAppHotkey_ReturnsNull_WhenConfiguredButUnavailable()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(new TestSettingsWorkspace(nameof(MainWindowTrayMenuTests)), Dispatcher.CurrentDispatcher);
            harness.SetCachedSettings(new Settings { Hotkeys = new HotkeysSettings { App = new HotkeysAppSettings { ShowApp = "Ctrl+Alt+H" } } });
            SetHotkeyOutcome(harness.Hotkeys, AppConstants.Hotkeys.ShowAppHotkeyId, new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.ExternalConflict));

            string? actual = harness.ViewModel.GetTrayShowAppHotkey();

            Assert.Null(actual);
        });
    }

    [Fact]
    public void TrayMenu_Show_Click_MarksInteractiveShowRequest()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(new TestSettingsWorkspace(nameof(MainWindowTrayMenuTests)), Dispatcher.CurrentDispatcher);
            MainWindow window = CreateMainWindowShell(harness.ViewModel);

            window.TrayMenuShowClickForTests(new MenuItem(), new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.True(harness.ViewModel.HasInteractiveShowRequest);
        });
    }

    [Fact]
    public void TrayMenu_Settings_Click_SelectsSettingsTab_AndMarksInteractiveShowRequest()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(new TestSettingsWorkspace(nameof(MainWindowTrayMenuTests)), Dispatcher.CurrentDispatcher);
            MainWindow window = CreateMainWindowShell(harness.ViewModel);

            window.TrayMenuSettingsClickForTests(new MenuItem(), new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal(3, harness.ViewModel.SelectedSettingsTabIndex);
            Assert.True(harness.ViewModel.HasInteractiveShowRequest);
        });
    }

    [Fact]
    public void TrayMenu_Settings_Click_ResetsMainScrollToTop()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(new TestSettingsWorkspace(nameof(MainWindowTrayMenuTests)), Dispatcher.CurrentDispatcher);
            MainWindow window = CreateMainWindowShell(harness.ViewModel);
            ScrollViewer scrollViewer = CreateScrollableMainContentScrollViewer();
            TestPrivateAccess.SetField(window, "MainContentScrollViewer", scrollViewer);

            scrollViewer.ScrollToVerticalOffset(300);
            scrollViewer.UpdateLayout();

            Assert.True(scrollViewer.VerticalOffset > 0);

            window.TrayMenuSettingsClickForTests(new MenuItem(), new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal(0, scrollViewer.VerticalOffset);
        });
    }

    [Fact]
    public void TaskbarIcon_TrayMouseDoubleClick_MarksHandled_AndRequestsShow()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(new TestSettingsWorkspace(nameof(MainWindowTrayMenuTests)), Dispatcher.CurrentDispatcher);
            MainWindow window = CreateMainWindowShell(harness.ViewModel);
            RoutedEventArgs args = new(MenuItem.ClickEvent);

            window.TaskbarIconTrayMouseDoubleClickForTests(new object(), args);

            Assert.True(args.Handled);
            Assert.True(harness.ViewModel.HasInteractiveShowRequest);
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void TrayMenu_Hide_Click_MinimizesWindowToTray()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(TrayMenu_Hide_Click_MinimizesWindowToTray)))
        {
            return;
        }

        TestExecutionGuards.RunIsolatedSta(() =>
        {
            InvokeWithLocalApplication(() =>
            {
                Window window = TestWindowFactory.CreateOffscreenWindow(showInTaskbar: true, width: 320, height: 240);
                TaskbarIcon tray = new();
                var shell = new AppShellService(window, tray, AppShellService.WindowInteractionStrategy.NonInteractive);
                AppViewModel viewModel = AppViewModelHarnessBuilder.CreateTrayCapableViewModelShell(shell);
                MainWindow mainWindow = CreateMainWindowShell(viewModel);

                try
                {
                    TestWindowFactory.ShowWindowForTest(window);

                    mainWindow.TrayMenuHideClickForTests(new MenuItem(), new RoutedEventArgs(MenuItem.ClickEvent));

                    Assert.False(window.ShowInTaskbar);
                    Assert.Equal(WindowState.Minimized, window.WindowState);
                    Assert.Equal(Visibility.Visible, tray.Visibility);
                }
                finally
                {
                    shell.Dispose();
                    if (window.IsLoaded)
                    {
                        window.Close();
                    }
                }
            });
        });
    }

    [Fact]
    public void TrayMenu_Exit_Click_LogsExitRequest()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            using var loggerScope = new TestLoggerScope(nameof(MainWindowTrayMenuTests), "tray-exit.log");
            AppViewModel viewModel = AppViewModelHarnessBuilder.CreateUninitializedViewModelShell(loggerScope.Logger);
            MainWindow window = CreateMainWindowShell(viewModel);

            AppViewModel.ExitApplicationOverrideForTests = static () => { };

            try
            {
                window.TrayMenuExitClickForTests(new MenuItem(), new RoutedEventArgs(MenuItem.ClickEvent));
            }
            finally
            {
                AppViewModel.ResetTestHooks();
            }

            string logText = loggerScope.DisposeAndReadLogText();

            Assert.Contains("Exit requested", logText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SelectRoutineListItem_SelectsBoundItem()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            AudioRoutine routine = new() { Id = "routine-1", Name = "Desk" };
            ListBoxItem item = new()
            {
                DataContext = routine,
            };

            bool selected = MainWindow.SelectRoutineListItem(item);

            Assert.True(selected);
            Assert.True(item.IsSelected);
        });
    }

    [Fact]
    public void SelectRoutineListItem_ReturnsFalse_WhenItemHasNoRoutine()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            bool selected = MainWindow.SelectRoutineListItem(new ListBoxItem());

            Assert.False(selected);
        });
    }

    [Fact]
    public void TryGetRoutineListItemFromEventSource_ResolvesDirectListBoxItem()
    {
        TestExecutionGuards.RunSta(() =>
        {
            AudioRoutine routine = new() { Id = "routine-1", Name = "Desk" };
            ListBoxItem item = new()
            {
                DataContext = routine,
            };

            bool resolved = MainWindow.TryGetRoutineListItemFromEventSource(item, out ListBoxItem? resolvedItem);

            Assert.True(resolved);
            Assert.Same(item, resolvedItem);
        });
    }

    private static void EnsureApplication()
    {
        if (Application.Current == null)
        {
            _ = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
        }
    }

    private static void InvokeWithLocalApplication(Action action)
    {
        FieldInfo? appInstanceField = typeof(Application).GetField("_appInstance", BindingFlags.Static | BindingFlags.NonPublic);
        FieldInfo? appCreatedField = typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.Static | BindingFlags.NonPublic);
        FieldInfo? isShuttingDownField = typeof(Application).GetField("_isShuttingDown", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(appInstanceField);
        Assert.NotNull(appCreatedField);
        Assert.NotNull(isShuttingDownField);

        object? originalAppInstance = appInstanceField!.GetValue(null);
        object? originalAppCreated = appCreatedField!.GetValue(null);
        object? originalIsShuttingDown = isShuttingDownField!.GetValue(null);

        try
        {
            appInstanceField.SetValue(null, null);
            appCreatedField.SetValue(null, false);
            isShuttingDownField.SetValue(null, false);

            Application localApplication = new()
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };

            action();

            localApplication.Shutdown();
        }
        finally
        {
            appInstanceField.SetValue(null, originalAppInstance);
            appCreatedField.SetValue(null, originalAppCreated);
            isShuttingDownField.SetValue(null, originalIsShuttingDown);
        }
    }

    private static MainWindow CreateMainWindowShell(AppViewModel viewModel)
    {
        MainWindow window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        TestPrivateAccess.SetField(window, "_appVm", viewModel);
        return window;
    }

    private static void SetHotkeyOutcome(HotkeyService hotkeys, int id, HotkeyRegistrationOutcome outcome)
    {
        Dictionary<int, HotkeyRegistrationOutcome> outcomes =
            TestPrivateAccess.GetField<Dictionary<int, HotkeyRegistrationOutcome>>(hotkeys, "_registrationOutcomeById");
        outcomes[id] = outcome;
    }

    private static ScrollViewer CreateScrollableMainContentScrollViewer()
    {
        ScrollViewer scrollViewer = new()
        {
            Width = 200,
            Height = 120,
            Content = new StackPanel
            {
                Width = 200,
                Height = 1200,
            },
        };

        scrollViewer.Measure(new Size(200, 120));
        scrollViewer.Arrange(new Rect(0, 0, 200, 120));
        scrollViewer.UpdateLayout();
        return scrollViewer;
    }
}
