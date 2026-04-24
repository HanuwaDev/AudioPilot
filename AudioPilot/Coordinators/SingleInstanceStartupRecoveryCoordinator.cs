using System.Windows;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    internal enum SingleInstanceRecoveryPromptResult
    {
        Retry = 0,
        TerminateExistingAndContinue = 1,
        Cancel = 2,
    }

    internal readonly record struct SingleInstanceStartupRecoveryResult(
        bool ContinueStartup,
        int ExitCode,
        string? FailureReason = null);

    internal sealed class SingleInstanceStartupRecoveryCoordinator(
        SingleInstanceProcessRecoveryHelper processRecoveryHelper,
        Logger logger,
        Func<SingleInstanceRecoveryPromptResult>? promptForRecovery = null,
        Action<string, string>? showError = null)
    {
        private readonly SingleInstanceProcessRecoveryHelper _processRecoveryHelper = processRecoveryHelper;
        private readonly Logger _logger = logger;
        private readonly Func<SingleInstanceRecoveryPromptResult> _promptForRecovery = promptForRecovery ?? PromptForRecovery;
        private readonly Action<string, string> _showError = showError ?? MessageBoxService.ShowError;

        internal SingleInstanceStartupRecoveryResult Resolve(Func<SingleInstanceAcquireResult> tryAcquire)
        {
            SingleInstanceRecoveryPromptResult promptResult = _promptForRecovery();
            return promptResult switch
            {
                SingleInstanceRecoveryPromptResult.Retry => RetryAcquire(tryAcquire),
                SingleInstanceRecoveryPromptResult.TerminateExistingAndContinue => TerminateAndReacquire(tryAcquire),
                _ => new SingleInstanceStartupRecoveryResult(false, 0, "cancelled"),
            };
        }

        private SingleInstanceStartupRecoveryResult RetryAcquire(Func<SingleInstanceAcquireResult> tryAcquire)
        {
            if (_logger.IsEnabled(LogLevel.Info))
            {
                _logger.Info("App", AppConstants.Audio.LogEvents.SingleInstance.RecoveryRetry);
            }

            SingleInstanceAcquireResult retryResult = tryAcquire();
            if (retryResult.Acquired)
            {
                return new SingleInstanceStartupRecoveryResult(true, 0);
            }

            if (retryResult.ExistingHealthy)
            {
                return new SingleInstanceStartupRecoveryResult(false, 0, "healthy-existing-instance");
            }

            _showError(
                "AudioPilot is still not responding. Close the existing instance or try again later.",
                DialogText.Captions.StartupError);
            return new SingleInstanceStartupRecoveryResult(false, 4, "retry-unresponsive");
        }

        private SingleInstanceStartupRecoveryResult TerminateAndReacquire(Func<SingleInstanceAcquireResult> tryAcquire)
        {
            if (_logger.IsEnabled(LogLevel.Info))
            {
                _logger.Info("App", AppConstants.Audio.LogEvents.SingleInstance.RecoveryTerminateStart);
            }

            SingleInstanceProcessRecoveryResult terminationResult = _processRecoveryHelper.TryTerminateMatchingExistingProcess();
            if (!terminationResult.Success)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.Warning("App", () => $"{AppConstants.Audio.LogEvents.SingleInstance.RecoveryTerminateFailed} | reason={(terminationResult.FailureReason ?? "unknown")} matched={terminationResult.MatchedProcessCount}");
                }

                _showError(
                    "AudioPilot could not terminate the unresponsive existing instance.",
                    DialogText.Captions.StartupError);
                return new SingleInstanceStartupRecoveryResult(false, 4, terminationResult.FailureReason ?? "terminate-failed");
            }

            if (_logger.IsEnabled(LogLevel.Info))
            {
                _logger.Info("App", () => $"{AppConstants.Audio.LogEvents.SingleInstance.RecoveryTerminateSuccess} | matched={terminationResult.MatchedProcessCount}");
            }

            SingleInstanceAcquireResult reacquireResult = tryAcquire();
            if (reacquireResult.Acquired)
            {
                return new SingleInstanceStartupRecoveryResult(true, 0);
            }

            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.Warning("App", () => $"{AppConstants.Audio.LogEvents.SingleInstance.RecoveryReacquireFailed} | disposition={reacquireResult.Disposition} failureKind={reacquireResult.FailureKind}");
            }

            _showError(
                "AudioPilot could not restart after terminating the unresponsive instance.",
                DialogText.Captions.StartupError);
            return new SingleInstanceStartupRecoveryResult(false, 4, "reacquire-failed");
        }

        private static SingleInstanceRecoveryPromptResult PromptForRecovery()
        {
            MessageBoxResult result = MessageBoxService.Show(
                "AudioPilot appears to be running but is not responding.\n\nYes = Retry\nNo = Terminate the existing AudioPilot instance and continue startup\nCancel = Exit",
                DialogText.Captions.StartupError,
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            return result switch
            {
                MessageBoxResult.Yes => SingleInstanceRecoveryPromptResult.Retry,
                MessageBoxResult.No => SingleInstanceRecoveryPromptResult.TerminateExistingAndContinue,
                _ => SingleInstanceRecoveryPromptResult.Cancel,
            };
        }
    }
}
