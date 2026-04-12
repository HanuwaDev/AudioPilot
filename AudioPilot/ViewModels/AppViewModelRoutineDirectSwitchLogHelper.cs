using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal static class AppViewModelRoutineDirectSwitchLogHelper
{
    internal static string BuildStartedMessage(AudioRoutine routine, string flow, string opId, int? appStartProcessId, string? currentDeviceName = null)
    {
        string message = $"routine-{flow}-switch-started | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow, opId, appStartProcessId, perAppRouting: false)}";
        return string.IsNullOrWhiteSpace(currentDeviceName)
            ? message
            : $"{message} currentDevice={AppViewModel.FormatRoutineLogDevice(currentDeviceName)}";
    }

    internal static string BuildCompletedMessage(AudioRoutine routine, string flow, string opId, int? appStartProcessId, bool success, string? deviceName)
    {
        return $"routine-{flow}-switch-completed | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow, opId, appStartProcessId, perAppRouting: false)} success={success} deviceName={AppViewModel.FormatRoutineLogDevice(deviceName)}";
    }

    internal static string BuildOutputSkippedNoDefaultMessage(AudioRoutine routine, int? appStartProcessId)
    {
        string opId = AppViewModel.CreateRoutineOperationId("routine-output");
        return $"routine-output-switch-skipped | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow: "output", opId, appStartProcessId, perAppRouting: false)} reason=no-default-playback-device";
    }

    internal static string BuildOutputAlreadyTargetMessage(AudioRoutine routine, int? appStartProcessId, string? currentDeviceName)
    {
        string opId = AppViewModel.CreateRoutineOperationId("routine-output");
        return $"routine-output-switch-skipped | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow: "output", opId, appStartProcessId, perAppRouting: false)} reason=already-target currentDevice={AppViewModel.FormatRoutineLogDevice(currentDeviceName)}";
    }

    internal static string BuildInputSkippedNoDefaultMessage(AudioRoutine routine, int? appStartProcessId)
    {
        string opId = AppViewModel.CreateRoutineOperationId("routine-input");
        return $"routine-input-switch-skipped | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow: "input", opId, appStartProcessId, perAppRouting: false)} reason=no-default-recording-device";
    }

    internal static string BuildInputAlreadyTargetMessage(AudioRoutine routine, int? appStartProcessId, string? currentDeviceName)
    {
        string opId = AppViewModel.CreateRoutineOperationId("routine-input");
        return $"routine-input-switch-skipped | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow: "input", opId, appStartProcessId, perAppRouting: false)} reason=already-target currentDevice={AppViewModel.FormatRoutineLogDevice(currentDeviceName)}";
    }
}
