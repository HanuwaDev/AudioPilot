using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    internal static class AppViewModelRoutineConflictHelper
    {
        public static IReadOnlyList<string> BuildDistinctConflictWarnings(IEnumerable<AudioRoutine>? routines)
        {
            IReadOnlyDictionary<string, string> summaries = BuildConflictSummaries(routines);
            return
            [
                .. summaries.Values
                    .Where(static summary => !string.IsNullOrWhiteSpace(summary))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static summary => summary, StringComparer.Ordinal)
            ];
        }

        public static IReadOnlyDictionary<string, string> BuildConflictSummaries(IEnumerable<AudioRoutine>? routines)
        {
            if (routines == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            Dictionary<string, string> summaries = new(StringComparer.OrdinalIgnoreCase);
            List<AudioRoutine> enabledRoutines =
            [
                .. routines.Where(static routine =>
                    routine.Enabled &&
                    (routine.HasOutputTarget || routine.HasInputTarget))
            ];

            foreach (IGrouping<string, AudioRoutine> group in enabledRoutines
                .Where(static routine => routine.TriggerKind == RoutineTriggerKind.AppStartup && routine.HasAppStartTrigger)
                .GroupBy(static routine => RoutineTriggerPathHelper.NormalizeTriggerTarget(routine.TriggerAppPath), StringComparer.OrdinalIgnoreCase))
            {
                ApplyTriggerGroupConflicts(
                    [.. group],
                    summaries,
                    $"Application start for {RoutineTriggerPathHelper.GetTriggerDisplayName(group.Key)}");
            }

            List<AudioRoutine> audioPilotStartupRoutines =
            [
                .. enabledRoutines.Where(static routine => routine.TriggerKind == RoutineTriggerKind.AudioPilotStartup)
            ];
            ApplyTriggerGroupConflicts(audioPilotStartupRoutines, summaries, "AudioPilot startup");

            List<AudioRoutine> deviceChangeRoutines =
            [
                .. enabledRoutines.Where(static routine => routine.TriggerKind == RoutineTriggerKind.DeviceChange)
            ];
            ApplyTriggerGroupConflicts(deviceChangeRoutines, summaries, "Device change");

            List<AudioRoutine> steamBigPictureRoutines =
            [
                .. enabledRoutines.Where(static routine => routine.TriggerKind == RoutineTriggerKind.SteamBigPicture)
            ];
            ApplyTriggerGroupConflicts(steamBigPictureRoutines, summaries, "Steam Big Picture");

            return summaries;
        }

        private static void ApplyTriggerGroupConflicts(
            List<AudioRoutine> routines,
            Dictionary<string, string> summaries,
            string triggerLabel)
        {
            if (routines.Count < 2)
            {
                return;
            }

            List<AudioRoutine> outputConflictRoutines = BuildFlowConflictMembers(routines, static routine => routine.OutputDeviceId);
            List<AudioRoutine> inputConflictRoutines = BuildFlowConflictMembers(routines, static routine => routine.InputDeviceId);
            HashSet<string> involvedRoutineIds =
            [
                .. outputConflictRoutines.Select(static routine => NormalizeRoutineId(routine.Id)),
                .. inputConflictRoutines.Select(static routine => NormalizeRoutineId(routine.Id))
            ];

            if (involvedRoutineIds.Count < 2)
            {
                return;
            }

            foreach (AudioRoutine routine in routines)
            {
                string routineId = NormalizeRoutineId(routine.Id);
                if (!involvedRoutineIds.Contains(routineId))
                {
                    continue;
                }

                bool hasOutputConflict = outputConflictRoutines.Any(candidate => string.Equals(candidate.Id, routine.Id, StringComparison.OrdinalIgnoreCase));
                bool hasInputConflict = inputConflictRoutines.Any(candidate => string.Equals(candidate.Id, routine.Id, StringComparison.OrdinalIgnoreCase));
                int otherCount = Math.Max(1, involvedRoutineIds.Count - 1);
                summaries[routineId] = BuildConflictSummary(triggerLabel, hasOutputConflict, hasInputConflict, otherCount);
            }
        }

        private static List<AudioRoutine> BuildFlowConflictMembers(
            IReadOnlyList<AudioRoutine> routines,
            Func<AudioRoutine, string?> getTargetId)
        {
            List<IGrouping<string, AudioRoutine>> groups =
            [
                .. routines
                    .Where(routine => !string.IsNullOrWhiteSpace(getTargetId(routine)))
                    .GroupBy(routine => getTargetId(routine)!, StringComparer.OrdinalIgnoreCase)
            ];
            if (groups.Count < 2)
            {
                return [];
            }

            return [.. groups.SelectMany(static group => group)];
        }

        private static string BuildConflictSummary(string triggerLabel, bool hasOutputConflict, bool hasInputConflict, int otherCount)
        {
            string targetSummary = (hasOutputConflict, hasInputConflict) switch
            {
                (true, true) => "different output and input targets",
                (true, false) => "different output targets",
                (false, true) => "different input targets",
                _ => "conflicting targets",
            };
            string routineCountLabel = otherCount == 1
                ? "1 other enabled routine"
                : $"{otherCount} other enabled routines";
            return $"{triggerLabel} conflicts with {routineCountLabel}: {targetSummary}.";
        }

        private static string NormalizeRoutineId(string? routineId)
        {
            return string.IsNullOrWhiteSpace(routineId)
                ? "unknown"
                : routineId.Trim();
        }
    }
}
