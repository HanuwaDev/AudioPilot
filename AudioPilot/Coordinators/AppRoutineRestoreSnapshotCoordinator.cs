using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal readonly record struct RoutineRestoreDeviceInfo(
        string DeviceId,
        string DeviceName,
        float? VolumePercent = null,
        bool? IsMuted = null);

    internal static class AppRoutineRestoreSnapshotCoordinator
    {
        /// <summary>
        /// Captures the default output and input devices that should be restored when a stateful routine deactivates.
        /// </summary>
        /// <remarks>
        /// Snapshot capture is limited to routines whose trigger can remain active over time and only when restore-on-
        /// deactivate is enabled. Output capture is preserved even if recording-device capture fails so partial restore
        /// remains possible instead of discarding the whole activation context.
        /// </remarks>
        internal static AppViewModel.RoutineAudioRestoreSnapshot? CaptureSnapshot(
            AudioRoutine? routine,
            Func<RoutineRestoreDeviceInfo> getDefaultPlaybackDeviceInfo,
            Func<RoutineRestoreDeviceInfo> getDefaultRecordingDeviceInfo,
            Logger logger)
        {
            if (routine == null || !routine.RestorePreviousAudioOnDeactivate || !routine.IsStatefulTrigger)
            {
                return null;
            }

            string previousOutputDeviceId = string.Empty;
            string previousOutputDeviceName = string.Empty;
            string previousInputDeviceId = string.Empty;
            string previousInputDeviceName = string.Empty;
            float? previousOutputVolumePercent = null;
            bool? previousOutputMuted = null;
            float? previousInputVolumePercent = null;
            bool? previousInputMuted = null;

            try
            {
                RoutineRestoreDeviceInfo outputDevice = getDefaultPlaybackDeviceInfo();
                previousOutputDeviceId = outputDevice.DeviceId;
                previousOutputDeviceName = outputDevice.DeviceName;
                previousOutputVolumePercent = outputDevice.VolumePercent;
                previousOutputMuted = outputDevice.IsMuted;

                RoutineRestoreDeviceInfo inputDevice = getDefaultRecordingDeviceInfo();
                previousInputDeviceId = inputDevice.DeviceId;
                previousInputDeviceName = inputDevice.DeviceName;
                previousInputVolumePercent = inputDevice.VolumePercent;
                previousInputMuted = inputDevice.IsMuted;
            }
            catch (Exception ex)
            {
                logger.Warning("AppViewModel", "routine-restore-snapshot-capture-failed", nameof(CaptureSnapshot), ex);
            }

            if (string.IsNullOrWhiteSpace(previousOutputDeviceId) && string.IsNullOrWhiteSpace(previousInputDeviceId))
            {
                logger.Info(
                    "AppViewModel",
                    () => $"routine-restore-snapshot-skipped | {AppViewModel.BuildRoutineExecutionLogContext(routine, "stateful-capture", showOverlay: false, appStartProcessId: null)} reason=no-default-devices");
                return null;
            }

            AppViewModel.RoutineAudioRestoreSnapshot snapshot = new(
                previousOutputDeviceId,
                previousOutputDeviceName,
                previousInputDeviceId,
                previousInputDeviceName,
                previousOutputVolumePercent,
                previousOutputMuted,
                previousInputVolumePercent,
                previousInputMuted);

            logger.Info(
                "AppViewModel",
                () => $"routine-restore-snapshot-captured | {AppViewModel.BuildRoutineExecutionLogContext(routine, "stateful-capture", showOverlay: false, appStartProcessId: null)} {AppViewModel.BuildRoutineRestoreSnapshotLogContext(snapshot)}");

            return snapshot;
        }
    }
}
