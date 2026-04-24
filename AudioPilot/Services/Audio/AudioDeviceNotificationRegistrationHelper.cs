using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal sealed class AudioDeviceNotificationRegistrationHelper(
        Logger logger,
        object syncRoot,
        Func<bool> isRegistered,
        Action<bool> setRegistered,
        Action registerNotificationCallback,
        Action unregisterNotificationCallback,
        Action onRegistered,
        Action onUnregistered)
    {
        private const string RegisterMethodName = "RegisterNotificationClient";
        private const string UnregisterMethodName = "UnregisterNotificationClient";

        private readonly Logger _logger = logger;
        private readonly object _syncRoot = syncRoot;
        private readonly Func<bool> _isRegistered = isRegistered;
        private readonly Action<bool> _setRegistered = setRegistered;
        private readonly Action _registerNotificationCallback = registerNotificationCallback;
        private readonly Action _unregisterNotificationCallback = unregisterNotificationCallback;
        private readonly Action _onRegistered = onRegistered;
        private readonly Action _onUnregistered = onUnregistered;

        public void Register()
        {
            if (_isRegistered())
            {
                return;
            }

            bool monitorEntered = false;
            try
            {
                monitorEntered = Monitor.TryEnter(_syncRoot, AppConstants.Timing.CleanupWaitMs);
                if (!monitorEntered)
                {
                    _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.DeviceNotifications.Register} | success=false reason=monitor-timeout");
                    return;
                }

                if (_isRegistered())
                {
                    return;
                }

                _registerNotificationCallback();
                _setRegistered(true);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.DeviceNotifications.Register} | success=true");
                }

                _onRegistered();
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, RegisterMethodName, ex);
            }
            finally
            {
                if (monitorEntered)
                {
                    Monitor.Exit(_syncRoot);
                }
            }
        }

        public void Unregister()
        {
            if (!_isRegistered())
            {
                return;
            }

            bool monitorEntered = false;
            try
            {
                monitorEntered = Monitor.TryEnter(_syncRoot, AppConstants.Timing.CleanupWaitMs);
                if (!monitorEntered)
                {
                    _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.DeviceNotifications.Unregister} | success=false reason=monitor-timeout fallback=lock-free-attempt");
                    UnregisterWithoutLock();
                    return;
                }

                if (!_isRegistered())
                {
                    return;
                }

                UnregisterCore();
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, UnregisterMethodName, ex);
            }
            finally
            {
                if (monitorEntered)
                {
                    Monitor.Exit(_syncRoot);
                }
            }
        }

        private void UnregisterWithoutLock()
        {
            if (!_isRegistered())
            {
                return;
            }

            try
            {
                UnregisterCore();
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, UnregisterMethodName, ex);
            }
        }

        private void UnregisterCore()
        {
            _unregisterNotificationCallback();
            _setRegistered(false);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.DeviceNotifications.Unregister} | success=true");
            }

            _onUnregistered();
        }
    }
}
