using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Tests.Helpers;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Tests.Services.Audio;

[Collection("RuntimeTuningConfigIsolation")]
public sealed class DeviceRoleSwitchEngineTests
{
    public DeviceRoleSwitchEngineTests()
    {
        RuntimeTuningConfig.SwitchRetryDelayMs = AppConstants.Timing.SwitchRetryDelayMs;
        RuntimeTuningConfig.SwitchRetryMaxDelayMs = AppConstants.Timing.SwitchRetryMaxDelayMs;
        RuntimeTuningConfig.SwitchMaxRetries = AppConstants.Timing.SwitchMaxRetries;
    }

    [Fact]
    public async Task TrySwitchOutputRolesAsync_RetriesConfiguredAttempts_WhenVerificationNeverSucceeds()
    {
        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-output-retries.log");

        bool success = await DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(
            targetDeviceId: "target-id",
            outputRoles: [NRole.Multimedia],
            applyConfiguredRoles: (_, _) => applyCalls++,
            getDefaultPlaybackDevice: () => null,
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchOutputRolesAsync_RetriesConfiguredAttempts_WhenVerificationNeverSucceeds));

        Assert.False(success);
        Assert.Equal(AudioPilot.Constants.AppConstants.Timing.SwitchMaxRetries, applyCalls);
    }

    [Fact]
    public async Task TrySwitchInputRolesAsync_RetriesConfiguredAttempts_WhenVerificationNeverSucceeds()
    {
        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-input-retries.log");

        bool success = await DeviceRoleSwitchEngine.TrySwitchInputRolesAsync(
            targetDeviceId: "target-id",
            targetName: "Target",
            inputRoles: [NRole.Communications],
            applyConfiguredRoles: (_, _) => applyCalls++,
            getDefaultRecordingDevice: () => null,
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchInputRolesAsync_RetriesConfiguredAttempts_WhenVerificationNeverSucceeds),
            emitVerifyRetryWarning: false,
            traceComRetry: false);

        Assert.False(success);
        Assert.InRange(applyCalls, 1, AudioPilot.Constants.AppConstants.Timing.SwitchMaxRetries);
    }

    [Fact]
    public async Task TrySwitchOutputRolesAsync_ThrowsOnFinalAttempt_WhenComExceptionPersists()
    {
        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-output-com-exception.log");

        await Assert.ThrowsAsync<COMException>(() => DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(
            targetDeviceId: "target-id",
            outputRoles: [NRole.Multimedia],
            applyConfiguredRoles: (_, _) =>
            {
                applyCalls++;
                throw new COMException("simulated", unchecked((int)0x80004005));
            },
            getDefaultPlaybackDevice: () => null,
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchOutputRolesAsync_ThrowsOnFinalAttempt_WhenComExceptionPersists)));

        Assert.Equal(AudioPilot.Constants.AppConstants.Timing.SwitchMaxRetries, applyCalls);
    }

    [Fact]
    public async Task TrySwitchOutputRolesAsync_ThrowsOnFinalAttempt_WhenVerificationThrows()
    {
        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-output-verify-exception.log");

        await Assert.ThrowsAsync<InvalidOperationException>(() => DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(
            targetDeviceId: "target-id",
            outputRoles: [NRole.Multimedia],
            applyConfiguredRoles: (_, _) => applyCalls++,
            getDefaultPlaybackDevice: () => throw new InvalidOperationException("verify failed"),
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchOutputRolesAsync_ThrowsOnFinalAttempt_WhenVerificationThrows)));

        Assert.InRange(applyCalls, 1, AudioPilot.Constants.AppConstants.Timing.SwitchMaxRetries);
    }

    [Fact]
    public async Task TrySwitchOutputRolesAsync_WhenCanceledDuringRetryDelay_StopsAfterCurrentAttempt()
    {
        RuntimeTuningConfig.SwitchRetryDelayMs = 500;

        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-output-cancel.log");
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.CancelAfter(25);

        Exception? exception = await Record.ExceptionAsync(() => DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(
            targetDeviceId: "target-id",
            outputRoles: [NRole.Multimedia],
            applyConfiguredRoles: (_, _) => applyCalls++,
            getDefaultPlaybackDevice: () => null,
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchOutputRolesAsync_WhenCanceledDuringRetryDelay_StopsAfterCurrentAttempt),
            cancellationToken: cancellationTokenSource.Token));

        Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.Equal(1, applyCalls);
    }

    [Fact]
    public async Task TrySwitchOutputRolesAsync_DoesNotWaitForSharedCoreAudioWorker()
    {
        RuntimeTuningConfig.SwitchMaxRetries = 1;

        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-output-worker-starvation.log");
        using var blockerStarted = new ManualResetEventSlim(false);
        using var releaseBlocker = new ManualResetEventSlim(false);

        Task blockerTask = Task.Run(() => ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
        {
            blockerStarted.Set();
            Assert.True(releaseBlocker.Wait(TimeSpan.FromSeconds(5)));
        }));

        Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(5)));

        int applyCalls = 0;
        bool success = await DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(
            targetDeviceId: "target-id",
            outputRoles: [NRole.Multimedia],
            applyConfiguredRoles: (_, _) => applyCalls++,
            getDefaultPlaybackDevice: () => null,
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchOutputRolesAsync_DoesNotWaitForSharedCoreAudioWorker))
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(success);
        Assert.Equal(1, applyCalls);

        releaseBlocker.Set();
        await blockerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TrySwitchInputRolesAsync_ThrowsOnFinalAttempt_WhenComExceptionPersists()
    {
        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-input-com-exception.log");

        await Assert.ThrowsAsync<COMException>(() => DeviceRoleSwitchEngine.TrySwitchInputRolesAsync(
            targetDeviceId: "target-id",
            targetName: "Target",
            inputRoles: [NRole.Multimedia],
            applyConfiguredRoles: (_, _) =>
            {
                applyCalls++;
                throw new COMException("simulated", unchecked((int)0x80004005));
            },
            getDefaultRecordingDevice: () => null,
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchInputRolesAsync_ThrowsOnFinalAttempt_WhenComExceptionPersists),
            emitVerifyRetryWarning: false,
            traceComRetry: true));

        Assert.Equal(AudioPilot.Constants.AppConstants.Timing.SwitchMaxRetries, applyCalls);
    }

    [Fact]
    public async Task TrySwitchInputRolesAsync_ThrowsOnFinalAttempt_WhenVerificationThrows()
    {
        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-input-verify-exception.log");

        await Assert.ThrowsAsync<InvalidOperationException>(() => DeviceRoleSwitchEngine.TrySwitchInputRolesAsync(
            targetDeviceId: "target-id",
            targetName: "Target",
            inputRoles: [NRole.Multimedia],
            applyConfiguredRoles: (_, _) => applyCalls++,
            getDefaultRecordingDevice: () => throw new InvalidOperationException("verify failed"),
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchInputRolesAsync_ThrowsOnFinalAttempt_WhenVerificationThrows),
            emitVerifyRetryWarning: true,
            traceComRetry: false));

        Assert.InRange(applyCalls, 1, AudioPilot.Constants.AppConstants.Timing.SwitchMaxRetries);
    }

    [Fact]
    public async Task TrySwitchInputRolesAsync_WhenCanceledDuringRetryDelay_StopsAfterCurrentAttempt()
    {
        RuntimeTuningConfig.SwitchRetryDelayMs = 500;

        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-input-cancel.log");
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.CancelAfter(25);

        Exception? exception = await Record.ExceptionAsync(() => DeviceRoleSwitchEngine.TrySwitchInputRolesAsync(
            targetDeviceId: "target-id",
            targetName: "Target",
            inputRoles: [NRole.Communications],
            applyConfiguredRoles: (_, _) => applyCalls++,
            getDefaultRecordingDevice: () => null,
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchInputRolesAsync_WhenCanceledDuringRetryDelay_StopsAfterCurrentAttempt),
            emitVerifyRetryWarning: false,
            traceComRetry: false,
            cancellationToken: cancellationTokenSource.Token));

        Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.Equal(1, applyCalls);
    }
}

