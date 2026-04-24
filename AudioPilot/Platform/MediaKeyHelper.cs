using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;
using Windows.Media.Control;

namespace AudioPilot.Platform
{
    public static partial class MediaKeyHelper
    {
        private const uint ExpectedInputCount = 2;

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
            public static int Size => Marshal.SizeOf<INPUT>();
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg; public ushort wParamL; public ushort wParamH;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
        private const ushort VK_MEDIA_PREV_TRACK = 0xB1;
        private const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

        internal enum SystemMediaCommand
        {
            PlayPause,
            NextTrack,
            PreviousTrack,
        }

        internal enum MediaCommandRouteKind
        {
            None,
            Delegate,
            TestOverride,
            CurrentGsmc,
            PlayingGsmc,
            ControllableGsmc,
            SendInputFallback,
        }

        internal readonly record struct MediaCommandSendOutcome(
            bool Sent,
            MediaCommandRouteKind Route,
            bool SuppressFallback = false,
            bool FocusedProcessAvailable = false,
            bool FocusedCandidateFound = false,
            bool FocusedCandidateAttempted = false,
            bool FocusedTargetRejected = false,
            bool NoFocusedMatch = false,
            string? CandidateSourceAppUserModelId = null,
            string? FocusedProcessName = null,
            string? FocusedProcessExecutablePath = null,
            string? FailureReason = null,
            double ElapsedMs = 0,
            int? ErrorCode = null)
        {
            public static MediaCommandSendOutcome FromDelegate(bool sent) =>
                new(sent, MediaCommandRouteKind.Delegate, FailureReason: sent ? null : "delegate-returned-false");

            public bool UsedSendInputFallback => Route == MediaCommandRouteKind.SendInputFallback;
        }

        private enum SystemMediaCommandCandidateKind
        {
            Current,
            Playing,
            Controllable,
        }

        private readonly record struct SystemMediaCommandCandidate(
            GlobalSystemMediaTransportControlsSession Session,
            SystemMediaCommandCandidateKind Kind);

        private static readonly Lock _lock = new();
        private static readonly Lock _systemMediaManagerLock = new();
        private static Task<GlobalSystemMediaTransportControlsSessionManager>? _systemMediaManagerTask;

        internal static Func<ushort, (uint Result, int ErrorCode)>? SendInputOverrideForTests { get; set; }
        internal static Func<SystemMediaCommand, MediaCommandSendOutcome>? DetailedSystemMediaCommandOverrideForTests { get; set; }
        internal static Func<SystemMediaCommand, bool>? SystemMediaCommandOverrideForTests { get; set; }
        internal static ILogger? LoggerOverrideForTests { get; set; }

