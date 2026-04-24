using System.Diagnostics;
using System.IO;
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

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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

        internal readonly record struct FocusedProcessInfo(int ProcessId, string ProcessName, string ExecutablePath)
        {
            public bool HasIdentity => ProcessId > 0 || !string.IsNullOrWhiteSpace(ProcessName) || !string.IsNullOrWhiteSpace(ExecutablePath);
        }

        private enum SystemMediaCommandCandidateKind
        {
            Focused,
            Current,
            Playing,
            Controllable,
        }

        private readonly record struct SystemMediaCommandCandidate(
            GlobalSystemMediaTransportControlsSession Session,
            SystemMediaCommandCandidateKind Kind);

        private readonly record struct SystemMediaCommandSendResult(bool Sent, bool SuppressFallback);

        private static readonly Lock _lock = new();
        private static readonly Lock _systemMediaManagerLock = new();
        private static Task<GlobalSystemMediaTransportControlsSessionManager>? _systemMediaManagerTask;

        internal static Func<ushort, (uint Result, int ErrorCode)>? SendInputOverrideForTests { get; set; }
        internal static Func<SystemMediaCommand, bool>? SystemMediaCommandOverrideForTests { get; set; }
        internal static Func<FocusedProcessInfo>? FocusedProcessOverrideForTests { get; set; }
        internal static ILogger? LoggerOverrideForTests { get; set; }

        public static bool TryPressPlayPause() => SendCommand(SystemMediaCommand.PlayPause, VK_MEDIA_PLAY_PAUSE, "PlayPause");
        public static bool TryPressNextTrack() => SendCommand(SystemMediaCommand.NextTrack, VK_MEDIA_NEXT_TRACK, "NextTrack");
        public static bool TryPressPreviousTrack() => SendCommand(SystemMediaCommand.PreviousTrack, VK_MEDIA_PREV_TRACK, "PreviousTrack");
        public static Task<bool> TryPressPlayPauseAsync() => SendCommandAsync(SystemMediaCommand.PlayPause, VK_MEDIA_PLAY_PAUSE, "PlayPause");
        public static Task<bool> TryPressNextTrackAsync() => SendCommandAsync(SystemMediaCommand.NextTrack, VK_MEDIA_NEXT_TRACK, "NextTrack");
        public static Task<bool> TryPressPreviousTrackAsync() => SendCommandAsync(SystemMediaCommand.PreviousTrack, VK_MEDIA_PREV_TRACK, "PreviousTrack");
        public static async Task PrewarmSystemMediaCommandsAsync()
        {
            if (SystemMediaCommandOverrideForTests != null || SendInputOverrideForTests != null)
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

        private static bool SendCommand(SystemMediaCommand command, ushort fallbackVk, string keyName)
        {
            lock (_lock)
            {
                SystemMediaCommandSendResult result = TrySendSystemMediaCommand(command, keyName);
                if (result.Sent)
                {
                    return true;
                }

                if (result.SuppressFallback)
                {
                    return false;
                }

                return SendInputFallback(fallbackVk, keyName);
            }
        }

        private static async Task<bool> SendCommandAsync(SystemMediaCommand command, ushort fallbackVk, string keyName)
        {
            SystemMediaCommandSendResult result = await TrySendSystemMediaCommandAsync(command, keyName).ConfigureAwait(false);
            if (result.Sent)
            {
                return true;
            }

            if (result.SuppressFallback)
            {
                return false;
            }

            lock (_lock)
            {
                return SendInputFallback(fallbackVk, keyName);
            }
        }

        internal static void ResetTestHooks()
        {
            SendInputOverrideForTests = null;
            SystemMediaCommandOverrideForTests = null;
            FocusedProcessOverrideForTests = null;
            LoggerOverrideForTests = null;
        }

        private static SystemMediaCommandSendResult TrySendSystemMediaCommand(SystemMediaCommand command, string keyName)
            => TrySendSystemMediaCommandAsync(command, keyName).GetAwaiter().GetResult();

        private static async Task<SystemMediaCommandSendResult> TrySendSystemMediaCommandAsync(SystemMediaCommand command, string keyName)
        {
            if (SystemMediaCommandOverrideForTests != null)
            {
                return new SystemMediaCommandSendResult(SystemMediaCommandOverrideForTests(command), SuppressFallback: false);
            }

            if (SendInputOverrideForTests != null)
            {
                return new SystemMediaCommandSendResult(Sent: false, SuppressFallback: false);
            }

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(AppConstants.Timing.SystemMediaCommandTimeoutMs));
                return await TrySendSystemMediaCommandCoreAsync(command, keyName, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                GetLogger()?.Trace("MediaKeyHelper", () => $"media-command-gsmc-timeout:{keyName} timeoutMs={AppConstants.Timing.SystemMediaCommandTimeoutMs}", nameof(TrySendSystemMediaCommand));
                return new SystemMediaCommandSendResult(Sent: false, SuppressFallback: false);
            }
            catch (Exception ex)
            {
                ResetFaultedSystemMediaManagerTask();
                GetLogger()?.Trace("MediaKeyHelper", () => $"media-command-gsmc-fallback:{keyName} reason={ex.GetType().Name}", nameof(TrySendSystemMediaCommand));
                return new SystemMediaCommandSendResult(Sent: false, SuppressFallback: false);
            }
        }

        private static bool SendInputFallback(ushort fallbackVk, string keyName)
        {
            try
            {
                var (result, errorCode) = SendInputMediaKey(fallbackVk);

                if (result != ExpectedInputCount)
                {
                    Exception failure = errorCode != 0
                        ? new System.ComponentModel.Win32Exception(errorCode)
                        : new InvalidOperationException($"SendInput returned {result} instead of {ExpectedInputCount}.");
                    GetLogger()?.Error("MediaKeyHelper", $"media-key-send-failed:{keyName}", nameof(SendCommand), failure);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                GetLogger()?.Error("MediaKeyHelper", $"media-key-send-exception:{keyName}", nameof(SendCommand), ex);
                return false;
            }
        }

        private static async Task<SystemMediaCommandSendResult> TrySendSystemMediaCommandCoreAsync(SystemMediaCommand command, string keyName, CancellationToken cancellationToken)
        {
            GlobalSystemMediaTransportControlsSessionManager manager = await GetSystemMediaManagerAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            FocusedProcessInfo focusedProcess = GetFocusedProcessInfo();
            List<SystemMediaCommandCandidate> candidates = SelectSystemMediaCommandCandidates(manager, command, focusedProcess);
            LogFocusedCandidateMissIfNeeded(manager, candidates, command, keyName, focusedProcess);
            bool targetedCandidateAttempted = false;

            foreach (SystemMediaCommandCandidate candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.Kind == SystemMediaCommandCandidateKind.Focused)
                {
                    targetedCandidateAttempted = true;
                }

                bool sent = await TrySendSystemMediaCommandAsync(candidate.Session, command, cancellationToken).ConfigureAwait(false);
                if (sent)
                {
                    GetLogger()?.Trace(
                        "MediaKeyHelper",
                        () => $"media-command-sent:gsmc:{keyName} candidate={candidate.Kind} source={LogPrivacy.Id(candidate.Session.SourceAppUserModelId)} focusedProcess={LogPrivacy.Label(focusedProcess.ProcessName)}",
                        nameof(TrySendSystemMediaCommand));
                    return new SystemMediaCommandSendResult(Sent: true, SuppressFallback: false);
                }

                if (candidate.Kind == SystemMediaCommandCandidateKind.Focused)
                {
                    continue;
                }

                if (targetedCandidateAttempted)
                {
                    GetLogger()?.Trace(
                        "MediaKeyHelper",
                        () => $"media-command-focused-target-rejected | command={keyName} suppressFallback=true focusedProcess={LogPrivacy.Label(focusedProcess.ProcessName)}",
                        nameof(TrySendSystemMediaCommand));
                    return new SystemMediaCommandSendResult(Sent: false, SuppressFallback: true);
                }
            }

            return new SystemMediaCommandSendResult(Sent: false, SuppressFallback: targetedCandidateAttempted);
        }

        private static void LogFocusedCandidateMissIfNeeded(
            GlobalSystemMediaTransportControlsSessionManager manager,
            IReadOnlyCollection<SystemMediaCommandCandidate> candidates,
            SystemMediaCommand command,
            string keyName,
            FocusedProcessInfo focusedProcess)
        {
            ILogger? logger = GetLogger();
            if (logger == null || candidates.Any(candidate => candidate.Kind == SystemMediaCommandCandidateKind.Focused))
            {
                return;
            }

            if (!focusedProcess.HasIdentity)
            {
                logger.Trace(
                    "MediaKeyHelper",
                    () => $"media-command-focused-unavailable | command={keyName} reason=no-foreground-process-identity",
                    nameof(TrySendSystemMediaCommand));
                return;
            }

            try
            {
                IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = manager.GetSessions();
                int commandableCount = sessions.Count(session => SupportsSystemMediaCommand(session, command));
                int playingCount = sessions.Count(session => session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
                string currentSource = LogPrivacy.Id(manager.GetCurrentSession()?.SourceAppUserModelId);
                string sessionSummary = BuildFocusedCandidateMissSessionSummary(sessions, command);
                string focusedExecutable = LogPrivacy.Label(Path.GetFileNameWithoutExtension(focusedProcess.ExecutablePath));

                logger.Trace(
                    "MediaKeyHelper",
                    () => $"media-command-focused-no-match | command={keyName} focusedProcess={LogPrivacy.Label(focusedProcess.ProcessName)} focusedExecutable={focusedExecutable} sessions={sessions.Count} commandable={commandableCount} playing={playingCount} currentSource={currentSource} candidates={candidates.Count} sessionSummary={sessionSummary}",
                    nameof(TrySendSystemMediaCommand));
            }
            catch (Exception ex)
            {
                logger.Trace(
                    "MediaKeyHelper",
                    () => $"media-command-focused-no-match-diagnostics-failed | command={keyName} focusedProcess={LogPrivacy.Label(focusedProcess.ProcessName)} reason={ex.GetType().Name}",
                    nameof(TrySendSystemMediaCommand));
            }
        }

        private static string BuildFocusedCandidateMissSessionSummary(
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
            GlobalSystemMediaTransportControlsSessionManager manager,
            SystemMediaCommand command,
            FocusedProcessInfo focusedProcess)
        {
            List<SystemMediaCommandCandidate> candidates = [];
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = manager.GetSessions();
            foreach (GlobalSystemMediaTransportControlsSession session in sessions.Where(session =>
                SupportsSystemMediaCommand(session, command)
                && DoesSessionSourceMatchFocusedProcess(session.SourceAppUserModelId, focusedProcess)))
            {
                AddCandidateIfNew(candidates, session, SystemMediaCommandCandidateKind.Focused);
            }

            GlobalSystemMediaTransportControlsSession? current = manager.GetCurrentSession();
            if (current != null)
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

        internal static bool DoesSessionSourceMatchFocusedProcess(string? sourceAppUserModelId, FocusedProcessInfo focusedProcess)
        {
            if (string.IsNullOrWhiteSpace(sourceAppUserModelId) || !focusedProcess.HasIdentity)
            {
                return false;
            }

            string normalizedSource = sourceAppUserModelId.Trim();
            HashSet<string> focusedNames = GetFocusedProcessNameCandidates(focusedProcess);
            if (focusedNames.Count == 0)
            {
                return false;
            }

            string sourceFileName = Path.GetFileNameWithoutExtension(normalizedSource);
            if (!string.IsNullOrWhiteSpace(sourceFileName) && focusedNames.Contains(sourceFileName))
            {
                return true;
            }

            string sourceWithoutExtension = normalizedSource.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? normalizedSource[..^4]
                : normalizedSource;
            if (focusedNames.Contains(sourceWithoutExtension))
            {
                return true;
            }

            foreach (string sourceToken in normalizedSource.Split(['.', '!', '_', '-', ' ', '\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (focusedNames.Contains(sourceToken))
                {
                    return true;
                }
            }

            return focusedNames.Any(name =>
                name.Length >= 4 &&
                normalizedSource.Contains(name, StringComparison.OrdinalIgnoreCase));
        }

        private static HashSet<string> GetFocusedProcessNameCandidates(FocusedProcessInfo focusedProcess)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddFocusedProcessName(names, focusedProcess.ProcessName);
            AddFocusedProcessName(names, Path.GetFileNameWithoutExtension(focusedProcess.ExecutablePath));

            if (names.Contains("msedge"))
            {
                names.Add("edge");
                names.Add("MicrosoftEdge");
            }

            return names;
        }

        private static void AddFocusedProcessName(HashSet<string> names, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string trimmed = value.Trim();
            if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^4];
            }

            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                names.Add(trimmed);
            }
        }

        private static FocusedProcessInfo GetFocusedProcessInfo()
        {
            if (FocusedProcessOverrideForTests != null)
            {
                return FocusedProcessOverrideForTests();
            }

            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out uint processId) == 0 || processId == 0)
                {
                    return default;
                }

                using Process process = Process.GetProcessById((int)processId);
                string processName = process.ProcessName;
                string executablePath = GetProcessExecutablePathSafe(process);
                return new FocusedProcessInfo((int)processId, processName, executablePath);
            }
            catch
            {
                return default;
            }
        }

        private static string GetProcessExecutablePathSafe(Process process)
        {
            try
            {
                return process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
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
