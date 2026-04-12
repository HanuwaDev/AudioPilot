using AudioPilot.Coordinators;
using AudioPilot.Logging;

namespace AudioPilot.Tests.Coordinators;

public sealed class SingleInstanceStartupRecoveryCoordinatorTests
{
    [Fact]
    public void Resolve_WhenRetrySucceedsByAcquiring_ContinuesStartup()
    {
        var coordinator = CreateCoordinator(
            SingleInstanceRecoveryPromptResult.Retry,
            new SingleInstanceProcessRecoveryHelper(Logger.Instance));

        SingleInstanceStartupRecoveryResult result = coordinator.Resolve(
            static () => new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.Acquired));

        Assert.True(result.ContinueStartup);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Resolve_WhenRetryFindsHealthyExistingInstance_ExitsCleanly()
    {
        var coordinator = CreateCoordinator(
            SingleInstanceRecoveryPromptResult.Retry,
            new SingleInstanceProcessRecoveryHelper(Logger.Instance));

        SingleInstanceStartupRecoveryResult result = coordinator.Resolve(
            static () => new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingHealthyInstance));

        Assert.False(result.ContinueStartup);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("healthy-existing-instance", result.FailureReason);
    }

    [Fact]
    public void Resolve_WhenRetryStillUnresponsive_ShowsError()
    {
        List<string> errors = [];
        var coordinator = CreateCoordinator(
            SingleInstanceRecoveryPromptResult.Retry,
            new SingleInstanceProcessRecoveryHelper(Logger.Instance),
            errors);

        SingleInstanceStartupRecoveryResult result = coordinator.Resolve(
            static () => new SingleInstanceAcquireResult(
                SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance,
                SingleInstanceSignalFailureKind.ConnectionFailed));

        Assert.False(result.ContinueStartup);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal("retry-unresponsive", result.FailureReason);
        Assert.Single(errors);
    }

    [Fact]
    public void Resolve_WhenTerminateSucceedsAndReacquires_ContinuesStartup()
    {
        var processRecoveryHelper = new SingleInstanceProcessRecoveryHelper(
            Logger.Instance,
            enumerateProcesses: static () =>
            [
                new SingleInstanceProcessInfo(42, "AudioPilot", @"C:\Apps\AudioPilot.exe", HasMainWindow: true),
            ],
            getCurrentProcessId: static () => 99,
            getCurrentExecutablePath: static () => @"C:\Apps\AudioPilot.exe",
            tryCloseMainWindow: static _ => true,
            waitForExit: static (_, _) => true);

        var coordinator = CreateCoordinator(
            SingleInstanceRecoveryPromptResult.TerminateExistingAndContinue,
            processRecoveryHelper);

        SingleInstanceStartupRecoveryResult result = coordinator.Resolve(
            static () => new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.Acquired));

        Assert.True(result.ContinueStartup);
    }

    [Fact]
    public void Resolve_WhenTerminateFails_ShowsError()
    {
        List<string> errors = [];
        var processRecoveryHelper = new SingleInstanceProcessRecoveryHelper(
            Logger.Instance,
            enumerateProcesses: static () => [],
            getCurrentProcessId: static () => 99,
            getCurrentExecutablePath: static () => @"C:\Apps\AudioPilot.exe");

        var coordinator = CreateCoordinator(
            SingleInstanceRecoveryPromptResult.TerminateExistingAndContinue,
            processRecoveryHelper,
            errors);

        SingleInstanceStartupRecoveryResult result = coordinator.Resolve(
            static () => new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance));

        Assert.False(result.ContinueStartup);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal("no-matching-process", result.FailureReason);
        Assert.Single(errors);
    }

    [Fact]
    public void Resolve_WhenCancelled_ExitsWithoutContinuing()
    {
        var coordinator = CreateCoordinator(
            SingleInstanceRecoveryPromptResult.Cancel,
            new SingleInstanceProcessRecoveryHelper(Logger.Instance));

        SingleInstanceStartupRecoveryResult result = coordinator.Resolve(
            static () => new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance));

        Assert.False(result.ContinueStartup);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("cancelled", result.FailureReason);
    }

    private static SingleInstanceStartupRecoveryCoordinator CreateCoordinator(
        SingleInstanceRecoveryPromptResult promptResult,
        SingleInstanceProcessRecoveryHelper processRecoveryHelper,
        List<string>? shownErrors = null)
    {
        return new SingleInstanceStartupRecoveryCoordinator(
            processRecoveryHelper,
            Logger.Instance,
            promptForRecovery: () => promptResult,
            showError: (message, _) => shownErrors?.Add(message));
    }
}
