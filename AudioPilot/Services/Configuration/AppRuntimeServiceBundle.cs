using AudioPilot.Logging;

namespace AudioPilot.Services.Configuration
{
    public sealed class AppRuntimeServiceBundle : IDisposable, IAsyncDisposable
    {
        private int _disposeStarted;

        private AppRuntimeServiceBundle(
            SettingsService settingsService,
            StartupService startupService,
            AudioDeviceService audioService,
            Lazy<BluetoothReconnectCoordinator> bluetoothReconnectCoordinator)
        {
            SettingsService = settingsService;
            StartupService = startupService;
            AudioService = audioService;
            BluetoothReconnectCoordinator = bluetoothReconnectCoordinator;
        }

        public SettingsService SettingsService { get; }
        public StartupService StartupService { get; }
        public AudioDeviceService AudioService { get; }
        public Lazy<BluetoothReconnectCoordinator> BluetoothReconnectCoordinator { get; }

        public static AppRuntimeServiceBundle CreateDefault()
        {
            return new AppRuntimeServiceBundle(
                new SettingsService(),
                new StartupService(),
                new AudioDeviceService(),
                new Lazy<BluetoothReconnectCoordinator>(() => new BluetoothReconnectCoordinator(new BluetoothReconnectService(), Logger.Instance), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));
        }

        internal static AppRuntimeServiceBundle Create(
            SettingsService settingsService,
            StartupService startupService,
            AudioDeviceService audioService,
            Lazy<BluetoothReconnectCoordinator> bluetoothReconnectCoordinator)
        {
            return new AppRuntimeServiceBundle(
                settingsService,
                startupService,
                audioService,
                bluetoothReconnectCoordinator);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            AudioService.Dispose();
            BluetoothAssociationEndpointSource.DisposeWatcherCache(Logger.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            await AudioService.DisposeAsync();
            BluetoothAssociationEndpointSource.DisposeWatcherCache(Logger.Instance);
        }
    }
}
