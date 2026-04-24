using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Services.Audio
{
    internal static class DeviceRoleSwitchEngine
    {
        private static Task<bool> ExecuteApplyVerifyAttemptAsync(Func<bool> attempt, string operationName, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ComThreadingHelper.ThrowIfComInitializationFailed(operationName);
                return attempt();
            }, cancellationToken);
        }

        public static async Task<bool> TrySwitchOutputRolesAsync(
            string targetDeviceId,
            IReadOnlyList<NRole> outputRoles,
            Action<string, IReadOnlyList<NRole>> applyConfiguredRoles,
            Func<MMDevice?> getDefaultPlaybackDevice,
            Logger logger,
            string opId,
            string contextMethod,
            CancellationToken cancellationToken = default)
        {
            bool switched = false;
            var stopwatch = Stopwatch.StartNew();
            double applyMs = 0;
            double verifyMs = 0;
            double retryDelayMs = 0;
            int attemptsUsed = 0;
            string result = "verify-failed";

            for (int attempt = 1; attempt <= RuntimeTuningConfig.SwitchMaxRetries && !switched; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attemptsUsed = attempt;
                try
                {
                    double applyVerifyStartMs = stopwatch.Elapsed.TotalMilliseconds;
                    switched = await ExecuteApplyVerifyAttemptAsync(() =>
                    {
                        applyConfiguredRoles(targetDeviceId, outputRoles);
                        using var verifyDevice = getDefaultPlaybackDevice();
                        return verifyDevice != null && verifyDevice.ID == targetDeviceId;
                    }, nameof(TrySwitchOutputRolesAsync), cancellationToken);
                    double applyVerifyDurationMs = stopwatch.Elapsed.TotalMilliseconds - applyVerifyStartMs;
                    double apportionedPhaseMs = applyVerifyDurationMs / 2d;
                    applyMs += apportionedPhaseMs;
                    verifyMs += apportionedPhaseMs;

                    if (!switched && attempt < RuntimeTuningConfig.SwitchMaxRetries)
                    {
                        if (logger.IsEnabled(LogLevel.Debug))
                        {
                            logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.VerifyRetry} | opId={opId} attempt={attempt}");
                        }

                        double delayStartMs = stopwatch.Elapsed.TotalMilliseconds;
                        await Task.Delay(RuntimeTuningConfig.SwitchRetryDelayMs, cancellationToken);
                        retryDelayMs += stopwatch.Elapsed.TotalMilliseconds - delayStartMs;
                    }
                }
                catch (COMException ex) when (attempt < RuntimeTuningConfig.SwitchMaxRetries)
                {
                    result = "com-retry";
                    logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.ComRetry} | opId={opId} attempt={attempt}", contextMethod, ex);
                    double delayStartMs = stopwatch.Elapsed.TotalMilliseconds;
                    await Task.Delay(RuntimeTuningConfig.SwitchRetryMaxDelayMs, cancellationToken);
                    retryDelayMs += stopwatch.Elapsed.TotalMilliseconds - delayStartMs;
                }
                catch (Exception verifyEx) when (attempt < RuntimeTuningConfig.SwitchMaxRetries)
                {
                    result = verifyEx.GetType().Name;
                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        logger.Trace("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.VerifyFailed} | opId={opId} attempt={attempt} reason={verifyEx.GetType().Name}");
                    }

                    double delayStartMs = stopwatch.Elapsed.TotalMilliseconds;
                    await Task.Delay(RuntimeTuningConfig.SwitchRetryDelayMs, cancellationToken);
                    retryDelayMs += stopwatch.Elapsed.TotalMilliseconds - delayStartMs;
                }
            }

            if (switched)
            {
                result = "success";
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.Debug(
                    "AudioDeviceService",
                    () => $"{AppConstants.Audio.LogEvents.OutputSwitch.EnginePhases} | opId={opId} attempts={attemptsUsed} applyMs={applyMs:F1} verifyMs={verifyMs:F1} retryDelayMs={retryDelayMs:F1} totalMs={stopwatch.Elapsed.TotalMilliseconds:F1} result={result}");
            }

            return switched;
        }

        public static async Task<bool> TrySwitchInputRolesAsync(
            string targetDeviceId,
            string targetName,
            IReadOnlyList<NRole> inputRoles,
            Action<string, IReadOnlyList<NRole>> applyConfiguredRoles,
            Func<MMDevice?> getDefaultRecordingDevice,
            Logger logger,
            string opId,
            string contextMethod,
            bool emitVerifyRetryWarning,
            bool traceComRetry,
            CancellationToken cancellationToken = default)
        {
            bool success = false;
            var stopwatch = Stopwatch.StartNew();
            double applyMs = 0;
            double verifyMs = 0;
            double retryDelayMs = 0;
            int attemptsUsed = 0;
            string result = "verify-failed";

            for (int attempt = 1; attempt <= RuntimeTuningConfig.SwitchMaxRetries && !success; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attemptsUsed = attempt;
                try
                {
                    double applyVerifyStartMs = stopwatch.Elapsed.TotalMilliseconds;
                    success = await ExecuteApplyVerifyAttemptAsync(() =>
                    {
                        applyConfiguredRoles(targetDeviceId, inputRoles);
                        using var confirmDevice = getDefaultRecordingDevice();
                        return confirmDevice != null && confirmDevice.ID == targetDeviceId;
                    }, nameof(TrySwitchInputRolesAsync), cancellationToken);
                    double applyVerifyDurationMs = stopwatch.Elapsed.TotalMilliseconds - applyVerifyStartMs;
                    double apportionedPhaseMs = applyVerifyDurationMs / 2d;
                    applyMs += apportionedPhaseMs;
                    verifyMs += apportionedPhaseMs;

                    if (!success && attempt < RuntimeTuningConfig.SwitchMaxRetries)
                    {
                        if (emitVerifyRetryWarning)
                        {
                            logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Retry} | opId={opId} attempt={attempt}");
                        }

                        double delayStartMs = stopwatch.Elapsed.TotalMilliseconds;
                        await Task.Delay(RuntimeTuningConfig.SwitchRetryDelayMs, cancellationToken);
                        retryDelayMs += stopwatch.Elapsed.TotalMilliseconds - delayStartMs;
                    }
                }
                catch (COMException ex) when (attempt < RuntimeTuningConfig.SwitchMaxRetries)
                {
                    result = "com-retry";
                    if (traceComRetry)
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            logger.Trace("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.ComRetry} | opId={opId} attempt={attempt} target={LogPrivacy.Device(targetName)} targetId={LogPrivacy.Id(targetDeviceId)}");
                        }
                    }
                    else
                    {
                        logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.ComRetry} | opId={opId} attempt={attempt}", contextMethod, ex);
                    }

                    double delayStartMs = stopwatch.Elapsed.TotalMilliseconds;
                    await Task.Delay(RuntimeTuningConfig.SwitchRetryMaxDelayMs, cancellationToken);
                    retryDelayMs += stopwatch.Elapsed.TotalMilliseconds - delayStartMs;
                }
                catch (Exception ex) when (attempt < RuntimeTuningConfig.SwitchMaxRetries)
                {
                    result = ex.GetType().Name;
                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        logger.Trace("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.VerifyFailed} | opId={opId} attempt={attempt} reason={ex.GetType().Name}");
                    }

                    double delayStartMs = stopwatch.Elapsed.TotalMilliseconds;
                    await Task.Delay(RuntimeTuningConfig.SwitchRetryDelayMs, cancellationToken);
                    retryDelayMs += stopwatch.Elapsed.TotalMilliseconds - delayStartMs;
                }
            }

            if (success)
            {
                result = "success";
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.Debug(
                    "AudioDeviceService",
                    () => $"{AppConstants.Audio.LogEvents.InputSwitch.EnginePhases} | opId={opId} attempts={attemptsUsed} applyMs={applyMs:F1} verifyMs={verifyMs:F1} retryDelayMs={retryDelayMs:F1} totalMs={stopwatch.Elapsed.TotalMilliseconds:F1} result={result}");
            }

            return success;
        }
    }
}
