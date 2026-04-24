using System.Collections.ObjectModel;
using System.Diagnostics;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using Windows.Networking.Connectivity;

namespace AudioPilot.Coordinators
{
    internal interface INetworkConnectionMonitor : IDisposable
    {
        event EventHandler? ConnectivityChanged;
        void Start();
        void Stop();
        IReadOnlyCollection<string> GetConnectedNetworkNames();
    }

    internal sealed class WinRtNetworkMonitor(Logger logger) : INetworkConnectionMonitor
    {
        private readonly Lock _lock = new();
        private bool _started;

        public event EventHandler? ConnectivityChanged;

        public void Start()
        {
            lock (_lock)
            {
                if (_started)
                {
                    return;
                }

                NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
                _started = true;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_started)
                {
                    return;
                }

                NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
                _started = false;
            }
        }

        public IReadOnlyCollection<string> GetConnectedNetworkNames()
        {
            var networkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                AddResolvedNetworkNames(NetworkInformation.GetInternetConnectionProfile(), networkNames);

                foreach (ConnectionProfile p in NetworkInformation.GetConnectionProfiles())
                {
                    AddResolvedNetworkNames(p, networkNames);
                }
            }
            catch (Exception ex)
            {
                logger.Warning("WinRtNetworkMonitor", () => $"network-name-resolution-failed | reason={ex.GetType().Name}", nameof(GetConnectedNetworkNames), ex);
            }

