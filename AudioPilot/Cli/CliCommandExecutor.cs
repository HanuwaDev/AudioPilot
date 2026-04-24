namespace AudioPilot.Cli
{
    public readonly record struct CliExecutionResult(int ExitCode, string? Output = null);

    public interface ICliCommandRuntime
    {
        void ShowWindow();
        void HideWindow();
        void MediaPlayPause();
        void MediaNextTrack();
        void MediaPreviousTrack();
        string GetMediaStatus(bool jsonOutput, bool redactOutput);
        bool ToggleMuteMic();
        bool SetMuteMic(bool enabled);
        bool ToggleMuteSound();
        bool SetMuteSound(bool enabled);
        bool ToggleDeafen();
        bool SetDeafen(bool enabled);
        bool ToggleListenToInput();
        bool SetListenToInput(bool enabled);
        string GetMuteStatus(string target, bool jsonOutput);
        string GetListenStatus(bool jsonOutput, bool redactOutput);
        (bool Success, string Output) GetVolume(bool playback, string? deviceId, bool jsonOutput);
        (bool Success, string Output) SetVolume(bool playback, string? deviceId, float percent, bool jsonOutput);
        string GetRoutineList(bool jsonOutput, bool redactOutput);
        Task<CliExecutionResult> RunRoutineAsync(string routineSelector, bool jsonOutput, bool redactOutput);
        CliExecutionResult SetRoutineEnabled(string routineSelector, bool enabled, bool jsonOutput, bool redactOutput);
        CliExecutionResult CreateRoutine(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput);
        CliExecutionResult UpdateRoutine(string routineSelector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput);
        CliExecutionResult DeleteRoutine(string routineSelector, bool jsonOutput, bool redactOutput);
        CliExecutionResult ImportRoutines(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput);
        ValueTask<bool> SwitchOutputAsync(bool muteMic, bool muteSound, bool deafen, bool reverse);
        ValueTask<bool> SwitchInputAsync(bool reverse);
        Task RefreshAsync();
        bool SetStartupEnabled(bool enabled);
        bool OpenStartupSettings();
        string GetStartupStatus(bool jsonOutput);
        string GetStatus(bool jsonOutput, bool redactOutput);
        string GetDiagnosticsStatus(bool jsonOutput, bool showPaths, bool redactOutput);
        string GetDiagnosticsHistory(bool jsonOutput, int? limit, string? type, bool redactOutput);
        (bool Found, string Output) GetDiagnosticsHistoryDetail(string opId, bool jsonOutput, bool redactOutput);
        string GetDeviceList(bool output, bool jsonOutput, bool redactOutput);
        (bool Found, string Output) GetDevice(bool output, string selector, bool jsonOutput, bool redactOutput);
        (bool Found, string Output) FindDevices(bool output, string query, bool jsonOutput, bool redactOutput);
        string GetCycle(bool output, bool jsonOutput, bool redactOutput);
        (bool IsValid, string Output) GetCycleValidation(bool output, bool jsonOutput, bool redactOutput);
        (bool CanSwitch, string Output) GetCycleTest(bool output, bool jsonOutput, bool redactOutput);
        (bool Success, string Output) AddCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput);
        (bool Success, string Output) RemoveCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput);
        (bool Success, string Output) ReorderCycle(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput);
        (bool CanSwitch, string Output) PreviewSwitch(bool output, bool reverse, bool jsonOutput, bool redactOutput);
        string? GetCurrentDeviceId(bool output);
        Task<(bool Found, string Output)> WaitForDeviceAsync(string deviceId, int timeoutMs, bool outputOnly, bool inputOnly, bool jsonOutput, bool redactOutput);
        (bool Found, string? Value, string? Error) GetConfig(string key);
        string GetConfigList(bool jsonOutput);
        (bool Updated, string? Error) SetConfig(string key, string value);
        (bool Found, string? Value, string? Error) GetRuntime(string key);
        string GetRuntimeList(bool jsonOutput);
        (bool Updated, string? Error) SetRuntime(string key, string value);
        (bool IsValid, string Output) GetConfigValidation(bool jsonOutput, bool redactOutput);
        (bool Success, string Output) ExportLogs(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput, bool redactOutput);
        (bool Success, string Output) ExportDiagnosticBundle(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool includeSensitive, bool jsonOutput);
        (bool Success, string Output) ResetPerAppAudioRouting(bool jsonOutput);
        (bool Success, string Output) ExportRoutines(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput);
        (bool Success, string Output) ExportConfig(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput);
        (bool Success, string Output) ImportConfig(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput);
        string GetNetworkList(bool jsonOutput);
    }

    public static partial class CliCommandExecutor
    {
        public static async Task<CliExecutionResult> ExecuteAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            switch (command.Action)
            {
                case CliAction.None:
                    return new CliExecutionResult(0);
                case CliAction.Show:
                    runtime.ShowWindow();
                    return new CliExecutionResult(0);
                case CliAction.Hide:
                    runtime.HideWindow();
                    return new CliExecutionResult(0);
                case CliAction.MediaPlayPause:
                case CliAction.MediaNextTrack:
                case CliAction.MediaPreviousTrack:
                case CliAction.MediaStatus:
                    return ExecuteMediaCommand(command, runtime);
                case CliAction.MuteMicToggle:
                case CliAction.MuteMicOn:
                case CliAction.MuteMicOff:
                case CliAction.MuteSoundToggle:
                case CliAction.MuteSoundOn:
                case CliAction.MuteSoundOff:
                case CliAction.DeafenToggle:
                case CliAction.DeafenOn:
                case CliAction.DeafenOff:
                case CliAction.ListenToggle:
                case CliAction.ListenOn:
                case CliAction.ListenOff:
                case CliAction.VolumeGetMaster:
                case CliAction.VolumeGetMic:
                case CliAction.VolumeSetMaster:
                case CliAction.VolumeSetMic:
                case CliAction.SwitchOutput:
                case CliAction.SwitchInput:
                    return ExecuteMediaAndVolumeCommand(command, runtime);
                case CliAction.NetworkList:
                    return ExecuteNetworkCommand(command, runtime);
                case CliAction.RoutineList:
                case CliAction.RoutineRun:
                case CliAction.RoutineEnable:
                case CliAction.RoutineDisable:
                case CliAction.RoutineCreate:
                case CliAction.RoutineUpdate:
                case CliAction.RoutineDelete:
                case CliAction.RoutineImport:
                case CliAction.RoutineExport:
                case CliAction.DiagnosticsStatus:
                case CliAction.DiagnosticsHistory:
                case CliAction.DiagnosticsHistoryDetail:
                case CliAction.DiagnosticsExportLogs:
                case CliAction.DiagnosticsExportBundle:
                case CliAction.DiagnosticsResetPerAppAudio:
                    return await ExecuteRoutinesAndDiagnosticsAsync(command, runtime);
                case CliAction.WaitForDevice:
                case CliAction.Refresh:
                case CliAction.StartupEnable:
                case CliAction.StartupDisable:
                case CliAction.StartupStatus:
                case CliAction.StartupOpen:
                case CliAction.Status:
                case CliAction.DevicesListOutput:
                case CliAction.DevicesListInput:
                case CliAction.DevicesGetOutput:
                case CliAction.DevicesGetInput:
                case CliAction.DevicesFindOutput:
                case CliAction.DevicesFindInput:
                case CliAction.CycleShowOutput:
                case CliAction.CycleShowInput:
                case CliAction.CycleValidateOutput:
                case CliAction.CycleValidateInput:
                case CliAction.CycleTestOutput:
                case CliAction.CycleTestInput:
                case CliAction.CycleAddOutput:
                case CliAction.CycleAddInput:
                case CliAction.CycleRemoveOutput:
                case CliAction.CycleRemoveInput:
                case CliAction.CycleReorderOutput:
                case CliAction.CycleReorderInput:
                case CliAction.ConfigGet:
                case CliAction.ConfigList:
                case CliAction.ConfigSet:
                case CliAction.RuntimeGet:
                case CliAction.RuntimeList:
                case CliAction.RuntimeSet:
                case CliAction.ConfigValidate:
                case CliAction.ConfigExport:
                case CliAction.ConfigImport:
                    return await ExecuteDevicesAndConfigAsync(command, runtime);
                default:
                    return BuildErrorResult(2, "unsupported-command", "Unsupported command.", command.JsonOutput);
            }
        }

        public static CliExecutionResult BuildRuntimeUnavailableResult(bool jsonOutput)
        {
            return BuildErrorResult(3, "app-not-ready", "App is not ready.", jsonOutput);
        }

        public static CliExecutionResult BuildExecutionFailureResult(int exitCode, string errorCode, string message, bool jsonOutput)
        {
            return BuildErrorResult(exitCode, errorCode, message, jsonOutput);
        }

        public static string BuildJsonErrorPayload(int exitCode, string errorCode, string message)
        {
            return CliOutputFormatter.SerializeCliJson(new
            {
                Error = new
                {
                    Code = errorCode,
                    Message = message,
                    ExitCode = exitCode,
                }
            });
        }

        private static CliExecutionResult BuildErrorResult(int exitCode, string errorCode, string message, bool jsonOutput)
        {
            if (!jsonOutput)
            {
                return new CliExecutionResult(exitCode, message);
            }

            string output = BuildJsonErrorPayload(exitCode, errorCode, message);
            return new CliExecutionResult(exitCode, output);
        }

        private static IReadOnlyList<string> SplitDeviceIds(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return [];
            }

            return [.. value
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static id => !string.IsNullOrWhiteSpace(id))];
        }

        private static CliExecutionResult ExecuteNetworkCommand(CliCommand command, ICliCommandRuntime runtime)
        {
            return command.Action switch
            {
                CliAction.NetworkList => new CliExecutionResult(0, runtime.GetNetworkList(command.JsonOutput)),
                _ => BuildErrorResult(2, "unsupported-network-command", "Unsupported network command.", command.JsonOutput),
            };
        }
    }
}
