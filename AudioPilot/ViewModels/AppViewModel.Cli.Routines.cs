using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using Newtonsoft.Json;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        public string GetRoutineListFromCli(bool jsonOutput, bool redactOutput = false)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            return CliOutputFormatter.FormatRoutineList(AppViewModel.CloneRoutines(settings.Routines.Items), jsonOutput, redactOutput);
        }

        public async Task<CliExecutionResult> RunRoutineFromCliAsync(string selector, bool jsonOutput, bool redactOutput = false)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            List<AudioRoutine> routines = AppViewModel.CloneRoutines(settings.Routines.Items);
            CliRoutineResolutionResult resolution = CliRoutineResolver.Resolve(routines, selector);
            if (resolution.Status != CliRoutineResolutionStatus.Success || resolution.Routine == null)
            {
                return BuildRoutineErrorResult(5, resolution.ErrorCode, resolution.Message, jsonOutput, redactOutput: redactOutput);
            }

            AudioRoutine routine = resolution.Routine;
            if (!routine.Enabled)
            {
                RecordExecutionHistory(new ExecutionHistoryEntry(
                    OpId: $"cli-routine-run:{Guid.NewGuid():N}",
                    TimestampUtc: DateTimeOffset.UtcNow,
                    Kind: ExecutionHistoryKind.Routine,
                    Source: "cli",
                    Action: "routine-run",
                    Success: false,
                    Skipped: true,
                    Summary: $"Routine '{routine.Name}' skipped.",
                    Reason: "Routine is disabled.",
                    RoutineId: routine.Id,
                    RoutineName: routine.Name,
                    Target: routine.TargetSummary,
                    DiagCode: "routine-disabled",
                    Details: new Dictionary<string, string> { ["trigger"] = routine.TriggerKind.ToString(), ["executionSource"] = "cli" }));
                return BuildRoutineErrorResult(5, "routine-disabled", $"Routine '{routine.Name}' is disabled.", jsonOutput, routine, redactOutput: redactOutput);
            }

            if (!routine.HasExecutionTarget)
            {
                RecordExecutionHistory(new ExecutionHistoryEntry(
                    OpId: $"cli-routine-run:{Guid.NewGuid():N}",
                    TimestampUtc: DateTimeOffset.UtcNow,
                    Kind: ExecutionHistoryKind.Routine,
                    Source: "cli",
                    Action: "routine-run",
                    Success: false,
                    Skipped: true,
                    Summary: $"Routine '{routine.Name}' skipped.",
                    Reason: "Routine has no configured targets.",
                    RoutineId: routine.Id,
                    RoutineName: routine.Name,
                    DiagCode: "routine-has-no-targets",
                    Details: new Dictionary<string, string> { ["trigger"] = routine.TriggerKind.ToString(), ["executionSource"] = "cli" }));
                return BuildRoutineErrorResult(5, "routine-has-no-targets", $"Routine '{routine.Name}' has no configured targets.", jsonOutput, routine, redactOutput: redactOutput);
            }

            if (!CliRoutineExecutionPolicy.TryResolveManualRunProcessId(routine, _routineProcessSnapshotProvider, out int? processId, out string? errorCode, out string? errorMessage))
            {
                RecordExecutionHistory(new ExecutionHistoryEntry(
                    OpId: $"cli-routine-run:{Guid.NewGuid():N}",
                    TimestampUtc: DateTimeOffset.UtcNow,
                    Kind: ExecutionHistoryKind.Routine,
                    Source: "cli",
                    Action: "routine-run",
                    Success: false,
                    Skipped: true,
                    Summary: $"Routine '{routine.Name}' skipped.",
                    Reason: errorMessage,
                    RoutineId: routine.Id,
                    RoutineName: routine.Name,
                    Target: CliRoutineExecutionPolicy.GetTriggerApplicationDisplayName(routine.TriggerAppPath),
                    DiagCode: errorCode ?? "routine-trigger-app-not-running",
                    Details: new Dictionary<string, string> { ["trigger"] = routine.TriggerKind.ToString(), ["executionSource"] = "cli" }));
                return BuildRoutineErrorResult(
                    5,
                    errorCode ?? "routine-trigger-app-not-running",
                    errorMessage ?? $"Routine '{routine.Name}' requires the target application to be running.",
                    jsonOutput,
                    routine,
                    CliRoutineExecutionPolicy.GetTriggerApplicationDisplayName(routine.TriggerAppPath),
                        requiresRunningTriggerProcess: true,
                        redactOutput: redactOutput);
            }

            RoutineExecutionResult result = await ExecuteRoutineAsync(routine, showOverlay: true, applicationProcessId: processId, executionSource: "cli");
            bool hasFailureDetail = !string.IsNullOrWhiteSpace(result.OutputFailureDetail) || !string.IsNullOrWhiteSpace(result.InputFailureDetail);
            bool success = result.Success && !hasFailureDetail;
            string? outputDeviceName = result.OutputDeviceName;
            string? inputDeviceName = result.InputDeviceName;
            if (!success)
            {
                bool? outputSucceeded = result.OutputSucceeded;
                bool? inputSucceeded = result.InputSucceeded;

                if (!string.IsNullOrWhiteSpace(result.OutputFailureDetail) && outputSucceeded == true && !result.AppOutputApplied)
                {
                    outputSucceeded = false;
                }

                if (!string.IsNullOrWhiteSpace(result.InputFailureDetail) && inputSucceeded == true && !result.AppInputApplied)
                {
                    inputSucceeded = false;
                }

                return BuildRoutineErrorResult(
                    3,
                    "routine-run-failed",
                    $"Failed to run routine '{routine.Name}'.",
                    jsonOutput,
                    routine,
                    outputSucceeded: outputSucceeded,
                    appliedOutputDeviceName: outputSucceeded == true ? outputDeviceName ?? routine.OutputDeviceName : null,
                    outputFailureDetail: result.OutputFailureDetail,
                    inputSucceeded: inputSucceeded,
                    appliedInputDeviceName: inputSucceeded == true ? inputDeviceName ?? routine.InputDeviceName : null,
                    inputFailureDetail: result.InputFailureDetail,
                    redactOutput: redactOutput);
            }

            return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineRunResult(routine, outputDeviceName, inputDeviceName, jsonOutput, redactOutput));
        }

        public CliExecutionResult SetRoutineEnabledFromCli(string selector, bool enabled, bool jsonOutput, bool redactOutput = false)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            CliRoutineResolutionResult resolution = CliRoutineResolver.Resolve(settings.Routines.Items, selector);
            if (resolution.Status != CliRoutineResolutionStatus.Success || resolution.Routine == null)
            {
                return BuildRoutineErrorResult(5, resolution.ErrorCode, resolution.Message, jsonOutput, redactOutput: redactOutput);
            }

            AudioRoutine routine = resolution.Routine;
            bool updated = routine.Enabled != enabled;
            routine.Enabled = enabled;

            try
            {
                _settings.SaveSettings(settings);
                ApplyExternallyReloadedSettings(settings);
                lock (_settingsLock)
                {
                    _cachedSettings = settings;
                }

                UpdateLastSettingsWriteTime();
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineStateChange(routine, enabled, updated, jsonOutput, redactOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-routine-set-failed", nameof(SetRoutineEnabledFromCli), ex);
                return BuildRoutineErrorResult(3, "routine-update-failed", $"Failed to update routine '{routine.Name}'.", jsonOutput, routine, redactOutput: redactOutput);
            }
        }

        public CliExecutionResult CreateRoutineFromCli(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            if (!TryLoadRoutineDraftForCli(path, allowAnyPath, out string? fullPath, out AudioRoutine? draft, out CliExecutionResult errorResult, jsonOutput))
            {
                return errorResult;
            }

            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Create(settings, draft!);
            if (!mutation.Success)
            {
                return BuildCliRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                PersistCliSettingsMutation(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Created", jsonOutput, redactOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-routine-create-failed", nameof(CreateRoutineFromCli), ex);
                return BuildCliRoutineMutationError(3, "routine-create-failed", $"Failed to create routine from {CliOutputFormatter.FormatPath(fullPath!, redactOutput)}.", jsonOutput);
            }
        }

        public CliExecutionResult UpdateRoutineFromCli(string selector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            if (!TryLoadRoutineDraftForCli(path, allowAnyPath, out string? fullPath, out AudioRoutine? draft, out CliExecutionResult errorResult, jsonOutput))
            {
                return errorResult;
            }

            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Update(settings, selector, draft!);
            if (!mutation.Success)
            {
                return BuildCliRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                PersistCliSettingsMutation(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Updated", jsonOutput, redactOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-routine-update-failed", nameof(UpdateRoutineFromCli), ex);
                return BuildCliRoutineMutationError(3, "routine-update-failed", $"Failed to update routine from {CliOutputFormatter.FormatPath(fullPath!, redactOutput)}.", jsonOutput);
            }
        }

        public CliExecutionResult DeleteRoutineFromCli(string selector, bool jsonOutput, bool redactOutput = false)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Delete(settings, selector);
            if (!mutation.Success)
            {
                return BuildCliRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                PersistCliSettingsMutation(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Deleted", jsonOutput, redactOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-routine-delete-failed", nameof(DeleteRoutineFromCli), ex);
                return BuildCliRoutineMutationError(3, "routine-delete-failed", "Failed to delete routine.", jsonOutput);
            }
        }

        public CliExecutionResult ImportRoutinesFromCli(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            if (!TryLoadRoutineCollectionForCli(path, allowAnyPath, out string? fullPath, out List<AudioRoutine>? routines, out CliExecutionResult errorResult, jsonOutput))
            {
                return errorResult;
            }

            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Import(settings, routines!, replaceImport);
            if (!mutation.Success)
            {
                return BuildCliRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                PersistCliSettingsMutation(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineImportResult(mutation.ImportedCount, replaceImport, jsonOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-routine-import-failed", nameof(ImportRoutinesFromCli), ex);
                return BuildCliRoutineMutationError(3, "routine-import-failed", $"Failed to import routines from {CliOutputFormatter.FormatPath(fullPath!, redactOutput)}.", jsonOutput);
            }
        }

        private static CliExecutionResult BuildRoutineErrorResult(
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
            return jsonOutput
                  ? new CliExecutionResult(exitCode, CliOutputFormatter.FormatRoutineError(exitCode, errorCode, message, jsonOutput: true, routine, triggerApplicationName, requiresRunningTriggerProcess, outputSucceeded, appliedOutputDeviceName, outputFailureDetail, inputSucceeded, appliedInputDeviceName, inputFailureDetail, redactOutput))
                  : new CliExecutionResult(exitCode, CliOutputFormatter.FormatRoutineError(exitCode, errorCode, message, jsonOutput: false, routine, triggerApplicationName, requiresRunningTriggerProcess, outputSucceeded, appliedOutputDeviceName, outputFailureDetail, inputSucceeded, appliedInputDeviceName, inputFailureDetail, redactOutput));
        }

        private static CliExecutionResult BuildCliRoutineMutationError(int exitCode, string errorCode, string message, bool jsonOutput)
        {
            return jsonOutput
                ? new CliExecutionResult(exitCode, CliCommandExecutor.BuildJsonErrorPayload(exitCode, errorCode, message))
                : new CliExecutionResult(exitCode, $"[diag-code:{errorCode}] {message}");
        }

        private bool TryLoadRoutineDraftForCli(string path, bool allowAnyPath, out string? fullPath, out AudioRoutine? draft, out CliExecutionResult errorResult, bool jsonOutput)
        {
            if (!CliRoutineTransferHelper.TryLoadRoutineDraft(
                path,
                _settings.GetSettingsPath(),
                allowAnyPath,
                out fullPath,
                out draft,
                out string? errorCode,
                out string? errorMessage))
            {
                errorResult = BuildCliRoutineMutationError(5, errorCode ?? "routine-import-invalid", errorMessage ?? "Failed to load routine.", jsonOutput);
                return false;
            }

            errorResult = default;
            return true;
        }

        private bool TryLoadRoutineCollectionForCli(string path, bool allowAnyPath, out string? fullPath, out List<AudioRoutine>? routines, out CliExecutionResult errorResult, bool jsonOutput)
        {
            if (!CliRoutineTransferHelper.TryLoadRoutineCollection(
                path,
                _settings.GetSettingsPath(),
                allowAnyPath,
                out fullPath,
                out routines,
                out string? errorCode,
                out string? errorMessage))
            {
                errorResult = BuildCliRoutineMutationError(5, errorCode ?? "routine-import-invalid", errorMessage ?? "Failed to load routines.", jsonOutput);
                return false;
            }

            errorResult = default;
            return true;
        }

        private void PersistCliSettingsMutation(Settings settings)
        {
            _settings.SaveSettings(settings);
            ApplyExternallyReloadedSettings(settings);
            lock (_settingsLock)
            {
                _cachedSettings = settings;
            }

            UpdateLastSettingsWriteTime();
        }


        public (bool Success, string Output) ExportRoutinesFromCli(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            try
            {
                Settings settings = CurrentSettings ?? _settings.LoadSettings();
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "routine-export-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:routine-export-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                List<AudioRoutine> routines = [.. settings.Routines.Items
                    .Where(static routine => routine != null)
                    .Select(static routine => routine.Clone())];

                string payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    SchemaVersion = Settings.CurrentSchemaVersion,
                    Routines = routines,
                }, Newtonsoft.Json.Formatting.Indented);

                File.WriteAllText(fullPath, payload);
                return (true, CliOutputFormatter.FormatRoutineExportResult(fullPath, routines.Count, jsonOutput, redactOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-routine-export-failed", nameof(ExportRoutinesFromCli), ex);
                return jsonOutput
                    ? (false, SerializeCliJson(new { Success = false, DiagCode = "routine-export-failed", Error = "Failed to export routines." }))
                    : (false, "[diag-code:routine-export-failed] Failed to export routines.");
            }
        }
    }
}
