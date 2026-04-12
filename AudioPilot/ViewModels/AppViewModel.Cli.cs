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
        internal const string CliJsonSchemaVersion = CliOutputFormatter.JsonSchemaVersion;

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
                    Target: routine.TargetSummary));
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
                    RoutineName: routine.Name));
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
                    Target: CliRoutineExecutionPolicy.GetTriggerApplicationDisplayName(routine.TriggerAppPath)));
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

            RoutineExecutionResult result = await ExecuteRoutineAsync(routine, showOverlay: true, appStartProcessId: processId, executionSource: "cli");
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

        public (bool Found, string? Value, string? Error) GetConfigFromCli(string key)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            bool found = CliConfigManager.TryGet(settings, key, out string value, out string? error);
            return (found, found ? value : null, error);
        }

        public (bool Updated, string? Error) SetConfigFromCli(string key, string value)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            if (!CliConfigManager.TrySet(settings, key, value, out string? error))
            {
                return (false, error);
            }

            try
            {
                _settings.SaveSettings(settings);

                if (string.Equals(key, "run-at-startup", StringComparison.OrdinalIgnoreCase))
                {
                    _ = SetStartupEnabledFromCli(settings.RunAtStartup);
                }

                ApplyExternallyReloadedSettings(settings);
                lock (_settingsLock)
                {
                    _cachedSettings = settings;
                }
                UpdateLastSettingsWriteTime();

                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-set-failed", nameof(SetConfigFromCli), ex);
                return (false, "Failed to persist config value.");
            }
        }

        public static (bool Found, string? Value, string? Error) GetRuntimeFromCli(string key)
        {
            bool found = CliRuntimeManager.TryGet(key, out string value, out string? error);
            return (found, found ? value : null, error);
        }

        public static (bool Updated, string? Error) SetRuntimeFromCli(string key, string value)
        {
            return CliRuntimeManager.TrySet(key, value, out string? error)
                ? (true, null)
                : (false, error);
        }

        public (bool IsValid, string Output) GetConfigValidationFromCli(bool jsonOutput)
        {
            var diagnostics = BuildSettingsDiagnostics();
            List<string> warnings = BuildDiagnosticMessages(diagnostics.Warnings);

            return (warnings.Count == 0, FormatConfigValidation(warnings, jsonOutput));
        }

        public (bool CanSwitch, string Output) PreviewSwitchFromCli(bool output, bool reverse, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            var cycleDevices = output ? OutputCycleDevices : InputCycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();

            var preflight = SettingsValidationService.EvaluateCycleSwitchPreflight(
                cycleDevices,
                activeDevices,
                hasDefaultInputDevice: HasDefaultInputDevice(),
                output);

            if (!preflight.CanSwitch)
            {
                return (
                    false,
                    FormatCycleTest(
                        kind,
                        preflight.ConfiguredCount,
                        preflight.ConnectedConfiguredCount,
                        preflight.HasDefaultInputDevice,
                        preflight.Reasons,
                        jsonOutput));
            }

            string? currentId = GetCurrentDeviceIdFromCli(output);
            int currentIndex = FindCycleDeviceIndex(cycleDevices, currentId);
            int targetIndex = AppSwitchCommandCoordinator.ResolveCycleTargetIndex(currentIndex, cycleDevices.Count, reverse);
            string targetId = cycleDevices[targetIndex].Id;
            string targetName = cycleDevices[targetIndex].Name;

            if (jsonOutput)
            {
                return (true, SerializeCliJson(new
                {
                    Kind = kind,
                    DryRun = true,
                    CurrentDeviceId = currentId,
                    TargetDeviceId = targetId,
                    TargetDeviceName = CliOutputFormatter.FormatDeviceName(targetName, redactOutput),
                    DiagCode = "switch-dry-run",
                }));
            }

            return (true, $"[diag-code:switch-dry-run] {kind} switch would target '{CliOutputFormatter.FormatDeviceName(targetName, redactOutput)}' ({targetId}).");
        }

        private static int FindCycleDeviceIndex(ObservableCollection<CycleDevice> cycleDevices, string? currentId)
        {
            if (string.IsNullOrWhiteSpace(currentId))
            {
                return -1;
            }

            for (int index = 0; index < cycleDevices.Count; index++)
            {
                if (string.Equals(cycleDevices[index].Id, currentId, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        public string? GetCurrentDeviceIdFromCli(bool output)
        {
            try
            {
                if (DeviceCacheHelper.IsInitialized)
                {
                    if (output)
                    {
                        using var cachedPlayback = DeviceCacheHelper.Instance.GetPrimaryPlaybackDevice();
                        if (cachedPlayback != null)
                        {
                            return cachedPlayback.ID;
                        }
                    }
                    else
                    {
                        using var cachedRecording = DeviceCacheHelper.Instance.GetPrimaryRecordingDevice();
                        if (cachedRecording != null)
                        {
                            return cachedRecording.ID;
                        }
                    }
                }

                if (output)
                {
                    using var device = _audio.GetDefaultPlaybackDevice();
                    return device?.ID;
                }

                using var inputDevice = _audio.GetDefaultRecordingDevice();
                return inputDevice?.ID;
            }
            catch
            {
                return null;
            }
        }

        public async Task<(bool Found, string Output)> WaitForDeviceFromCliAsync(string deviceId, int timeoutMs, bool outputOnly, bool inputOnly, bool jsonOutput)
        {
            var stopwatch = Stopwatch.StartNew();
            do
            {
                bool outputFound = (outputOnly || (!outputOnly && !inputOnly)) && ContainsDeviceId(GetActiveOutputDeviceInfos(), deviceId);
                bool inputFound = (inputOnly || (!outputOnly && !inputOnly)) && ContainsDeviceId(GetActiveInputDeviceInfos(), deviceId);

                bool found = outputFound || inputFound;
                if (found)
                {
                    if (jsonOutput)
                    {
                        return (true, SerializeCliJson(new
                        {
                            DeviceId = deviceId,
                            Found = true,
                            Scope = outputOnly ? "output" : inputOnly ? "input" : "any",
                            ElapsedMs = stopwatch.ElapsedMilliseconds,
                            DiagCode = "wait-device-found",
                        }));
                    }

                    return (true, $"[diag-code:wait-device-found] Device '{deviceId}' is available.");
                }

                await Task.Delay(250);
            }
            while (stopwatch.ElapsedMilliseconds < timeoutMs);

            if (jsonOutput)
            {
                return (false, SerializeCliJson(new
                {
                    DeviceId = deviceId,
                    Found = false,
                    Scope = outputOnly ? "output" : inputOnly ? "input" : "any",
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    TimeoutMs = timeoutMs,
                    DiagCode = "wait-device-timeout",
                }));
            }

            return (false, $"[diag-code:wait-device-timeout] Timed out waiting for device '{deviceId}' after {timeoutMs}ms.");
        }

        private static bool ContainsDeviceId(List<CycleDevice> devices, string deviceId)
        {
            for (int index = 0; index < devices.Count; index++)
            {
                if (string.Equals(devices[index].Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public (bool Success, string Output) ExportConfigFromCli(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            try
            {
                Settings settings = CurrentSettings ?? _settings.LoadSettings();
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-export-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:config-export-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                SettingsTransferService.ExportSettings(settings, fullPath);
                if (jsonOutput)
                {
                    return (true, SerializeCliJson(new { ExportPath = CliOutputFormatter.FormatPath(fullPath, redactOutput), Success = true, DiagCode = "config-export-success" }));
                }

                return (true, $"[diag-code:config-export-success] Exported config to {CliOutputFormatter.FormatPath(fullPath, redactOutput)}.");
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-export-failed", nameof(ExportConfigFromCli), ex);
                return jsonOutput
                    ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-export-failed", Error = "Failed to export config." }))
                    : (false, "[diag-code:config-export-failed] Failed to export config.");
            }
        }

        public (bool Success, string Output) ImportConfigFromCli(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-import-path-blocked", Error = pathError ?? "Import path is not allowed." }))
                        : (false, $"[diag-code:config-import-path-blocked] {pathError ?? "Import path is not allowed."}");
                }

                if (!File.Exists(fullPath))
                {
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-import-file-missing", Path = CliOutputFormatter.FormatPath(fullPath, redactOutput) }))
                        : (false, $"[diag-code:config-import-file-missing] Import file not found: {CliOutputFormatter.FormatPath(fullPath, redactOutput)}");
                }

                Settings current = CurrentSettings ?? _settings.LoadSettings();
                string importJson = SettingsTransferService.ReadImportText(fullPath, _settings.ReadTextFileWithSettingsLock);
                Settings imported = SettingsTransferService.ParseImportedSettings(importJson, current, replaceImport);
                _settings.SaveSettings(imported);
                ApplyExternallyReloadedSettings(imported);
                lock (_settingsLock)
                {
                    _cachedSettings = imported;
                }
                UpdateLastSettingsWriteTime();

                string mode = replaceImport ? "replace" : "merge";
                if (jsonOutput)
                {
                    return (true, SerializeCliJson(new { Success = true, Mode = mode, DiagCode = "config-import-success", Path = CliOutputFormatter.FormatPath(fullPath, redactOutput) }));
                }

                return (true, $"[diag-code:config-import-success] Imported config from {CliOutputFormatter.FormatPath(fullPath, redactOutput)} using {mode} mode.");
            }
            catch (InvalidDataException ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCli), ex);
                return BuildConfigImportFailure("config-import-invalid-data", ex.Message, jsonOutput);
            }
            catch (JsonException ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCli), ex);
                return BuildConfigImportFailure("config-import-invalid-json", "Imported config is not valid JSON.", jsonOutput);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCli), ex);
                return jsonOutput
                    ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-import-failed", Error = "Failed to import config." }))
                    : (false, "[diag-code:config-import-failed] Failed to import config.");
            }
        }

        private static (bool Success, string Output) BuildConfigImportFailure(string diagCode, string message, bool jsonOutput)
        {
            return jsonOutput
                ? (false, SerializeCliJson(new { Success = false, DiagCode = diagCode, Error = message }))
                : (false, $"[diag-code:{diagCode}] {message}");
        }

        public bool SetStartupEnabledFromCli(bool enabled)
        {
            try
            {
                string startupRegistryOpId = $"startup-registry:{Guid.NewGuid():N}";
                if (enabled)
                {
                    _startup.AddToStartup(startupRegistryOpId);
                }
                else
                {
                    _startup.RemoveFromStartup(startupRegistryOpId);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool IsStartupEnabledFromCli()
        {
            return _startup.IsInStartupWithValidPath();
        }

        public bool OpenStartupSettingsFromCli()
        {
            try
            {
                ShowWindow();
                return TryOpenStartupSettingsUri("ms-settings:startupapps")
                    || TryOpenStartupSettingsUri("shell:startup");
            }
            catch
            {
                return false;
            }
        }

        public string GetStartupStatusFromCli(bool jsonOutput)
        {
            bool startupEnabled = IsStartupEnabledFromCli();
            return FormatStartupStatus(startupEnabled, jsonOutput);
        }

        public string GetStatusFromCli(bool jsonOutput, bool redactOutput = false)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            var diagnostics = BuildSettingsDiagnostics();
            List<string> warnings = BuildDiagnosticMessages(diagnostics.Warnings);

            var (currentInputListenEnabled, listenMonitorTargetOutputDeviceName) = GetCurrentListenStatusSnapshot();

            return FormatStatusSnapshot(
                IsStartupEnabledFromCli(),
                GetActiveOutputDeviceInfos().Count,
                GetActiveInputDeviceInfos().Count,
                OutputCycleDevices.Count,
                InputCycleDevices.Count,
                currentInputListenEnabled,
                listenMonitorTargetOutputDeviceName,
                warnings,
                settings.Miscellaneous.BluetoothReconnectEnabled,
                BluetoothReconnectRuntimeConfig.MaxAttempts,
                BluetoothReconnectRuntimeConfig.AttemptTimeoutMs,
                BluetoothReconnectRuntimeConfig.CooldownMs,
                BluetoothReconnectRuntimeConfig.OnlyLikelyBluetoothEndpoints,
                RuntimeTuningConfig.OutputSwitchDebounceMs,
                RuntimeTuningConfig.InputSwitchDebounceMs,
                RuntimeTuningConfig.SwitchRetryDelayMs,
                RuntimeTuningConfig.SwitchRetryMaxDelayMs,
                RuntimeTuningConfig.SwitchMaxRetries,
                RuntimeTuningConfig.HotplugRefreshDebounceMs,
                RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs,
                RuntimeTuningConfig.MixerSessionRefreshDebounceMs,
                RuntimeTuningConfig.MixerSnapshotCacheInteractiveMs,
                RuntimeTuningConfig.MixerSnapshotCacheBackgroundMs,
                RuntimeTuningConfig.ResumeHotkeyRetryDelayMs,
                RuntimeTuningConfig.MixerDiagnosticsSummaryWindowSeconds,
                RuntimeTuningConfig.MixerCacheWindowDiagnosticsLogEveryNRefreshes,
                RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs,
                RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitThreshold,
                RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitOpenMs,
                jsonOutput,
                redactOutput: redactOutput);
        }

        public string GetDiagnosticsStatusFromCli(bool jsonOutput, bool showPaths, bool redactOutput = false)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            LogFileInventory logInventory = LogArchiveExportService.GetInventory(AppDomain.CurrentDomain.BaseDirectory);

            string settingsPath = _settings.GetSettingsPath();
            string? settingsDirectory = Path.GetDirectoryName(settingsPath);
            string settingsBackupDirectory = Path.Combine(settingsDirectory ?? string.Empty, AppConstants.Files.BackupFolderName);
            List<string> settingsBackupFiles = GetSortedFiles(settingsBackupDirectory, AppConstants.Files.SettingsFileName + ".bak*");

            return FormatDiagnosticsStatus(
                logInventory.LogFilePath,
                logInventory.LogFileExists,
                logInventory.LogFileBytes,
                logInventory.LogBackupDirectory,
                logInventory.LogBackupFiles,
                AppConstants.Files.LogBackupRetentionCount,
                AppConstants.Logging.LogBackupMaxAgeDays,
                settingsPath,
                settingsBackupDirectory,
                settingsBackupFiles,
                AppConstants.Files.SettingsBackupRetentionCount,
                settings.Miscellaneous.BluetoothReconnectEnabled,
                BluetoothReconnectRuntimeConfig.MaxAttempts,
                BluetoothReconnectRuntimeConfig.AttemptTimeoutMs,
                BluetoothReconnectRuntimeConfig.CooldownMs,
                BluetoothReconnectRuntimeConfig.OnlyLikelyBluetoothEndpoints,
                RuntimeTuningConfig.OutputSwitchDebounceMs,
                RuntimeTuningConfig.InputSwitchDebounceMs,
                RuntimeTuningConfig.SwitchRetryDelayMs,
                RuntimeTuningConfig.SwitchRetryMaxDelayMs,
                RuntimeTuningConfig.SwitchMaxRetries,
                RuntimeTuningConfig.HotplugRefreshDebounceMs,
                RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs,
                RuntimeTuningConfig.MixerSessionRefreshDebounceMs,
                RuntimeTuningConfig.MixerSnapshotCacheInteractiveMs,
                RuntimeTuningConfig.MixerSnapshotCacheBackgroundMs,
                RuntimeTuningConfig.ResumeHotkeyRetryDelayMs,
                RuntimeTuningConfig.MixerDiagnosticsSummaryWindowSeconds,
                RuntimeTuningConfig.MixerCacheWindowDiagnosticsLogEveryNRefreshes,
                RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs,
                RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitThreshold,
                RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitOpenMs,
                jsonOutput,
                includeSensitivePaths: showPaths,
                redactOutput: redactOutput);
        }

        public string GetDiagnosticsHistoryFromCli(bool jsonOutput, int? limit, string? type, bool redactOutput = false)
        {
            return GetDiagnosticsHistoryOutput(jsonOutput, limit, type, redactOutput);
        }

        public (bool Found, string Output) GetDiagnosticsHistoryDetailFromCli(string opId, bool jsonOutput, bool redactOutput = false)
        {
            return GetDiagnosticsHistoryDetailOutput(opId, jsonOutput, redactOutput);
        }

        public async ValueTask<bool> SwitchOutputFromCliAsync(bool muteMic, bool muteSound, bool deafen, bool reverse)
        {
            string? beforeDevice = TryGetCurrentDefaultDeviceName(output: true, reason: "cli-history:output-before");
            bool success = await SwitchDevicesAsync(muteMic, muteSound, deafen, reverse);
            string? afterDevice = TryGetCurrentDefaultDeviceName(output: true, reason: "cli-history:output-after");

            RecordCliActionHistory(
                ExecutionHistoryKind.Switch,
                reverse ? "switch-output-reverse" : "switch-output",
                success,
                skipped: false,
                success ? $"Output switch completed{(string.IsNullOrWhiteSpace(afterDevice) ? string.Empty : $" to '{afterDevice}'")}." : "Output switch failed or was rejected.",
                success ? null : "Output switch failed or was rejected.",
                target: beforeDevice,
                outputDeviceName: afterDevice);

            return success;
        }

        public async ValueTask<bool> SwitchInputFromCliAsync(bool reverse)
        {
            string? beforeDevice = TryGetCurrentDefaultDeviceName(output: false, reason: "cli-history:input-before");
            bool success = await SwitchInputDevicesAsync(reverse);
            string? afterDevice = TryGetCurrentDefaultDeviceName(output: false, reason: "cli-history:input-after");

            RecordCliActionHistory(
                ExecutionHistoryKind.Switch,
                reverse ? "switch-input-reverse" : "switch-input",
                success,
                skipped: false,
                success ? $"Input switch completed{(string.IsNullOrWhiteSpace(afterDevice) ? string.Empty : $" to '{afterDevice}'")}." : "Input switch failed or was rejected.",
                success ? null : "Input switch failed or was rejected.",
                target: beforeDevice,
                inputDeviceName: afterDevice);

            return success;
        }

        public (bool Success, string Output) ExportLogsFromCli(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput, bool redactOutput = false)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:diagnostics-export-logs-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    const string message = "Only .zip log exports are supported.";
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-invalid-path", Error = message }))
                        : (false, $"[diag-code:diagnostics-export-logs-invalid-path] {message}");
                }

                LogArchiveExportResult result = LogArchiveExportService.ExportLogs(AppDomain.CurrentDomain.BaseDirectory, fullPath);
                return (true, CliOutputFormatter.FormatLogExportResult(result, detailLevel, jsonOutput, redactOutput));
            }
            catch (InvalidOperationException ex)
            {
                return jsonOutput
                    ? (false, SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-unavailable", Error = ex.Message }))
                    : (false, $"[diag-code:diagnostics-export-logs-unavailable] {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-diagnostics-export-logs-failed", nameof(ExportLogsFromCli), ex);
                return jsonOutput
                    ? (false, SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-failed", Error = "Failed to export log archive." }))
                    : (false, "[diag-code:diagnostics-export-logs-failed] Failed to export log archive.");
            }
        }

        public (bool Success, string Output) ResetPerAppAudioRoutingFromCli(bool jsonOutput)
        {
            try
            {
                PerAppAudioRoutingResetResult result = _audio.ResetAllPerAppAudioRouting();
                return (result.Success, CliOutputFormatter.FormatPerAppAudioResetResult(result, jsonOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-reset-per-app-audio-failed", nameof(ResetPerAppAudioRoutingFromCli), ex);
                PerAppAudioRoutingResetResult failed = new(Success: false, HadAssignments: false);
                return (false, CliOutputFormatter.FormatPerAppAudioResetResult(failed, jsonOutput));
            }
        }

        public IReadOnlyList<string> GetConfigurationWarningsForUi()
        {
            var diagnostics = BuildSettingsDiagnostics();
            var warnings = new List<string>();
            for (int index = 0; index < diagnostics.Warnings.Count; index++)
            {
                warnings.Add(FormatDiagnosticForUi(diagnostics.Warnings[index]));
            }

            if (!string.IsNullOrWhiteSpace(_settings.LastLoadUserWarning))
            {
                warnings.Add(_settings.LastLoadUserWarning);
            }

            return warnings;
        }

        private static string FormatDiagnosticForUi(SettingsDiagnostic warning)
        {
            return $"{warning.Message} {warning.SuggestedAction}".Trim();
        }

        private static string FormatDiagnostic(SettingsDiagnostic warning)
        {
            return $"[diag-code:{warning.Code}] {warning.Message} {warning.SuggestedAction}";
        }

        private static List<string> BuildDiagnosticMessages(IReadOnlyList<SettingsDiagnostic> warnings)
        {
            var messages = new List<string>(warnings.Count);
            for (int index = 0; index < warnings.Count; index++)
            {
                messages.Add(FormatDiagnostic(warnings[index]));
            }

            return messages;
        }

        private static List<string> GetSortedFiles(string directory, string pattern)
        {
            if (!Directory.Exists(directory))
            {
                return [];
            }

            string[] files = Directory.GetFiles(directory, pattern);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return [.. files];
        }

        private SettingsDiagnosticsResult BuildSettingsDiagnostics()
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            List<CycleDevice> outputDevices;
            List<CycleDevice> inputDevices;

            if (_outputDevices.Count > 0 || _inputDevices.Count > 0)
            {
                outputDevices = CloneDeviceInfoList(_outputDevices);
                inputDevices = CloneDeviceInfoList(_inputDevices);
            }
            else
            {
                outputDevices = GetActiveOutputDeviceInfos();
                inputDevices = GetActiveInputDeviceInfos();
            }

            return SettingsValidationService.EvaluateDiagnostics(
                settings,
                outputDevices,
                inputDevices);
        }

        public string GetDeviceListFromCli(bool output, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            var devices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            return FormatDeviceList(kind, devices, jsonOutput, redactOutput);
        }

        public (bool Found, string Output) GetDeviceFromCli(bool output, string selector, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            List<CycleDevice> devices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            CliDeviceSelectorResolution resolution = CliDeviceSelectorResolver.ResolveExact(devices, selector);
            if (resolution.Success && resolution.Device != null)
            {
                return (true, CliOutputFormatter.FormatDeviceGetResult(kind, resolution.Device, jsonOutput, redactOutput));
            }

            string message = resolution.Ambiguous
                ? CliDeviceSelectorResolver.BuildAmbiguousMessage(kind, resolution.Selector, resolution.Matches)
                : CliDeviceSelectorResolver.BuildNotFoundMessage(kind, resolution.Selector);
            return (false, CliOutputFormatter.FormatDeviceGetError(kind, resolution.Ambiguous ? "device-selector-ambiguous" : "device-not-found", message, jsonOutput));
        }

        public (bool Found, string Output) FindDevicesFromCli(bool output, string query, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            List<CycleDevice> devices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            IReadOnlyList<CycleDevice> matches = CliDeviceSelectorResolver.FindMatches(devices, query);
            return (matches.Count > 0, CliOutputFormatter.FormatDeviceFindResult(kind, query, matches, jsonOutput, redactOutput));
        }

        public string GetCycleFromCli(bool output, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            return FormatCycleList(kind, output ? OutputCycleDevices : InputCycleDevices, jsonOutput, redactOutput);
        }

        public (bool IsValid, string Output) GetCycleValidationFromCli(bool output, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            IReadOnlyList<CycleDevice> cycleDevices = output ? OutputCycleDevices : InputCycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            var result = SettingsValidationService.ValidateCycle(cycleDevices, activeDevices);
            string formatted = FormatCycleValidation(kind, result.DuplicateDeviceNames, result.DisconnectedDeviceNames, jsonOutput, redactOutput);
            return (result.IsValid, formatted);
        }

        public (bool CanSwitch, string Output) GetCycleTestFromCli(bool output, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            IReadOnlyList<CycleDevice> cycleDevices = output ? OutputCycleDevices : InputCycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            var preflight = SettingsValidationService.EvaluateCycleSwitchPreflight(
                cycleDevices,
                activeDevices,
                hasDefaultInputDevice: HasDefaultInputDevice(),
                output);

            return (
                preflight.CanSwitch,
                FormatCycleTest(
                    kind,
                    preflight.ConfiguredCount,
                    preflight.ConnectedConfiguredCount,
                    preflight.HasDefaultInputDevice,
                    preflight.Reasons,
                    jsonOutput,
                    redactOutput));
        }

        public (bool Success, string Output) AddCycleDeviceFromCli(bool output, string deviceId, bool jsonOutput, bool redactOutput = false)
        {
            return UpdateCycleFromCli(output, "add", deviceId, null, jsonOutput, redactOutput);
        }

        public (bool Success, string Output) RemoveCycleDeviceFromCli(bool output, string deviceId, bool jsonOutput, bool redactOutput = false)
        {
            return UpdateCycleFromCli(output, "remove", deviceId, null, jsonOutput, redactOutput);
        }

        public (bool Success, string Output) ReorderCycleFromCli(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput = false)
        {
            return UpdateCycleFromCli(output, "reorder", null, deviceIds, jsonOutput, redactOutput);
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

        private (bool Success, string Output) UpdateCycleFromCli(
            bool output,
            string action,
            string? deviceId,
            IReadOnlyList<string>? orderedDeviceIds,
            bool jsonOutput,
            bool redactOutput)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            List<CycleDevice> cycleDevices = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
            List<CycleDevice> activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            string kind = output ? "output" : "input";
            string? deviceName = FindCycleMutationDeviceName(cycleDevices, activeDevices, deviceId);

            bool updated;
            string? error;
            switch (action)
            {
                case "add":
                    if (!TryResolveCycleMutationSelector(activeDevices, kind, deviceId ?? string.Empty, out string resolvedAddDeviceId, out string? addSelectorError))
                    {
                        return (false, FormatCycleMutationFailure(action, addSelectorError ?? "Failed to resolve device selector.", jsonOutput));
                    }

                    deviceId = resolvedAddDeviceId;
                    deviceName = FindCycleMutationDeviceName(cycleDevices, activeDevices, deviceId);
                    updated = CliCycleManager.TryAddDevice(cycleDevices, activeDevices, resolvedAddDeviceId, out _, out _, out string addMessage);
                    error = addMessage;
                    break;
                case "remove":
                    if (!TryResolveCycleMutationSelector(cycleDevices, kind, deviceId ?? string.Empty, out string resolvedRemoveDeviceId, out string? removeSelectorError, selector => $"Device '{selector}' is not configured in the cycle."))
                    {
                        return (false, FormatCycleMutationFailure(action, removeSelectorError ?? "Failed to resolve device selector.", jsonOutput));
                    }

                    deviceId = resolvedRemoveDeviceId;
                    deviceName = FindCycleMutationDeviceName(cycleDevices, activeDevices, deviceId);
                    updated = CliCycleManager.TryRemoveDevice(cycleDevices, resolvedRemoveDeviceId, out _, out _, out string removeMessage);
                    error = removeMessage;
                    break;
                case "reorder":
                    updated = CliCycleManager.TryReorder(cycleDevices, orderedDeviceIds ?? [], out _, out string reorderMessage);
                    error = reorderMessage;
                    break;
                default:
                    updated = false;
                    error = "Unsupported cycle action.";
                    break;
            }

            if (!updated)
            {
                return (false, FormatCycleMutationFailure(action, error ?? "Failed to update cycle.", jsonOutput));
            }

            try
            {
                _settings.SaveSettings(settings);
                ApplyExternallyReloadedSettings(settings);
                lock (_settingsLock)
                {
                    _cachedSettings = settings;
                }
                UpdateLastSettingsWriteTime();

                return (true, CliOutputFormatter.FormatCycleMutationResult(kind, action, GetCycleDiagCode(action), cycleDevices, deviceId, deviceName, jsonOutput, redactOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-cycle-update-failed", nameof(UpdateCycleFromCli), ex);
                return (false, FormatCycleMutationFailure(action, "Failed to persist cycle changes.", jsonOutput));
            }
        }

        private static string? FindCycleMutationDeviceName(List<CycleDevice> cycleDevices, List<CycleDevice> activeDevices, string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return null;
            }

            for (int index = 0; index < cycleDevices.Count; index++)
            {
                if (string.Equals(cycleDevices[index].Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return cycleDevices[index].Name;
                }
            }

            for (int index = 0; index < activeDevices.Count; index++)
            {
                if (string.Equals(activeDevices[index].Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return activeDevices[index].Name;
                }
            }

            return null;
        }

        private static bool TryResolveCycleMutationSelector(IReadOnlyList<CycleDevice> devices, string kind, string selectorSpec, out string resolvedDeviceId, out string? error, Func<string, string>? notFoundMessageFactory = null)
        {
            resolvedDeviceId = selectorSpec;
            error = null;

            CliDeviceSelectorResolution resolution = CliDeviceSelectorResolver.ResolveExact(devices, selectorSpec);
            if (resolution.Success && resolution.Device != null)
            {
                resolvedDeviceId = resolution.Device.Id;
                return true;
            }

            error = resolution.Ambiguous
                ? CliDeviceSelectorResolver.BuildAmbiguousMessage(kind, resolution.Selector, resolution.Matches)
                : notFoundMessageFactory?.Invoke(resolution.Selector) ?? CliDeviceSelectorResolver.BuildNotFoundMessage(kind, resolution.Selector);
            return false;
        }

        private static string GetCycleDiagCode(string action)
        {
            return action switch
            {
                "add" => "cycle-add-success",
                "remove" => "cycle-remove-success",
                "reorder" => "cycle-reorder-success",
                _ => "cycle-update-success",
            };
        }

        private static string FormatCycleMutationFailure(string action, string message, bool jsonOutput)
        {
            string diagCode = action switch
            {
                "add" => "cycle-add-failed",
                "remove" => "cycle-remove-failed",
                "reorder" => "cycle-reorder-failed",
                _ => "cycle-update-failed",
            };

            return jsonOutput
                ? SerializeCliJson(new { Success = false, DiagCode = diagCode, Error = message })
                : $"[diag-code:{diagCode}] {message}";
        }

        public static string FormatStartupStatus(bool startupEnabled, bool jsonOutput)
        {
            return CliOutputFormatter.FormatStartupStatus(startupEnabled, jsonOutput);
        }

        public static string FormatStatusSnapshot(
            bool startupEnabled,
            int availableOutputDevices,
            int availableInputDevices,
            int configuredOutputCycleDevices,
            int configuredInputCycleDevices,
            bool? currentInputListenEnabled,
            string? listenMonitorTargetOutputDeviceName,
            bool jsonOutput,
            bool redactOutput = false)
        {
            return FormatStatusSnapshot(
                startupEnabled,
                availableOutputDevices,
                availableInputDevices,
                configuredOutputCycleDevices,
                configuredInputCycleDevices,
                currentInputListenEnabled,
                listenMonitorTargetOutputDeviceName,
                [],
                jsonOutput,
                redactOutput);
        }

        public static string FormatStatusSnapshot(
            bool startupEnabled,
            int availableOutputDevices,
            int availableInputDevices,
            int configuredOutputCycleDevices,
            int configuredInputCycleDevices,
            bool? currentInputListenEnabled,
            string? listenMonitorTargetOutputDeviceName,
            IReadOnlyList<string> warnings,
            bool bluetoothReconnectEnabled,
            int bluetoothReconnectMaxAttempts,
            int bluetoothReconnectAttemptTimeoutMs,
            int bluetoothReconnectCooldownMs,
            bool bluetoothReconnectOnlyLikely,
            int outputSwitchDebounceMs,
            int inputSwitchDebounceMs,
            int switchRetryDelayMs,
            int switchRetryMaxDelayMs,
            int switchMaxRetries,
            int hotplugRefreshDebounceMs,
            int hotplugConnectedOverlaySuppressAfterSwitchMs,
            int mixerSessionRefreshDebounceMs,
            int mixerSnapshotCacheInteractiveMs,
            int mixerSnapshotCacheBackgroundMs,
            int resumeHotkeyRetryDelayMs,
            int mixerDiagnosticsSummaryWindowSeconds,
            int mixerCacheWindowDiagnosticsLogEveryNRefreshes,
            int bluetoothReconnectSuccessObservedRecheckIntervalMs,
            int bluetoothReconnectTimeoutCircuitThreshold,
            int bluetoothReconnectTimeoutCircuitOpenMs,
            bool jsonOutput,
            bool redactOutput = false)
        {
            return CliOutputFormatter.FormatStatusSnapshot(
                startupEnabled,
                availableOutputDevices,
                availableInputDevices,
                configuredOutputCycleDevices,
                configuredInputCycleDevices,
                currentInputListenEnabled,
                listenMonitorTargetOutputDeviceName,
                warnings,
                jsonOutput,
                bluetoothReconnectEnabled,
                bluetoothReconnectMaxAttempts,
                bluetoothReconnectAttemptTimeoutMs,
                bluetoothReconnectCooldownMs,
                bluetoothReconnectOnlyLikely,
                outputSwitchDebounceMs,
                inputSwitchDebounceMs,
                switchRetryDelayMs,
                switchRetryMaxDelayMs,
                switchMaxRetries,
                hotplugRefreshDebounceMs,
                hotplugConnectedOverlaySuppressAfterSwitchMs,
                mixerSessionRefreshDebounceMs,
                mixerSnapshotCacheInteractiveMs,
                mixerSnapshotCacheBackgroundMs,
                resumeHotkeyRetryDelayMs,
                mixerDiagnosticsSummaryWindowSeconds,
                mixerCacheWindowDiagnosticsLogEveryNRefreshes,
                bluetoothReconnectSuccessObservedRecheckIntervalMs,
                bluetoothReconnectTimeoutCircuitThreshold,
                bluetoothReconnectTimeoutCircuitOpenMs,
                redactOutput: redactOutput);
        }

        public static string FormatStatusSnapshot(
            bool startupEnabled,
            int availableOutputDevices,
            int availableInputDevices,
            int configuredOutputCycleDevices,
            int configuredInputCycleDevices,
            bool? currentInputListenEnabled,
            string? listenMonitorTargetOutputDeviceName,
            IReadOnlyList<string> warnings,
            bool jsonOutput,
            bool redactOutput = false)
        {
            return CliOutputFormatter.FormatStatusSnapshot(
                startupEnabled,
                availableOutputDevices,
                availableInputDevices,
                configuredOutputCycleDevices,
                configuredInputCycleDevices,
                currentInputListenEnabled,
                listenMonitorTargetOutputDeviceName,
                warnings,
                jsonOutput,
                redactOutput: redactOutput);
        }

        public static string FormatStatusSnapshot(
            bool startupEnabled,
            int availableOutputDevices,
            int availableInputDevices,
            int configuredOutputCycleDevices,
            int configuredInputCycleDevices,
            bool jsonOutput)
        {
            return FormatStatusSnapshot(
                startupEnabled,
                availableOutputDevices,
                availableInputDevices,
                configuredOutputCycleDevices,
                configuredInputCycleDevices,
                null,
                null,
                [],
                jsonOutput);
        }

        public static string FormatDeviceList(string kind, IReadOnlyList<CycleDevice> devices, bool jsonOutput, bool redactOutput = false)
        {
            return CliOutputFormatter.FormatDeviceList(kind, devices, jsonOutput, redactOutput);
        }

        public static string FormatCycleList(string kind, IReadOnlyList<CycleDevice> cycleDevices, bool jsonOutput, bool redactOutput = false)
        {
            return CliOutputFormatter.FormatCycleList(kind, cycleDevices, jsonOutput, redactOutput);
        }

        public static string FormatCycleValidation(
            string kind,
            IReadOnlyList<string> duplicateDeviceNames,
            IReadOnlyList<string> disconnectedDeviceNames,
            bool jsonOutput,
            bool redactOutput = false)
        {
            return CliOutputFormatter.FormatCycleValidation(kind, duplicateDeviceNames, disconnectedDeviceNames, jsonOutput, redactOutput);
        }

        public static string FormatCycleTest(
            string kind,
            int configuredCount,
            int connectedConfiguredCount,
            bool hasDefaultInputDevice,
            IReadOnlyList<string> reasons,
            bool jsonOutput,
            bool redactOutput = false)
        {
            return CliOutputFormatter.FormatCycleTest(
                kind,
                configuredCount,
                connectedConfiguredCount,
                hasDefaultInputDevice,
                reasons,
                jsonOutput,
                redactOutput);
        }

        public static string FormatConfigValidation(IReadOnlyList<string> warnings, bool jsonOutput)
        {
            return CliOutputFormatter.FormatConfigValidation(warnings, jsonOutput);
        }

        public static string FormatDiagnosticsStatus(
            string logFilePath,
            bool logFileExists,
            long logFileBytes,
            string logBackupDirectory,
            IReadOnlyList<string> logBackupFiles,
            int logBackupRetentionCount,
            int logBackupMaxAgeDays,
            string settingsPath,
            string settingsBackupDirectory,
            IReadOnlyList<string> settingsBackupFiles,
            int settingsBackupRetentionCount,
            bool bluetoothReconnectEnabled,
            int bluetoothReconnectMaxAttempts,
            int bluetoothReconnectAttemptTimeoutMs,
            int bluetoothReconnectCooldownMs,
            bool bluetoothReconnectOnlyLikely,
            int outputSwitchDebounceMs,
            int inputSwitchDebounceMs,
            int switchRetryDelayMs,
            int switchRetryMaxDelayMs,
            int switchMaxRetries,
            int hotplugRefreshDebounceMs,
            int hotplugConnectedOverlaySuppressAfterSwitchMs,
            int mixerSessionRefreshDebounceMs,
            int mixerSnapshotCacheInteractiveMs,
            int mixerSnapshotCacheBackgroundMs,
            int resumeHotkeyRetryDelayMs,
            int mixerDiagnosticsSummaryWindowSeconds,
            int mixerCacheWindowDiagnosticsLogEveryNRefreshes,
            int bluetoothReconnectSuccessObservedRecheckIntervalMs,
            int bluetoothReconnectTimeoutCircuitThreshold,
            int bluetoothReconnectTimeoutCircuitOpenMs,
            bool jsonOutput,
            bool includeSensitivePaths = false,
            bool redactOutput = false)
        {
            return CliOutputFormatter.FormatDiagnosticsStatus(
                logFilePath,
                logFileExists,
                logFileBytes,
                logBackupDirectory,
                logBackupFiles,
                logBackupRetentionCount,
                logBackupMaxAgeDays,
                settingsPath,
                settingsBackupDirectory,
                settingsBackupFiles,
                settingsBackupRetentionCount,
                jsonOutput,
                bluetoothReconnectEnabled,
                bluetoothReconnectMaxAttempts,
                bluetoothReconnectAttemptTimeoutMs,
                bluetoothReconnectCooldownMs,
                bluetoothReconnectOnlyLikely,
                outputSwitchDebounceMs,
                inputSwitchDebounceMs,
                switchRetryDelayMs,
                switchRetryMaxDelayMs,
                switchMaxRetries,
                hotplugRefreshDebounceMs,
                hotplugConnectedOverlaySuppressAfterSwitchMs,
                mixerSessionRefreshDebounceMs,
                mixerSnapshotCacheInteractiveMs,
                mixerSnapshotCacheBackgroundMs,
                resumeHotkeyRetryDelayMs,
                mixerDiagnosticsSummaryWindowSeconds,
                mixerCacheWindowDiagnosticsLogEveryNRefreshes,
                bluetoothReconnectSuccessObservedRecheckIntervalMs,
                bluetoothReconnectTimeoutCircuitThreshold,
                bluetoothReconnectTimeoutCircuitOpenMs,
                includeSensitivePaths: includeSensitivePaths,
                redactOutput: redactOutput);
        }

        public static string FormatDiagnosticsStatus(
            string logFilePath,
            bool logFileExists,
            long logFileBytes,
            string logBackupDirectory,
            IReadOnlyList<string> logBackupFiles,
            int logBackupRetentionCount,
            int logBackupMaxAgeDays,
            string settingsPath,
            string settingsBackupDirectory,
            IReadOnlyList<string> settingsBackupFiles,
            int settingsBackupRetentionCount,
            bool jsonOutput,
            bool includeSensitivePaths = false,
            bool redactOutput = false)
        {
            return CliOutputFormatter.FormatDiagnosticsStatus(
                logFilePath,
                logFileExists,
                logFileBytes,
                logBackupDirectory,
                logBackupFiles,
                logBackupRetentionCount,
                logBackupMaxAgeDays,
                settingsPath,
                settingsBackupDirectory,
                settingsBackupFiles,
                settingsBackupRetentionCount,
                jsonOutput,
                includeSensitivePaths: includeSensitivePaths,
                redactOutput: redactOutput);
        }

        private bool HasDefaultInputDevice()
        {
            try
            {
                using var device = _audio.GetDefaultRecordingDevice();
                return device != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryOpenStartupSettingsUri(string uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true,
                });

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string SerializeCliJson<T>(T data)
        {
            return CliOutputFormatter.SerializeCliJson(data);
        }
    }
}
