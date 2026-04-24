using System.Collections.Concurrent;
using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Internal
{
    internal sealed class HotkeyDispatchCoordinator(Logger logger)
    {
        private static readonly long DebounceTicks = AppConstants.Timing.HotkeyDebounceTicks;
        private static readonly long DebounceRetentionTicks = AppConstants.Timing.HotkeyDebounceRetentionTicks;

        private readonly Logger _logger = logger;
        private readonly Func<long> _timestampTicksProvider = GetMonotonicTimestampTicks;
        private readonly ConcurrentDictionary<int, long> _debounceTimestamps = new();
        private readonly Lock _metricsLock = new();
        private DateTime _metricsWindowStartUtc = DateTime.UtcNow;
        private int _metricsDispatchCount;
        private double _metricsDispatchTotalMs;
        private double _metricsDispatchMaxMs;
        private long _executeTraceSampleCounter;
        private long _dispatchLatencyTraceSampleCounter;
        private long _lastDebounceTrimTicks;

        internal int DebounceTimestampCountForTests => _debounceTimestamps.Count;

        internal HotkeyDispatchCoordinator(Logger logger, Func<long> timestampTicksProvider)
            : this(logger)
        {
            _timestampTicksProvider = timestampTicksProvider;
        }

        public void ExecuteCallback(int hotkeyId, string description, Action? callback, long? enqueueTimestamp = null)
        {
            long now = _timestampTicksProvider();
            MaybeTrimDebounceTimestamps(now);

            if (!TryReserveDebounceSlot(hotkeyId, now))
            {
                return;
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                long executeCount = Interlocked.Increment(ref _executeTraceSampleCounter);
                int executeTraceInterval = Math.Max(1, AppConstants.Timing.HotkeyExecuteTraceLogEveryN);
                if (executeCount == 1 || (executeCount % executeTraceInterval) == 0)
                {
                    _logger.Trace("HotkeyService", () => $"{AppConstants.Audio.LogEvents.Hotkey.Execute} | id={hotkeyId} description={FormatHotkeyDescriptionForLog(description)} sampled=true count={executeCount}");
                }
            }

            double hookToQueueMs = 0;
            if (enqueueTimestamp.HasValue)
            {
                long elapsedTicks = Stopwatch.GetTimestamp() - enqueueTimestamp.Value;
                hookToQueueMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
                RecordDispatchMetric(hookToQueueMs);

                if (_logger.IsEnabled(LogLevel.Trace) && hookToQueueMs >= 2.0)
                {
                    long latencyCount = Interlocked.Increment(ref _dispatchLatencyTraceSampleCounter);
                    int latencyTraceInterval = Math.Max(1, AppConstants.Timing.HotkeyDispatchLatencyTraceLogEveryN);
                    if (latencyCount == 1 || (latencyCount % latencyTraceInterval) == 0)
                    {
                        _logger.Trace("HotkeyService", () => $"{AppConstants.Audio.LogEvents.Hotkey.DispatchLatency} | id={hotkeyId} ms={hookToQueueMs:F2} description={FormatHotkeyDescriptionForLog(description)} sampled=true count={latencyCount}");
                    }
                }
            }

            ThreadPool.UnsafeQueueUserWorkItem(static state =>
            {
                var (callback, description, logger, dispatchLatencyMs) = state;
                try
                {
                    if (dispatchLatencyMs >= 8.0 && logger.IsEnabled(LogLevel.Warning))
                    {
                        logger.Warning("HotkeyService", () => $"hotkey-dispatch-latency-high | ms={dispatchLatencyMs:F2} description={FormatHotkeyDescriptionForLog(description)}");
                    }

                    callback?.Invoke();
                }
                catch (Exception ex)
                {
                    logger.Error("HotkeyService", () => $"hotkey-dispatch-failed | description={FormatHotkeyDescriptionForLog(description)}", null, ex);
                }
            }, (callback, description, _logger, hookToQueueMs), preferLocal: true);
        }

        private bool TryReserveDebounceSlot(int hotkeyId, long now)
        {
            while (true)
            {
                if (!_debounceTimestamps.TryGetValue(hotkeyId, out long last))
                {
                    if (_debounceTimestamps.TryAdd(hotkeyId, now))
                    {
                        return true;
                    }

                    continue;
                }

                long elapsedTicks = now - last;
                if (elapsedTicks >= 0 && elapsedTicks < DebounceTicks)
                {
                    return false;
                }

                if (_debounceTimestamps.TryUpdate(hotkeyId, now, last))
                {
                    return true;
                }
            }
        }

        public void Reset()
        {
            _debounceTimestamps.Clear();
            Interlocked.Exchange(ref _lastDebounceTrimTicks, 0);
        }

        private void MaybeTrimDebounceTimestamps(long now)
        {
            int count = _debounceTimestamps.Count;
            long lastTrim = Interlocked.Read(ref _lastDebounceTrimTicks);
            bool retentionWindowElapsed = (now - lastTrim) >= DebounceRetentionTicks;
            bool overLimit = count > AppConstants.Limits.MaxHotkeyDebounceEntries;

            if (!retentionWindowElapsed && !overLimit)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastDebounceTrimTicks, now, lastTrim) != lastTrim)
            {
                return;
            }

            long cutoff = now - DebounceRetentionTicks;
            foreach (var kvp in _debounceTimestamps)
            {
                if (kvp.Value < cutoff)
                {
                    _debounceTimestamps.TryRemove(kvp.Key, out _);
                }
            }

            while (_debounceTimestamps.Count > AppConstants.Limits.MaxHotkeyDebounceEntries)
            {
                int oldestKey = 0;
                long oldestTimestamp = long.MaxValue;

                foreach (var kvp in _debounceTimestamps)
                {
                    if (kvp.Value >= oldestTimestamp)
                    {
                        continue;
                    }

                    oldestKey = kvp.Key;
                    oldestTimestamp = kvp.Value;
                }

                if (oldestTimestamp == long.MaxValue)
                {
                    break;
                }

                _debounceTimestamps.TryRemove(oldestKey, out _);
            }
        }

        private void RecordDispatchMetric(double latencyMs)
        {
            lock (_metricsLock)
            {
                _metricsDispatchCount++;
                _metricsDispatchTotalMs += latencyMs;
                _metricsDispatchMaxMs = Math.Max(_metricsDispatchMaxMs, latencyMs);

                DateTime now = DateTime.UtcNow;
                TimeSpan windowElapsed = now - _metricsWindowStartUtc;
                if (windowElapsed < TimeSpan.FromSeconds(AppConstants.Timing.HotkeyDiagnosticsWindowSeconds))
                {
                    return;
                }

                if (_metricsDispatchCount > 0 && _logger.IsEnabled(LogLevel.Debug))
                {
                    double avg = _metricsDispatchTotalMs / _metricsDispatchCount;
                    _logger.Debug("HotkeyService", () => $"{AppConstants.Audio.LogEvents.Hotkey.DispatchDiagnostics} | count={_metricsDispatchCount} avgMs={avg:F2} maxMs={_metricsDispatchMaxMs:F2} windowSeconds={windowElapsed.TotalSeconds:F0}");
                }

                _metricsWindowStartUtc = now;
                _metricsDispatchCount = 0;
                _metricsDispatchTotalMs = 0;
                _metricsDispatchMaxMs = 0;
            }
        }

        private static string FormatHotkeyDescriptionForLog(string? description)
        {
            return LogPrivacy.Label(description);
        }

        private static long GetMonotonicTimestampTicks()
        {
            return Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks;
        }
    }
}
