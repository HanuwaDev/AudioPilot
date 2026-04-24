using System.Runtime.CompilerServices;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using Microsoft.Win32;

namespace AudioPilot.Tests;

public sealed class MainWindowLifecycleSubscriptionTests
{
    [Fact]
    public void DetachSystemEventHandlers_WhenNotRegistered_RemainsSafeAndDisposesCoordinator()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var recoveryHandler = new FakeResumeRecoveryHandler();
            var coordinator = new MainWindowStartupResumeCoordinator(
                Logger.Instance,
                recoveryHandler,
                new MainWindowStartupResumeDependencies(
                    RegisterNotificationClient: static () => { },
                    SettingsFileExists: static () => true,
                    InitializeStartupAsync: static _ => Task.CompletedTask,
                    CaptureInitialHotplugSnapshot: static () => { }),
                queueResumeRecoveryWork: static work => Task.Run(work),
                showStartupError: static _ => { },
                shutdown: static () => { });

            MainWindow window = CreateMainWindowShell(coordinator);

            Exception? exception = Record.Exception(window.DetachSystemEventHandlersForTests);

            Assert.Null(exception);
            Assert.False(window.AreSystemEventHandlersRegisteredForTests());

            coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Resume), nameof(DetachSystemEventHandlers_WhenNotRegistered_RemainsSafeAndDisposesCoordinator));
            Assert.Equal(0, recoveryHandler.InvocationCount);
        });
    }

    [Fact]
    public void RegisterSystemEventHandlers_ThenDetachSystemEventHandlers_TracksRegistrationState()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var coordinator = new MainWindowStartupResumeCoordinator(
                Logger.Instance,
                new FakeResumeRecoveryHandler(),
                new MainWindowStartupResumeDependencies(
                    RegisterNotificationClient: static () => { },
                    SettingsFileExists: static () => true,
                    InitializeStartupAsync: static _ => Task.CompletedTask,
                    CaptureInitialHotplugSnapshot: static () => { }),
                queueResumeRecoveryWork: static work => Task.Run(work),
                showStartupError: static _ => { },
                shutdown: static () => { });

            MainWindow window = CreateMainWindowShell(coordinator);

            window.RegisterSystemEventHandlersForTests();
            Assert.True(window.AreSystemEventHandlersRegisteredForTests());

            window.DetachSystemEventHandlersForTests();
            Assert.False(window.AreSystemEventHandlersRegisteredForTests());

            Exception? exception = Record.Exception(window.DetachSystemEventHandlersForTests);
            Assert.Null(exception);
        });
    }

    [Fact]
    public void OnPowerModeChanged_WhenShutdownStarted_DoesNotQueueResumeRecovery()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var recoveryHandler = new FakeResumeRecoveryHandler();
            var coordinator = new MainWindowStartupResumeCoordinator(
                Logger.Instance,
                recoveryHandler,
                new MainWindowStartupResumeDependencies(
                    RegisterNotificationClient: static () => { },
                    SettingsFileExists: static () => true,
                    InitializeStartupAsync: static _ => Task.CompletedTask,
                    CaptureInitialHotplugSnapshot: static () => { }),
                queueResumeRecoveryWork: static work => Task.Run(work),
                showStartupError: static _ => { },
                shutdown: static () => { });

            MainWindow window = CreateMainWindowShell(coordinator);
            TestPrivateAccess.SetField(window, "_shutdownStarted", 1);

            window.OnPowerModeChangedForTests(new PowerModeChangedEventArgs(PowerModes.Resume));

            Assert.Equal(0, recoveryHandler.InvocationCount);
        });
    }

    [Fact]
    public void OnUserPreferenceChanged_WhenShutdownStarted_ReturnsBeforeTouchingWindowState()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var coordinator = new MainWindowStartupResumeCoordinator(
                Logger.Instance,
                new FakeResumeRecoveryHandler(),
                new MainWindowStartupResumeDependencies(
                    RegisterNotificationClient: static () => { },
                    SettingsFileExists: static () => true,
                    InitializeStartupAsync: static _ => Task.CompletedTask,
                    CaptureInitialHotplugSnapshot: static () => { }),
                queueResumeRecoveryWork: static work => Task.Run(work),
                showStartupError: static _ => { },
                shutdown: static () => { });

            MainWindow window = CreateMainWindowShell(coordinator);
            TestPrivateAccess.SetField(window, "_shutdownStarted", 1);

            Exception? exception = Record.Exception(() =>
                window.OnUserPreferenceChangedForTests(new UserPreferenceChangedEventArgs(UserPreferenceCategory.General)));

            Assert.Null(exception);
        });
    }

    private static MainWindow CreateMainWindowShell(MainWindowStartupResumeCoordinator coordinator)
    {
        MainWindow window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        TestPrivateAccess.SetField(window, "_startupResumeCoordinator", coordinator);
        return window;
    }
}
