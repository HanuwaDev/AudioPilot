using AudioPilot.Logging;
using Microsoft.Win32;

namespace AudioPilot.Services.Audio
{
    internal sealed class RegistryPerAppAudioRoutingResetter : IPerAppAudioRoutingResetter
    {
        internal const string PropertyStorePath = @"Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore";

        private readonly Logger _logger;
        private readonly RegistryKey _rootKey;
        private readonly string _propertyStorePath;

        public RegistryPerAppAudioRoutingResetter(Logger logger)
            : this(Registry.CurrentUser, PropertyStorePath, logger)
        {
        }

        internal RegistryPerAppAudioRoutingResetter(RegistryKey rootKey, string propertyStorePath, Logger logger)
        {
            _rootKey = rootKey;
            _propertyStorePath = propertyStorePath;
            _logger = logger;
        }

        public PerAppAudioRoutingResetResult TryResetAll()
        {
            try
            {
                using RegistryKey? propertyStoreKey = _rootKey.OpenSubKey(_propertyStorePath, writable: false);
                bool hadAssignments = propertyStoreKey is { ValueCount: > 0 } || propertyStoreKey is { SubKeyCount: > 0 };
                if (!hadAssignments)
                {
                    _logger.Info("RegistryPerAppAudioRoutingResetter", "No persisted per-app audio assignments found to reset");
                    return new PerAppAudioRoutingResetResult(Success: true, HadAssignments: false);
                }

                _rootKey.DeleteSubKeyTree(_propertyStorePath, throwOnMissingSubKey: false);
                _logger.Info("RegistryPerAppAudioRoutingResetter", "Cleared persisted per-app audio assignments from Windows policy store");
                return new PerAppAudioRoutingResetResult(Success: true, HadAssignments: true);
            }
            catch (Exception ex)
            {
                _logger.Warning("RegistryPerAppAudioRoutingResetter", "Failed to clear persisted per-app audio assignments", nameof(TryResetAll), ex);
                return new PerAppAudioRoutingResetResult(Success: false, HadAssignments: false);
            }
        }
    }
}
