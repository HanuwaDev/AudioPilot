using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Hotkeys
{
    internal static class ThreadedWin32HostHelper
    {
        public static Thread StartBackgroundWorker(ThreadStart workerLoop, string threadName)
        {
            Thread worker = new(workerLoop)
            {
                IsBackground = true,
                Name = threadName,
            };

            worker.Start();
            return worker;
        }

        public static bool WaitForStartup(
            ManualResetEventSlim started,
            Exception? startupFailure,
            Func<bool> isRunning,
            Logger logger,
            string logSource,
            string timeoutMessage)
        {
            if (!started.Wait(AppConstants.Timing.CleanupWaitMs))
            {
                logger.Warning(logSource, () => timeoutMessage);
                return false;
            }

            return startupFailure == null && isRunning();
        }

        public static void RequestStopAndJoin(
            Thread? worker,
            uint workerThreadId,
            Action<uint> requestStop,
            Logger logger,
            string logSource,
            string timeoutMessage)
        {
            if (worker == null)
            {
                return;
            }

            if (workerThreadId != 0)
            {
                requestStop(workerThreadId);
            }

            if (!worker.Join(AppConstants.Timing.CleanupWaitMs))
            {
                logger.Warning(logSource, () => timeoutMessage);
            }
        }
    }
}
