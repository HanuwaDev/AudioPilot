using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;

namespace AudioPilot.Platform
{
    internal readonly record struct SingleInstanceProcessInfo(
        int ProcessId,
        string ProcessName,
        string? ExecutablePath,
        bool HasMainWindow);

    internal readonly record struct SingleInstanceProcessRecoveryResult(
        bool Success,
        int MatchedProcessCount,
        string? FailureReason = null);

    internal sealed class SingleInstanceProcessRecoveryHelper(
        Logger logger,
        Func<IReadOnlyList<SingleInstanceProcessInfo>>? enumerateProcesses = null,
        Func<int>? getCurrentProcessId = null,
        Func<string?>? getCurrentExecutablePath = null,
        Func<int, bool>? tryCloseMainWindow = null,
        Func<int, TimeSpan, bool>? waitForExit = null,
        Action<int>? killProcess = null)
    {
        private readonly Logger _logger = logger;
        private readonly Func<IReadOnlyList<SingleInstanceProcessInfo>> _enumerateProcesses = enumerateProcesses ?? EnumerateProcesses;
        private readonly Func<int> _getCurrentProcessId = getCurrentProcessId ?? (() => Environment.ProcessId);
        private readonly Func<string?> _getCurrentExecutablePath = getCurrentExecutablePath ?? ResolveCurrentExecutablePath;
        private readonly Func<int, bool> _tryCloseMainWindow = tryCloseMainWindow ?? TryCloseMainWindow;
        private readonly Func<int, TimeSpan, bool> _waitForExit = waitForExit ?? WaitForExit;
        private readonly Action<int> _killProcess = killProcess ?? KillProcess;

        internal SingleInstanceProcessRecoveryResult TryTerminateMatchingExistingProcess()
        {
            string currentExecutablePath = RoutineTriggerPathHelper.NormalizeExecutablePath(_getCurrentExecutablePath());
            if (string.IsNullOrWhiteSpace(currentExecutablePath))
            {
                return new SingleInstanceProcessRecoveryResult(false, 0, "current-executable-unavailable");
            }

            int currentProcessId = _getCurrentProcessId();
            List<SingleInstanceProcessInfo> matches = [.. _enumerateProcesses()
                .Where(process => process.ProcessId != currentProcessId)
                .Where(process => !string.IsNullOrWhiteSpace(process.ExecutablePath))
                .Where(process => string.Equals(
                    RoutineTriggerPathHelper.NormalizeExecutablePath(process.ExecutablePath),
                    currentExecutablePath,
                    StringComparison.OrdinalIgnoreCase))];

            if (matches.Count == 0)
            {
                return new SingleInstanceProcessRecoveryResult(false, 0, "no-matching-process");
            }

            TimeSpan gracefulCloseTimeout = TimeSpan.FromMilliseconds(AppConstants.Timing.SingleInstanceRecoveryGracefulCloseTimeoutMs);
            TimeSpan killWaitTimeout = TimeSpan.FromMilliseconds(AppConstants.Timing.SingleInstanceRecoveryKillWaitTimeoutMs);

            foreach (SingleInstanceProcessInfo process in matches)
            {
                if (process.HasMainWindow && _tryCloseMainWindow(process.ProcessId) && _waitForExit(process.ProcessId, gracefulCloseTimeout))
                {
                    continue;
                }

                try
                {
                    _killProcess(process.ProcessId);
                }
                catch (Exception ex)
                {
                    _logger.Warning("SingleInstanceProcessRecoveryHelper", "Failed to terminate matching AudioPilot process", nameof(TryTerminateMatchingExistingProcess), ex);
                    return new SingleInstanceProcessRecoveryResult(false, matches.Count, "terminate-failed");
                }

                if (!_waitForExit(process.ProcessId, killWaitTimeout))
                {
                    return new SingleInstanceProcessRecoveryResult(false, matches.Count, "terminate-timeout");
                }
            }

            return new SingleInstanceProcessRecoveryResult(true, matches.Count);
        }

        private static IReadOnlyList<SingleInstanceProcessInfo> EnumerateProcesses()
        {
            List<SingleInstanceProcessInfo> processes = [];
            ProcessEnumerationHelper.EnumerateProcesses(process =>
            {
                string? executablePath = null;
                try
                {
                    executablePath = process.MainModule?.FileName;
                }
                catch
                {
                }

                processes.Add(new SingleInstanceProcessInfo(
                    process.Id,
                    process.ProcessName,
                    executablePath,
                    process.MainWindowHandle != IntPtr.Zero));
            });

            return processes;
        }

        private static string? ResolveCurrentExecutablePath()
        {
            try
            {
                return Process.GetCurrentProcess().MainModule?.FileName
                    ?? Environment.ProcessPath;
            }
            catch
            {
                return Environment.ProcessPath;
            }
        }

        private static bool TryCloseMainWindow(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return process.CloseMainWindow();
            }
            catch
            {
                return false;
            }
        }

        private static bool WaitForExit(int processId, TimeSpan timeout)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return process.WaitForExit((int)timeout.TotalMilliseconds);
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        private static void KillProcess(int processId)
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill();
        }
    }
}
