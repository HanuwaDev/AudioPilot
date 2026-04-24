using System.Collections.ObjectModel;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public class NetworkTriggerCoordinatorTests
{
    [Fact]
    public void NetworkHelper_NormalizeNetworkName_Null_ReturnsEmptyString()
    {
        string result = NetworkHelper.NormalizeNetworkName(null);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void NetworkHelper_NormalizeNetworkName_Empty_ReturnsEmptyString()
    {
        string result = NetworkHelper.NormalizeNetworkName(string.Empty);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void NetworkHelper_NormalizeNetworkName_Whitespace_ReturnsEmptyString()
    {
        string result = NetworkHelper.NormalizeNetworkName("   ");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void NetworkHelper_NormalizeNetworkName_ValidName_ReturnsTrimmed()
    {
        string result = NetworkHelper.NormalizeNetworkName("  TestNetwork  ");
        Assert.Equal("TestNetwork", result);
    }

    [Fact]
    public void NetworkHelper_NormalizeNetworkName_ValidNameNoWhitespace_ReturnsSame()
    {
        string result = NetworkHelper.NormalizeNetworkName("TestNetwork");
        Assert.Equal("TestNetwork", result);
    }

    [Fact]
    public void GetAvailableNetworkNames_ReturnsHashSet()
    {
        HashSet<string> result = NetworkTriggerCoordinator.GetAvailableNetworkNames();
        Assert.NotNull(result);
        Assert.IsType<HashSet<string>>(result);
    }

    [Fact]
    public void GetAvailableNetworkNames_UsesOrdinalIgnoreCaseComparer()
    {
        HashSet<string> result = NetworkTriggerCoordinator.GetAvailableNetworkNames();
        var comparer = result.Comparer;
        Assert.NotNull(comparer);
        Assert.Equal(StringComparer.OrdinalIgnoreCase, comparer);
    }

    [Fact]
    public void GetAvailableNetworkNames_DoesNotThrow()
    {
        var exception = Record.Exception(() => NetworkTriggerCoordinator.GetAvailableNetworkNames());
        Assert.Null(exception);
    }

    [Fact]
    public void Start_StartsMonitorAndRecordsInitialNetwork()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        Assert.True(monitor.Started);
        Assert.Equal(["HomeWiFi"], monitor.LastObservedNetworkNames);
    }

    [Fact]
    public void Start_Idempotent_DoesNotStartMonitorTwice()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();
        coordinator.Start();

        Assert.Equal(1, monitor.StartCount);
    }

    [Fact]
    public void Stop_StopsMonitor()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();
        coordinator.Stop();

        Assert.False(monitor.Started);
    }

    [Fact]
    public void Stop_Idempotent_DoesNotStopMonitorTwice()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();
        coordinator.Stop();
        coordinator.Stop();

        Assert.Equal(1, monitor.StopCount);
    }

    [Fact]
    public void OnConnectivityChanged_ExecutesMatchingRoutine_WhenNetworkChanges()
    {
        var monitor = new FakeNetworkMonitor(string.Empty);
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "OfficeRoutine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "OfficeEthernet",
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                OutputDeviceId = "out-1"
            }
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("OfficeEthernet");

        Assert.Single(executedRoutines);
        Assert.Equal("OfficeRoutine", executedRoutines[0]);
    }

    [Fact]
    public void OnConnectivityChanged_DoesNotExecuteRoutine_WhenNetworkNameMatches()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "HomeRoutine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "HomeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                OutputDeviceId = "out-1"
            }
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("HomeWiFi");

        Assert.Empty(executedRoutines);
    }

    [Fact]
    public void OnConnectivityChanged_DoesNotExecuteDisabledRoutine()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "HomeRoutine",
                Enabled = false,
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "OfficeEthernet",
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                OutputDeviceId = "out-1"
            }
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("OfficeEthernet");

        Assert.Empty(executedRoutines);
    }

    [Fact]
    public void OnConnectivityChanged_DoesNotExecuteNonNetworkTrigger()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "HotkeyRoutine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Hotkey,
                TriggerNetworkName = "OfficeEthernet",
                OutputDeviceId = "out-1"
            }
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("OfficeEthernet");

        Assert.Empty(executedRoutines);
    }

    [Fact]
    public void OnConnectivityChanged_NormalizesNetworkNameComparison()
    {
        var monitor = new FakeNetworkMonitor(string.Empty);
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "HomeRoutine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "  HOME-WIFI  ",
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                OutputDeviceId = "out-1"
            }
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("home-wifi");

        Assert.Single(executedRoutines);
        Assert.Equal("HomeRoutine", executedRoutines[0]);
    }

    [Fact]
    public void Dispose_StopsMonitorAndDisposesIt()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();
        coordinator.Dispose();

        Assert.True(monitor.Disposed);
        Assert.False(monitor.Started);
    }

    [Fact]
    public void OnConnectivityChanged_ExecutesDisconnectRoutine_WhenNetworkDisconnects()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "DisconnectRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = string.Empty,
                NetworkTriggerDirection = NetworkTriggerDirection.Disconnect,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange(string.Empty);

        Assert.Single(executedRoutines);
        Assert.Equal("DisconnectRoutine", executedRoutines[0]);
    }

    [Fact]
    public void OnConnectivityChanged_DoesNotExecuteDisconnectRoutine_WhenNetworkConnects()
    {
        var monitor = new FakeNetworkMonitor(string.Empty);
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "DisconnectRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = string.Empty,
                NetworkTriggerDirection = NetworkTriggerDirection.Disconnect,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("HomeWiFi");

        Assert.Empty(executedRoutines);
    }

    [Fact]
    public void OnConnectivityChanged_ExecutesBothDirectionRoutine_WhenNetworkConnects()
    {
        var monitor = new FakeNetworkMonitor(string.Empty);
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "BothRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "HomeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Both,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("HomeWiFi");

        Assert.Single(executedRoutines);
        Assert.Equal("BothRoutine", executedRoutines[0]);
    }

    [Fact]
    public void OnConnectivityChanged_ExecutesBothDirectionRoutine_WhenNetworkDisconnects()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "BothRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "HomeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Both,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange(string.Empty);

        Assert.Single(executedRoutines);
        Assert.Equal("BothRoutine", executedRoutines[0]);
    }

    [Fact]
    public void OnConnectivityChanged_DoesNotExecuteBothDirectionRoutine_WhenAnotherNetworkDisconnects()
    {
        var monitor = new FakeNetworkMonitor(["HomeWiFi", "GuestWiFi"]);
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "HomeRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "HomeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Both,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange(["HomeWiFi"]);

        Assert.Empty(executedRoutines);
    }

    [Fact]
    public void OnConnectivityChanged_ExecutesBothDirectionRoutine_WhenTargetNetworkIsRemovedDuringSwitch()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "HomeRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "HomeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Both,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("OfficeWiFi");

        Assert.Single(executedRoutines);
        Assert.Equal("HomeRoutine", executedRoutines[0]);
    }

    [Fact]
    public void OnConnectivityChanged_ExecutesConnectRoutine_WhenTargetNetworkAppearsAlongsideExistingNetwork()
    {
        var monitor = new FakeNetworkMonitor("Ethernet");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-office",
                Name = "OfficeRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "OfficeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange(["Ethernet", "OfficeWiFi"]);

        Assert.Single(executedRoutines);
        Assert.Equal("OfficeRoutine", executedRoutines[0]);
    }

    [Fact]
    public void OnConnectivityChanged_ExecutesConnectRoutine_WhenSwitchingNetworks()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-office",
                Name = "OfficeRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "OfficeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("OfficeWiFi");

        Assert.Single(executedRoutines);
        Assert.Equal("OfficeRoutine", executedRoutines[0]);
    }

    private sealed class FakeNetworkMonitor(IEnumerable<string> initialNetworks) : INetworkConnectionMonitor
    {
        public FakeNetworkMonitor(string initialNetwork)
            : this(string.IsNullOrWhiteSpace(initialNetwork) ? [] : [initialNetwork])
        {
        }

        public event EventHandler? ConnectivityChanged;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public IReadOnlyList<string> LastObservedNetworkNames { get; private set; } = [.. initialNetworks];

        public void Start()
        {
            Started = true;
            StartCount++;
        }

        public void Stop()
        {
            Started = false;
            StopCount++;
        }

        public IReadOnlyCollection<string> GetConnectedNetworkNames()
        {
            return [.. LastObservedNetworkNames];
        }

        public void Dispose()
        {
            Disposed = true;
        }

        public void SimulateConnectivityChange(string newNetworkName)
        {
            LastObservedNetworkNames = string.IsNullOrWhiteSpace(newNetworkName)
                ? []
                : [newNetworkName];
            ConnectivityChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SimulateConnectivityChange(IEnumerable<string> newNetworkNames)
        {
            LastObservedNetworkNames = [.. newNetworkNames];
            ConnectivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
