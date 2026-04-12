using System.Windows;
using System.Windows.Media.Imaging;
using AudioPilot.Constants;
using AudioPilot.Tests.Helpers;
using Hardcodet.Wpf.TaskbarNotification;

namespace AudioPilot.Tests.Services.UI;

[Trait(TestCategories.Name, TestCategories.Integration)]
[Collection("WpfApplicationIsolation")]
public sealed class AppShellServiceIntegrationTests
{
    private static BitmapImage CreateIconFromRepoPath()
    {
        string iconPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "AudioPilot", "Images", "sound.ico"));

        Assert.True(File.Exists(iconPath), $"Icon file not found at {iconPath}");
        return new BitmapImage(new Uri(iconPath, UriKind.Absolute));
    }

    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void AppShellService_HandlesFirstShow_MinimizeRestore_AndDispose_WhenIntegrationEnabled()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(AppShellService_HandlesFirstShow_MinimizeRestore_AndDispose_WhenIntegrationEnabled)))
        {
            return;
        }

        TestExecutionGuards.RunSta(() =>
        {
            bool createdApplication = false;
            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                return;
            }

            if (Application.Current == null)
            {
                _ = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                createdApplication = true;
            }

            var window = TestWindowFactory.CreateOffscreenWindow(showInTaskbar: true, width: 480, height: 320);

            var tray = new TaskbarIcon();
            var icon = CreateIconFromRepoPath();
            var shell = new AppShellService(window, tray, AppShellService.WindowInteractionStrategy.NonInteractive);

            try
            {
                TestWindowFactory.ShowWindowForTest(window);
                shell.InitializeIcons(icon);

                Assert.NotNull(window.Icon);
                Assert.NotNull(tray.IconSource);
                Assert.Equal(AppConstants.Identity.DisplayName, tray.ToolTipText);

                shell.ShowWindowFrontAndCenter();
                Assert.True(window.IsLoaded);
                Assert.Equal(WindowState.Minimized, window.WindowState);
                Assert.True(window.ShowInTaskbar);

                bool hideCallbackInvoked = false;
                shell.MinimizeToTray(() => hideCallbackInvoked = true, showBalloon: false);

                Assert.True(hideCallbackInvoked);
                Assert.False(shell.IsWindowVisible);
                Assert.False(window.ShowInTaskbar);
                Assert.Equal(Visibility.Visible, tray.Visibility);

                shell.ShowWindowFrontAndCenter();
                Assert.True(window.IsLoaded);
                Assert.Equal(WindowState.Minimized, window.WindowState);
                Assert.True(window.ShowInTaskbar);
            }
            finally
            {
                shell.Dispose();

                if (window.IsLoaded)
                {
                    window.Close();
                }

                if (createdApplication && Application.Current != null)
                {
                    Application.Current.Shutdown();
                }
            }
        });
    }

    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void AppShellService_StartHiddenToTray_RestoresOnFirstShow_WhenIntegrationEnabled()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(AppShellService_StartHiddenToTray_RestoresOnFirstShow_WhenIntegrationEnabled)))
        {
            return;
        }

        TestExecutionGuards.RunSta(() =>
        {
            bool createdApplication = false;
            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                return;
            }

            if (Application.Current == null)
            {
                _ = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                createdApplication = true;
            }

            var window = TestWindowFactory.CreateOffscreenWindow(showInTaskbar: false, width: 480, height: 320);

            var tray = new TaskbarIcon();
            var icon = CreateIconFromRepoPath();
            var shell = new AppShellService(window, tray, AppShellService.WindowInteractionStrategy.NonInteractive);

            try
            {
                shell.InitializeIcons(icon);
                shell.StartHiddenToTray();

                Assert.False(shell.IsWindowVisible);
                Assert.False(window.ShowInTaskbar);
                Assert.Equal(Visibility.Visible, tray.Visibility);

                shell.ShowWindowFrontAndCenter();

                Assert.True(window.IsLoaded);
                Assert.Equal(WindowState.Minimized, window.WindowState);
                Assert.True(window.ShowInTaskbar);
            }
            finally
            {
                shell.Dispose();

                if (window.IsLoaded)
                {
                    window.Close();
                }

                if (createdApplication && Application.Current != null)
                {
                    Application.Current.Shutdown();
                }
            }
        });
    }

    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void AppShellService_ShutdownCycleSoak_MinimizeRestoreDispose_WhenIntegrationEnabled()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(AppShellService_ShutdownCycleSoak_MinimizeRestoreDispose_WhenIntegrationEnabled)))
        {
            return;
        }

        TestExecutionGuards.RunSta(() =>
        {
            bool createdApplication = false;
            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                return;
            }

            if (Application.Current == null)
            {
                _ = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                createdApplication = true;
            }

            var icon = CreateIconFromRepoPath();

            const int cycles = 12;
            for (int i = 0; i < cycles; i++)
            {
                var window = TestWindowFactory.CreateOffscreenWindow(showInTaskbar: true, width: 420, height: 280);

                var tray = new TaskbarIcon();
                var shell = new AppShellService(window, tray, AppShellService.WindowInteractionStrategy.NonInteractive);

                try
                {
                    TestWindowFactory.ShowWindowForTest(window);
                    shell.InitializeIcons(icon);
                    shell.ShowWindowFrontAndCenter();
                    Assert.Equal(WindowState.Minimized, window.WindowState);
                    Assert.True(window.ShowInTaskbar);

                    bool hideCallbackInvoked = false;
                    shell.MinimizeToTray(() => hideCallbackInvoked = true, showBalloon: false);
                    Assert.True(hideCallbackInvoked);
                    Assert.False(window.ShowInTaskbar);

                    shell.ShowWindowFrontAndCenter();
                    Assert.True(window.IsLoaded);
                    Assert.Equal(WindowState.Minimized, window.WindowState);
                    Assert.True(window.ShowInTaskbar);
                }
                finally
                {
                    shell.Dispose();
                    if (window.IsLoaded)
                    {
                        window.Close();
                    }
                }
            }

            if (createdApplication && Application.Current != null)
            {
                Application.Current.Shutdown();
            }
        }, timeout: TimeSpan.FromSeconds(60));
    }
}

