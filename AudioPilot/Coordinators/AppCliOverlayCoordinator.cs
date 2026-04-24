using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace AudioPilot.Coordinators
{
    internal sealed class AppCliOverlayCoordinator(
        AudioDeviceService audio,
        OverlayService overlay,
        MediaOverlayCommandService mediaOverlayCommands,
        Logger logger,
        Func<Settings?> currentSettingsProvider,
        Func<bool>? mediaPlayPauseCommand = null,
        Func<bool>? mediaNextTrackCommand = null,
        Func<bool>? mediaPreviousTrackCommand = null,
        Func<Task<bool>>? mediaPlayPauseCommandAsync = null,
        Func<Task<bool>>? mediaNextTrackCommandAsync = null,
        Func<Task<bool>>? mediaPreviousTrackCommandAsync = null,
        Action<ExecutionHistoryEntry>? mediaHistoryRecorder = null)
    {
        private int _mediaOverlayCaptureInFlight;
        private int _latestMediaOverlayRequestVersion;
        private readonly Lock _pendingMediaOverlayLock = new();
        private readonly SemaphoreSlim _mediaCommandSendGate = new(1, 1);
        private PendingMediaOverlayCapture? _pendingMediaOverlayCapture;
        private readonly Func<Task<bool>> _mediaPlayPauseCommand = ResolveMediaCommand(mediaPlayPauseCommandAsync, mediaPlayPauseCommand, MediaKeyHelper.TryPressPlayPauseAsync);
        private readonly Func<Task<bool>> _mediaNextTrackCommand = ResolveMediaCommand(mediaNextTrackCommandAsync, mediaNextTrackCommand, MediaKeyHelper.TryPressNextTrackAsync);
        private readonly Func<Task<bool>> _mediaPreviousTrackCommand = ResolveMediaCommand(mediaPreviousTrackCommandAsync, mediaPreviousTrackCommand, MediaKeyHelper.TryPressPreviousTrackAsync);
        private const string CliSource = "cli";
        private const string HotkeySource = "hotkey";

        private readonly record struct PendingMediaOverlayCapture(
            MediaOverlayCommand Command,
            Func<Task<bool>> SendCommandAsync,
            int RequestVersion,
            string Source);

        internal bool IsMediaOverlayCaptureInFlightForTests => Volatile.Read(ref _mediaOverlayCaptureInFlight) != 0;

        public void MediaPlayPause(string source = CliSource)
        {
            StartMediaCommand(MediaOverlayCommand.PlayPause, _mediaPlayPauseCommand, NormalizeMediaCommandSource(source));
        }

        public void MediaNextTrack(string source = CliSource)
        {
            StartMediaCommand(MediaOverlayCommand.NextTrack, _mediaNextTrackCommand, NormalizeMediaCommandSource(source));
        }

        public void MediaPreviousTrack(string source = CliSource)
        {
            StartMediaCommand(MediaOverlayCommand.PreviousTrack, _mediaPreviousTrackCommand, NormalizeMediaCommandSource(source));
        }

        public void ShowCurrentTrack()
        {
            int requestVersion = Interlocked.Increment(ref _latestMediaOverlayRequestVersion);
            if (Interlocked.CompareExchange(ref _mediaOverlayCaptureInFlight, 1, 0) != 0)
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.Trace("AppCliOverlayCoordinator", () => "media-overlay-capture-skipped | command=show-current-track reason=capture-in-flight");
                }

                return;
            }

            _ = ShowCurrentTrackOverlayAsync(requestVersion);
        }

        public Task<MediaOverlaySessionSnapshot> GetCurrentMediaSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return mediaOverlayCommands.GetCurrentMediaSnapshotAsync(cancellationToken);
        }

        public static MediaOverlayResult BuildCurrentMediaOverlayResult(MediaOverlaySessionSnapshot snapshot)
        {
            if (MediaOverlayEngine.IsSessionMissing(snapshot) || !MediaOverlayEngine.HasTrackData(snapshot))
            {
                return MediaOverlayResult.Plain("No current track");
            }

            string header = snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
                ? "Current track paused"
                : "Current track";
            string title = string.IsNullOrWhiteSpace(snapshot.Title) ? "Unknown title" : snapshot.Title;

            return MediaOverlayResult.Track(header, title, snapshot.Artist);
        }

        public static MediaOverlayResult BuildTrailingMediaOverlayResult(MediaOverlayCommand command, MediaOverlaySessionSnapshot snapshot)
        {
            if (MediaOverlayEngine.IsSessionMissing(snapshot) || !MediaOverlayEngine.HasTrackData(snapshot))
            {
                return BuildImmediateMediaCommandAcknowledgement(command);
            }

            string title = string.IsNullOrWhiteSpace(snapshot.Title) ? "Unknown title" : snapshot.Title;
            string header = command switch
            {
                MediaOverlayCommand.NextTrack => "Next track",
                MediaOverlayCommand.PreviousTrack => "Previous track",
                MediaOverlayCommand.PlayPause when snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "Playback paused",
                MediaOverlayCommand.PlayPause when snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "Playback resumed",
                MediaOverlayCommand.PlayPause => "Play/pause command sent",
                _ => "Media command sent",
            };

            return MediaOverlayResult.Track(header, title, snapshot.Artist);
        }

        public bool ToggleMuteMic(Func<bool> currentValueProvider, Action<bool> applyMuteMic)
        {
            return ToggleState(currentValueProvider, applyMuteMic, "Microphone muted", "Microphone unmuted");
        }

        public bool SetMuteMic(bool enabled, Action<bool> applyMuteMic)
        {
            return SetState(enabled, applyMuteMic, "Microphone muted", "Microphone unmuted");
        }

        public bool ToggleMuteSound(Func<bool> currentValueProvider, Action<bool> applyMuteSound)
        {
            return ToggleState(currentValueProvider, applyMuteSound, "Sound muted", "Sound unmuted");
        }

        public bool SetMuteSound(bool enabled, Action<bool> applyMuteSound)
        {
            return SetState(enabled, applyMuteSound, "Sound muted", "Sound unmuted");
        }

        public bool ToggleDeafen(Func<bool> currentValueProvider, Action<bool> applyDeafen)
        {
            return ToggleState(currentValueProvider, applyDeafen, "Deafened", "Undeafened");
        }

        public bool SetDeafen(bool enabled, Action<bool> applyDeafen)
        {
            return SetState(enabled, applyDeafen, "Deafened", "Undeafened");
        }

        public bool StepMasterVolume(bool increase)
        {
            return StepEndpointVolume(
                getDevice: () => audio.GetDefaultPlaybackDevice(),
                stepPercent: GetConfiguredVolumeStepPercent(playback: true),
                increase,
                reason: "hotkey-volume:master",
                overlayLabel: "Master volume");
        }

        public bool StepMicVolume(bool increase)
        {
            return StepEndpointVolume(
                getDevice: () => audio.GetDefaultRecordingDevice(),
                stepPercent: GetConfiguredVolumeStepPercent(playback: false),
                increase,
                reason: "hotkey-volume:recording",
                overlayLabel: "Microphone volume");
        }

        /// <summary>
        /// Toggles the Windows "Listen to this device" state for the current default input endpoint.
        /// </summary>
        /// <remarks>
        /// This operation reflects endpoint-level listen state and is independent from output/input cycle switching.
        /// </remarks>
        public bool ToggleListenToInput()
        {
            Settings? currentSettings = currentSettingsProvider();
            string preferredMonitorOutputDeviceId = currentSettings?.Hotkeys.Listen.MonitorOutputDeviceId ?? string.Empty;
            string preferredMonitorOutputDeviceName = currentSettings?.Hotkeys.Listen.MonitorOutputDeviceName ?? string.Empty;
            if (!audio.TryToggleCurrentInputListenState(preferredMonitorOutputDeviceId, preferredMonitorOutputDeviceName, out bool enabled, out string? error))
            {
                logger.Warning("AppCliOverlayCoordinator", () => $"{AppConstants.Audio.LogEvents.Listen.ToggleFailed} | error={error ?? "unknown"}");
                return false;
            }

            ShowListenToInputOverlay(enabled);
            return true;
        }

        /// <summary>
        /// Sets the Windows "Listen to this device" state for the current default input endpoint.
        /// </summary>
        /// <remarks>
        /// This call is idempotent for CLI use: requesting an already-applied state is treated as success.
        /// </remarks>
        public bool SetListenToInput(bool enabled)
        {
            Settings? currentSettings = currentSettingsProvider();
            string preferredMonitorOutputDeviceId = currentSettings?.Hotkeys.Listen.MonitorOutputDeviceId ?? string.Empty;
            string preferredMonitorOutputDeviceName = currentSettings?.Hotkeys.Listen.MonitorOutputDeviceName ?? string.Empty;
            if (!audio.TrySetCurrentInputListenState(enabled, preferredMonitorOutputDeviceId, preferredMonitorOutputDeviceName, out _, out string? error))
            {
                logger.Warning("AppCliOverlayCoordinator", () => $"{AppConstants.Audio.LogEvents.Listen.SetFailed} | target={enabled} error={error ?? "unknown"}");
                return false;
            }

            ShowListenToInputOverlay(enabled);
            return true;
        }

        internal static string GetListenToInputOverlayHeader(bool enabled)
        {
            return enabled ? "Input listen enabled" : "Input listen disabled";
        }

        internal static string NormalizeListenToInputOverlayDeviceName(string? friendlyName)
        {
            return string.IsNullOrWhiteSpace(friendlyName)
                ? "Current input device"
                : friendlyName;
        }

        internal static string ComposeListenToInputOverlayDeviceText(bool enabled, string inputDeviceName, string? monitorTargetOutputDeviceName)
        {
            if (!enabled)
            {
                return inputDeviceName;
            }

            string outputTarget = string.IsNullOrWhiteSpace(monitorTargetOutputDeviceName)
                ? "Default output"
                : monitorTargetOutputDeviceName;

            return $"{inputDeviceName}\nTo: {outputTarget}";
        }

        internal static float ComputeSteppedVolumePercent(float currentPercent, int stepPercent, bool increase)
        {
            float normalizedCurrent = Math.Clamp(currentPercent, 0f, 100f);
            int normalizedStep = NormalizeVolumeStepPercent(stepPercent);
            float delta = increase ? normalizedStep : -normalizedStep;
            return Math.Clamp(normalizedCurrent + delta, 0f, 100f);
        }

        internal static string BuildVolumeOverlayMessage(string label, float resultingPercent)
        {
            int roundedPercent = (int)Math.Round(Math.Clamp(resultingPercent, 0f, 100f), MidpointRounding.AwayFromZero);
            return $"{label} {roundedPercent}%";
        }

        internal static bool TryGetEndpointVolumeState(Logger logger, MMDevice? device, string reason, out float currentPercent, out bool muted)
        {
            currentPercent = 0f;
            muted = false;

            if (device == null)
            {
                return false;
            }

            if (!AudioDeviceHelper.TryGetEndpointVolume(logger, device, out var endpointVolume, reason))
            {
                return false;
            }

            currentPercent = Math.Clamp(endpointVolume.MasterVolumeLevelScalar * 100f, 0f, 100f);
            muted = endpointVolume.Mute;
            return true;
        }

        internal static bool TryApplyEndpointVolume(Logger logger, MMDevice? device, float targetPercent, string reason, bool muteAtZero, bool unmuteAboveZero, out float appliedPercent)
        {
            appliedPercent = 0f;
            if (device == null)
            {
                return false;
            }

            if (!AudioDeviceHelper.TryGetEndpointVolume(logger, device, out var endpointVolume, reason))
            {
                return false;
            }

            bool applied = TryApplyEndpointVolume(endpointVolume, targetPercent, muteAtZero, unmuteAboveZero, out appliedPercent);

            return applied;
        }

        internal static bool TryApplyEndpointVolume(AudioEndpointVolume endpointVolume, float targetPercent, bool muteAtZero, bool unmuteAboveZero, out float appliedPercent)
        {
            appliedPercent = Math.Clamp(targetPercent, 0f, 100f);
            endpointVolume.MasterVolumeLevelScalar = appliedPercent / 100f;

            if (muteAtZero && appliedPercent <= 0f)
            {
                endpointVolume.Mute = true;
            }
            else if (unmuteAboveZero && appliedPercent > 0f)
            {
                endpointVolume.Mute = false;
            }

            return true;
        }

        internal static OverlayActionStateKind GetVolumeOverlayStateKind(bool increase)
        {
            return increase ? OverlayActionStateKind.Enabled : OverlayActionStateKind.Disabled;
        }

        internal static int NormalizeVolumeStepPercent(int stepPercent)
        {
            return stepPercent < 1 ? 5 : Math.Clamp(stepPercent, 1, 100);
        }

        private void StartMediaCommand(MediaOverlayCommand command, Func<Task<bool>> sendCommandAsync, string source)
        {
            int requestVersion = Interlocked.Increment(ref _latestMediaOverlayRequestVersion);
            if (Interlocked.CompareExchange(ref _mediaOverlayCaptureInFlight, 1, 0) != 0)
            {
                _ = SendMediaCommandWithoutCaptureAsync(command, sendCommandAsync, requestVersion, source);
                return;
            }

            _ = ShowMediaOverlayAsync(command, sendCommandAsync, requestVersion, source);
        }

        private async Task SendMediaCommandWithoutCaptureAsync(MediaOverlayCommand command, Func<Task<bool>> sendCommandAsync, int requestVersion, string source)
        {
            long started = Stopwatch.GetTimestamp();
            if (!await SendMediaCommandSerializedAsync(command, sendCommandAsync).ConfigureAwait(false))
            {
                MediaOverlayResult failureResult = MediaOverlayEngine.BuildCommandSendFailureResult(command);
                ApplyMediaOverlayResult(failureResult);
                RecordMediaCommandHistory(
                    command,
                    failureResult,
                    source,
                    success: false,
                    skipped: false,
                    diagCode: "media-command-send-failed",
                    reason: "Media command send failed.",
                    elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    details: new Dictionary<string, string>
                    {
                        ["overlayCapture"] = "in-flight",
                        ["fallback"] = "send-only",
                    });
                return;
            }

            QueuePendingMediaOverlayCapture(command, sendCommandAsync, requestVersion, source);
            ApplyMediaOverlayResult(BuildImmediateMediaCommandAcknowledgement(command));

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.Trace("AppCliOverlayCoordinator", () => $"media-overlay-capture-deferred | command={command} reason=capture-in-flight");
            }
        }

        private async Task ShowMediaOverlayAsync(MediaOverlayCommand command, Func<Task<bool>> sendCommandAsync, int requestVersion, string source)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                MediaOverlayCommandResult commandResult = await mediaOverlayCommands.SendWithDetailedResultAsync(
                    command,
                    () => SendMediaCommandSerializedAsync(command, sendCommandAsync));
                MediaOverlayResult mediaOverlay = commandResult.Overlay;
                if (requestVersion != Volatile.Read(ref _latestMediaOverlayRequestVersion))
                {
                    if (!HasPendingMediaOverlayCaptureForLatestVersion())
                    {
                        RecordMediaCommandHistory(
                            command,
                            mediaOverlay,
                            source,
                            success: true,
                            skipped: true,
                            diagCode: "media-overlay-stale-suppressed",
                            reason: "A newer media command superseded this overlay result.",
                            elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                            details: BuildMediaCommandDetails(commandResult, new Dictionary<string, string>
                            {
                                ["overlayCapture"] = "superseded",
                            }));
                    }

                    return;
                }

                ApplyMediaOverlayResult(mediaOverlay);
                RecordMediaCommandHistory(
                    command,
                    mediaOverlay,
                    source,
                    success: !IsCommandSendFailure(command, mediaOverlay),
                    skipped: mediaOverlay.Kind == MediaOverlayResultKind.Hidden,
                    diagCode: commandResult.DiagCode,
                    reason: GetMediaOverlayHistoryReason(command, mediaOverlay),
                    elapsedMs: commandResult.ElapsedMs ?? Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    details: BuildMediaCommandDetails(commandResult));
            }
            catch (Exception ex)
            {
                logger.Warning("AppCliOverlayCoordinator", "Failed to resolve media session state for overlay", nameof(ShowMediaOverlayAsync), ex);
                RecordMediaCommandHistory(
                    command,
                    MediaOverlayResult.Hidden,
                    source,
                    success: false,
                    skipped: false,
                    diagCode: "media-overlay-resolution-failed",
                    reason: $"Overlay resolution failed with {ex.GetType().Name}.",
                    elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    details: new Dictionary<string, string>
                    {
                        ["exceptionType"] = ex.GetType().Name,
                    });
                if (command == MediaOverlayCommand.NextTrack)
                {
                    overlay.Show("Next track unknown");
                }
                else if (command == MediaOverlayCommand.PreviousTrack)
                {
                    overlay.Show("Previous track unknown");
                }
            }
            finally
            {
                Interlocked.Exchange(ref _mediaOverlayCaptureInFlight, 0);
                StartPendingMediaOverlayCaptureIfNeeded();
            }
        }

        private async Task<bool> SendMediaCommandSerializedAsync(MediaOverlayCommand command, Func<Task<bool>> sendCommandAsync)
        {
            await _mediaCommandSendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await MediaOverlayEngine.TrySendCommandAsync(sendCommandAsync, command).ConfigureAwait(false);
            }
            finally
            {
                _mediaCommandSendGate.Release();
            }
        }

        private void QueuePendingMediaOverlayCapture(MediaOverlayCommand command, Func<Task<bool>> sendCommandAsync, int requestVersion, string source)
        {
            lock (_pendingMediaOverlayLock)
            {
                _pendingMediaOverlayCapture = new PendingMediaOverlayCapture(command, sendCommandAsync, requestVersion, source);
            }
        }

        private bool HasPendingMediaOverlayCaptureForLatestVersion()
        {
            lock (_pendingMediaOverlayLock)
            {
                return _pendingMediaOverlayCapture is { } pending
                    && pending.RequestVersion == Volatile.Read(ref _latestMediaOverlayRequestVersion);
            }
        }

        private void StartPendingMediaOverlayCaptureIfNeeded()
        {
            PendingMediaOverlayCapture? pending;
            lock (_pendingMediaOverlayLock)
            {
                pending = _pendingMediaOverlayCapture;
                _pendingMediaOverlayCapture = null;
            }

            if (pending is not { } capture)
            {
                return;
            }

            if (capture.RequestVersion != Volatile.Read(ref _latestMediaOverlayRequestVersion))
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _mediaOverlayCaptureInFlight, 1, 0) != 0)
            {
                QueuePendingMediaOverlayCapture(capture.Command, capture.SendCommandAsync, capture.RequestVersion, capture.Source);
                return;
            }

            _ = ShowCurrentMediaStateOverlayAsync(capture.Command, capture.RequestVersion, capture.Source);
        }

        private async Task ShowCurrentMediaStateOverlayAsync(MediaOverlayCommand command, int requestVersion, string source)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                MediaOverlaySessionSnapshot snapshot = await mediaOverlayCommands.GetCurrentMediaSnapshotAsync();
                if (requestVersion != Volatile.Read(ref _latestMediaOverlayRequestVersion))
                {
                    RecordMediaCommandHistory(
                        command,
                        MediaOverlayResult.Hidden,
                        source,
                        success: true,
                        skipped: true,
                        diagCode: "media-overlay-trailing-stale-suppressed",
                        reason: "A newer media command superseded this trailing overlay result.",
                        elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        details: new Dictionary<string, string>
                        {
                            ["overlayCapture"] = "trailing-superseded",
                        });
                    return;
                }

                MediaOverlayResult mediaOverlay = BuildTrailingMediaOverlayResult(command, snapshot);
                ApplyMediaOverlayResult(mediaOverlay);
                RecordMediaCommandHistory(
                    command,
                    mediaOverlay,
                    source,
                    success: true,
                    skipped: mediaOverlay.Kind == MediaOverlayResultKind.Hidden,
                    diagCode: mediaOverlay.Kind == MediaOverlayResultKind.TrackMessage
                        ? "media-overlay-trailing-track"
                        : "media-overlay-trailing-fallback",
                    reason: mediaOverlay.Kind == MediaOverlayResultKind.PlainMessage ? mediaOverlay.Message : null,
                    elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    details: new Dictionary<string, string>
                    {
                        ["overlayCapture"] = "trailing",
                    });
            }
            catch (Exception ex)
            {
                logger.Warning("AppCliOverlayCoordinator", "Failed to resolve trailing media state for overlay", nameof(ShowCurrentMediaStateOverlayAsync), ex);
            }
            finally
            {
                Interlocked.Exchange(ref _mediaOverlayCaptureInFlight, 0);
                StartPendingMediaOverlayCaptureIfNeeded();
            }
        }

        private void RecordMediaCommandHistory(MediaOverlayCommand command, MediaOverlayResult result, string source, bool success, bool skipped, string diagCode, string? reason, double elapsedMs, IReadOnlyDictionary<string, string>? details = null)
        {
            if (mediaHistoryRecorder == null)
            {
                return;
            }

            Dictionary<string, string> mergedDetails = new(StringComparer.OrdinalIgnoreCase)
            {
                ["command"] = command.ToString(),
                ["resultKind"] = result.Kind.ToString(),
                ["source"] = source,
            };

            if (details != null)
            {
                foreach (KeyValuePair<string, string> detail in details)
                {
                    if (!string.IsNullOrWhiteSpace(detail.Key) && detail.Value != null)
                    {
                        mergedDetails[detail.Key] = detail.Value;
                    }
                }
            }

            string action = command switch
            {
                MediaOverlayCommand.PlayPause => "media-play-pause",
                MediaOverlayCommand.NextTrack => "media-next-track",
                MediaOverlayCommand.PreviousTrack => "media-previous-track",
                _ => "media-command",
            };
            string summary = BuildMediaHistorySummary(command, result, success, skipped);

            mediaHistoryRecorder(new ExecutionHistoryEntry(
                OpId: $"media-{command.ToString().ToLowerInvariant()}:{Guid.NewGuid():N}",
                TimestampUtc: DateTimeOffset.UtcNow,
                Kind: ExecutionHistoryKind.Media,
                Source: source,
                Action: action,
                Success: success,
                Skipped: skipped,
                Summary: summary,
                Reason: reason,
                Target: command.ToString(),
                DiagCode: diagCode,
                ElapsedMs: elapsedMs,
                Details: mergedDetails));
        }

        private static Dictionary<string, string> BuildMediaCommandDetails(
            MediaOverlayCommandResult commandResult,
            IReadOnlyDictionary<string, string>? extraDetails = null)
        {
            Dictionary<string, string> details = new(StringComparer.OrdinalIgnoreCase)
            {
                ["commandDiagCode"] = commandResult.DiagCode,
            };

            if (commandResult.TrackNavigationDiagnostics is { } trackDiagnostics)
            {
                details["finalPhase"] = trackDiagnostics.FinalPhase;
                details["outcome"] = trackDiagnostics.Outcome;
                details["finalChangeKind"] = trackDiagnostics.FinalChangeKind;
                details["finalFallbackClassification"] = trackDiagnostics.FinalFallbackClassification;
                details["sawSessionDrop"] = FormatBool(trackDiagnostics.SawSessionDrop);
                details["usedSessionDropRecovery"] = FormatBool(trackDiagnostics.UsedSessionDropRecovery);
                details["usedLateTrackLoadRecovery"] = FormatBool(trackDiagnostics.UsedLateTrackLoadRecovery);
                details["usedRecoveredAlternateSource"] = FormatBool(trackDiagnostics.UsedRecoveredAlternateSource);
                details["sameSourceConflictObserved"] = FormatBool(trackDiagnostics.SameSourceConflictObserved);
                details["sameSourceConflictActive"] = FormatBool(trackDiagnostics.SameSourceConflictActive);
                details["sameSourceDistinctCandidateCount"] = FormatInt(trackDiagnostics.SameSourceDistinctCandidateCount);
                details["sameSourceActiveRivalCount"] = FormatInt(trackDiagnostics.SameSourceActiveRivalCount);
                details["sameSourceReinforcedRivalCount"] = FormatInt(trackDiagnostics.SameSourceReinforcedRivalCount);
                details["sameSourceStaleRivalCount"] = FormatInt(trackDiagnostics.SameSourceStaleRivalCount);
            }

            if (commandResult.PlayPauseDiagnostics is { } playPauseDiagnostics)
            {
                details["finalPath"] = playPauseDiagnostics.FinalPath;
                details["outcome"] = playPauseDiagnostics.Outcome;
                details["usedEventAssist"] = FormatBool(playPauseDiagnostics.UsedEventAssist);
                details["usedChangedBySourceSnapshots"] = FormatBool(playPauseDiagnostics.UsedChangedBySourceSnapshots);
                details["usedImmediateCurrentEvidence"] = FormatBool(playPauseDiagnostics.UsedImmediateCurrentEvidence);
                details["reusedBaselineMetadata"] = FormatBool(playPauseDiagnostics.ReusedBaselineMetadata);
            }

            if (extraDetails != null)
            {
                foreach (KeyValuePair<string, string> detail in extraDetails)
                {
                    if (!string.IsNullOrWhiteSpace(detail.Key) && detail.Value != null)
                    {
                        details[detail.Key] = detail.Value;
                    }
                }
            }

            return details;
        }

        private static string BuildMediaHistorySummary(MediaOverlayCommand command, MediaOverlayResult result, bool success, bool skipped)
        {
            if (!success)
            {
                return $"{GetMediaCommandLabel(command)} media command failed.";
            }

            if (skipped && result.Kind == MediaOverlayResultKind.Hidden)
            {
                return $"{GetMediaCommandLabel(command)} media command sent without an overlay.";
            }

            if (result.Kind == MediaOverlayResultKind.TrackMessage)
            {
                return $"{GetMediaCommandLabel(command)} media command resolved to updated track metadata.";
            }

            if (result.Kind == MediaOverlayResultKind.Hidden)
            {
                return $"{GetMediaCommandLabel(command)} media command completed with no visible overlay.";
            }

            return $"{GetMediaCommandLabel(command)} media command completed.";
        }

        private static string GetMediaCommandLabel(MediaOverlayCommand command)
        {
            return command switch
            {
                MediaOverlayCommand.PlayPause => "Play/Pause",
                MediaOverlayCommand.NextTrack => "Next Track",
                MediaOverlayCommand.PreviousTrack => "Previous Track",
                _ => "Media",
            };
        }

        private static string? GetMediaOverlayHistoryReason(MediaOverlayCommand command, MediaOverlayResult result)
        {
            if (IsCommandSendFailure(command, result))
            {
                return "Media command send failed.";
            }

            return result.Kind switch
            {
                MediaOverlayResultKind.Hidden => "No visible media overlay was available.",
                MediaOverlayResultKind.PlainMessage when !string.IsNullOrWhiteSpace(result.Message) => result.Message,
                _ => null,
            };
        }

        private static bool IsCommandSendFailure(MediaOverlayCommand command, MediaOverlayResult result)
        {
            string? message = result.Message;
            if (result.Kind != MediaOverlayResultKind.PlainMessage || string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return command switch
            {
                MediaOverlayCommand.PlayPause => string.Equals(message, "Play/pause failed", StringComparison.Ordinal),
                MediaOverlayCommand.NextTrack => string.Equals(message, "Next track failed", StringComparison.Ordinal),
                MediaOverlayCommand.PreviousTrack => string.Equals(message, "Previous track failed", StringComparison.Ordinal),
                _ => string.Equals(message, "Media command failed", StringComparison.Ordinal),
            };
        }

        private static MediaOverlayResult BuildImmediateMediaCommandAcknowledgement(MediaOverlayCommand command)
        {
            return MediaOverlayResult.Plain(command switch
            {
                MediaOverlayCommand.PlayPause => "Play/pause command sent",
                MediaOverlayCommand.NextTrack => "Next track",
                MediaOverlayCommand.PreviousTrack => "Previous track",
                _ => "Media command sent",
            });
        }

        private static string NormalizeMediaCommandSource(string? source)
        {
            return string.Equals(source, HotkeySource, StringComparison.OrdinalIgnoreCase)
                ? HotkeySource
                : CliSource;
        }

        private static string FormatBool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string FormatInt(int value)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private async Task ShowCurrentTrackOverlayAsync(int requestVersion)
        {
            try
            {
                MediaOverlaySessionSnapshot snapshot = await mediaOverlayCommands.GetCurrentMediaSnapshotAsync();
                if (requestVersion != Volatile.Read(ref _latestMediaOverlayRequestVersion))
                {
                    return;
                }

                ApplyMediaOverlayResult(BuildCurrentMediaOverlayResult(snapshot));
            }
            catch (Exception ex)
            {
                logger.Warning("AppCliOverlayCoordinator", "Failed to capture current media state for overlay", nameof(ShowCurrentTrackOverlayAsync), ex);
                overlay.Show("No current track");
            }
            finally
            {
                Interlocked.Exchange(ref _mediaOverlayCaptureInFlight, 0);
            }
        }

        private void ApplyMediaOverlayResult(MediaOverlayResult mediaOverlay)
        {
            if (mediaOverlay.Kind == MediaOverlayResultKind.Hidden)
            {
                return;
            }

            if (mediaOverlay.Kind == MediaOverlayResultKind.TrackMessage && !string.IsNullOrWhiteSpace(mediaOverlay.Title))
            {
                overlay.ShowMediaTrack(mediaOverlay.Header, mediaOverlay.Title, mediaOverlay.Artist);
                return;
            }

            if (!string.IsNullOrWhiteSpace(mediaOverlay.Message))
            {
                overlay.Show(mediaOverlay.Message);
            }
        }

        private int GetConfiguredVolumeStepPercent(bool playback)
        {
            Settings? settings = currentSettingsProvider();
            int configured = playback
                ? settings?.Hotkeys.Volume.MasterVolumeStepPercent ?? 5
                : settings?.Hotkeys.Volume.MicVolumeStepPercent ?? 5;

            return NormalizeVolumeStepPercent(configured);
        }

        private static Func<Task<bool>> ResolveMediaCommand(Func<Task<bool>>? asyncCommand, Func<bool>? syncCommand, Func<Task<bool>> defaultCommand)
        {
            if (asyncCommand != null)
            {
                return asyncCommand;
            }

            return syncCommand == null
                ? defaultCommand
                : () => Task.FromResult(syncCommand());
        }

        private bool ToggleState(Func<bool> currentValueProvider, Action<bool> applyState, string enabledMessage, string disabledMessage)
        {
            return SetState(!currentValueProvider(), applyState, enabledMessage, disabledMessage);
        }

        private bool SetState(bool enabled, Action<bool> applyState, string enabledMessage, string disabledMessage)
        {
            applyState(enabled);
            overlay.Show(enabled ? OverlayActionStateKind.Disabled : OverlayActionStateKind.Enabled, enabled ? enabledMessage : disabledMessage);
            return true;
        }

        private bool StepEndpointVolume(Func<MMDevice?> getDevice, int stepPercent, bool increase, string reason, string overlayLabel)
        {
            try
            {
                int normalizedStepPercent = NormalizeVolumeStepPercent(stepPercent);
                using var device = getDevice();
                if (device == null)
                {
                    logger.Warning("AppCliOverlayCoordinator", () => $"volume-step-failed | target={overlayLabel} direction={(increase ? "up" : "down")} stepPercent={normalizedStepPercent} reason=no-default-device");
                    return false;
                }

                if (!TryGetEndpointVolumeState(logger, device, reason, out float currentPercent, out _))
                {
                    logger.Warning("AppCliOverlayCoordinator", () => $"volume-step-failed | target={overlayLabel} direction={(increase ? "up" : "down")} stepPercent={normalizedStepPercent} device={LogPrivacy.Device(device.FriendlyName)} reason=endpoint-volume-unavailable");
                    return false;
                }

                float updatedPercent = ComputeSteppedVolumePercent(currentPercent, stepPercent, increase);
                if (!TryApplyEndpointVolume(logger, device, updatedPercent, reason, muteAtZero: false, unmuteAboveZero: increase, out float appliedPercent))
                {
                    logger.Warning("AppCliOverlayCoordinator", () => $"volume-step-failed | target={overlayLabel} direction={(increase ? "up" : "down")} stepPercent={normalizedStepPercent} device={LogPrivacy.Device(device.FriendlyName)} currentPercent={currentPercent:F1} targetPercent={updatedPercent:F1} reason=endpoint-volume-apply-failed");
                    return false;
                }

                overlay.Show(GetVolumeOverlayStateKind(increase), BuildVolumeOverlayMessage(overlayLabel, appliedPercent));
                return true;
            }
            catch (Exception ex)
            {
                logger.Warning("AppCliOverlayCoordinator", $"Failed to adjust {overlayLabel.ToLowerInvariant()} via hotkey | direction={(increase ? "up" : "down")} stepPercent={NormalizeVolumeStepPercent(stepPercent)}", nameof(StepEndpointVolume), ex);
                return false;
            }
        }

        private void ShowListenToInputOverlay(bool enabled)
        {
            string inputDeviceName = "Current input device";
            string? monitorTargetOutputDeviceName = null;
            try
            {
                using var inputDevice = audio.GetDefaultRecordingDevice();
                inputDeviceName = NormalizeListenToInputOverlayDeviceName(inputDevice?.FriendlyName);

                audio.TryGetCurrentInputListenTargetOutputDeviceName(out monitorTargetOutputDeviceName, out _);
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(AudioPilot.Logging.LogLevel.Trace))
                {
                    logger.Trace("AppCliOverlayCoordinator", () => $"Listen overlay input name fallback used: {ex.GetType().Name}");
                }
            }

            string deviceText = ComposeListenToInputOverlayDeviceText(enabled, inputDeviceName, monitorTargetOutputDeviceName);
            overlay.Show(OverlayDeviceKind.Input, GetListenToInputOverlayHeader(enabled), deviceText);
        }
    }
}
