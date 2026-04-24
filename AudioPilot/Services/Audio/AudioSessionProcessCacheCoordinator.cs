using System.Collections.Concurrent;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal sealed class AudioSessionProcessCacheCoordinator(Logger logger, TimeSpan cacheEntryTtl) : IDisposable
    {
        internal readonly record struct CacheEntry(
            string ProcessName,
            string? DisplayName,
            string? MainWindowTitle,
            long TimestampTicks)
        {
            public bool IsExpired(TimeSpan ttl) =>
                (DateTime.UtcNow.Ticks - TimestampTicks) > ttl.Ticks;

            public static CacheEntry Create(string processName, string? displayName, string? mainWindowTitle) =>
                new(processName, displayName, mainWindowTitle, DateTime.UtcNow.Ticks);
        }

        internal readonly record struct SessionProcessMetadata(
            string ProcessName,
            string DisplayName,
            string? MainWindowTitle);

        private readonly Logger _logger = logger;
        private readonly TimeSpan _cacheEntryTtl = cacheEntryTtl;
        private readonly ConcurrentDictionary<uint, CacheEntry> _processCache = new();
        private readonly ConcurrentBag<List<uint>> _pidCleanupListPool = [];
        private readonly SemaphoreSlim _cleanupLock = new(1, 1);
        private readonly Lock _cleanupStartLock = new();
        private const int MaxProcessCacheEntries = AppConstants.Limits.MaxProcessCacheEntries;
        private const int MaxPooledCleanupListCapacity = AppConstants.Limits.MaxPidProcessMapEntries;
        private CancellationTokenSource? _cleanupCts;
        private Task? _cleanupTask;

        internal Task? CleanupTaskForTests => _cleanupTask;
        internal int ProcessCacheCount => _processCache.Count;
        internal bool IsCleanupLoopStarted => _cleanupCts != null;

        internal (string ProcessName, string? DisplayName, string? MainWindowTitle, long TimestampTicks)? GetCachedProcessInfo(uint pid)
        {
            if (_processCache.TryGetValue(pid, out var entry))
            {
                return (entry.ProcessName, entry.DisplayName, entry.MainWindowTitle, entry.TimestampTicks);
            }

            return null;
        }

        internal bool IsCacheEntryExpired(long timestampTicks) =>
            (DateTime.UtcNow.Ticks - timestampTicks) > _cacheEntryTtl.Ticks;

        internal Task StartCleanupTaskAsync(Func<bool> isDisposed)
        {
            if (isDisposed())
            {
                return Task.CompletedTask;
            }

            if (_cleanupCts != null)
            {
                return Task.CompletedTask;
            }

            lock (_cleanupStartLock)
            {
                if (isDisposed() || _cleanupCts != null)
                {
                    return Task.CompletedTask;
                }

                var cleanupCts = new CancellationTokenSource();
                _cleanupCts = cleanupCts;
                CancellationToken cleanupToken = cleanupCts.Token;
                _cleanupTask = Task.Run(() => CleanupLoopAsync(cleanupToken), cleanupToken);
            }

            return Task.CompletedTask;
        }

        internal void AddProcessCacheEntryForTests(uint pid, string processName, string? displayName, string? mainWindowTitle, long timestampTicks)
        {
            _processCache[pid] = new CacheEntry(processName, displayName, mainWindowTitle, timestampTicks);
        }

        internal void TrimProcessCacheForTests()
        {
            TrimProcessCacheIfNeeded();
        }

        internal bool TryGetOrAddEntry(
            uint processId,
            Func<CacheEntry?> cacheEntryFactory,
            out CacheEntry entry)
        {
            if (_processCache.TryGetValue(processId, out var cachedEntry) &&
                !cachedEntry.IsExpired(_cacheEntryTtl))
            {
                entry = cachedEntry;
                return true;
            }

            CacheEntry? createdEntry = cacheEntryFactory();
            if (createdEntry == null)
            {
                entry = default;
                return false;
            }

            entry = createdEntry.Value;
            _processCache[processId] = entry;
            TrimProcessCacheIfNeeded();
            return true;
        }

        internal static bool TryProjectSessionProcessMetadata(
            string processName,
            string? displayName,
            string? mainWindowTitle,
            out SessionProcessMetadata metadata)
        {
            return TryProjectSessionProcessMetadata(CacheEntry.Create(processName, displayName, mainWindowTitle), out metadata);
        }

        internal static bool TryProjectSessionProcessMetadata(CacheEntry cacheEntry, out SessionProcessMetadata metadata)
        {
            string finalDisplayName = AudioDeviceHelper.GetSessionDisplayNameFromCache(
                cacheEntry.ProcessName,
                cacheEntry.DisplayName);

            if (AudioDeviceHelper.ShouldIgnoreSessionFromCache(finalDisplayName, cacheEntry.ProcessName))
            {
                metadata = default;
                return false;
            }

            metadata = new SessionProcessMetadata(
                cacheEntry.ProcessName,
                finalDisplayName,
                cacheEntry.MainWindowTitle);
            return true;
        }

        internal static bool ShouldSkipSelfSession(uint processId, int currentProcessId)
        {
            return processId != 0 && processId == (uint)currentProcessId;
        }

        internal void Clear()
        {
            _processCache.Clear();
            while (_pidCleanupListPool.TryTake(out _))
            {
            }
        }

        private async Task CleanupLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(AppConstants.Timing.CacheCleanupIntervalMs, cancellationToken);
                    CleanupExpiredEntries();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error("AudioSessionService", "Cleanup loop error", nameof(CleanupLoopAsync), ex);
                }
            }
        }

        private void CleanupExpiredEntries()
        {
            if (!_cleanupLock.Wait(0))
            {
                return;
            }

            List<uint>? processCacheExpired = null;
            try
            {
                processCacheExpired = RentPidCleanupList();

                foreach (var kvp in _processCache)
                {
                    if (kvp.Value.IsExpired(_cacheEntryTtl))
                    {
                        processCacheExpired.Add(kvp.Key);
                    }
                }

                foreach (var pid in processCacheExpired)
                {
                    _processCache.TryRemove(pid, out _);
                }

                TrimProcessCacheIfNeeded();

                int cleanedCount = processCacheExpired.Count;
                if (cleanedCount > 0 && _logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("AudioSessionService", () => $"Cleaned {cleanedCount} expired cache entries");
                }
            }
            finally
            {
                ReturnPidCleanupList(processCacheExpired);
                _cleanupLock.Release();
            }
        }

        private List<uint> RentPidCleanupList()
        {
            if (_pidCleanupListPool.TryTake(out var list))
            {
                list.Clear();
                return list;
            }

            return [];
        }

        private void ReturnPidCleanupList(List<uint>? list)
        {
            if (list == null)
            {
                return;
            }

            int capacity = list.Capacity;
            list.Clear();
            if (capacity > MaxPooledCleanupListCapacity)
            {
                return;
            }

            _pidCleanupListPool.Add(list);
        }

        private void TrimProcessCacheIfNeeded()
        {
            int count = _processCache.Count;
            if (count <= MaxProcessCacheEntries)
            {
                return;
            }

            var orderedEntries = _processCache.ToArray();
            Array.Sort(orderedEntries, static (left, right) => left.Value.TimestampTicks.CompareTo(right.Value.TimestampTicks));

            int entriesToRemove = count - MaxProcessCacheEntries;
            int removed = 0;
            for (int index = 0; index < orderedEntries.Length && removed < entriesToRemove; index++)
            {
                if (_processCache.TryRemove(orderedEntries[index].Key, out _))
                {
                    removed++;
                }
            }

            if (removed > 0 && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("AudioSessionService", () => $"Trimmed process cache entries: removed={removed}, remaining={_processCache.Count}");
            }
        }

        public void Dispose()
        {
            CancellationTokenSource? cleanupCts = Interlocked.Exchange(ref _cleanupCts, null);
            _ = Interlocked.Exchange(ref _cleanupTask, null);

            try
            {
                cleanupCts?.Cancel();
            }
            catch
            {
            }
            finally
            {
                cleanupCts?.Dispose();
            }

            _cleanupLock.Dispose();
            Clear();
        }
    }
}
