using System.Collections.ObjectModel;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal sealed class ScheduleTriggerCoordinator(
        ObservableCollection<AudioRoutine> routines,
        Action<AudioRoutine, string> executeRoutine,
        Logger logger,
        TimeSpan debounceInterval = default,
        Func<DateTime>? nowProvider = null) : IDisposable
    {
        private Timer? _timer;
        private readonly Lock _lock = new();
        private readonly Dictionary<string, DateTime> _lastExecutionByRoutineId = [];
        private DateTime? _lastCheckUtc;
        private bool _disposed;
        private readonly TimeSpan _debounceInterval = debounceInterval == default ? TimeSpan.FromMinutes(1) : debounceInterval;
        private readonly Func<DateTime> _nowProvider = nowProvider ?? (() => DateTime.Now);

        public void Start()
        {
            DateTime catchUpStartUtc;
            DateTime nowUtc;

            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                if (_timer != null)
                {
                    return;
                }

                DateTime now = _nowProvider();
                nowUtc = NormalizeToUtc(now);
                catchUpStartUtc = TruncateToMinute(nowUtc);
                _lastCheckUtc = nowUtc;

                DateTime nextMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind).AddMinutes(1);
                TimeSpan initialDelay = nextMinute - now;

                _timer = new Timer(
                    CheckScheduledRoutines,
                    null,
                    initialDelay,
                    TimeSpan.FromMinutes(1));

                logger.Info("ScheduleTriggerCoordinator", () => "Scheduler started");
            }

            CheckScheduledRoutinesCore(catchUpStartUtc, nowUtc, includeWindowStart: true);
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                    logger.Info("ScheduleTriggerCoordinator", () => "Scheduler stopped");
                }
            }
        }

        private void CheckScheduledRoutines(object? state)
        {
            DateTime nowUtc;
            DateTime windowStartUtc;

            lock (_lock)
            {
                if (_disposed || _timer == null)
                {
                    return;
                }

                nowUtc = NormalizeToUtc(_nowProvider());
                windowStartUtc = _lastCheckUtc ?? TruncateToMinute(nowUtc);
                _lastCheckUtc = nowUtc;
            }

            CheckScheduledRoutinesCore(windowStartUtc, nowUtc, includeWindowStart: false);
        }

        private void CheckScheduledRoutinesCore(DateTime windowStartUtc, DateTime nowUtc, bool includeWindowStart)
        {
            var routinesToExecute = new List<AudioRoutine>();
            List<AudioRoutine> routinesCopy;

            lock (_lock)
            {
                routinesCopy = [.. routines];
            }

            foreach (AudioRoutine routine in routinesCopy)
            {
                if (!routine.Enabled || routine.TriggerKind != RoutineTriggerKind.Scheduled)
                {
                    continue;
                }

                TimeZoneInfo routineTimeZone = ResolveRoutineTimeZone(routine.ScheduleTimeZoneId);
                if (!HasScheduledOccurrenceInWindow(routine, routineTimeZone, windowStartUtc, nowUtc, includeWindowStart))
                {
                    continue;
                }

                lock (_lock)
                {
                    if (_lastExecutionByRoutineId.TryGetValue(routine.Id, out DateTime lastExecution))
                    {
                        if ((nowUtc - lastExecution) < _debounceInterval)
                        {
                            continue;
                        }
                    }
                }

                logger.Info("ScheduleTriggerCoordinator", () => $"scheduled-routine-trigger | routineName={LogPrivacy.Label(routine.Name)}");
                routinesToExecute.Add(routine);
            }

            foreach (AudioRoutine routine in routinesToExecute)
            {
                try
                {
                    executeRoutine(routine, "Scheduled trigger");
                    lock (_lock)
                    {
                        _lastExecutionByRoutineId[routine.Id] = nowUtc;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error("ScheduleTriggerCoordinator", () => $"scheduled-routine-trigger-failed | routineName={LogPrivacy.Label(routine.Name)} reason={ex.GetType().Name}");
                }
            }
        }

        internal static TimeZoneInfo ResolveRoutineTimeZone(string? timeZoneId)
        {
            try
            {
                return string.IsNullOrWhiteSpace(timeZoneId)
                    ? TimeZoneInfo.Local
                    : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch
            {
                return TimeZoneInfo.Local;
            }
        }

        internal static DateTime NormalizeToUtc(DateTime now)
        {
            if (now.Kind == DateTimeKind.Utc)
            {
                return now;
            }

            DateTime localNow = now.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(now, DateTimeKind.Local)
                : now;

            return localNow.ToUniversalTime();
        }

        internal static DateTime TruncateToMinute(DateTime utcTime)
        {
            DateTime normalizedUtc = NormalizeToUtc(utcTime);
            return new DateTime(normalizedUtc.Year, normalizedUtc.Month, normalizedUtc.Day, normalizedUtc.Hour, normalizedUtc.Minute, 0, DateTimeKind.Utc);
        }

        internal static DateTime ConvertNowToRoutineTimeZone(DateTime now, TimeZoneInfo routineTimeZone)
        {
            DateTime utcNow = NormalizeToUtc(now);
            return TimeZoneInfo.ConvertTimeFromUtc(utcNow, routineTimeZone);
        }

        internal static bool HasScheduledOccurrenceInWindow(
            AudioRoutine routine,
            TimeZoneInfo routineTimeZone,
            DateTime windowStartUtc,
            DateTime windowEndUtc,
            bool includeWindowStart)
        {
            DateTime normalizedStartUtc = NormalizeToUtc(windowStartUtc);
            DateTime normalizedEndUtc = NormalizeToUtc(windowEndUtc);

            if (normalizedEndUtc < normalizedStartUtc)
            {
                return false;
            }

            DateOnly startDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(normalizedStartUtc, routineTimeZone));
            DateOnly endDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(normalizedEndUtc, routineTimeZone));

            for (DateOnly localDate = startDate; localDate <= endDate; localDate = localDate.AddDays(1))
            {
                if (!OccursOnLocalDate(routine, localDate.DayOfWeek))
                {
                    continue;
                }

                if (!TryCreateScheduledOccurrenceUtc(localDate, routine.ScheduleTime, routineTimeZone, out DateTime scheduledUtc))
                {
                    continue;
                }

                if (scheduledUtc > normalizedEndUtc)
                {
                    continue;
                }

                if (includeWindowStart)
                {
                    if (scheduledUtc >= normalizedStartUtc)
                    {
                        return true;
                    }

                    continue;
                }

                if (scheduledUtc > normalizedStartUtc)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool TryCreateScheduledOccurrenceUtc(
            DateOnly localDate,
            TimeOnly scheduledTime,
            TimeZoneInfo routineTimeZone,
            out DateTime scheduledUtc)
        {
            DateTime localDateTime = localDate.ToDateTime(scheduledTime, DateTimeKind.Unspecified);
            if (routineTimeZone.IsInvalidTime(localDateTime))
            {
                scheduledUtc = default;
                return false;
            }

            TimeSpan offset;
            if (routineTimeZone.IsAmbiguousTime(localDateTime))
            {
                offset = routineTimeZone.GetAmbiguousTimeOffsets(localDateTime).Max();
            }
            else
            {
                offset = routineTimeZone.GetUtcOffset(localDateTime);
            }

            scheduledUtc = new DateTimeOffset(localDateTime, offset).UtcDateTime;
            return true;
        }

        private static bool OccursOnLocalDate(AudioRoutine routine, DayOfWeek localDay)
        {
            return routine.ScheduleDays.Count == 0 || routine.ScheduleDays.Contains(localDay);
        }

        internal void CheckScheduledRoutinesForTests()
        {
            CheckScheduledRoutines(null);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Stop();
                _lastExecutionByRoutineId.Clear();
                _lastCheckUtc = null;
            }
        }
    }
}
