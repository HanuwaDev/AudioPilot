using System.Text.RegularExpressions;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using Microsoft.Win32;

namespace AudioPilot.Tests.Coordinators;

public sealed partial class MainWindowStartupResumeCoordinatorTests
{
    [Fact]
    public async Task HandlePowerModeChanged_LogsCorrelatedResumeAndPassesOpIdToRecoveryHandler()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowStartupResumeCoordinatorTests), "resume-coordinator.log", LogLevel.Info);

        var recoveryHandler = new FakeResumeRecoveryHandler();
        var coordinator = CreateCoordinator(loggerScope.Logger, recoveryHandler);

        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), nameof(HandlePowerModeChanged_LogsCorrelatedResumeAndPassesOpIdToRecoveryHandler));

        string receivedOpId = await recoveryHandler.WaitForInvocationAsync();

        string logText = loggerScope.DisposeAndReadLogText();

        Match opIdMatch = MyRegex().Match(logText);
        Assert.True(opIdMatch.Success, $"Expected resume opId in log.\nLog text:\n{logText}");
        string loggedOpId = opIdMatch.Groups[1].Value;

        Assert.Equal(loggedOpId, receivedOpId);
        Assert.Contains($"power-resume-detected | opId={loggedOpId}", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlePowerModeChanged_LogsCorrelatedFailure_WhenRecoveryThrows()
    {
        using var loggerScope = TestLoggerScope.CreateFileBacked(nameof(MainWindowStartupResumeCoordinatorTests), "resume-coordinator-fail.log", LogLevel.Info);

        var recoveryHandler = new FakeResumeRecoveryHandler
        {
            ExceptionToThrow = new InvalidOperationException("boom"),
        };
        var coordinator = CreateCoordinator(loggerScope.Logger, recoveryHandler);

        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), nameof(HandlePowerModeChanged_LogsCorrelatedFailure_WhenRecoveryThrows));

        string receivedOpId = await recoveryHandler.WaitForInvocationAsync();
        await recoveryHandler.WaitForCompletionAsync();

        string logText = TestLogFileAssert.WaitForLogText(
            loggerScope.LogPath,
            2000,
            $"power-resume-detected | opId={receivedOpId}",
            $"power-resume-recovery-failed | opId={receivedOpId}");
        loggerScope.Logger.Dispose();

        Assert.Contains($"power-resume-detected | opId={receivedOpId}", logText, StringComparison.Ordinal);
        Assert.Contains($"power-resume-recovery-failed | opId={receivedOpId}", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlePowerModeChanged_IgnoresDuplicateResumeSignalsWithinCooldownWindow()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowStartupResumeCoordinatorTests), "resume-coordinator-duplicate.log", LogLevel.Info);

        var recoveryHandler = new FakeResumeRecoveryHandler();
        var coordinator = CreateCoordinator(loggerScope.Logger, recoveryHandler);

        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), nameof(HandlePowerModeChanged_IgnoresDuplicateResumeSignalsWithinCooldownWindow));
        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), nameof(HandlePowerModeChanged_IgnoresDuplicateResumeSignalsWithinCooldownWindow));

        await recoveryHandler.WaitForCompletionAsync();

        Assert.Equal(1, recoveryHandler.InvocationCount);
    }

    [Fact]
    public async Task HandlePowerModeChanged_SkipsDuplicateResumeSignals_WhenRecoveryIsAlreadyInProgress()
    {
        using var loggerScope = TestLoggerScope.CreateFileBacked(nameof(MainWindowStartupResumeCoordinatorTests), "resume-coordinator-skip.log", LogLevel.Info);

        var recoveryHandler = new FakeResumeRecoveryHandler
        {
            BlockUntilReleased = true,
        };
        var coordinator = CreateCoordinator(loggerScope.Logger, recoveryHandler);

        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), nameof(HandlePowerModeChanged_SkipsDuplicateResumeSignals_WhenRecoveryIsAlreadyInProgress));
        string opId = await recoveryHandler.WaitForInvocationAsync();

        await recoveryHandler.WaitForBlockEntryAsync();
        TestPrivateAccess.SetField(coordinator, "_lastResumeSignalUtc", DateTime.UtcNow.AddSeconds(-2));
        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), nameof(HandlePowerModeChanged_SkipsDuplicateResumeSignals_WhenRecoveryIsAlreadyInProgress));

        recoveryHandler.Release();
        await recoveryHandler.WaitForCompletionAsync();

        Assert.Equal(1, recoveryHandler.InvocationCount);
    }

    [Fact]
    public async Task HandlePowerModeChanged_AfterDispose_DoesNotRunQueuedRecovery()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowStartupResumeCoordinatorTests), "resume-coordinator-disposed.log", LogLevel.Info);

        var recoveryHandler = new FakeResumeRecoveryHandler();
        Func<Task>? queuedRecovery = null;
        var queuedRecoveryCaptured = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new MainWindowStartupResumeCoordinator(
            loggerScope.Logger,
            recoveryHandler,
            new MainWindowStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => true,
                InitializeStartupAsync: static _ => Task.CompletedTask,
                CaptureInitialHotplugSnapshot: static () => { }),
            queueResumeRecoveryWork: work =>
            {
                queuedRecovery = work;
                queuedRecoveryCaptured.TrySetResult(true);
                return Task.CompletedTask;
            },
            showStartupError: _ => { },
            shutdown: () => { });

        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), nameof(HandlePowerModeChanged_AfterDispose_DoesNotRunQueuedRecovery));
        await queuedRecoveryCaptured.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.Dispose();
        Assert.NotNull(queuedRecovery);

        await queuedRecovery!();
        await Task.Delay(50);

        Assert.Equal(0, recoveryHandler.InvocationCount);
    }

    [Fact]
    public async Task HandleWindowLoadedAsync_InitializesStartup_WithNoSettingsFlagAndCapturesSnapshot()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowStartupResumeCoordinatorTests), "startup-load.log", LogLevel.Info);

        bool? receivedNoSettingsFlag = null;
        int captureSnapshotCalls = 0;
        var coordinator = CreateCoordinator(
            loggerScope.Logger,
            new FakeResumeRecoveryHandler(),
            new MainWindowStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => false,
                InitializeStartupAsync: noSettings =>
                {
                    receivedNoSettingsFlag = noSettings;
                    return Task.CompletedTask;
                },
                CaptureInitialHotplugSnapshot: () => captureSnapshotCalls++));

        await coordinator.HandleWindowLoadedAsync(nameof(HandleWindowLoadedAsync_InitializesStartup_WithNoSettingsFlagAndCapturesSnapshot));

        Assert.True(receivedNoSettingsFlag);
        Assert.Equal(1, captureSnapshotCalls);
    }

    [Fact]
    public async Task HandleWindowLoadedAsync_IgnoresDuplicateLoadedEvent()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowStartupResumeCoordinatorTests), "startup-duplicate.log", LogLevel.Info);

        int initializeCalls = 0;
        int captureSnapshotCalls = 0;
        var coordinator = CreateCoordinator(
            loggerScope.Logger,
            new FakeResumeRecoveryHandler(),
            new MainWindowStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => true,
                InitializeStartupAsync: _ =>
                {
                    initializeCalls++;
                    return Task.CompletedTask;
                },
                CaptureInitialHotplugSnapshot: () => captureSnapshotCalls++));

        await coordinator.HandleWindowLoadedAsync(nameof(HandleWindowLoadedAsync_IgnoresDuplicateLoadedEvent));
        await coordinator.HandleWindowLoadedAsync(nameof(HandleWindowLoadedAsync_IgnoresDuplicateLoadedEvent));

        Assert.Equal(1, initializeCalls);
        Assert.Equal(1, captureSnapshotCalls);
    }

    [Fact]
    public async Task HandleWindowLoadedAsync_ContinuesStartup_WhenNotificationRegistrationFails()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowStartupResumeCoordinatorTests), "startup-notification-warning.log", LogLevel.Info);

        int initializeCalls = 0;
        int captureSnapshotCalls = 0;
        int shutdownCalls = 0;
        int showErrorCalls = 0;
        var coordinator = CreateCoordinator(
            loggerScope.Logger,
            new FakeResumeRecoveryHandler(),
            new MainWindowStartupResumeDependencies(
                RegisterNotificationClient: static () => throw new InvalidOperationException("boom"),
                SettingsFileExists: static () => true,
                InitializeStartupAsync: _ =>
                {
                    initializeCalls++;
                    return Task.CompletedTask;
                },
                CaptureInitialHotplugSnapshot: () => captureSnapshotCalls++),
            _ => showErrorCalls++,
            () => shutdownCalls++);

        await coordinator.HandleWindowLoadedAsync(nameof(HandleWindowLoadedAsync_ContinuesStartup_WhenNotificationRegistrationFails));

        Assert.Equal(1, initializeCalls);
        Assert.Equal(1, captureSnapshotCalls);
        Assert.Equal(0, shutdownCalls);
        Assert.Equal(0, showErrorCalls);
    }

    [Fact]
    public async Task HandleWindowLoadedAsync_WhenStartupCanceled_SkipsSnapshotAndShutdown()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowStartupResumeCoordinatorTests), "startup-cancel.log", LogLevel.Info);

        int captureSnapshotCalls = 0;
        int shutdownCalls = 0;
        int showErrorCalls = 0;
        var coordinator = CreateCoordinator(
            loggerScope.Logger,
            new FakeResumeRecoveryHandler(),
            new MainWindowStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => true,
                InitializeStartupAsync: static _ => Task.FromCanceled(new CancellationToken(canceled: true)),
                CaptureInitialHotplugSnapshot: () => captureSnapshotCalls++),
            _ => showErrorCalls++,
            () => shutdownCalls++);

        await coordinator.HandleWindowLoadedAsync(nameof(HandleWindowLoadedAsync_WhenStartupCanceled_SkipsSnapshotAndShutdown));

        Assert.Equal(0, captureSnapshotCalls);
        Assert.Equal(0, shutdownCalls);
        Assert.Equal(0, showErrorCalls);
    }

    [Fact]
    public async Task HandleWindowLoadedAsync_WhenStartupFails_ShowsErrorAndShutsDown()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowStartupResumeCoordinatorTests), "startup-fail.log", LogLevel.Info);

        int captureSnapshotCalls = 0;
        int shutdownCalls = 0;
        string? errorMessage = null;
        var coordinator = CreateCoordinator(
            loggerScope.Logger,
            new FakeResumeRecoveryHandler(),
            new MainWindowStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => true,
                InitializeStartupAsync: static _ => Task.FromException(new InvalidOperationException("boom")),
                CaptureInitialHotplugSnapshot: () => captureSnapshotCalls++),
            message => errorMessage = message,
            () => shutdownCalls++);

        await coordinator.HandleWindowLoadedAsync(nameof(HandleWindowLoadedAsync_WhenStartupFails_ShowsErrorAndShutsDown));

        Assert.Equal(0, captureSnapshotCalls);
        Assert.Equal(1, shutdownCalls);
        Assert.NotNull(errorMessage);
        Assert.Contains("Failed to initialize application services", errorMessage, StringComparison.Ordinal);
    }

    private static MainWindowStartupResumeCoordinator CreateCoordinator(Logger logger, IResumeRecoveryHandler recoveryHandler)
    {
        return CreateCoordinator(
            logger,
            recoveryHandler,
            new MainWindowStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => true,
                InitializeStartupAsync: static _ => Task.CompletedTask,
                CaptureInitialHotplugSnapshot: static () => { }));
    }

    private static MainWindowStartupResumeCoordinator CreateCoordinator(
        Logger logger,
        IResumeRecoveryHandler recoveryHandler,
        MainWindowStartupResumeDependencies dependencies,
        Action<string>? showStartupError = null,
        Action? shutdown = null)
    {
        return new MainWindowStartupResumeCoordinator(
            logger,
            recoveryHandler,
            dependencies,
            static work => Task.Run(work),
            showStartupError ?? (_ => { }),
            shutdown ?? (() => { }));
    }

    [GeneratedRegex(@"opId=(resume:[0-9a-f]{32})", RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
}
