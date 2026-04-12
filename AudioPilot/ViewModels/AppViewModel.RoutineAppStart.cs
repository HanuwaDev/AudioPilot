using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Models;
using RoutineAppStartProcessSnapshot = AudioPilot.Platform.RoutineProcessSnapshot;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        internal sealed class RoutineAppOutputLease(
            string leaseKey,
            string routineId,
            string routineName,
            int rootProcessId,
            string triggerAppPath,
            string outputDeviceId,
            string outputDeviceName,
            string inputDeviceId = "",
            string inputDeviceName = "")
        {
            public string LeaseKey { get; } = leaseKey;
            public string RoutineId { get; set; } = routineId;
            public string RoutineName { get; set; } = routineName;
            public int RootProcessId { get; } = rootProcessId;
            public string TriggerAppPath { get; set; } = triggerAppPath;
            public string OutputDeviceId { get; set; } = outputDeviceId;
            public string OutputDeviceName { get; set; } = outputDeviceName;
            public string InputDeviceId { get; set; } = inputDeviceId;
            public string InputDeviceName { get; set; } = inputDeviceName;
            public bool CompletionOverlayShown { get; set; }
            public HashSet<uint> AppliedOutputProcessIds { get; } = [];
            public HashSet<uint> AppliedInputProcessIds { get; } = [];

            public RoutineAppOutputLease Clone()
            {
                var clone = new RoutineAppOutputLease(LeaseKey, RoutineId, RoutineName, RootProcessId, TriggerAppPath, OutputDeviceId, OutputDeviceName, InputDeviceId, InputDeviceName)
                {
                    CompletionOverlayShown = CompletionOverlayShown,
                };
                clone.AppliedOutputProcessIds.UnionWith(AppliedOutputProcessIds);
                clone.AppliedInputProcessIds.UnionWith(AppliedInputProcessIds);
                return clone;
            }
        }

        internal readonly record struct RoutineAppStartMatch(AudioRoutine Routine, int ProcessId);
        internal readonly record struct RoutineAppStartSnapshotSet(
            IReadOnlyList<RoutineAppStartProcessSnapshot> Snapshots,
            Dictionary<int, RoutineAppStartProcessSnapshot> SnapshotsByPid);

        private readonly Lock _routineAppStartMonitorLock = new();
        private List<AudioRoutine> _appStartTriggeredRoutines = [];
        private Dictionary<string, RoutineAppOutputLease> _activeRoutineAppOutputLeases = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _pendingRoutineAppStartRoots = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _routineAppOutputLeaseRefreshDebounceCts;
        private int _pendingRoutineAppOutputLeaseSignals;
        private bool _routineAppStartMonitorRunning;
        private bool _routineAppStartMonitoringEnabled;
        private string _routineAppStartMonitorStatus = "inactive";

        private void InitializeRoutineAppStartInfrastructure()
        {
            _routineAppProcessMonitor.ProcessStarted -= OnRoutineAppProcessStarted;
            _routineAppProcessMonitor.ProcessStarted += OnRoutineAppProcessStarted;
            _routineAppProcessMonitor.ProcessStopped -= OnRoutineAppProcessStopped;
            _routineAppProcessMonitor.ProcessStopped += OnRoutineAppProcessStopped;
        }

        internal void EnableRoutineAppStartMonitoring()
        {
            _routineAppStartMonitoringEnabled = true;
            RefreshRoutineRuntimeTriggers();
        }

        private void RefreshRoutineRuntimeTriggers()
        {
            List<AudioRoutine> watchedRoutines =
            [
                .. GetPersistedRoutineSnapshot().Where(static routine =>
                    routine.Enabled &&
                    routine.HasAppStartTrigger &&
                    RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(routine.TriggerAppPath))
            ];
            List<AudioRoutine> steamBigPictureRoutines =
            [
                .. GetPersistedRoutineSnapshot().Where(static routine =>
                    routine.Enabled &&
                    routine.TriggerKind == RoutineTriggerKind.SteamBigPicture &&
                    (routine.HasOutputTarget || routine.HasInputTarget))
            ];
            List<string> invalidStatefulSessionKeys;
            long latestActivationSequence;

            lock (_routineAppStartMonitorLock)
            {
                _appStartTriggeredRoutines = watchedRoutines;
                _steamBigPictureTriggeredRoutines = steamBigPictureRoutines;
                _activeRoutineAppOutputLeases = SynchronizeRoutineAppOutputLeasesWithWatchedRoutines(_activeRoutineAppOutputLeases, watchedRoutines);
                _pendingRoutineAppStartRoots = SynchronizePendingRoutineAppStartRoots(_pendingRoutineAppStartRoots, watchedRoutines);
                invalidStatefulSessionKeys = AppRoutineStatefulCoordinator.GetInvalidRoutineStatefulSessionKeys(
                    _activeRoutineStatefulSessions,
                    watchedRoutines,
                    steamBigPictureRoutines);
                latestActivationSequence = AppRoutineStatefulCoordinator.GetLatestActivationSequence(_activeRoutineStatefulSessions.Values);
            }

            UpdateRoutineAppStartMonitorState();
            UpdateSteamBigPictureMonitorState();

            if (invalidStatefulSessionKeys.Count == 0)
            {
                return;
            }

            RunBackgroundWork(async cancellationToken =>
            {
                foreach (string sessionKey in invalidStatefulSessionKeys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await DeactivateRoutineStatefulSessionAsync(sessionKey, latestActivationSequence);
                }
            }, nameof(RefreshRoutineRuntimeTriggers));
        }

        private void UpdateRoutineAppStartMonitorState()
        {
            bool isRunning;
            int watchedRoutineCount;
            int activeLeaseCount;
            bool hasActiveAppStartStatefulSessions;
            lock (_routineAppStartMonitorLock)
            {
                watchedRoutineCount = _appStartTriggeredRoutines.Count;
                activeLeaseCount = _activeRoutineAppOutputLeases.Count;
                hasActiveAppStartStatefulSessions = _activeRoutineStatefulSessions.Values.Any(static session => session.TriggerKind == RoutineTriggerKind.AppStartup);
                isRunning = _routineAppStartMonitorRunning;
            }

            RoutineAppStartMonitorDecision decision = AppRoutineAppStartCoordinator.ResolveMonitorDecision(
                _routineAppStartMonitoringEnabled,
                _isCleaningUp,
                watchedRoutineCount,
                activeLeaseCount,
                hasActiveAppStartStatefulSessions,
                isRunning);

            if (decision.Action == RoutineAppStartMonitorAction.Stop)
            {
                _routineAppProcessMonitor.Stop();
                lock (_routineAppStartMonitorLock)
                {
                    _routineAppStartMonitorRunning = false;
                    _routineAppStartMonitorStatus = "inactive";
                }
                _logger.Info("AppViewModel", "routine-app-start-monitor-inactive | reason=no-watched-routines");
                return;
            }

            if (decision.Action == RoutineAppStartMonitorAction.Start)
            {
                ProcessLifecycleMonitorStartResult startResult = _routineAppProcessMonitor.Start();
                lock (_routineAppStartMonitorLock)
                {
                    _routineAppStartMonitorRunning = startResult.Success;
                    _routineAppStartMonitorStatus = startResult.Status;
                }

                if (startResult.Success)
                {
                    _logger.Info("AppViewModel", () => $"routine-app-start-monitor-active | watcher={_routineAppProcessMonitor.GetType().Name} triggerCount={_appStartTriggeredRoutines.Count} leaseCount={_activeRoutineAppOutputLeases.Count}");
                }
                else
                {
                    string failureReason = string.IsNullOrWhiteSpace(startResult.FailureReason) ? "unknown" : startResult.FailureReason;
                    _logger.Warning("AppViewModel", () => $"routine-app-start-monitor-inactive | reason={failureReason} watchedRoutineCount={_appStartTriggeredRoutines.Count} leaseCount={_activeRoutineAppOutputLeases.Count}");
                    _logger.Warning("AppViewModel", () => $"routine-app-start-monitor-start-failed | reason={failureReason} appStartRoutinesAvailable=false");
                }
            }
        }

        private void OnRoutineAppProcessStarted(int processId)
        {
            if (_isCleaningUp || processId <= 0)
            {
                return;
            }

            RequestSteamBigPictureFallbackRevalidation();

            List<AudioRoutine> watchedRoutines;
            int activeLeaseCount;
            lock (_routineAppStartMonitorLock)
            {
                watchedRoutines = [.. _appStartTriggeredRoutines];
                activeLeaseCount = _activeRoutineAppOutputLeases.Count;
            }

            if (!_routineAppStartMonitoringEnabled || watchedRoutines.Count == 0)
            {
                return;
            }

            RunBackgroundWork(async cancellationToken =>
            {
                RoutineProcessSnapshotCaptureOptions startedSnapshotCaptureOptions = GetCaptureOptionsForTriggerTargets(
                    watchedRoutines.Select(static routine => routine.TriggerAppPath));
                RoutineAppStartProcessWorkload? workload = await AppRoutineAppStartCoordinator.PrepareStartedProcessWorkloadAsync(
                    processId,
                    watchedRoutines,
                    activeLeaseCount,
                    startedProcessId => TryCaptureProcessSnapshot(startedProcessId, startedSnapshotCaptureOptions),
                    cancellationToken);
                if (!workload.HasValue)
                {
                    return;
                }

                string opId = CreateRoutineAppStartOperationId("app-start-routine");
                _logger.Info(
                    "AppViewModel",
                    () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RoutineAppStartBatch} | phase=start opId={NormalizeRoutineLogValue(opId)} processId={FormatRoutineLogProcessId(processId)} matchCount={workload.Value.Matches.Count} requiresProcessSnapshotCapture={workload.Value.RequiresProcessSnapshotCapture} activeLeaseCount={activeLeaseCount}");

                List<RoutineAppStartProcessSnapshot> processSnapshots = [];
                RoutineAppStartSnapshotSet? processSnapshotSet = null;
                List<RoutineAppOutputLease> activeLeases = [];
                if (workload.Value.RequiresProcessSnapshotCapture)
                {
                    processSnapshots = await Task.Run(
                        () => CaptureProcessSnapshots(RoutineProcessSnapshotCaptureOptions.Full),
                        cancellationToken);
                    processSnapshotSet = CreateRoutineAppStartSnapshotSet(processSnapshots);
                    lock (_routineAppStartMonitorLock)
                    {
                        activeLeases = [.. _activeRoutineAppOutputLeases.Values.Select(static lease => lease.Clone())];
                    }
                }

                IReadOnlyList<RoutineAppStartMatchExecutionPlan> executionPlans = processSnapshotSet.HasValue
                    ? AppRoutineAppStartCoordinator.PlanStartedMatchExecutions(
                        workload.Value.Matches,
                        workload.Value.Snapshot,
                        activeLeases,
                        processSnapshotSet.Value)
                    : AppRoutineAppStartCoordinator.PlanStartedMatchExecutions(
                        workload.Value.Matches,
                        workload.Value.Snapshot,
                        activeLeases,
                        processSnapshots);

                int executedCount = 0;
                int skippedExistingLeaseCount = 0;
                int skippedClaimCount = 0;

                foreach (RoutineAppStartMatchExecutionPlan executionPlan in executionPlans)
                {
                    if (executionPlan.Action == RoutineAppStartMatchExecutionAction.SkipExistingActiveLease)
                    {
                        skippedExistingLeaseCount++;
                        LogRoutineAppStartMatchSkipped(executionPlan.Match, "existing-active-lease", opId);
                        continue;
                    }

                    RoutineAppStartMatch match = executionPlan.Match;
                    if (!TryClaimRoutineAppStartMatch(match.Routine.Id, match.ProcessId, workload.Value.Snapshot))
                    {
                        skippedClaimCount++;
                        LogRoutineAppStartMatchSkipped(match, "claim-unavailable", opId);
                        continue;
                    }

                    try
                    {
                        executedCount++;
                        await ExecuteRoutineFromAppStartAsync(match, cancellationToken, opId);
                    }
                    finally
                    {
                        ReleaseRoutineAppStartMatchClaim(match.Routine.Id, match.ProcessId);
                    }
                }

                _logger.Info(
                    "AppViewModel",
                    () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RoutineAppStartBatch} | phase=completed opId={NormalizeRoutineLogValue(opId)} processId={FormatRoutineLogProcessId(processId)} matchCount={workload.Value.Matches.Count} executedCount={executedCount} skippedExistingLeaseCount={skippedExistingLeaseCount} skippedClaimCount={skippedClaimCount} processSnapshotCount={processSnapshots.Count}");
            }, nameof(OnRoutineAppProcessStarted));
        }

        private void LogRoutineAppStartMatchSkipped(RoutineAppStartMatch match, string reason, string? correlatedOperationId = null)
        {
            _logger.Info(
                "AppViewModel",
                () => $"routine-app-start-match-skipped | processId={FormatRoutineLogProcessId(match.ProcessId)} reason={reason} {BuildRoutineExecutionLogContext(match.Routine, "app-start", showOverlay: true, appStartProcessId: match.ProcessId)}{BuildRoutineExecutionCorrelationLogContext(correlatedOperationId)}");
        }

        private void OnRoutineAppProcessStopped(int processId)
        {
            if (_isCleaningUp || processId <= 0)
            {
                return;
            }

            RequestSteamBigPictureFallbackRevalidation();

            int activeLeaseCount;
            int activeAppStartStatefulSessionCount;
            lock (_routineAppStartMonitorLock)
            {
                activeLeaseCount = _activeRoutineAppOutputLeases.Count;
                activeAppStartStatefulSessionCount = _activeRoutineStatefulSessions.Values.Count(static session => session.TriggerKind == RoutineTriggerKind.AppStartup);
            }

            if (!ShouldCaptureProcessSnapshotsForStoppedProcess(activeLeaseCount, activeAppStartStatefulSessionCount))
            {
                UpdateRoutineAppStartMonitorState();
                return;
            }

            RunBackgroundWork(async cancellationToken =>
            {
                List<RoutineAppStartProcessSnapshot> processSnapshots = await Task.Run(
                    () => CaptureProcessSnapshots(RoutineProcessSnapshotCaptureOptions.Full),
                    cancellationToken);
                RoutineAppStartSnapshotSet processSnapshotSet = CreateRoutineAppStartSnapshotSet(processSnapshots);
                List<AudioRoutine> watchedRoutines;
                lock (_routineAppStartMonitorLock)
                {
                    watchedRoutines = [.. _appStartTriggeredRoutines];
                    _activeRoutineAppOutputLeases = ReconcileRoutineAppOutputLeases(_activeRoutineAppOutputLeases, watchedRoutines, processSnapshotSet);
                }

                await DeactivateEndedAppStartSessionsAsync(processSnapshots, cancellationToken);

                UpdateRoutineAppStartMonitorState();
            }, nameof(OnRoutineAppProcessStopped));
        }

        private async Task ExecuteRoutineFromAppStartAsync(RoutineAppStartMatch match, CancellationToken cancellationToken, string? correlatedOperationId = null)
        {
            AudioRoutine routine = match.Routine;
            if (!routine.Enabled)
            {
                return;
            }

            if (!routine.HasExecutionTarget)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteRoutineForResolvedProcessAsync(
                routine,
                match.ProcessId,
                showOverlay: true,
                executionSource: "app-start",
                applyAppStabilityWait: true,
                correlatedOperationId: correlatedOperationId,
                cancellationToken: cancellationToken);
        }

        private void RegisterRoutineAppOutputLease(AudioRoutine routine, int rootProcessId, bool outputApplied, bool inputApplied, bool completionOverlayShown)
        {
            string triggerAppPath = RoutineTriggerPathHelper.NormalizeTriggerTarget(routine.TriggerAppPath);
            if (rootProcessId <= 0 ||
                string.IsNullOrWhiteSpace(triggerAppPath) ||
                (string.IsNullOrWhiteSpace(routine.OutputDeviceId) && string.IsNullOrWhiteSpace(routine.InputDeviceId)))
            {
                return;
            }

            string routineId = string.IsNullOrWhiteSpace(routine.Id) ? "unknown" : routine.Id;
            string leaseKey = CreateRoutineAppOutputLeaseKey(routineId, rootProcessId);
            bool outputTargetChanged = false;
            bool inputTargetChanged = false;
            bool leaseCreated = false;
            RoutineAppOutputLease? leaseForLog = null;

            lock (_routineAppStartMonitorLock)
            {
                if (_activeRoutineAppOutputLeases.TryGetValue(leaseKey, out RoutineAppOutputLease? matchingLease))
                {
                    outputTargetChanged = !string.Equals(matchingLease.OutputDeviceId, routine.OutputDeviceId, StringComparison.OrdinalIgnoreCase);
                    inputTargetChanged = !string.Equals(matchingLease.InputDeviceId, routine.InputDeviceId, StringComparison.OrdinalIgnoreCase);
                    matchingLease.RoutineId = routineId;
                    matchingLease.RoutineName = routine.Name;
                    matchingLease.TriggerAppPath = triggerAppPath;
                    matchingLease.OutputDeviceId = routine.OutputDeviceId;
                    matchingLease.OutputDeviceName = routine.OutputDeviceName;
                    matchingLease.InputDeviceId = routine.InputDeviceId;
                    matchingLease.InputDeviceName = routine.InputDeviceName;
                    matchingLease.CompletionOverlayShown = matchingLease.CompletionOverlayShown || completionOverlayShown;
                    if (outputTargetChanged)
                    {
                        matchingLease.AppliedOutputProcessIds.Clear();
                    }

                    if (inputTargetChanged)
                    {
                        matchingLease.AppliedInputProcessIds.Clear();
                    }

                    if (outputTargetChanged || inputTargetChanged)
                    {
                        matchingLease.CompletionOverlayShown = completionOverlayShown;
                    }

                    ApplyInitialLeaseProcessState(matchingLease, (uint)rootProcessId, outputApplied, inputApplied);
                    leaseForLog = matchingLease.Clone();
                }
                else
                {
                    leaseCreated = true;
                    _activeRoutineAppOutputLeases[leaseKey] = new RoutineAppOutputLease(
                        leaseKey,
                        routineId,
                        routine.Name,
                        rootProcessId,
                        triggerAppPath,
                        routine.OutputDeviceId,
                        routine.OutputDeviceName,
                        routine.InputDeviceId,
                        routine.InputDeviceName)
                    {
                        CompletionOverlayShown = completionOverlayShown,
                    };

                    ApplyInitialLeaseProcessState(_activeRoutineAppOutputLeases[leaseKey], (uint)rootProcessId, outputApplied, inputApplied);
                    leaseForLog = _activeRoutineAppOutputLeases[leaseKey].Clone();
                }
            }

            if (leaseForLog != null)
            {
                string eventName = leaseCreated
                    ? "routine-app-start-lease-created"
                    : "routine-app-start-lease-updated";
                _logger.Info(
                    "AppViewModel",
                    () => $"{eventName} | {BuildRoutineAppOutputLeaseLogContext(leaseForLog)} outputTargetChanged={outputTargetChanged} inputTargetChanged={inputTargetChanged} initialOutputApplied={outputApplied} initialInputApplied={inputApplied}");
            }

            UpdateRoutineAppStartMonitorState();
        }

        private void QueueRoutineAppOutputLeaseRefresh()
        {
            if (_isCleaningUp)
            {
                return;
            }

            lock (_routineAppStartMonitorLock)
            {
                if (_activeRoutineAppOutputLeases.Count == 0)
                {
                    return;
                }
            }

            int queuedSignals = Interlocked.Increment(ref _pendingRoutineAppOutputLeaseSignals);
            CancellationTokenSource nextDebounceCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(ref _routineAppOutputLeaseRefreshDebounceCts);
            RunBackgroundWork(async shutdownToken =>
            {
                await AppDebouncedBackgroundWorkCoordinator.ExecuteDelayedAsync(
                    nextDebounceCts,
                    ownedDebounce => ReleaseOwnedDebounce(ref _routineAppOutputLeaseRefreshDebounceCts, ownedDebounce),
                    RuntimeTuningConfig.MixerSessionRefreshDebounceMs,
                    async linkedToken =>
                    {
                        int coalescedSignals = Interlocked.Exchange(ref _pendingRoutineAppOutputLeaseSignals, 0);
                        if (coalescedSignals <= 0)
                        {
                            coalescedSignals = queuedSignals;
                        }

                        string opId = $"app-start-lease-refresh:{Guid.NewGuid():N}";
                        await ApplyRoutineAppOutputLeasesAsync(opId, coalescedSignals, linkedToken);
                    },
                    shutdownToken);
            }, nameof(QueueRoutineAppOutputLeaseRefresh));
        }

        private async Task ApplyRoutineAppOutputLeasesAsync(string operationId, int coalescedSignals, CancellationToken cancellationToken)
        {
            _logger.Info(
                "AppViewModel",
                () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RoutineAppStartLeaseRefresh} | phase=start opId={NormalizeRoutineLogValue(operationId)} coalescedSignals={coalescedSignals}");
            RoutineProcessSnapshotCaptureOptions captureOptions = GetCaptureOptionsForTriggerTargets(
                _appStartTriggeredRoutines.Select(static routine => routine.TriggerAppPath));
            List<RoutineAppStartProcessSnapshot> processSnapshots = await Task.Run(
                () => CaptureProcessSnapshots(captureOptions),
                cancellationToken);
            RoutineAppStartSnapshotSet processSnapshotSet = CreateRoutineAppStartSnapshotSet(processSnapshots);

            IReadOnlyList<RoutineAppOutputLease> activeLeases;
            int previousLeaseCount;
            lock (_routineAppStartMonitorLock)
            {
                RoutineAppOutputLeaseRefreshPreparation preparation = AppRoutineAppStartCoordinator.PrepareLeaseRefresh(
                    _activeRoutineAppOutputLeases,
                    _appStartTriggeredRoutines,
                    processSnapshots);
                previousLeaseCount = preparation.PreviousLeaseCount;
                _activeRoutineAppOutputLeases = preparation.ReconciledLeases;
                activeLeases = preparation.ActiveLeases;
            }

            _logger.Info(
                "AppViewModel",
                () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RoutineAppStartLeaseRefresh} | phase=completed opId={NormalizeRoutineLogValue(operationId)} coalescedSignals={coalescedSignals} previousLeaseCount={previousLeaseCount} activeLeaseCount={activeLeases.Count} processSnapshotCount={processSnapshots.Count}");

            UpdateRoutineAppStartMonitorState();

            if (activeLeases.Count == 0)
            {
                return;
            }

            List<RoutineAppOutputLease> pendingLeases = [];
            for (int index = 0; index < activeLeases.Count; index++)
            {
                RoutineAppOutputLease lease = activeLeases[index];
                if (AppRoutineAppStartCoordinator.HasPendingLeaseApplications(lease))
                {
                    pendingLeases.Add(lease);
                }
            }

            if (pendingLeases.Count == 0)
            {
                _logger.Info(
                    "AppViewModel",
                    () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RoutineAppStartLeaseRefresh} | phase=skipped-no-pending-work opId={NormalizeRoutineLogValue(operationId)} coalescedSignals={coalescedSignals} activeLeaseCount={activeLeases.Count}");
                return;
            }

            IReadOnlyList<AudioSessionSnapshot> sessionSnapshots = await _audio.GetAllAudioSessionSnapshotsAsync(cancellationToken: cancellationToken);

            await AppRoutineAppStartCoordinator.ExecuteLeaseApplicationsAsync(
                pendingLeases,
                processSnapshots,
                sessionSnapshots,
                (rootProcessId, triggerAppPath, _, currentSessionSnapshots) => CollectRoutineAppOutputCandidateProcessIds(
                    rootProcessId,
                    triggerAppPath,
                    processSnapshotSet,
                    currentSessionSnapshots),
                TryApplyRoutineAppOutputLeaseOutputAsync,
                TryApplyRoutineAppOutputLeaseInputAsync,
                ShowRoutineAppOutputLeaseOverlayAsync,
                MarkRoutineAppOutputLeaseProcessApplied,
                MarkRoutineAppOutputLeaseOverlayShown,
                _logger,
                cancellationToken);
        }

        private async Task<bool> TryApplyRoutineAppOutputLeaseOutputAsync(RoutineAppOutputLease lease, uint processId)
        {
            string opId = CreateRoutineAppStartOperationId("routine-app-output-lease");
            ProcessAudioDeviceSwitchResult outputResult = await _audio.SwitchApplicationOutputDeviceDetailedAsync(
                processId,
                lease.OutputDeviceId,
                lease.OutputDeviceName,
                opId);

            return outputResult.Result == ProcessAudioRoutingResult.Applied;
        }

        private async Task<bool> TryApplyRoutineAppOutputLeaseInputAsync(RoutineAppOutputLease lease, uint processId)
        {
            string opId = CreateRoutineAppStartOperationId("routine-app-input-lease");
            ProcessAudioDeviceSwitchResult inputResult = await _audio.SwitchApplicationInputDeviceDetailedAsync(
                processId,
                lease.InputDeviceId,
                lease.InputDeviceName,
                opId);

            return inputResult.Result == ProcessAudioRoutingResult.Applied;
        }

        internal static string BuildRoutineAppOutputLeaseLogContext(RoutineAppOutputLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease);

            return $"leaseKey={FormatRoutineLogIdentifier(lease.LeaseKey)} routineId={FormatRoutineLogIdentifier(lease.RoutineId)} routineName={FormatRoutineLogLabel(lease.RoutineName)} rootProcessId={FormatRoutineLogProcessId(lease.RootProcessId)} hasOutputTarget={!string.IsNullOrWhiteSpace(lease.OutputDeviceId)} hasInputTarget={!string.IsNullOrWhiteSpace(lease.InputDeviceId)} completionOverlayShown={lease.CompletionOverlayShown} appliedOutputProcessCount={lease.AppliedOutputProcessIds.Count} appliedInputProcessCount={lease.AppliedInputProcessIds.Count}";
        }

        private async Task ShowRoutineAppOutputLeaseOverlayAsync(RoutineAppOutputLease lease, string? appliedOutputDeviceName, string? appliedInputDeviceName)
        {
            string? outputDeviceName = AppViewModelRoutineOverlayHelper.ResolveRoutineOverlayDeviceName(
                !string.IsNullOrWhiteSpace(lease.OutputDeviceId),
                appliedOutputDeviceName,
                lease.OutputDeviceName,
                fallbackLabel: "Output device");
            string? inputDeviceName = AppViewModelRoutineOverlayHelper.ResolveRoutineOverlayDeviceName(
                !string.IsNullOrWhiteSpace(lease.InputDeviceId),
                appliedInputDeviceName,
                lease.InputDeviceName,
                fallbackLabel: "Input device");

            if (string.IsNullOrWhiteSpace(outputDeviceName) && string.IsNullOrWhiteSpace(inputDeviceName))
            {
                return;
            }

            await InvokeOnDispatcherAsync(() =>
            {
                if (!AppViewModelRoutineOverlayHelper.TryBuildRoutineSuccessOverlayPlan(
                        lease.RoutineName,
                        outputDeviceName,
                        inputDeviceName,
                        out AppViewModelRoutineOverlayHelper.RoutineSuccessOverlayPlan plan))
                {
                    return;
                }

                if (plan.ShowCombined)
                {
                    _overlay.ShowRoutine(plan.Header, plan.OutputDeviceName!, plan.InputDeviceName!);
                    return;
                }

                _overlay.Show(plan.Kind, plan.Header, plan.DeviceName ?? string.Empty);
            });
        }

        internal static IReadOnlyList<RoutineAppStartMatch> EvaluateRoutineAppStartMatchesForProcess(
            IReadOnlyList<AudioRoutine> watchedRoutines,
            RoutineAppStartProcessSnapshot processSnapshot)
        {
            var matches = new List<RoutineAppStartMatch>();

            foreach (AudioRoutine routine in watchedRoutines)
            {
                if (RoutineTriggerPathHelper.LooksLikeExecutablePath(routine.TriggerAppPath))
                {
                    string normalizedExecutablePath = RoutineTriggerPathHelper.NormalizeExecutablePath(processSnapshot.ExecutablePath);
                    if (string.IsNullOrWhiteSpace(normalizedExecutablePath) ||
                        !string.Equals(routine.TriggerAppPath, normalizedExecutablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                else if (RoutineTriggerPathHelper.LooksLikePackagedAppId(routine.TriggerAppPath))
                {
                    if (!RoutineTriggerPathHelper.IsPackagedAppMatch(routine.TriggerAppPath, processSnapshot.AppUserModelId))
                    {
                        continue;
                    }
                }
                else
                {
                    continue;
                }

                matches.Add(new RoutineAppStartMatch(routine, processSnapshot.ProcessId));
            }

            return matches;
        }

        internal static bool ShouldCaptureProcessSnapshotsForStartedMatches(
            IReadOnlyList<RoutineAppStartMatch> matches,
            int activeLeaseCount)
        {
            ArgumentNullException.ThrowIfNull(matches);

            return activeLeaseCount > 0 && matches.Any(static match => match.Routine.SwitchOutputPerApp);
        }

        internal static bool ShouldCaptureProcessSnapshotsForStoppedProcess(
            int activeLeaseCount,
            int activeAppStartStatefulSessionCount)
        {
            return activeLeaseCount > 0 || activeAppStartStatefulSessionCount > 0;
        }

        internal static HashSet<string> SynchronizePendingRoutineAppStartRoots(
            IReadOnlySet<string> currentClaims,
            IReadOnlyList<AudioRoutine> watchedRoutines)
        {
            HashSet<string> watchedRoutineIds = watchedRoutines
                .Where(static routine => routine.Enabled && routine.HasAppStartTrigger)
                .Select(static routine => string.IsNullOrWhiteSpace(routine.Id) ? "unknown" : routine.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return [..
                currentClaims.Where(claimKey => watchedRoutineIds.Contains(GetRoutineIdFromLeaseKey(claimKey)))
            ];
        }

        internal static Dictionary<string, RoutineAppOutputLease> SynchronizeRoutineAppOutputLeasesWithWatchedRoutines(
            IReadOnlyDictionary<string, RoutineAppOutputLease> currentLeases,
            IReadOnlyList<AudioRoutine> watchedRoutines)
        {
            Dictionary<string, AudioRoutine> routinesById = watchedRoutines
                .Where(static routine =>
                    routine.Enabled &&
                    routine.SwitchOutputPerApp &&
                    routine.HasAppStartTrigger &&
                    RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(routine.TriggerAppPath) &&
                    (!string.IsNullOrWhiteSpace(routine.OutputDeviceId) || !string.IsNullOrWhiteSpace(routine.InputDeviceId)))
                .GroupBy(static routine => string.IsNullOrWhiteSpace(routine.Id) ? "unknown" : routine.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);

            var synchronized = new Dictionary<string, RoutineAppOutputLease>(StringComparer.OrdinalIgnoreCase);
            foreach ((string leaseKey, RoutineAppOutputLease lease) in currentLeases)
            {
                if (!routinesById.TryGetValue(lease.RoutineId, out AudioRoutine? routine))
                {
                    continue;
                }

                string normalizedTriggerPath = RoutineTriggerPathHelper.NormalizeTriggerTarget(routine.TriggerAppPath);
                if (string.IsNullOrWhiteSpace(normalizedTriggerPath))
                {
                    continue;
                }

                RoutineAppOutputLease updatedLease = lease.Clone();
                bool outputTargetChanged = !string.Equals(updatedLease.OutputDeviceId, routine.OutputDeviceId, StringComparison.OrdinalIgnoreCase);
                bool inputTargetChanged = !string.Equals(updatedLease.InputDeviceId, routine.InputDeviceId, StringComparison.OrdinalIgnoreCase);
                updatedLease.RoutineName = routine.Name;
                updatedLease.TriggerAppPath = normalizedTriggerPath;
                updatedLease.OutputDeviceId = routine.OutputDeviceId;
                updatedLease.OutputDeviceName = routine.OutputDeviceName;
                updatedLease.InputDeviceId = routine.InputDeviceId;
                updatedLease.InputDeviceName = routine.InputDeviceName;
                if (outputTargetChanged)
                {
                    updatedLease.AppliedOutputProcessIds.Clear();
                }

                if (inputTargetChanged)
                {
                    updatedLease.AppliedInputProcessIds.Clear();
                }

                if (outputTargetChanged || inputTargetChanged)
                {
                    updatedLease.CompletionOverlayShown = false;
                }

                synchronized[leaseKey] = updatedLease;
            }

            return synchronized;
        }

        internal static Dictionary<string, RoutineAppOutputLease> ReconcileRoutineAppOutputLeases(
            IReadOnlyDictionary<string, RoutineAppOutputLease> currentLeases,
            IReadOnlyList<AudioRoutine> watchedRoutines,
            IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots)
        {
            return ReconcileRoutineAppOutputLeases(
                currentLeases,
                watchedRoutines,
                CreateRoutineAppStartSnapshotSet(processSnapshots));
        }

        internal static Dictionary<string, RoutineAppOutputLease> ReconcileRoutineAppOutputLeases(
            IReadOnlyDictionary<string, RoutineAppOutputLease> currentLeases,
            IReadOnlyList<AudioRoutine> watchedRoutines,
            RoutineAppStartSnapshotSet processSnapshotSet)
        {
            Dictionary<string, RoutineAppOutputLease> synchronized = SynchronizeRoutineAppOutputLeasesWithWatchedRoutines(currentLeases, watchedRoutines);
            var reconciled = new Dictionary<string, RoutineAppOutputLease>(StringComparer.OrdinalIgnoreCase);

            foreach ((string leaseKey, RoutineAppOutputLease lease) in synchronized)
            {
                if (IsRoutineAppOutputLeaseAlive(lease.RootProcessId, lease.TriggerAppPath, processSnapshotSet))
                {
                    reconciled[leaseKey] = lease;
                }
            }

            return reconciled;
        }

        internal static bool IsRoutineAppOutputLeaseAlive(
            int rootProcessId,
            string triggerAppPath,
            IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots)
        {
            return IsRoutineAppOutputLeaseAlive(
                rootProcessId,
                triggerAppPath,
                CreateRoutineAppStartSnapshotSet(processSnapshots));
        }

        internal static bool IsRoutineAppOutputLeaseAlive(
            int rootProcessId,
            string triggerAppPath,
            RoutineAppStartSnapshotSet processSnapshotSet)
        {
            string normalizedTriggerPath = RoutineTriggerPathHelper.NormalizeTriggerTarget(triggerAppPath);
            if (rootProcessId <= 0 || !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(normalizedTriggerPath))
            {
                return false;
            }

            bool rootProcessAlive = DoesRoutineAppLeaseRootMatchTriggerTarget(
                rootProcessId,
                normalizedTriggerPath,
                processSnapshotSet.SnapshotsByPid);

            return processSnapshotSet.Snapshots.Any(snapshot =>
                IsRoutineAppOutputCandidateProcess(
                    snapshot.ProcessId,
                    rootProcessId,
                    processSnapshotSet.SnapshotsByPid,
                    rootProcessAlive));
        }

        internal static IReadOnlyList<uint> CollectRoutineAppOutputCandidateProcessIds(
            int rootProcessId,
            string triggerAppPath,
            IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots,
            IReadOnlyList<AudioSessionSnapshot> sessionSnapshots)
        {
            return CollectRoutineAppOutputCandidateProcessIds(
                rootProcessId,
                triggerAppPath,
                CreateRoutineAppStartSnapshotSet(processSnapshots),
                sessionSnapshots);
        }

        internal static IReadOnlyList<uint> CollectRoutineAppOutputCandidateProcessIds(
            int rootProcessId,
            string triggerAppPath,
            RoutineAppStartSnapshotSet processSnapshotSet,
            IReadOnlyList<AudioSessionSnapshot> sessionSnapshots)
        {
            string normalizedTriggerPath = RoutineTriggerPathHelper.NormalizeTriggerTarget(triggerAppPath);
            if (rootProcessId <= 0 || !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(normalizedTriggerPath))
            {
                return [];
            }

            bool rootProcessAlive = DoesRoutineAppLeaseRootMatchTriggerTarget(
                rootProcessId,
                normalizedTriggerPath,
                processSnapshotSet.SnapshotsByPid);
            var candidateProcessIds = new HashSet<uint>();
            if (rootProcessAlive)
            {
                candidateProcessIds.Add((uint)rootProcessId);
            }

            foreach (RoutineAppStartProcessSnapshot snapshot in processSnapshotSet.Snapshots)
            {
                if (IsRoutineAppOutputCandidateProcess(snapshot.ProcessId, rootProcessId, processSnapshotSet.SnapshotsByPid, rootProcessAlive))
                {
                    candidateProcessIds.Add((uint)snapshot.ProcessId);
                }
            }

            foreach (AudioSessionSnapshot sessionSnapshot in sessionSnapshots)
            {
                if (!sessionSnapshot.ProcessId.HasValue || sessionSnapshot.ProcessId.Value == 0 || sessionSnapshot.ProcessId.Value > int.MaxValue)
                {
                    continue;
                }

                int sessionProcessId = (int)sessionSnapshot.ProcessId.Value;
                if (IsRoutineAppOutputCandidateProcess(
                    sessionProcessId,
                    rootProcessId,
                    processSnapshotSet.SnapshotsByPid,
                    rootProcessAlive))
                {
                    candidateProcessIds.Add((uint)sessionProcessId);
                }
            }

            return [.. candidateProcessIds.OrderBy(static processId => processId)];
        }

        internal static RoutineAppStartSnapshotSet CreateRoutineAppStartSnapshotSet(
            IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots)
        {
            ArgumentNullException.ThrowIfNull(processSnapshots);

            var snapshotsByPid = new Dictionary<int, RoutineAppStartProcessSnapshot>();
            foreach (RoutineAppStartProcessSnapshot snapshot in processSnapshots)
            {
                if (snapshot.ProcessId > 0)
                {
                    snapshotsByPid[snapshot.ProcessId] = snapshot;
                }
            }

            return new RoutineAppStartSnapshotSet(processSnapshots, snapshotsByPid);
        }

        internal static int? FindRunningRoutineTriggerProcessId(
            AudioRoutine routine,
            IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots)
        {
            if (routine == null || !routine.HasAppStartTrigger)
            {
                return null;
            }

            if (RoutineTriggerPathHelper.LooksLikeExecutablePath(routine.TriggerAppPath))
            {
                string normalizedTriggerPath = RoutineTriggerPathHelper.NormalizeExecutablePath(routine.TriggerAppPath);
                if (string.IsNullOrWhiteSpace(normalizedTriggerPath))
                {
                    return null;
                }

                return processSnapshots
                    .Where(snapshot =>
                        snapshot.ProcessId > 0 &&
                        RoutineTriggerPathHelper.IsExecutablePathMatch(snapshot.ExecutablePath, normalizedTriggerPath))
                    .OrderBy(static snapshot => snapshot.ProcessId)
                    .Select(static snapshot => (int?)snapshot.ProcessId)
                    .FirstOrDefault();
            }

            if (RoutineTriggerPathHelper.LooksLikePackagedAppId(routine.TriggerAppPath))
            {
                return processSnapshots
                    .Where(snapshot =>
                        snapshot.ProcessId > 0 &&
                        RoutineTriggerPathHelper.IsPackagedAppMatch(routine.TriggerAppPath, snapshot.AppUserModelId))
                    .OrderBy(static snapshot => snapshot.ProcessId)
                    .Select(static snapshot => (int?)snapshot.ProcessId)
                    .FirstOrDefault();
            }

            return null;
        }

        internal static IReadOnlyList<int> FindRunningProcessIdsForTriggerTarget(
            string triggerTarget,
            IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots)
        {
            string normalizedTriggerTarget = RoutineTriggerPathHelper.NormalizeTriggerTarget(triggerTarget);
            if (!RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(normalizedTriggerTarget))
            {
                return [];
            }

            return
            [
                .. processSnapshots
                    .Where(snapshot => snapshot.ProcessId > 0 && IsRoutineAppDirectMatch(normalizedTriggerTarget, snapshot))
                    .OrderBy(static snapshot => snapshot.ProcessId)
                    .Select(static snapshot => snapshot.ProcessId)
            ];
        }

        private static bool IsRoutineAppOutputCandidateProcess(
            int processId,
            int rootProcessId,
            Dictionary<int, RoutineAppStartProcessSnapshot> snapshotsByPid,
            bool rootProcessAlive)
        {
            if (processId <= 0)
            {
                return false;
            }

            if (processId == rootProcessId)
            {
                return rootProcessAlive;
            }

            if (!rootProcessAlive)
            {
                return false;
            }

            return IsDescendantProcess(processId, rootProcessId, snapshotsByPid);
        }

        private static bool DoesRoutineAppLeaseRootMatchTriggerTarget(
            int rootProcessId,
            string triggerAppPath,
            Dictionary<int, RoutineAppStartProcessSnapshot> snapshotsByPid)
        {
            if (rootProcessId <= 0 || !snapshotsByPid.TryGetValue(rootProcessId, out RoutineAppStartProcessSnapshot rootSnapshot))
            {
                return false;
            }

            return IsRoutineAppDirectMatch(triggerAppPath, rootSnapshot);
        }

        private static bool IsRoutineAppDirectMatch(
            string triggerTarget,
            RoutineAppStartProcessSnapshot processSnapshot)
        {
            if (RoutineTriggerPathHelper.LooksLikeExecutablePath(triggerTarget))
            {
                return RoutineTriggerPathHelper.IsExecutablePathMatch(processSnapshot.ExecutablePath, triggerTarget);
            }

            if (RoutineTriggerPathHelper.LooksLikePackagedAppId(triggerTarget))
            {
                return RoutineTriggerPathHelper.IsPackagedAppMatch(triggerTarget, processSnapshot.AppUserModelId);
            }

            return false;
        }

        private static bool IsDescendantProcess(
            int processId,
            int rootProcessId,
            Dictionary<int, RoutineAppStartProcessSnapshot> snapshotsByPid)
        {
            var visitedProcessIds = new HashSet<int> { processId };
            int currentProcessId = processId;

            for (int depth = 0; depth < 12; depth++)
            {
                int parentProcessId;
                if (snapshotsByPid.TryGetValue(currentProcessId, out RoutineAppStartProcessSnapshot snapshot) && snapshot.ParentProcessId is > 0)
                {
                    parentProcessId = snapshot.ParentProcessId.Value;
                }
                else
                {
                    parentProcessId = AudioDeviceHelper.GetParentPid(currentProcessId);
                }

                if (parentProcessId <= 0)
                {
                    return false;
                }

                if (parentProcessId == rootProcessId)
                {
                    return true;
                }

                if (!visitedProcessIds.Add(parentProcessId))
                {
                    return false;
                }

                currentProcessId = parentProcessId;
            }

            return false;
        }

        private static bool HasRoutineAppOutputLeaseCompleted(RoutineAppOutputLease lease)
        {
            bool outputCompleted = string.IsNullOrWhiteSpace(lease.OutputDeviceId) || lease.AppliedOutputProcessIds.Count > 0;
            bool inputCompleted = string.IsNullOrWhiteSpace(lease.InputDeviceId) || lease.AppliedInputProcessIds.Count > 0;
            return outputCompleted && inputCompleted;
        }

        private static void ApplyInitialLeaseProcessState(RoutineAppOutputLease lease, uint rootProcessId, bool outputApplied, bool inputApplied)
        {
            if (outputApplied && !string.IsNullOrWhiteSpace(lease.OutputDeviceId))
            {
                lease.AppliedOutputProcessIds.Add(rootProcessId);
            }

            if (inputApplied && !string.IsNullOrWhiteSpace(lease.InputDeviceId))
            {
                lease.AppliedInputProcessIds.Add(rootProcessId);
            }
        }

        private void MarkRoutineAppOutputLeaseProcessApplied(RoutineAppOutputLease expectedLease, uint processId, bool output)
        {
            lock (_routineAppStartMonitorLock)
            {
                if (_activeRoutineAppOutputLeases.TryGetValue(expectedLease.LeaseKey, out RoutineAppOutputLease? lease)
                    && AppRoutineAppStartCoordinator.DoesLiveLeaseMatchExpectedSnapshot(lease, expectedLease))
                {
                    if (output)
                    {
                        lease.AppliedOutputProcessIds.Add(processId);
                    }
                    else
                    {
                        lease.AppliedInputProcessIds.Add(processId);
                    }
                }
            }
        }

        private void MarkRoutineAppOutputLeaseOverlayShown(RoutineAppOutputLease expectedLease)
        {
            lock (_routineAppStartMonitorLock)
            {
                if (_activeRoutineAppOutputLeases.TryGetValue(expectedLease.LeaseKey, out RoutineAppOutputLease? lease)
                    && AppRoutineAppStartCoordinator.DoesLiveLeaseMatchExpectedSnapshot(lease, expectedLease))
                {
                    lease.CompletionOverlayShown = true;
                }
            }
        }

        private bool TryClaimRoutineAppStartMatch(string? routineId, int processId, RoutineAppStartProcessSnapshot processSnapshot)
        {
            string normalizedRoutineId = string.IsNullOrWhiteSpace(routineId) ? "unknown" : routineId;
            string leaseKey = CreateRoutineAppOutputLeaseKey(normalizedRoutineId, processId);

            lock (_routineAppStartMonitorLock)
            {
                if (_activeRoutineAppOutputLeases.TryGetValue(leaseKey, out RoutineAppOutputLease? existingLease))
                {
                    string normalizedTriggerPath = RoutineTriggerPathHelper.NormalizeTriggerTarget(existingLease.TriggerAppPath);
                    if (IsRoutineAppDirectMatch(normalizedTriggerPath, processSnapshot))
                    {
                        return false;
                    }

                    _activeRoutineAppOutputLeases.Remove(leaseKey);
                }

                if (_pendingRoutineAppStartRoots.Contains(leaseKey))
                {
                    return false;
                }

                _pendingRoutineAppStartRoots.Add(leaseKey);
                return true;
            }
        }

        private void ReleaseRoutineAppStartMatchClaim(string? routineId, int processId)
        {
            string normalizedRoutineId = string.IsNullOrWhiteSpace(routineId) ? "unknown" : routineId;
            string leaseKey = CreateRoutineAppOutputLeaseKey(normalizedRoutineId, processId);

            lock (_routineAppStartMonitorLock)
            {
                _pendingRoutineAppStartRoots.Remove(leaseKey);
            }
        }

        internal static bool ShouldSkipRoutineAppStartMatchForExistingLease(
            RoutineAppStartMatch match,
            RoutineAppStartProcessSnapshot processSnapshot,
            IReadOnlyList<RoutineAppOutputLease> activeLeases,
            IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots)
        {
            return ShouldSkipRoutineAppStartMatchForExistingLease(
                match,
                processSnapshot,
                activeLeases,
                CreateRoutineAppStartSnapshotSet(processSnapshots));
        }

        internal static bool ShouldSkipRoutineAppStartMatchForExistingLease(
            RoutineAppStartMatch match,
            RoutineAppStartProcessSnapshot processSnapshot,
            IReadOnlyList<RoutineAppOutputLease> activeLeases,
            RoutineAppStartSnapshotSet processSnapshotSet)
        {
            if (!match.Routine.SwitchOutputPerApp || processSnapshot.ProcessId <= 0)
            {
                return false;
            }

            if (activeLeases.Count == 0)
            {
                return false;
            }

            foreach (RoutineAppOutputLease lease in activeLeases)
            {
                if (!string.Equals(lease.RoutineId, match.Routine.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool rootProcessAlive = DoesRoutineAppLeaseRootMatchTriggerTarget(
                    lease.RootProcessId,
                    RoutineTriggerPathHelper.NormalizeTriggerTarget(lease.TriggerAppPath),
                    processSnapshotSet.SnapshotsByPid);
                if (IsRoutineAppOutputCandidateProcess(
                    processSnapshot.ProcessId,
                    lease.RootProcessId,
                    processSnapshotSet.SnapshotsByPid,
                    rootProcessAlive))
                {
                    return true;
                }
            }

            return false;
        }

        private static string CreateRoutineAppOutputLeaseKey(string routineId, int rootProcessId)
        {
            string normalizedRoutineId = string.IsNullOrWhiteSpace(routineId) ? "unknown" : routineId;
            return $"{normalizedRoutineId}:{rootProcessId}";
        }

        private static string CreateRoutineAppStartOperationId(string prefix)
        {
            return $"{prefix}:{Guid.NewGuid():N}";
        }

        private static string GetRoutineIdFromLeaseKey(string leaseKey)
        {
            if (string.IsNullOrWhiteSpace(leaseKey))
            {
                return "unknown";
            }

            int separatorIndex = leaseKey.LastIndexOf(':');
            if (separatorIndex <= 0)
            {
                return leaseKey;
            }

            return leaseKey[..separatorIndex];
        }

        private RoutineAppStartProcessSnapshot? TryCaptureProcessSnapshot(
            int processId,
            RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId)
        {
            return _routineProcessSnapshotProvider.TryCapture(processId, options);
        }

        private List<RoutineAppStartProcessSnapshot> CaptureProcessSnapshots(
            RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full)
        {
            return _routineProcessSnapshotProvider.CaptureAll(options);
        }

        private static RoutineProcessSnapshotCaptureOptions GetCaptureOptionsForTriggerTarget(string? triggerTarget)
        {
            return RoutineTriggerPathHelper.LooksLikePackagedAppId(triggerTarget)
                ? RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId
                : RoutineProcessSnapshotCaptureOptions.None;
        }

        private static RoutineProcessSnapshotCaptureOptions GetCaptureOptionsForTriggerTargets(IEnumerable<string> triggerTargets)
        {
            var options = RoutineProcessSnapshotCaptureOptions.None;
            foreach (string? triggerTarget in triggerTargets)
            {
                if (RoutineTriggerPathHelper.LooksLikePackagedAppId(triggerTarget))
                {
                    options |= RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId;
                }
            }

            return options;
        }
    }
}
