using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceNotificationRegistrationHelperTests
{
    [Fact]
    public void Register_SetsState_AndRunsPostRegistrationAction()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceNotificationRegistrationHelperTests), "notification-helper-register.log", LogLevel.Debug);
        object syncRoot = new();
        bool isRegistered = false;
        int registerCalls = 0;
        int onRegisteredCalls = 0;

        var helper = new AudioDeviceNotificationRegistrationHelper(
            loggerScope.Logger,
            syncRoot,
            () => isRegistered,
            value => isRegistered = value,
            () => registerCalls++,
            static () => { },
            () => onRegisteredCalls++,
            static () => { });

        helper.Register();

        Assert.True(isRegistered);
        Assert.Equal(1, registerCalls);
        Assert.Equal(1, onRegisteredCalls);
    }

    [Fact]
    public void Unregister_ClearsState_AndRunsPostUnregistrationAction()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceNotificationRegistrationHelperTests), "notification-helper-unregister.log", LogLevel.Debug);
        object syncRoot = new();
        bool isRegistered = true;
        int unregisterCalls = 0;
        int onUnregisteredCalls = 0;

        var helper = new AudioDeviceNotificationRegistrationHelper(
            loggerScope.Logger,
            syncRoot,
            () => isRegistered,
            value => isRegistered = value,
            static () => { },
            () => unregisterCalls++,
            static () => { },
            () => onUnregisteredCalls++);

        helper.Unregister();

        Assert.False(isRegistered);
        Assert.Equal(1, unregisterCalls);
        Assert.Equal(1, onUnregisteredCalls);
    }

    [Fact]
    public void Register_DoesNothing_WhenAlreadyRegistered()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceNotificationRegistrationHelperTests), "notification-helper-already-registered.log", LogLevel.Debug);
        object syncRoot = new();
        bool isRegistered = true;
        int registerCalls = 0;
        int onRegisteredCalls = 0;

        var helper = new AudioDeviceNotificationRegistrationHelper(
            loggerScope.Logger,
            syncRoot,
            () => isRegistered,
            value => isRegistered = value,
            () => registerCalls++,
            static () => { },
            () => onRegisteredCalls++,
            static () => { });

        helper.Register();

        Assert.True(isRegistered);
        Assert.Equal(0, registerCalls);
        Assert.Equal(0, onRegisteredCalls);
    }

    [Fact]
    public void Register_LeavesStateUnchanged_WhenRegistrationThrows()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceNotificationRegistrationHelperTests), "notification-helper-register-throws.log", LogLevel.Debug);
        object syncRoot = new();
        bool isRegistered = false;
        int onRegisteredCalls = 0;

        var helper = new AudioDeviceNotificationRegistrationHelper(
            loggerScope.Logger,
            syncRoot,
            () => isRegistered,
            value => isRegistered = value,
            () => throw new InvalidOperationException("boom"),
            static () => { },
            () => onRegisteredCalls++,
            static () => { });

        helper.Register();

        Assert.False(isRegistered);
        Assert.Equal(0, onRegisteredCalls);
    }

    [Fact]
    public void Unregister_DoesNothing_WhenAlreadyUnregistered()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceNotificationRegistrationHelperTests), "notification-helper-already-unregistered.log", LogLevel.Debug);
        object syncRoot = new();
        bool isRegistered = false;
        int unregisterCalls = 0;
        int onUnregisteredCalls = 0;

        var helper = new AudioDeviceNotificationRegistrationHelper(
            loggerScope.Logger,
            syncRoot,
            () => isRegistered,
            value => isRegistered = value,
            static () => { },
            () => unregisterCalls++,
            static () => { },
            () => onUnregisteredCalls++);

        helper.Unregister();

        Assert.False(isRegistered);
        Assert.Equal(0, unregisterCalls);
        Assert.Equal(0, onUnregisteredCalls);
    }

    [Fact]
    public void Unregister_LeavesStateUnchanged_WhenUnregistrationThrows()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceNotificationRegistrationHelperTests), "notification-helper-unregister-throws.log", LogLevel.Debug);
        object syncRoot = new();
        bool isRegistered = true;
        int onUnregisteredCalls = 0;

        var helper = new AudioDeviceNotificationRegistrationHelper(
            loggerScope.Logger,
            syncRoot,
            () => isRegistered,
            value => isRegistered = value,
            static () => { },
            () => throw new InvalidOperationException("boom"),
            static () => { },
            () => onUnregisteredCalls++);

        helper.Unregister();

        Assert.True(isRegistered);
        Assert.Equal(0, onUnregisteredCalls);
    }

    [Fact]
    public void Unregister_UsesLockFreeFallback_WhenMonitorTimesOut()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceNotificationRegistrationHelperTests), "notification-helper-unregister-timeout.log", LogLevel.Debug);
        object syncRoot = new();
        bool isRegistered = true;
        int unregisterCalls = 0;
        int onUnregisteredCalls = 0;
        using ManualResetEventSlim lockEntered = new(false);
        using ManualResetEventSlim releaseLock = new(false);

        Thread lockHolder = new(() =>
        {
            lock (syncRoot)
            {
                lockEntered.Set();
                releaseLock.Wait();
            }
        });

        lockHolder.Start();
        Assert.True(lockEntered.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            var helper = new AudioDeviceNotificationRegistrationHelper(
                loggerScope.Logger,
                syncRoot,
                () => isRegistered,
                value => isRegistered = value,
                static () => { },
                () => unregisterCalls++,
                static () => { },
                () => onUnregisteredCalls++);

            helper.Unregister();

            Assert.False(isRegistered);
            Assert.Equal(1, unregisterCalls);
            Assert.Equal(1, onUnregisteredCalls);
        }
        finally
        {
            releaseLock.Set();
            Assert.True(lockHolder.Join(TimeSpan.FromSeconds(5)));
        }
    }
}
