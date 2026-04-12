using AudioPilot.Cli;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private static ExecutionHistoryKind? TryParseExecutionHistoryKind(string? type)
        {
            return type switch
            {
                null or "" => null,
                "routine" => ExecutionHistoryKind.Routine,
                "switch" => ExecutionHistoryKind.Switch,
                "media" => ExecutionHistoryKind.Media,
                "mute" => ExecutionHistoryKind.Mute,
                _ => null,
            };
        }

        private void RecordExecutionHistory(ExecutionHistoryEntry entry)
        {
            _executionHistory.Record(entry);
        }

        private string GetDiagnosticsHistoryOutput(bool jsonOutput, int? limit, string? type, bool redactOutput)
        {
            IReadOnlyList<ExecutionHistoryEntry> entries = _executionHistory.GetEntries(limit, TryParseExecutionHistoryKind(type));
            return CliOutputFormatter.FormatExecutionHistory(entries, jsonOutput, redactOutput);
        }

        private (bool Found, string Output) GetDiagnosticsHistoryDetailOutput(string opId, bool jsonOutput, bool redactOutput)
        {
            ExecutionHistoryEntry? entry = _executionHistory.GetEntry(opId);
            return entry == null
                ? (false, CliOutputFormatter.FormatExecutionHistoryNotFound(opId, jsonOutput))
                : (true, CliOutputFormatter.FormatExecutionHistoryDetail(entry, jsonOutput, redactOutput));
        }

        private void RecordRoutineExecutionHistory(AudioRoutine routine, string executionSource, RoutineExecutionResult result, string? correlatedOperationId)
        {
            string opId = string.IsNullOrWhiteSpace(correlatedOperationId)
                ? CreateRoutineOperationId("routine-execution")
                : correlatedOperationId!;

            string? reason = result.Skipped
                ? result.OutputFailureDetail ?? result.InputFailureDetail ?? routine.LastRunDetail
                : !result.Success
                    ? result.OutputFailureDetail ?? result.InputFailureDetail ?? "Routine execution failed."
                    : result.AwaitingAppCompletion
                        ? "Waiting for app audio to appear before completing per-app routing."
                        : null;

            string summary = result.Skipped
                ? $"Routine '{routine.Name}' skipped."
                : result.Success
                    ? $"Routine '{routine.Name}' completed."
                    : $"Routine '{routine.Name}' failed.";

            RecordExecutionHistory(new ExecutionHistoryEntry(
                OpId: opId,
                TimestampUtc: DateTimeOffset.UtcNow,
                Kind: ExecutionHistoryKind.Routine,
                Source: executionSource,
                Action: "routine-run",
                Success: result.Success,
                Skipped: result.Skipped,
                Summary: summary,
                Reason: reason,
                RoutineId: routine.Id,
                RoutineName: routine.Name,
                OutputDeviceName: result.OutputDeviceName,
                InputDeviceName: result.InputDeviceName,
                Target: routine.TargetSummary,
                OutputSucceeded: result.OutputSucceeded,
                InputSucceeded: result.InputSucceeded,
                AwaitingAppCompletion: result.AwaitingAppCompletion,
                AppOutputApplied: result.AppOutputApplied,
                AppInputApplied: result.AppInputApplied));
        }

        private void RecordCliActionHistory(ExecutionHistoryKind kind, string action, bool success, bool skipped, string summary, string? reason = null, string? target = null, string? outputDeviceName = null, string? inputDeviceName = null, bool? enabled = null)
        {
            RecordExecutionHistory(new ExecutionHistoryEntry(
                OpId: $"cli-{action}:{Guid.NewGuid():N}",
                TimestampUtc: DateTimeOffset.UtcNow,
                Kind: kind,
                Source: "cli",
                Action: action,
                Success: success,
                Skipped: skipped,
                Summary: summary,
                Reason: reason,
                OutputDeviceName: outputDeviceName,
                InputDeviceName: inputDeviceName,
                Target: target,
                Enabled: enabled));
        }

        private string? TryGetCurrentDefaultDeviceName(bool output, string reason)
        {
            try
            {
                using var device = output
                    ? _audio.GetDefaultPlaybackDevice(reason)
                    : _audio.GetDefaultRecordingDevice(reason);
                return device?.FriendlyName;
            }
            catch
            {
                return null;
            }
        }
    }
}
