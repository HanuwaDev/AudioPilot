using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Services.Audio
{
    public partial class AudioDeviceService
    {
        private void NotifyAudioSessionCreated(AudioMixerMode mixerMode)
        {
            AudioSessionCreated?.Invoke(mixerMode);
        }

        private void NotifyAudioSessionLifecycleChanged(AudioSessionLifecycleSignal signal)
        {
            AudioSessionLifecycleChanged?.Invoke(signal);
        }

        private void OnEndpointVolumeChanged(AudioMixerMode mixerMode, string endpointId, float volumePercent)
        {
            _sessionMonitoringFacade.OnEndpointVolumeChanged(mixerMode, endpointId, volumePercent);
        }

        private IReadOnlyList<ISessionMonitorEndpoint> GetActivePlaybackMonitorEndpoints()
        {
            return SessionMonitorEndpointFactory.Materialize(GetActivePlaybackDevices());
        }

        private IReadOnlyList<ISessionMonitorEndpoint> GetActiveCaptureMonitorEndpoints()
        {
            return SessionMonitorEndpointFactory.Materialize(GetActiveCaptureDevices());
        }

        internal void AcquireSessionMonitoring(AudioMixerMode mixerMode)
        {
            _sessionMonitoringFacade.Acquire(mixerMode);
        }

        internal void ReleaseSessionMonitoring(AudioMixerMode mixerMode)
        {
            _sessionMonitoringFacade.Release(mixerMode);
        }

        private void OnDeviceSwitchNotification(string deviceId, DataFlow flow, Role role)
        {
            _deviceStateMetricsTracker.TrackAndLog(_logger);
            DeviceCacheHelper? deviceCache = _deviceCacheAccessor();
            deviceCache?.InvalidateCache();
            if (flow != DataFlow.Render || role == Role.Communications)
            {
                return;
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("AudioDeviceService", "Detected default playback device change via notification");
            }

            _sessionService.InvalidateRecentMixerSnapshotState();
            UpdateSessionMonitoring();
        }

        private void OnDeviceStateChange()
        {
            _deviceStateMetricsTracker.TrackAndLog(_logger);
            DeviceCacheHelper? deviceCache = _deviceCacheAccessor();
            deviceCache?.InvalidateCache();
            _sessionService.InvalidateRecentMixerSnapshotState();
            UpdateSessionMonitoring();
            DeviceStateChanged?.Invoke();
        }

        public void RegisterNotificationClient()
        {
            _notificationRegistrationHelper.Register();
        }

        public void UnregisterNotificationClient()
        {
            _notificationRegistrationHelper.Unregister();
        }

        private void StopSessionMonitoring()
        {
            _sessionMonitoringFacade.Stop();
        }

        private Task StopSessionMonitoringAndDrainAsync()
        {
            return _sessionMonitoringFacade.StopAndDrainAsync(_sessionMonitoringDrainOverride);
        }

        /// <summary>
        /// Reconciles audio-session monitoring against all active playback and recording endpoints.
        /// </summary>
        /// <remarks>
        /// Reconcile operations are debounced and executed in background work to absorb endpoint churn during hotplug
        /// storms. Existing endpoint subscriptions are retained when still active, new ones are attached, and stale
        /// ones are detached/disposed.
        /// </remarks>
        private void UpdateSessionMonitoring()
        {
            _sessionMonitoringFacade.Update();
        }

        /// <summary>
        /// Handles low-level session-creation notifications and applies any persisted per-session volume.
        /// </summary>
        /// <remarks>
        /// Work is scheduled in background, then COM-sensitive session access is marshaled through the dedicated
        /// CoreAudio executor after a short initialization delay. On completion it emits
        /// <see cref="AudioSessionCreated"/> so UI layers can coalesce mixer refreshes.
        /// </remarks>
        private void OnSessionCreated(AudioMixerMode mixerMode, object? sender, IAudioSessionControl newSession)
        {
            RunSessionCreatedErrorBoundary(() =>
            {
                TryQueueSessionCreatedWork(
                    _disposed,
                    newSession,
                    RunBackgroundWork,
                    async shutdownToken =>
                    {
                        await RunSessionCreatedHandlerAsync(
                            token => TryRunSessionCreatedWorkBeforeNotifyAsync(
                                () => _disposed,
                                token => Task.Delay(AppConstants.Timing.SessionInitDelayMs, token),
                                token => ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
                                {
                                    using var sessionControl = new AudioSessionControl(newSession);
                                    AudioDeviceSessionControlRestoreHelper.TryRestoreSession(
                                        sessionControl.State,
                                        sessionControl.GetProcessID,
                                        sessionControl.DisplayName,
                                        (pid, displayName) => _sessionVolumeRestoreHelper.TryApplySavedVolume(
                                            pid,
                                            displayName,
                                            (processName, resolvedDisplayName) => _volumeService.ApplySavedVolume(sessionControl, processName, resolvedDisplayName)));
                                }, token),
                                token),
                            () => NotifyAudioSessionCreated(mixerMode),
                            ex => _logger.Error("AudioDeviceService", "Error handling new session", nameof(OnSessionCreated), ex),
                            shutdownToken);
                    },
                    nameof(OnSessionCreated));
            }, ex => _logger.Error("AudioDeviceService", "Error in OnSessionCreated handler", nameof(OnSessionCreated), ex));
        }

        internal static void RunSessionCreatedErrorBoundary(Action body, Action<Exception> logOuterFailure)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                logOuterFailure(ex);
            }
        }

        internal static bool TryQueueSessionCreatedWork(
            bool disposed,
            IAudioSessionControl? newSession,
            Action<Func<CancellationToken, Task>, string> runBackgroundWork,
            Func<CancellationToken, Task> backgroundHandler,
            string context)
        {
            if (disposed || newSession == null)
            {
                return false;
            }

            runBackgroundWork(backgroundHandler, context);
            return true;
        }

        internal static async Task RunSessionCreatedHandlerAsync(
            Func<CancellationToken, Task<bool>> tryRunBeforeNotifyAsync,
            Action notifyAudioSessionCreated,
            Action<Exception> logBackgroundFailure,
            CancellationToken shutdownToken)
        {
            try
            {
                bool shouldNotify = await tryRunBeforeNotifyAsync(shutdownToken);
                if (shouldNotify)
                {
                    notifyAudioSessionCreated();
                }
            }
            catch (Exception ex)
            {
                logBackgroundFailure(ex);
            }
        }

        internal static async Task<bool> TryRunSessionCreatedWorkBeforeNotifyAsync(
            Func<bool> isDisposed,
            Func<CancellationToken, Task> waitForInitializationAsync,
            Func<CancellationToken, Task> runRestoreAsync,
            CancellationToken shutdownToken)
        {
            if (shutdownToken.IsCancellationRequested || isDisposed())
            {
                return false;
            }

            await waitForInitializationAsync(shutdownToken);

            if (shutdownToken.IsCancellationRequested || isDisposed())
            {
                return false;
            }

            await runRestoreAsync(shutdownToken);
            return true;
        }
    }
}