            return [.. networkNames.OrderBy(static networkName => networkName, StringComparer.OrdinalIgnoreCase)];
        }

        public void Dispose()
        {
            Stop();
        }

        private void OnNetworkStatusChanged(object sender)
        {
            ConnectivityChanged?.Invoke(this, EventArgs.Empty);
        }

        private static void AddResolvedNetworkNames(ConnectionProfile? profile, HashSet<string> networkNames)
        {
            if (profile == null)
            {
                return;
            }

            foreach (string networkName in NetworkTriggerCoordinator.TryResolveNetworkNames(profile))
            {
                networkNames.Add(networkName);
            }
        }
    }

    internal sealed class NetworkTriggerCoordinator(
        ObservableCollection<AudioRoutine> routines,
        Action<AudioRoutine, string> executeRoutine,
        Logger logger,
        INetworkConnectionMonitor? monitor = null) : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly INetworkConnectionMonitor _monitor = monitor ?? new WinRtNetworkMonitor(logger);
        private bool _disposed;
        private bool _started;
        private HashSet<string> _lastObservedNetworkNames = new(StringComparer.OrdinalIgnoreCase);
        private bool _wasConnectedToAnyNetwork;
        private readonly Dictionary<string, long> _lastTriggerTimestampsByIdentity = new(StringComparer.Ordinal);
        private readonly TimeSpan _debounceInterval = TimeSpan.FromSeconds(1);

        private readonly HashSet<string> _reusableHashSet2 = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _reusableHashSet3 = new(StringComparer.OrdinalIgnoreCase);

        internal static IReadOnlyList<string> TryResolveNetworkNames(ConnectionProfile profile)
        {
            if (profile == null || profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
            {
                return [];
            }

            var networkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var resolvedNetworkNames = profile.GetNetworkNames();
                if (resolvedNetworkNames != null)
                {
                    foreach (object? networkName in resolvedNetworkNames)
                    {
                        string normalizedNetworkName = NetworkHelper.NormalizeNetworkName(networkName?.ToString());
                        if (!string.IsNullOrWhiteSpace(normalizedNetworkName))
                        {
                            networkNames.Add(normalizedNetworkName);
                        }
                    }
                }

                if (networkNames.Count == 0)
                {
                    string profileName = NetworkHelper.NormalizeNetworkName(profile.ProfileName);
                    if (!string.IsNullOrWhiteSpace(profileName))
                    {
                        networkNames.Add(profileName);
                    }
                }
            }
            catch
            {
                return [];
            }

            return [.. networkNames.OrderBy(static networkName => networkName, StringComparer.OrdinalIgnoreCase)];
        }

        internal static string TryResolveNetworkName(ConnectionProfile profile)
        {
            IReadOnlyList<string> names = TryResolveNetworkNames(profile);
            return names.Count > 0 ? names[0] : string.Empty;
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed || _started)
                {
                    return;
                }

                _lastObservedNetworkNames = GetNormalizedNetworkNames(_monitor.GetConnectedNetworkNames());
                _wasConnectedToAnyNetwork = _lastObservedNetworkNames.Count > 0;
                _monitor.ConnectivityChanged += OnConnectivityChanged;
                _monitor.Start();
                _started = true;
                logger.Info("NetworkTriggerCoordinator", () => $"network-trigger-monitor-started | initialNetworks={FormatNetworkLogLabel(_lastObservedNetworkNames)} connected={_wasConnectedToAnyNetwork}");
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_started)
                {
                    return;
                }

                _monitor.ConnectivityChanged -= OnConnectivityChanged;
                _monitor.Stop();
                _lastObservedNetworkNames.Clear();
                _wasConnectedToAnyNetwork = false;
                _lastTriggerTimestampsByIdentity.Clear();
                _started = false;
                logger.Info("NetworkTriggerCoordinator", () => "Network trigger monitor stopped");
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            Stop();
            _monitor.Dispose();
        }

        private void OnConnectivityChanged(object? sender, EventArgs e)
        {
            HashSet<string> currentNetworkNames = GetNormalizedNetworkNames(_monitor.GetConnectedNetworkNames());
            bool isConnectedToAnyNetwork = currentNetworkNames.Count > 0;
            HashSet<string> previousNetworkNames;
            HashSet<string> connectedNetworkNames;
            HashSet<string> disconnectedNetworkNames;
            bool previousConnectedToAnyNetwork;
            List<AudioRoutine> routinesCopy;
            List<(AudioRoutine Routine, string ExecutionSource)> routineExecutions = [];
            long nowTimestampTicks = Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks;

            lock (_lock)
            {
                if (_disposed || !_started)
                {
                    return;
                }

                previousNetworkNames = new HashSet<string>(_lastObservedNetworkNames, StringComparer.OrdinalIgnoreCase);

                previousConnectedToAnyNetwork = _wasConnectedToAnyNetwork;

                _reusableHashSet2.Clear();
                _reusableHashSet2.UnionWith(currentNetworkNames);
                _reusableHashSet2.ExceptWith(previousNetworkNames);
                connectedNetworkNames = new HashSet<string>(_reusableHashSet2, StringComparer.OrdinalIgnoreCase);

                _reusableHashSet3.Clear();
                _reusableHashSet3.UnionWith(previousNetworkNames);
                _reusableHashSet3.ExceptWith(currentNetworkNames);
                disconnectedNetworkNames = new HashSet<string>(_reusableHashSet3, StringComparer.OrdinalIgnoreCase);

                bool networkSetChanged = connectedNetworkNames.Count > 0 || disconnectedNetworkNames.Count > 0;
                bool connectivityStateChanged = previousConnectedToAnyNetwork != isConnectedToAnyNetwork;

                if (!networkSetChanged && !connectivityStateChanged)
                {
                    return;
                }

                routinesCopy = [.. routines];
                int debouncedRoutineCount = 0;
                foreach (AudioRoutine routine in routinesCopy)
                {
                    if (!TryClassifyRoutineTrigger(
                            routine,
                            connectedNetworkNames,
                            disconnectedNetworkNames,
                            previousConnectedToAnyNetwork,
                            isConnectedToAnyNetwork,
                            out string executionSource))
                    {
                        continue;
                    }

                    string triggerIdentity = BuildTriggerDebounceIdentity(routine, executionSource);
                    if (_lastTriggerTimestampsByIdentity.TryGetValue(triggerIdentity, out long lastTriggerTimestampTicks) &&
                        nowTimestampTicks - lastTriggerTimestampTicks < _debounceInterval.Ticks)
                    {
                        debouncedRoutineCount++;
                        continue;
                    }

                    _lastTriggerTimestampsByIdentity[triggerIdentity] = nowTimestampTicks;
                    routineExecutions.Add((routine, executionSource));
                }

                _lastObservedNetworkNames.Clear();
                _lastObservedNetworkNames.UnionWith(currentNetworkNames);
                _wasConnectedToAnyNetwork = isConnectedToAnyNetwork;

                if (debouncedRoutineCount > 0)
                {
                    logger.Debug("NetworkTriggerCoordinator", () => $"network-change-debounced | suppressedRoutines={debouncedRoutineCount} previous={FormatNetworkLogLabel(previousNetworkNames)} current={FormatNetworkLogLabel(currentNetworkNames)} added={FormatNetworkLogLabel(connectedNetworkNames)} removed={FormatNetworkLogLabel(disconnectedNetworkNames)} stateChanged={connectivityStateChanged}");
                }
            }

            string changeType = connectedNetworkNames.Count > 0 && disconnectedNetworkNames.Count > 0
                ? "network-change"
                : connectedNetworkNames.Count > 0
                    ? "connect"
                    : "disconnect";
            logger.Info("NetworkTriggerCoordinator", () => $"network-change-detected | type={changeType} previous={FormatNetworkLogLabel(previousNetworkNames)} current={FormatNetworkLogLabel(currentNetworkNames)} added={FormatNetworkLogLabel(connectedNetworkNames)} removed={FormatNetworkLogLabel(disconnectedNetworkNames)}");

            foreach ((AudioRoutine routine, string executionSource) in routineExecutions)
            {
                try
                {
                    executeRoutine(routine, executionSource);
                }
                catch (Exception ex)
                {
                    logger.Error("NetworkTriggerCoordinator", () => $"network-routine-trigger-failed | routineName={LogPrivacy.Label(routine.Name)} reason={ex.GetType().Name}");
                }
            }
        }

        private static string BuildTriggerDebounceIdentity(AudioRoutine routine, string executionSource)
        {
            string routineIdentity = routine.Id;
            if (string.IsNullOrWhiteSpace(routineIdentity))
            {
                routineIdentity = routine.Name;
            }

            string targetNetworkName = NetworkHelper.NormalizeNetworkName(routine.TriggerNetworkName);
            return string.Join("|", routineIdentity, executionSource, targetNetworkName);
        }

        private static bool TryClassifyRoutineTrigger(
            AudioRoutine routine,
            HashSet<string> connectedNetworkNames,
            HashSet<string> disconnectedNetworkNames,
            bool previousConnectedToAnyNetwork,
            bool currentConnectedToAnyNetwork,
            out string executionSource)
        {
            executionSource = string.Empty;

            if (!routine.Enabled ||
                routine.TriggerKind != RoutineTriggerKind.Network ||
                !routine.HasExecutionTarget)
            {
                return false;
            }

            string normalizedTargetNetworkName = NetworkHelper.NormalizeNetworkName(routine.TriggerNetworkName);
            switch (routine.NetworkTriggerDirection)
            {
                case NetworkTriggerDirection.Connect:
                    if (!string.IsNullOrWhiteSpace(normalizedTargetNetworkName) && connectedNetworkNames.Contains(normalizedTargetNetworkName))
                    {
                        executionSource = "network-connect-trigger";
                        return true;
                    }

                    return false;

                case NetworkTriggerDirection.Disconnect:
                    if (previousConnectedToAnyNetwork && !currentConnectedToAnyNetwork)
                    {
                        executionSource = "network-disconnect-trigger";
                        return true;
                    }

                    return false;

                case NetworkTriggerDirection.Both:
                    if (!string.IsNullOrWhiteSpace(normalizedTargetNetworkName) && connectedNetworkNames.Contains(normalizedTargetNetworkName))
                    {
                        executionSource = "network-connect-trigger";
                        return true;
                    }

                    if (!string.IsNullOrWhiteSpace(normalizedTargetNetworkName) && disconnectedNetworkNames.Contains(normalizedTargetNetworkName))
                    {
                        executionSource = "network-disconnect-trigger";
                        return true;
                    }

                    return false;

                default:
                    return false;
            }
        }

        private static HashSet<string> GetNormalizedNetworkNames(IEnumerable<string>? networkNames)
        {
            var normalizedNetworkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (networkNames == null)
            {
                return normalizedNetworkNames;
            }

            foreach (string networkName in networkNames)
            {
                string normalizedNetworkName = NetworkHelper.NormalizeNetworkName(networkName);
                if (!string.IsNullOrWhiteSpace(normalizedNetworkName))
                {
                    normalizedNetworkNames.Add(normalizedNetworkName);
                }
            }

            return normalizedNetworkNames;
        }

        private static string FormatNetworkLogLabel(IEnumerable<string>? networkNames)
        {
            string[] normalizedNetworkNames =
            [
                .. GetNormalizedNetworkNames(networkNames)
                    .OrderBy(static networkName => networkName, StringComparer.OrdinalIgnoreCase)
                    .Select(static networkName => LogPrivacy.Label(networkName))
            ];

            return normalizedNetworkNames.Length == 0
                ? "networks[none]"
                : $"networks[{string.Join(", ", normalizedNetworkNames)}]";
        }

        public static HashSet<string> GetAvailableNetworkNames()
        {
            var networkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (ConnectionProfile profile in NetworkInformation.GetConnectionProfiles())
                {
                    if (profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
                    {
                        continue;
                    }

                    foreach (string networkName in TryResolveNetworkNames(profile))
                    {
                        networkNames.Add(networkName);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Warning("NetworkTriggerCoordinator", () => $"available-network-enumeration-failed | reason={ex.GetType().Name}", nameof(GetAvailableNetworkNames), ex);
            }

            return networkNames;
        }
    }
}