        public static bool TryPressPlayPause() => TryPressPlayPauseDetailed().Sent;
        public static bool TryPressNextTrack() => TryPressNextTrackDetailed().Sent;
        public static bool TryPressPreviousTrack() => TryPressPreviousTrackDetailed().Sent;
        public static async Task<bool> TryPressPlayPauseAsync() => (await TryPressPlayPauseDetailedAsync().ConfigureAwait(false)).Sent;
        public static async Task<bool> TryPressNextTrackAsync() => (await TryPressNextTrackDetailedAsync().ConfigureAwait(false)).Sent;
        public static async Task<bool> TryPressPreviousTrackAsync() => (await TryPressPreviousTrackDetailedAsync().ConfigureAwait(false)).Sent;
        internal static MediaCommandSendOutcome TryPressPlayPauseDetailed() => SendCommandDetailed(SystemMediaCommand.PlayPause, VK_MEDIA_PLAY_PAUSE, "PlayPause");
        internal static MediaCommandSendOutcome TryPressNextTrackDetailed() => SendCommandDetailed(SystemMediaCommand.NextTrack, VK_MEDIA_NEXT_TRACK, "NextTrack");
        internal static MediaCommandSendOutcome TryPressPreviousTrackDetailed() => SendCommandDetailed(SystemMediaCommand.PreviousTrack, VK_MEDIA_PREV_TRACK, "PreviousTrack");
        internal static Task<MediaCommandSendOutcome> TryPressPlayPauseDetailedAsync() => SendCommandDetailedAsync(SystemMediaCommand.PlayPause, VK_MEDIA_PLAY_PAUSE, "PlayPause");
        internal static Task<MediaCommandSendOutcome> TryPressNextTrackDetailedAsync() => SendCommandDetailedAsync(SystemMediaCommand.NextTrack, VK_MEDIA_NEXT_TRACK, "NextTrack");
        internal static Task<MediaCommandSendOutcome> TryPressPreviousTrackDetailedAsync() => SendCommandDetailedAsync(SystemMediaCommand.PreviousTrack, VK_MEDIA_PREV_TRACK, "PreviousTrack");
        public static async Task PrewarmSystemMediaCommandsAsync()
        {
            if (DetailedSystemMediaCommandOverrideForTests != null || SystemMediaCommandOverrideForTests != null || SendInputOverrideForTests != null)
            {
                return;
            }

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(AppConstants.Timing.SystemMediaCommandTimeoutMs));
                _ = await GetSystemMediaManagerAsync(timeoutCts.Token).ConfigureAwait(false);
                GetLogger()?.Trace("MediaKeyHelper", "media-command-gsmc-prewarm-completed", nameof(PrewarmSystemMediaCommandsAsync));
            }
            catch (OperationCanceledException)
            {
                GetLogger()?.Trace("MediaKeyHelper", () => $"media-command-gsmc-prewarm-timeout timeoutMs={AppConstants.Timing.SystemMediaCommandTimeoutMs}", nameof(PrewarmSystemMediaCommandsAsync));
            }
            catch (Exception ex)
            {
                ResetFaultedSystemMediaManagerTask();
                GetLogger()?.Trace("MediaKeyHelper", () => $"media-command-gsmc-prewarm-failed reason={ex.GetType().Name}", nameof(PrewarmSystemMediaCommandsAsync));
            }
        }

        private static MediaCommandSendOutcome SendCommandDetailed(SystemMediaCommand command, ushort fallbackVk, string keyName)
        {
            long started = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                MediaCommandSendOutcome result = TrySendSystemMediaCommand(command, keyName);
                if (result.Sent)
                {
                    return CompleteOutcome(result, started);
                }

                if (result.SuppressFallback)
                {
                    return CompleteOutcome(result, started);
                }

                return CompleteOutcome(SendInputFallback(fallbackVk, keyName, result), started);
            }
        }

        private static async Task<MediaCommandSendOutcome> SendCommandDetailedAsync(SystemMediaCommand command, ushort fallbackVk, string keyName)
        {
            long started = Stopwatch.GetTimestamp();
            MediaCommandSendOutcome result = await TrySendSystemMediaCommandAsync(command, keyName).ConfigureAwait(false);
            if (result.Sent)
            {
                return CompleteOutcome(result, started);
            }

            if (result.SuppressFallback)
            {
                return CompleteOutcome(result, started);
            }

            lock (_lock)
            {
                return CompleteOutcome(SendInputFallback(fallbackVk, keyName, result), started);
            }
        }

        internal static void ResetTestHooks()
        {
            SendInputOverrideForTests = null;
            DetailedSystemMediaCommandOverrideForTests = null;
            SystemMediaCommandOverrideForTests = null;
            LoggerOverrideForTests = null;
        }

        private static MediaCommandSendOutcome TrySendSystemMediaCommand(SystemMediaCommand command, string keyName)
            => TrySendSystemMediaCommandAsync(command, keyName).GetAwaiter().GetResult();

        private static async Task<MediaCommandSendOutcome> TrySendSystemMediaCommandAsync(SystemMediaCommand command, string keyName)
        {
            if (DetailedSystemMediaCommandOverrideForTests != null)
            {
                return DetailedSystemMediaCommandOverrideForTests(command);
            }

            if (SystemMediaCommandOverrideForTests != null)
            {
                bool sent = SystemMediaCommandOverrideForTests(command);
                return new MediaCommandSendOutcome(
                    sent,
                    MediaCommandRouteKind.TestOverride,
                    FailureReason: sent ? null : "test-override-returned-false");
            }

            if (SendInputOverrideForTests != null)
            {
                return new MediaCommandSendOutcome(
                    Sent: false,
                    MediaCommandRouteKind.None,
                    FailureReason: "sendinput-test-override");
            }

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(AppConstants.Timing.SystemMediaCommandTimeoutMs));
                return await TrySendSystemMediaCommandCoreAsync(command, keyName, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                GetLogger()?.Trace("MediaKeyHelper", () => $"media-command-gsmc-timeout:{keyName} timeoutMs={AppConstants.Timing.SystemMediaCommandTimeoutMs}", nameof(TrySendSystemMediaCommand));
                return new MediaCommandSendOutcome(Sent: false, MediaCommandRouteKind.None, FailureReason: "gsmc-timeout");
            }
            catch (Exception ex)
            {
                ResetFaultedSystemMediaManagerTask();
                GetLogger()?.Trace("MediaKeyHelper", () => $"media-command-gsmc-fallback:{keyName} reason={ex.GetType().Name}", nameof(TrySendSystemMediaCommand));
                return new MediaCommandSendOutcome(Sent: false, MediaCommandRouteKind.None, FailureReason: $"gsmc-{ex.GetType().Name}");
            }
        }

        private static MediaCommandSendOutcome SendInputFallback(ushort fallbackVk, string keyName, MediaCommandSendOutcome previousOutcome)
        {
            try
            {
                var (result, errorCode) = SendInputMediaKey(fallbackVk);

                if (result != ExpectedInputCount)
                {
                    Exception failure = errorCode != 0
                        ? new System.ComponentModel.Win32Exception(errorCode)
                        : new InvalidOperationException($"SendInput returned {result} instead of {ExpectedInputCount}.");
                    GetLogger()?.Error("MediaKeyHelper", $"media-key-send-failed:{keyName}", nameof(SendCommandDetailed), failure);
                    return previousOutcome with
                    {
                        Sent = false,
                        Route = MediaCommandRouteKind.SendInputFallback,
                        FailureReason = "sendinput-partial",
                        ErrorCode = errorCode,
                    };
                }

                return previousOutcome with
                {
                    Sent = true,
                    Route = MediaCommandRouteKind.SendInputFallback,
                    FailureReason = null,
                    ErrorCode = null,
                };
            }
            catch (Exception ex)
            {
                GetLogger()?.Error("MediaKeyHelper", $"media-key-send-exception:{keyName}", nameof(SendCommandDetailed), ex);
                return previousOutcome with
                {
                    Sent = false,
                    Route = MediaCommandRouteKind.SendInputFallback,
                    FailureReason = $"sendinput-{ex.GetType().Name}",
                };
            }
        }

        private static async Task<MediaCommandSendOutcome> TrySendSystemMediaCommandCoreAsync(SystemMediaCommand command, string keyName, CancellationToken cancellationToken)
        {
            GlobalSystemMediaTransportControlsSessionManager manager = await GetSystemMediaManagerAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = manager.GetSessions();
            GlobalSystemMediaTransportControlsSession? current = manager.GetCurrentSession();
            List<SystemMediaCommandCandidate> candidates = SelectSystemMediaCommandCandidates(sessions, current, command);
            LogFocusedRoutingDiagnosticsIfNeeded(
                sessions,
                current,
                candidates,
                command,
                keyName);

            foreach (SystemMediaCommandCandidate candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool sent = await TrySendSystemMediaCommandAsync(candidate.Session, command, cancellationToken).ConfigureAwait(false);
                if (sent)
                {
                    GetLogger()?.Trace(
                        "MediaKeyHelper",
                        () => $"media-command-sent:gsmc:{keyName} candidate={candidate.Kind} source={LogPrivacy.Id(candidate.Session.SourceAppUserModelId)}",
                        nameof(TrySendSystemMediaCommand));
                    return BuildSystemMediaOutcome(
                        sent: true,
                        candidate,
                        focusedCandidateAttempted: false,
                        focusedTargetRejected: false,
                        noFocusedMatch: false,
                        suppressFallback: false,
                        failureReason: null);
                }
            }

            return new MediaCommandSendOutcome(
                Sent: false,
                MediaCommandRouteKind.None,
                SuppressFallback: false,
                FocusedProcessAvailable: false,
                FocusedCandidateFound: false,
                FocusedCandidateAttempted: false,
                FocusedTargetRejected: false,
                NoFocusedMatch: false,
                FailureReason: "no-system-media-candidate");
        }

        private static MediaCommandSendOutcome BuildSystemMediaOutcome(
            bool sent,
            SystemMediaCommandCandidate candidate,
            bool focusedCandidateAttempted,
            bool focusedTargetRejected,
            bool noFocusedMatch,
            bool suppressFallback,
            string? failureReason)
        {
            return new MediaCommandSendOutcome(
                sent,
                GetRouteKind(candidate.Kind),
                SuppressFallback: suppressFallback,
                FocusedProcessAvailable: false,
                FocusedCandidateFound: false,
                FocusedCandidateAttempted: focusedCandidateAttempted,
                FocusedTargetRejected: focusedTargetRejected,
                NoFocusedMatch: noFocusedMatch,
                CandidateSourceAppUserModelId: candidate.Session.SourceAppUserModelId,
                FailureReason: failureReason);
        }

        private static MediaCommandRouteKind GetRouteKind(SystemMediaCommandCandidateKind candidateKind)
        {
            return candidateKind switch
            {
                SystemMediaCommandCandidateKind.Current => MediaCommandRouteKind.CurrentGsmc,
                SystemMediaCommandCandidateKind.Playing => MediaCommandRouteKind.PlayingGsmc,
                SystemMediaCommandCandidateKind.Controllable => MediaCommandRouteKind.ControllableGsmc,
                _ => MediaCommandRouteKind.None,
            };
        }

        private static MediaCommandSendOutcome CompleteOutcome(MediaCommandSendOutcome outcome, long startedTimestamp)
        {
            return outcome with
            {
                ElapsedMs = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            };
        }

        private static void LogFocusedRoutingDiagnosticsIfNeeded(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
            GlobalSystemMediaTransportControlsSession? current,
            IReadOnlyCollection<SystemMediaCommandCandidate> candidates,
            SystemMediaCommand command,
            string keyName)
        {
            ILogger? logger = GetLogger();
            if (logger == null ||
                !logger.IsEnabled(LogLevel.Trace))
            {
                return;
            }

            try
            {
                bool currentSupports = SupportsSystemMediaCommand(current, command);
                int commandableCount = sessions.Count(session => SupportsSystemMediaCommand(session, command));
                int playingCount = sessions.Count(session => session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
                string currentSource = LogPrivacy.Id(current?.SourceAppUserModelId);
                string sessionSummary = BuildMediaCommandSessionSummary(sessions, command);
                string focusedMissReason = ClassifyFocusedRoutingSkippedReason(sessions, current, currentSupports, command);
                string nextRoute = candidates.Count == 0
                    ? "none"
                    : candidates.First().Kind.ToString();

                logger.Trace(
                    "MediaKeyHelper",
                    () => $"media-command-focused-routing-skipped | command={keyName} reason={focusedMissReason} nextRoute={nextRoute} sessions={sessions.Count} commandable={commandableCount} playing={playingCount} currentSource={currentSource} currentSupports={currentSupports} candidates={candidates.Count} sessionSummary={sessionSummary}",
                    nameof(TrySendSystemMediaCommand));
            }
            catch (Exception ex)
            {
                logger.Trace(
                    "MediaKeyHelper",
                    () => $"media-command-focused-routing-diagnostics-failed | command={keyName} reason={ex.GetType().Name}",
                    nameof(TrySendSystemMediaCommand));
            }
        }

        private static string BuildMediaCommandSessionSummary(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
            SystemMediaCommand command)
        {
            if (sessions.Count == 0)
            {
                return "[]";
            }

            IEnumerable<string> summaries = sessions.Take(5).Select(session =>
            {
                GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo = session.GetPlaybackInfo();
                return $"{LogPrivacy.Id(session.SourceAppUserModelId)}:status={playbackInfo.PlaybackStatus}:supports={SupportsSystemMediaCommand(session, command)}";
            });

            string suffix = sessions.Count > 5 ? $",+{sessions.Count - 5}" : string.Empty;
            return $"[{string.Join(",", summaries)}{suffix}]";
        }

        private static string ClassifyFocusedRoutingSkippedReason(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
            GlobalSystemMediaTransportControlsSession? current,
            bool currentSupports,
            SystemMediaCommand command)
        {
            if (sessions.Count == 0)
            {
                return "NoMediaSessions";
            }

            bool anyCommandable = sessions.Any(session => SupportsSystemMediaCommand(session, command));
            if (!anyCommandable)
            {
                return "NoCommandableSessions";
            }

            if (currentSupports)
            {
                return "CurrentCommandable";
            }

            bool anyPlayingCommandable = sessions.Any(session =>
                session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                && SupportsSystemMediaCommand(session, command));
            if (anyPlayingCommandable)
            {
                return "PlayingCommandable";
            }

            return current is null
                ? "NoCurrentSession"
                : "ControllableFallback";
        }

        private static async Task<bool> TrySendSystemMediaCommandAsync(
            GlobalSystemMediaTransportControlsSession session,
            SystemMediaCommand command,
            CancellationToken cancellationToken)
        {
            return command switch
            {
                SystemMediaCommand.PlayPause => await session.TryTogglePlayPauseAsync().AsTask(cancellationToken).ConfigureAwait(false),
                SystemMediaCommand.NextTrack => await session.TrySkipNextAsync().AsTask(cancellationToken).ConfigureAwait(false),
                SystemMediaCommand.PreviousTrack => await session.TrySkipPreviousAsync().AsTask(cancellationToken).ConfigureAwait(false),
                _ => false,
            };
        }

        private static async Task<GlobalSystemMediaTransportControlsSessionManager> GetSystemMediaManagerAsync(CancellationToken cancellationToken)
        {
            Task<GlobalSystemMediaTransportControlsSessionManager> requestTask;
            lock (_systemMediaManagerLock)
            {
                if (_systemMediaManagerTask == null || _systemMediaManagerTask.IsCanceled || _systemMediaManagerTask.IsFaulted)
                {
                    _systemMediaManagerTask = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
                }

                requestTask = _systemMediaManagerTask;
            }

            return await requestTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static void ResetFaultedSystemMediaManagerTask()
        {
            lock (_systemMediaManagerLock)
            {
                if (_systemMediaManagerTask?.IsFaulted == true || _systemMediaManagerTask?.IsCanceled == true)
                {
                    _systemMediaManagerTask = null;
                }
            }
        }

        private static List<SystemMediaCommandCandidate> SelectSystemMediaCommandCandidates(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
            GlobalSystemMediaTransportControlsSession? current,
            SystemMediaCommand command)
        {
            List<SystemMediaCommandCandidate> candidates = [];

            if (current != null && SupportsSystemMediaCommand(current, command))
            {
                AddCandidateIfNew(candidates, current, SystemMediaCommandCandidateKind.Current);
            }

            foreach (GlobalSystemMediaTransportControlsSession session in sessions.Where(session =>
                session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                && SupportsSystemMediaCommand(session, command)))
            {
                AddCandidateIfNew(candidates, session, SystemMediaCommandCandidateKind.Playing);
            }

            foreach (GlobalSystemMediaTransportControlsSession session in sessions.Where(session =>
                SupportsSystemMediaCommand(session, command)))
            {
                AddCandidateIfNew(candidates, session, SystemMediaCommandCandidateKind.Controllable);
            }

            return candidates;
        }

        private static void AddCandidateIfNew(
            List<SystemMediaCommandCandidate> candidates,
            GlobalSystemMediaTransportControlsSession session,
            SystemMediaCommandCandidateKind kind)
        {
            if (!candidates.Any(candidate => ReferenceEquals(candidate.Session, session)))
            {
                candidates.Add(new SystemMediaCommandCandidate(session, kind));
            }
        }

        private static bool SupportsSystemMediaCommand(
            GlobalSystemMediaTransportControlsSession? session,
            SystemMediaCommand command)
        {
            if (session == null)
            {
                return false;
            }

            GlobalSystemMediaTransportControlsSessionPlaybackControls controls = session.GetPlaybackInfo().Controls;
            return command switch
            {
                SystemMediaCommand.PlayPause => controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
                SystemMediaCommand.NextTrack => controls.IsNextEnabled,
                SystemMediaCommand.PreviousTrack => controls.IsPreviousEnabled,
                _ => false,
            };
        }

        private static (uint Result, int ErrorCode) SendInputMediaKey(ushort vk)
        {
            if (SendInputOverrideForTests != null)
            {
                return SendInputOverrideForTests(vk);
            }

            INPUT[] inputs =
            [
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = vk,
                            dwFlags = KEYEVENTF_EXTENDEDKEY
                        }
                    }
                },
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = vk,
                            dwFlags = KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP
                        }
                    }
                }
            ];

            uint result = SendInput(ExpectedInputCount, inputs, INPUT.Size);
            int errorCode = result == ExpectedInputCount ? 0 : Marshal.GetLastWin32Error();
            return (result, errorCode);
        }

        private static ILogger? GetLogger()
        {
            return LoggerOverrideForTests ?? Logger.Instance;
        }
    }
}
