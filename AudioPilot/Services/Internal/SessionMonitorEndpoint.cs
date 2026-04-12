using AudioPilot.Helpers;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Internal
{
    internal interface ISessionMonitorEndpoint : IDisposable
    {
        string EndpointId { get; }
        string DisplayName { get; }
        IReadOnlyList<AudioSessionControl> GetExistingSessions();
        void SubscribeSessionCreated(AudioSessionManager.SessionCreatedDelegate handler);
        void UnsubscribeSessionCreated(AudioSessionManager.SessionCreatedDelegate handler);
        void SubscribeEndpointVolume(AudioEndpointVolumeNotificationDelegate handler);
        void UnsubscribeEndpointVolume(AudioEndpointVolumeNotificationDelegate handler);
    }

    internal sealed class CoreAudioSessionMonitorEndpoint(MMDevice device) : ISessionMonitorEndpoint
    {
        private readonly MMDevice _device = device;
        private readonly AudioSessionManager? _sessionManager = device.AudioSessionManager;
        private readonly AudioEndpointVolume? _endpointVolume = TryResolveEndpointVolume(device);
        private bool _disposed;

        public string EndpointId { get; } = device.ID;

        public string DisplayName { get; } = device.FriendlyName;

        public IReadOnlyList<AudioSessionControl> GetExistingSessions()
        {
            if (_disposed || _sessionManager == null)
            {
                return [];
            }

            SessionCollection sessions = _sessionManager.Sessions;
            var materialized = new List<AudioSessionControl>(sessions.Count);
            for (int index = 0; index < sessions.Count; index++)
            {
                AudioSessionControl? session = null;
                try
                {
                    session = sessions[index];
                }
                catch
                {
                    session?.Dispose();
                    continue;
                }

                if (session != null)
                {
                    materialized.Add(session);
                }
            }

            return materialized;
        }

        public void SubscribeSessionCreated(AudioSessionManager.SessionCreatedDelegate handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (_disposed || _sessionManager == null)
            {
                return;
            }

            _sessionManager.OnSessionCreated += handler;
        }

        public void UnsubscribeSessionCreated(AudioSessionManager.SessionCreatedDelegate handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (_sessionManager == null)
            {
                return;
            }

            _sessionManager.OnSessionCreated -= handler;
        }

        public void SubscribeEndpointVolume(AudioEndpointVolumeNotificationDelegate handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (_disposed || _endpointVolume == null)
            {
                return;
            }

            _endpointVolume.OnVolumeNotification += handler;
        }

        public void UnsubscribeEndpointVolume(AudioEndpointVolumeNotificationDelegate handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (_endpointVolume == null)
            {
                return;
            }

            _endpointVolume.OnVolumeNotification -= handler;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _endpointVolume?.Dispose();
            }
            catch
            {
            }

            try
            {
                _device.Dispose();
            }
            catch
            {
            }
        }

        private static AudioEndpointVolume? TryResolveEndpointVolume(MMDevice device)
        {
            try
            {
                return device.AudioEndpointVolume;
            }
            catch
            {
                return null;
            }
        }
    }

    internal static class SessionMonitorEndpointFactory
    {
        public static IReadOnlyList<ISessionMonitorEndpoint> Materialize(MMDeviceCollection devices)
        {
            try
            {
                List<MMDevice> materializedDevices = AudioDeviceCollectionHelper.MaterializeDevices(devices);
                var endpoints = new List<ISessionMonitorEndpoint>(materializedDevices.Count);

                for (int index = 0; index < materializedDevices.Count; index++)
                {
                    MMDevice? device = materializedDevices[index];
                    if (device == null || string.IsNullOrWhiteSpace(device.ID))
                    {
                        device?.Dispose();
                        continue;
                    }

                    endpoints.Add(new CoreAudioSessionMonitorEndpoint(device));
                }

                return endpoints;
            }
            finally
            {
                if (devices is IDisposable disposableDevices)
                {
                    disposableDevices.Dispose();
                }
            }
        }
    }
}
