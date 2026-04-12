using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelCleanupDrainLogHelperTests
{
    [Fact]
    public void CreateCallbacks_LogsInitialTimeoutAndGraceCompletion()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelCleanupDrainLogHelperTests), "cleanup-drain.log");

        var callbacks = AppViewModelCleanupDrainLogHelper.CreateCallbacks(loggerScope.Logger, "cleanup:test-grace");

        callbacks.LogInitialTimeout(2);
        callbacks.LogCompletedWithinGrace();

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains(AppViewModelCleanupDrainLogHelper.BuildInitialTimeoutMessage("cleanup:test-grace", 2), logText, StringComparison.Ordinal);
        Assert.Contains(AppViewModelCleanupDrainLogHelper.BuildCompletedWithinGraceMessage("cleanup:test-grace"), logText, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateCallbacks_LogsForcedTimeout()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelCleanupDrainLogHelperTests), "cleanup-drain-forced.log");

        var callbacks = AppViewModelCleanupDrainLogHelper.CreateCallbacks(loggerScope.Logger, "cleanup:test-forced");

        callbacks.LogForcedTimeout(3);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains(AppViewModelCleanupDrainLogHelper.BuildForcedTimeoutMessage("cleanup:test-forced", 3), logText, StringComparison.Ordinal);
    }
}
