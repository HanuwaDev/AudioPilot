using System.Text;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Cli
{
    public static partial class CliOutputFormatter
    {
        public static string FormatRoutineList(IReadOnlyList<AudioRoutine> routines, bool jsonOutput, bool redactOutput = false)
        {
            IReadOnlyDictionary<string, string> conflictSummaries = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);
            var items = new List<CliRoutineItem>(routines.Count);
            for (int index = 0; index < routines.Count; index++)
            {
                AudioRoutine routine = routines[index];
                bool isAppStartupTrigger = routine.TriggerKind == RoutineTriggerKind.AppStartup;
                string timingSummary = FormatRoutineTimingSummary(routine);
                string timingPreset = routine.TimingPresetLabel;
                string conflictSummary = FormatRoutineConflictSummary(routine, conflictSummaries, redactOutput);
                items.Add(new CliRoutineItem(
                    index + 1,
                    routine.Id,
                    FormatRoutineName(routine.Name, redactOutput),
                    routine.Enabled,
                    routine.Hotkey,
                    GetRoutineTriggerMode(routine),
                    isAppStartupTrigger,
                    FormatOptionalPath(routine.TriggerAppPath, redactOutput) ?? string.Empty,
                    routine.RestorePreviousAudioOnDeactivate,
                    routine.SwitchOutputPerApp,
                    routine.ShowInTrayMenu,
                    redactOutput ? RedactQuotedLiterals(routine.TriggerSummary) : routine.TriggerSummary,
                    routine.OutputDeviceId,
                    FormatOptionalDeviceName(routine.OutputDeviceName, redactOutput) ?? string.Empty,
                    routine.InputDeviceId,
                    FormatOptionalDeviceName(routine.InputDeviceName, redactOutput) ?? string.Empty,
                    redactOutput ? RedactQuotedLiterals(routine.TargetSummary) : routine.TargetSummary,
                    routine.ExecutionDelayMs,
                    routine.CooldownSeconds,
                    routine.TriggerAppStableForMs,
                    timingPreset,
                    timingSummary,
                    conflictSummary));
            }

            if (jsonOutput)
            {
                return SerializeCliJson(new CliRoutineListSnapshot(items));
            }

            if (items.Count == 0)
            {
                return "No routines configured.";
            }

            var builder = new StringBuilder();
            foreach (CliRoutineItem item in items)
            {
                string status = item.Enabled ? "enabled" : "disabled";
                string hotkey = string.IsNullOrWhiteSpace(item.Hotkey) ? "none" : item.Hotkey;
                string targets = string.IsNullOrWhiteSpace(item.TargetSummary) ? "No targets" : item.TargetSummary;
                string triggerSummary = string.IsNullOrWhiteSpace(item.TriggerSummary) ? item.TriggerMode : item.TriggerSummary;
                string trayMenu = item.ShowInTrayMenu ? "shown" : "hidden";
                builder.Append(item.Order);
                builder.Append(". ");
                builder.Append(item.Name);
                builder.Append(" [");
                builder.Append(status);
                builder.Append("] | ");
                builder.Append(targets);
                builder.Append(" | Hotkey: ");
                builder.Append(hotkey);
                builder.Append(" | Trigger: ");
                builder.Append(triggerSummary);
                if (item.TriggerOnAppStart && !string.IsNullOrWhiteSpace(item.TriggerAppPath))
                {
                    builder.Append(" | App: ");
                    builder.Append(item.TriggerAppPath);
                }

                if (item.SwitchOutputPerApp)
                {
                    builder.Append(" | Scope: app-only audio");
                }

                if (!string.IsNullOrWhiteSpace(item.TimingPreset) || !string.IsNullOrWhiteSpace(item.TimingSummary))
                {
                    builder.Append(" | Timing: ");
                    builder.Append(FormatTimingDetails(item.TimingPreset, item.TimingSummary));
                }

                if (!string.IsNullOrWhiteSpace(item.ConflictSummary))
                {
                    builder.Append(" | Conflict: ");
                    builder.Append(item.ConflictSummary);
                }

                builder.Append(" | Tray: ");
                builder.Append(trayMenu);
                builder.Append(" | Id: ");
                builder.AppendLine(item.Id);
            }

            return builder.ToString().TrimEnd();
        }

        private static string GetRoutineTriggerMode(AudioRoutine routine)
        {
            return routine.TriggerKind switch
            {
                RoutineTriggerKind.AppStartup => "Application startup",
                RoutineTriggerKind.AudioPilotStartup => "AudioPilot startup",
                RoutineTriggerKind.SteamBigPicture => "Steam Big Picture",
                RoutineTriggerKind.DeviceChange => "Device change",
                _ => "Hotkey",
            };
        }

        public static string FormatRoutineRunResult(AudioRoutine routine, string? appliedOutputDeviceName, string? appliedInputDeviceName, bool jsonOutput, bool redactOutput = false)
        {
            string diagCode = "routine-run-success";
            bool isAppStartupTrigger = routine.TriggerKind == RoutineTriggerKind.AppStartup;
            string timingSummary = FormatRoutineTimingSummary(routine);
            string timingPreset = routine.TimingPresetLabel;
            var snapshot = new CliRoutineRunSnapshot(
                routine.Id,
                FormatRoutineName(routine.Name, redactOutput),
                routine.Enabled,
                GetRoutineTriggerMode(routine),
                isAppStartupTrigger,
                FormatOptionalPath(routine.TriggerAppPath, redactOutput) ?? string.Empty,
                routine.RestorePreviousAudioOnDeactivate,
                routine.SwitchOutputPerApp,
                routine.ShowInTrayMenu,
                redactOutput ? RedactQuotedLiterals(routine.TriggerSummary) : routine.TriggerSummary,
                routine.ExecutionDelayMs,
                routine.CooldownSeconds,
                routine.TriggerAppStableForMs,
                timingPreset,
                timingSummary,
                FormatOptionalDeviceName(appliedOutputDeviceName, redactOutput),
                FormatOptionalDeviceName(appliedInputDeviceName, redactOutput),
                diagCode);

            if (jsonOutput)
            {
                return SerializeCliJson(snapshot);
            }

            var targets = new List<string>();
            if (!string.IsNullOrWhiteSpace(appliedOutputDeviceName))
            {
                targets.Add($"output '{FormatDeviceName(appliedOutputDeviceName, redactOutput)}'");
            }

            if (!string.IsNullOrWhiteSpace(appliedInputDeviceName))
            {
                targets.Add($"input '{FormatDeviceName(appliedInputDeviceName, redactOutput)}'");
            }

            string result = $"[diag-code:{diagCode}] Ran routine '{FormatRoutineName(routine.Name, redactOutput)}'{(targets.Count > 0 ? " -> " + string.Join(", ", targets) : string.Empty)}.";
            string timingDetails = FormatTimingDetails(timingPreset, timingSummary);
            return string.IsNullOrWhiteSpace(timingDetails)
                ? result
                : $"{result} Timing: {timingDetails}.";
        }

        public static string FormatRoutineError(
            int exitCode,
            string errorCode,
            string message,
            bool jsonOutput,
            AudioRoutine? routine = null,
            string? triggerApplicationName = null,
            bool? requiresRunningTriggerProcess = null,
            bool? outputSucceeded = null,
            string? appliedOutputDeviceName = null,
            string? outputFailureDetail = null,
            bool? inputSucceeded = null,
            string? appliedInputDeviceName = null,
            string? inputFailureDetail = null,
            bool redactOutput = false)
        {
            string displayMessage = redactOutput
                ? RedactRoutineMessage(message, routine, triggerApplicationName)
                : message;

            string? displayOutputFailureDetail = FormatOptionalFailureDetail(outputFailureDetail, redactOutput);
            string? displayInputFailureDetail = FormatOptionalFailureDetail(inputFailureDetail, redactOutput);

            if (jsonOutput)
            {
                var snapshot = new CliRoutineErrorSnapshot(
                    errorCode,
                    displayMessage,
                    exitCode,
                    routine?.Id,
                    FormatOptionalRoutineName(routine?.Name, redactOutput),
                    routine == null ? null : GetRoutineTriggerMode(routine),
                    routine == null ? null : routine.TriggerKind == RoutineTriggerKind.AppStartup,
                    FormatOptionalPath(routine?.TriggerAppPath, redactOutput),
                    FormatOptionalProcessName(triggerApplicationName, redactOutput),
                    requiresRunningTriggerProcess,
                    routine?.RestorePreviousAudioOnDeactivate,
                    routine?.SwitchOutputPerApp,
                    routine?.OutputDeviceId,
                    routine?.InputDeviceId,
                    outputSucceeded,
                    FormatOptionalDeviceName(appliedOutputDeviceName, redactOutput),
                    displayOutputFailureDetail,
                    inputSucceeded,
                    FormatOptionalDeviceName(appliedInputDeviceName, redactOutput),
                    displayInputFailureDetail,
                    (outputSucceeded == true && inputSucceeded == false) || (outputSucceeded == false && inputSucceeded == true));

                return SerializeCliJson(new
                {
                    Error = snapshot,
                });
            }

            if ((outputSucceeded == true && inputSucceeded == false) || (outputSucceeded == false && inputSucceeded == true))
            {
                string partialFailureText = FormatRoutinePartialFailureText(routine, outputSucceeded, appliedOutputDeviceName, inputSucceeded, appliedInputDeviceName, redactOutput);
                string failureDetailText = FormatRoutineFailureDetailText(displayOutputFailureDetail, displayInputFailureDetail);
                return $"[diag-code:{errorCode}] {displayMessage} {partialFailureText}{(string.IsNullOrWhiteSpace(failureDetailText) ? string.Empty : " " + failureDetailText)}";
            }

            string fullFailureDetailText = FormatRoutineFailureDetailText(displayOutputFailureDetail, displayInputFailureDetail);
            return string.IsNullOrWhiteSpace(fullFailureDetailText)
                ? $"[diag-code:{errorCode}] {displayMessage}"
                : $"[diag-code:{errorCode}] {displayMessage} {fullFailureDetailText}";
        }

        private static string FormatRoutinePartialFailureText(
            AudioRoutine? routine,
            bool? outputSucceeded,
            string? appliedOutputDeviceName,
            bool? inputSucceeded,
            string? appliedInputDeviceName,
            bool redactOutput)
        {
            List<string> succeededTargets = [];
            List<string> failedTargets = [];

            if (outputSucceeded == true)
            {
                string outputName = string.IsNullOrWhiteSpace(appliedOutputDeviceName)
                    ? FormatOptionalDeviceName(routine?.OutputDeviceName, redactOutput) ?? "output"
                    : FormatDeviceName(appliedOutputDeviceName, redactOutput);
                succeededTargets.Add($"output '{outputName}'");
            }
            else if (outputSucceeded == false)
            {
                string outputName = string.IsNullOrWhiteSpace(routine?.OutputDeviceName)
                    ? "output"
                    : $"output '{FormatDeviceName(routine.OutputDeviceName, redactOutput)}'";
                failedTargets.Add(outputName);
            }

            if (inputSucceeded == true)
            {
                string inputName = string.IsNullOrWhiteSpace(appliedInputDeviceName)
                    ? FormatOptionalDeviceName(routine?.InputDeviceName, redactOutput) ?? "input"
                    : FormatDeviceName(appliedInputDeviceName, redactOutput);
                succeededTargets.Add($"input '{inputName}'");
            }
            else if (inputSucceeded == false)
            {
                string inputName = string.IsNullOrWhiteSpace(routine?.InputDeviceName)
                    ? "input"
                    : $"input '{FormatDeviceName(routine.InputDeviceName, redactOutput)}'";
                failedTargets.Add(inputName);
            }

            return $"Partial result: succeeded {string.Join(", ", succeededTargets)}; failed {string.Join(", ", failedTargets)}.";
        }

        private static string? FormatOptionalFailureDetail(string? detail, bool redactOutput)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return null;
            }

            return redactOutput ? RedactQuotedLiterals(detail) : detail;
        }

        private static string FormatRoutineFailureDetailText(string? outputFailureDetail, string? inputFailureDetail)
        {
            List<string> details = [];

            if (!string.IsNullOrWhiteSpace(outputFailureDetail))
            {
                details.Add($"Output failure: {outputFailureDetail}");
            }

            if (!string.IsNullOrWhiteSpace(inputFailureDetail))
            {
                details.Add($"Input failure: {inputFailureDetail}");
            }

            return string.Join(" ", details);
        }

        public static string FormatRoutineStateChange(AudioRoutine routine, bool enabled, bool updated, bool jsonOutput, bool redactOutput = false)
        {
            string diagCode = updated ? "routine-state-updated" : "routine-state-unchanged";
            var snapshot = new CliRoutineStateSnapshot(
                routine.Id,
                FormatRoutineName(routine.Name, redactOutput),
                enabled,
                updated,
                diagCode);

            if (jsonOutput)
            {
                return SerializeCliJson(snapshot);
            }

            string state = enabled ? "enabled" : "disabled";
            return updated
                ? $"[diag-code:{diagCode}] {state} routine '{FormatRoutineName(routine.Name, redactOutput)}'."
                : $"[diag-code:{diagCode}] Routine '{FormatRoutineName(routine.Name, redactOutput)}' is already {state}.";
        }

        public static string FormatRoutineMutationResult(AudioRoutine routine, string diagCode, string actionVerb, bool jsonOutput, bool redactOutput = false)
        {
            string routineName = FormatRoutineName(routine.Name, redactOutput);
            string triggerMode = GetRoutineTriggerMode(routine);
            if (jsonOutput)
            {
                return SerializeCliJson(new
                {
                    routine.Id,
                    Name = routineName,
                    routine.Enabled,
                    TriggerMode = triggerMode,
                    TriggerAppPath = FormatOptionalPath(routine.TriggerAppPath, redactOutput) ?? string.Empty,
                    routine.OutputDeviceId,
                    OutputDeviceName = FormatOptionalDeviceName(routine.OutputDeviceName, redactOutput) ?? string.Empty,
                    routine.InputDeviceId,
                    InputDeviceName = FormatOptionalDeviceName(routine.InputDeviceName, redactOutput) ?? string.Empty,
                    DiagCode = diagCode,
                });
            }

            return $"[diag-code:{diagCode}] {actionVerb} routine '{routineName}' (Id: {routine.Id}).";
        }

        public static string FormatRoutineImportResult(int importedCount, bool replaceImport, bool jsonOutput)
        {
            string mode = replaceImport ? "replace" : "merge";
            if (jsonOutput)
            {
                return SerializeCliJson(new
                {
                    ImportedCount = importedCount,
                    Mode = mode,
                    DiagCode = "routine-import-success",
                });
            }

            return $"[diag-code:routine-import-success] Imported {importedCount} routine{(importedCount == 1 ? string.Empty : "s")} using {mode} mode.";
        }

        public static string FormatRoutineExportResult(string path, int routineCount, bool jsonOutput, bool redactOutput = false)
        {
            if (jsonOutput)
            {
                return SerializeCliJson(new
                {
                    Success = true,
                    DiagCode = "routine-export-success",
                    ExportPath = FormatPath(path, redactOutput),
                    RoutineCount = routineCount,
                });
            }

            return $"[diag-code:routine-export-success] Exported {routineCount} routines to {FormatPath(path, redactOutput)}.";
        }
    }
}
