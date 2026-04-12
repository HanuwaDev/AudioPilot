using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal readonly record struct RoutineRestoreDependencies(
        Func<string, string, CycleDevice?> TryGetActivePlaybackCycleEntry,
        Func<string?> GetDefaultPlaybackDeviceId,
        Func<string, string, string, Task> SwitchOutputAsync,
        Func<string, string, CycleDevice?> TryGetActiveRecordingCycleEntry,
        Func<string, string, string, Task> SwitchInputAsync,
        Func<float, bool, string, Task>? RestoreOutputVolumeAsync = null,
        Func<float, bool, string, Task>? RestoreInputVolumeAsync = null);

    internal static class AppRoutineRestoreCoordinator
    {
        /// <summary>
        /// Restores the audio defaults captured for the most recently deactivated stateful routine session.
        /// </summary>
        /// <remarks>
        /// Restore work is intentionally best-effort: missing targets are skipped, output restore only runs when the
        /// current default differs from the captured device, and any failure is logged without aborting application
        /// cleanup or other stateful-session teardown.
        /// </remarks>
        internal static async Task ExecuteRestoreAsync(
            AppViewModel.RoutineStatefulSession session,
            RoutineRestoreDependencies dependencies,
            Logger logger)
        {
            if (!session.RestoreSnapshot.HasValue)
            {
                logger.Info(
                    "AppViewModel",
                    () => $"routine-stateful-restore-skipped | reason=no-snapshot {AppViewModel.BuildRoutineStatefulSessionLogContext(session, shouldRestore: true)}");
                return;
            }

            AppViewModel.RoutineAudioRestoreSnapshot snapshot = session.RestoreSnapshot.Value;
            logger.Info(
                "AppViewModel",
                () => $"routine-stateful-restore-started | {AppViewModel.BuildRoutineStatefulSessionLogContext(session, shouldRestore: true)} hasOutputSnapshot={!string.IsNullOrWhiteSpace(snapshot.PreviousOutputDeviceId)} hasInputSnapshot={!string.IsNullOrWhiteSpace(snapshot.PreviousInputDeviceId)}");

            try
            {
                if (!string.IsNullOrWhiteSpace(snapshot.PreviousOutputDeviceId))
                {
                    CycleDevice? targetOutputDevice = dependencies.TryGetActivePlaybackCycleEntry(
                        snapshot.PreviousOutputDeviceId,
                        snapshot.PreviousOutputDeviceName);
                    string? currentOutputDeviceId = dependencies.GetDefaultPlaybackDeviceId();

                    if (targetOutputDevice != null &&
                        !string.IsNullOrWhiteSpace(currentOutputDeviceId) &&
                        !string.Equals(currentOutputDeviceId, targetOutputDevice.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        await dependencies.SwitchOutputAsync(
                            currentOutputDeviceId,
                            targetOutputDevice.Id,
                            $"routine-stateful-restore-output:{session.SessionKey}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(snapshot.PreviousInputDeviceId))
                {
                    CycleDevice? targetInputDevice = dependencies.TryGetActiveRecordingCycleEntry(
                        snapshot.PreviousInputDeviceId,
                        snapshot.PreviousInputDeviceName);

                    if (targetInputDevice != null)
                    {
                        await dependencies.SwitchInputAsync(
                            targetInputDevice.Id,
                            targetInputDevice.Name,
                            $"routine-stateful-restore-input:{session.SessionKey}");
                    }
                }

                if (snapshot.PreviousOutputVolumePercent.HasValue &&
                    snapshot.PreviousOutputMuted.HasValue &&
                    dependencies.RestoreOutputVolumeAsync != null)
                {
                    await dependencies.RestoreOutputVolumeAsync(
                        snapshot.PreviousOutputVolumePercent.Value,
                        snapshot.PreviousOutputMuted.Value,
                        $"routine-stateful-restore-output-volume:{session.SessionKey}");
                }

                if (snapshot.PreviousInputVolumePercent.HasValue &&
                    snapshot.PreviousInputMuted.HasValue &&
                    dependencies.RestoreInputVolumeAsync != null)
                {
                    await dependencies.RestoreInputVolumeAsync(
                        snapshot.PreviousInputVolumePercent.Value,
                        snapshot.PreviousInputMuted.Value,
                        $"routine-stateful-restore-input-volume:{session.SessionKey}");
                }

                logger.Info(
                    "AppViewModel",
                    () => $"routine-stateful-restore-completed | {AppViewModel.BuildRoutineStatefulSessionLogContext(session, shouldRestore: true)} hasOutputSnapshot={!string.IsNullOrWhiteSpace(snapshot.PreviousOutputDeviceId)} hasInputSnapshot={!string.IsNullOrWhiteSpace(snapshot.PreviousInputDeviceId)}");
            }
            catch (Exception ex)
            {
                logger.Warning("AppViewModel", "routine-stateful-restore-failed", nameof(ExecuteRestoreAsync), ex);
            }
        }
    }
}
