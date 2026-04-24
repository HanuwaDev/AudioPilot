using System.Collections.Concurrent;

namespace AudioPilot.Services.Audio
{
    internal sealed class VolumeRetryStateTracker(TimeSpan retryStateTtl, TimeSpan circuitBreakerCooldown, Func<long>? utcNowTicksProvider = null)
    {
        private readonly Func<long> _utcNowTicksProvider = utcNowTicksProvider ?? (() => DateTime.UtcNow.Ticks);

        private sealed class RetryState(Func<long> utcNowTicksProvider)
        {
            private readonly Func<long> _utcNowTicksProvider = utcNowTicksProvider;
            private int _consecutiveFailures;
            private long _lastFailureTimeTicks;
            private long _lastAccessTimeTicks = utcNowTicksProvider();

            public long LastAccessTimeTicks => Interlocked.Read(ref _lastAccessTimeTicks);

            public bool IsCircuitOpen(TimeSpan circuitBreakerCooldown)
            {
                int failures = Volatile.Read(ref _consecutiveFailures);
                if (failures < 3)
                {
                    return false;
                }

                long failureTicks = Interlocked.Read(ref _lastFailureTimeTicks);
                return (_utcNowTicksProvider() - failureTicks) < circuitBreakerCooldown.Ticks;
            }

            public void RecordSuccess()
            {
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                long now = _utcNowTicksProvider();
                Interlocked.Exchange(ref _lastAccessTimeTicks, now);
            }

            public void RecordFailure()
            {
                Interlocked.Increment(ref _consecutiveFailures);
                long now = _utcNowTicksProvider();
                Interlocked.Exchange(ref _lastFailureTimeTicks, now);
                Interlocked.Exchange(ref _lastAccessTimeTicks, now);
            }
        }

        private readonly ConcurrentDictionary<string, RetryState> _retryStates = [];
        private readonly TimeSpan _retryStateTtl = retryStateTtl;
        private readonly TimeSpan _circuitBreakerCooldown = circuitBreakerCooldown;

        public void Reset(string deviceId)
        {
            if (_retryStates.TryGetValue(deviceId, out var state))
            {
                state.RecordSuccess();
            }
        }

        public void RecordFailure(string deviceId)
        {
            RetryState state = _retryStates.GetOrAdd(deviceId, _ => new RetryState(_utcNowTicksProvider));
            state.RecordFailure();
        }

        public bool IsCircuitOpen(string deviceId)
        {
            return _retryStates.TryGetValue(deviceId, out var state) && state.IsCircuitOpen(_circuitBreakerCooldown);
        }

        public int CleanupExpiredStates()
        {
            long nowTicks = _utcNowTicksProvider();
            var expiredDeviceIds = new List<string>();

            foreach (var kvp in _retryStates)
            {
                if ((nowTicks - kvp.Value.LastAccessTimeTicks) > _retryStateTtl.Ticks)
                {
                    expiredDeviceIds.Add(kvp.Key);
                }
            }

            foreach (string deviceId in expiredDeviceIds)
            {
                _retryStates.TryRemove(deviceId, out _);
            }

            return expiredDeviceIds.Count;
        }

        public void Clear()
        {
            _retryStates.Clear();
        }
    }
}
