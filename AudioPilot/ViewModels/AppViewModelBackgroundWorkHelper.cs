using System.Collections.Concurrent;
using AudioPilot.Helpers;
using AudioPilot.Logging;

namespace AudioPilot.ViewModels
{
    internal sealed class AppViewModelBackgroundWorkHelper(Logger logger, Func<bool> isCleaningUp)
    {
        private readonly Logger _logger = logger;
        private readonly Func<bool> _isCleaningUp = isCleaningUp;

        public bool TryQueue(
            ConcurrentDictionary<int, Task> backgroundTasks,
            ref int backgroundTaskId,
            CancellationTokenSource backgroundWorkCts,
            Func<CancellationToken, Task> operation,
            string operationName)
        {
            return BackgroundTaskHelper.TryQueueWithPolicy(
                backgroundTasks,
                ref backgroundTaskId,
                backgroundWorkCts,
                _isCleaningUp,
                operation,
                operationName,
                (name, ex) =>
                {
                    if (!_isCleaningUp())
                    {
                        _logger.Error("AppViewModel", () => $"background-operation-failed | operation={name} error={ex.GetType().Name}", name, ex);
                    }
                });
        }

        public static void Cancel(CancellationTokenSource backgroundWorkCts)
        {
            backgroundWorkCts.Cancel();
        }

        public static Task[] SnapshotPendingTasks(ConcurrentDictionary<int, Task> backgroundTasks)
        {
            return BackgroundTaskHelper.SnapshotPendingTasks(backgroundTasks);
        }

        public static void DisposeResources(CancellationTokenSource backgroundWorkCts, ConcurrentDictionary<int, Task> backgroundTasks)
        {
            BackgroundTaskHelper.DisposeResources(backgroundWorkCts, backgroundTasks);
        }
    }
}
