using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal sealed class AppSwitchRetryCoordinator(Logger logger)
    {
        private readonly Logger _logger = logger;

        public void TryQueueCoalescedRetry(
            Func<bool> tryBeginRetry,
            string skipEventName,
            string opId,
            AppSwitchRequestRejectionReason rejectionReason,
            IReadOnlyList<CycleDevice> configuredCycle,
            long lastRequestTicks,
            int debounceMs,
            Func<IReadOnlyList<CycleDevice>, List<CycleDevice>> cloneCycleSnapshot,
            Func<int, IReadOnlyList<CycleDevice>, Task> runBackgroundAsync)
        {
            if (!tryBeginRetry())
            {
                return;
            }

            List<CycleDevice> cycleSnapshot = cloneCycleSnapshot(configuredCycle);
            _logger.Debug("AppViewModel", () => $"{skipEventName} | opId={opId} reason=coalesced-retry-queued trigger={rejectionReason}");

            int retryDelayMs = ResolveCoalescedRetryDelayMs(
                wasDebounced: rejectionReason == AppSwitchRequestRejectionReason.Debounced,
                nowTicks: DateTime.UtcNow.Ticks,
                lastRequestTicks: lastRequestTicks,
                debounceWindowTicks: TimeSpan.FromMilliseconds(debounceMs).Ticks,
                fallbackDelayMs: debounceMs);

            _ = runBackgroundAsync(retryDelayMs, cycleSnapshot);
        }

        public static Task RunCoalescedRetryBackgroundAsync(
            int retryDelayMs,
            Func<Task> runSwitchAsync,
            Action endRetry,
            Func<CancellationToken> getLifetimeCancellationToken)
        {
            return RunCoalescedRetryBackgroundAsync(
                retryDelayMs,
                runSwitchAsync,
                endRetry,
                getLifetimeCancellationToken,
                static operationToken => CancellationTokenSource.CreateLinkedTokenSource(operationToken),
                CancellationToken.None);
        }

        public static async Task RunCoalescedRetryBackgroundAsync(
            int retryDelayMs,
            Func<Task> runSwitchAsync,
            Action endRetry,
            Func<CancellationToken> getLifetimeCancellationToken,
            Func<CancellationToken, CancellationTokenSource> createLinkedCancellationSource,
            CancellationToken operationCancellationToken)
        {
            try
            {
                if (!operationCancellationToken.CanBeCanceled)
                {
                    await Task.Delay(retryDelayMs, getLifetimeCancellationToken());
                    await runSwitchAsync();
                    return;
                }

                using CancellationTokenSource linkedCts = createLinkedCancellationSource(operationCancellationToken);
                await Task.Delay(retryDelayMs, linkedCts.Token);
                operationCancellationToken.ThrowIfCancellationRequested();
                await runSwitchAsync();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                endRetry();
            }
        }

        internal static int ResolveCoalescedRetryDelayMs(
            bool wasDebounced,
            long nowTicks,
            long lastRequestTicks,
            long debounceWindowTicks,
            int fallbackDelayMs)
        {
            return AppSwitchRequestCoordinator.ResolveCoalescedRetryDelayMs(
                wasDebounced,
                nowTicks,
                lastRequestTicks,
                debounceWindowTicks,
                fallbackDelayMs);
        }
    }
}
