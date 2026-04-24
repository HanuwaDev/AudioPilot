using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal interface IWindowFocusMonitor : IDisposable
    {
        event EventHandler<WindowFocusEventArgs>? WindowFocused;
        void Start();
        void Stop();
    }

    internal sealed class WindowFocusEventArgs(int processId, string processName, string executablePath, string windowTitle) : EventArgs
    {
        public int ProcessId { get; } = processId;
        public string ProcessName { get; } = processName;
        public string ExecutablePath { get; } = executablePath;
        public string WindowTitle { get; } = windowTitle;
    }

    internal sealed class WinEventHookWindowFocusMonitor : IWindowFocusMonitor
    {
        private readonly Logger _logger;
        private GCHandle _hookDelegateHandle;
        private IntPtr _hookHandle;
        private bool _started;
        private readonly Lock _lock = new();

        public event EventHandler<WindowFocusEventArgs>? WindowFocused;

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Retaining runtime marshalling for WinEvent hook callback interop.")]
        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Retaining runtime marshalling for WinEvent hook callback interop.")]
        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Retaining runtime marshalling for WinEvent hook callback interop.")]
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Retaining runtime marshalling for WinEvent hook callback interop.")]
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        private readonly WinEventDelegate _delegate;

        public WinEventHookWindowFocusMonitor(Logger logger)
        {
            _logger = logger;
            _delegate = OnWinEvent;
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_started)
                {
                    return;
                }

                _hookDelegateHandle = GCHandle.Alloc(_delegate);
                _hookHandle = SetWinEventHook(
                    EVENT_SYSTEM_FOREGROUND,
                    EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero,
                    _delegate,
                    0, 0,
                    WINEVENT_OUTOFCONTEXT);

                if (_hookHandle == IntPtr.Zero)
                {
                    _hookDelegateHandle.Free();
                    _logger.Error("WinEventHookWindowFocusMonitor", "Failed to set WinEventHook");
                    return;
                }

                _started = true;
                _logger.Debug("WinEventHookWindowFocusMonitor", "Window focus monitoring started");
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

                if (_hookHandle != IntPtr.Zero)
                {
                    UnhookWinEvent(_hookHandle);
                    _hookHandle = IntPtr.Zero;
                }

                if (_hookDelegateHandle.IsAllocated)
                {
                    _hookDelegateHandle.Free();
                }

                _started = false;
                _logger.Debug("WinEventHookWindowFocusMonitor", "Window focus monitoring stopped");
            }
        }

        private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (eventType != EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                if (GetWindowThreadProcessId(hwnd, out uint processId) == 0)
                {
                    return;
                }

                var titleBuilder = new System.Text.StringBuilder(256);
                GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
                string windowTitle = titleBuilder.ToString();

                (string processName, string executablePath) = GetProcessInfo((int)processId);

                WindowFocused?.Invoke(this, new WindowFocusEventArgs((int)processId, processName, executablePath, windowTitle));
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Debug, "WinEventHookWindowFocusMonitor", () => $"focus-event-failed | reason={ex.GetType().Name}", nameof(OnWinEvent), ex);
            }
        }

        private static string GetProcessName(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.ProcessName;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetProcessExecutablePath(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static (string processName, string executablePath) GetProcessInfo(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return (process.ProcessName, process.MainModule?.FileName ?? string.Empty);
            }
            catch
            {
                return (string.Empty, string.Empty);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }

    internal sealed class ApplicationTriggerCoordinator : IDisposable
    {
        private readonly IWindowFocusMonitor _focusMonitor;
        private readonly Logger _logger;
        private readonly Lock _lock = new();
        private readonly HashSet<int> _executingProcessIds = [];
        private static readonly TimeSpan TitleRegexTimeout = TimeSpan.FromMilliseconds(100);

        private readonly Dictionary<string, Regex> _compiledRegexCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _shutdownCts = new();
        private int _lastFocusedProcessId;
        private string _lastFocusedWindowTitle = string.Empty;
        private bool _started;
        private bool _disposed;

        public ApplicationTriggerCoordinator(
            IEnumerable<AudioRoutine> routines,
            Func<AudioRoutine, int, Task> executeRoutine,
            Logger logger,
            IWindowFocusMonitor? focusMonitor = null)
        {
            Routines = routines ?? throw new ArgumentNullException(nameof(routines));
            ExecuteRoutine = executeRoutine ?? throw new ArgumentNullException(nameof(executeRoutine));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _focusMonitor = focusMonitor ?? new WinEventHookWindowFocusMonitor(logger);
            _focusMonitor.WindowFocused += OnWindowFocused;
        }

        public IEnumerable<AudioRoutine> Routines { get; }
        public Func<AudioRoutine, int, Task> ExecuteRoutine { get; }

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed || _started)
                {
                    _logger.Debug("ApplicationTriggerCoordinator", $"Start() skipped: disposed={_disposed}, started={_started}");
                    return;
                }

                bool hasRoutines = HasProcessFocusRoutines();
                _logger.Debug("ApplicationTriggerCoordinator", $"Start() checking routines: hasProcessFocusRoutines={hasRoutines}, totalRoutines={Routines.Count()}");

                if (!hasRoutines)
                {
                    _logger.Debug("ApplicationTriggerCoordinator", "No ProcessFocus routines, not starting focus monitor");
                    return;
                }

                _focusMonitor.Start();
                _started = true;
                _logger.Info("ApplicationTriggerCoordinator", "Started monitoring for ProcessFocus routines");
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

                _focusMonitor.Stop();
                _executingProcessIds.Clear();
                _compiledRegexCache.Clear();
                _lastFocusedProcessId = 0;
                _lastFocusedWindowTitle = string.Empty;
                _started = false;
                _logger.Info("ApplicationTriggerCoordinator", "Stopped monitoring");
            }
        }

        public void RefreshRoutines()
        {
            lock (_lock)
            {
                _executingProcessIds.Clear();
                _compiledRegexCache.Clear();
                _lastFocusedProcessId = 0;
                _lastFocusedWindowTitle = string.Empty;

                bool hasProcessFocusRoutines = HasProcessFocusRoutines();

                if (_started && !hasProcessFocusRoutines)
                {
                    _focusMonitor.Stop();
                    _started = false;
                    _logger.Info("ApplicationTriggerCoordinator", "Stopped - no more ProcessFocus routines");
                }
                else if (!_started && hasProcessFocusRoutines)
                {
                    _focusMonitor.Start();
                    _started = true;
                    _logger.Info("ApplicationTriggerCoordinator", "Started - now has ProcessFocus routines");
                }
            }
        }

        private bool HasProcessFocusRoutines()
        {
            return Routines.Any(r =>
                r.Enabled &&
                r.TriggerKind == RoutineTriggerKind.Application &&
                r.ApplicationTriggerMode == ApplicationTriggerMode.ProcessFocus &&
                !string.IsNullOrWhiteSpace(r.TriggerAppPath));
        }

        private void OnWindowFocused(object? sender, WindowFocusEventArgs e)
        {
            if (e.ProcessId <= 0)
            {
                return;
            }

            _logger.Debug(
                "ApplicationTriggerCoordinator",
                () => $"window-focused | processId={LogPrivacy.Id(e.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))} processName={LogPrivacy.Process(e.ProcessName)} title={LogPrivacy.Label(e.WindowTitle)}");

            List<AudioRoutine> matchingRoutines;
            lock (_lock)
            {
                if (_lastFocusedProcessId == e.ProcessId &&
                    string.Equals(_lastFocusedWindowTitle, e.WindowTitle, StringComparison.Ordinal))
                {
                    _logger.Debug(
                        "ApplicationTriggerCoordinator",
                        () => $"window-focus-skipped | reason=duplicate-focus processId={LogPrivacy.Id(e.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
                    return;
                }

                _lastFocusedProcessId = e.ProcessId;
                _lastFocusedWindowTitle = e.WindowTitle;

                if (_executingProcessIds.Contains(e.ProcessId))
                {
                    _logger.Debug(
                        "ApplicationTriggerCoordinator",
                        () => $"window-focus-skipped | reason=execution-in-flight processId={LogPrivacy.Id(e.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
                    return;
                }

                var processFocusRoutines = Routines.Where(r =>
                    r.Enabled &&
                    r.TriggerKind == RoutineTriggerKind.Application &&
                    r.ApplicationTriggerMode == ApplicationTriggerMode.ProcessFocus).ToList();

                _logger.Debug("ApplicationTriggerCoordinator", $"Found {processFocusRoutines.Count} ProcessFocus routines");

                matchingRoutines = [];
                foreach (AudioRoutine routine in processFocusRoutines)
                {
                    bool processMatch = MatchesProcess(routine, e);
                    bool titleMatch = processMatch && MatchesTitlePattern(routine, e.WindowTitle);
                    _logger.Debug(
                        "ApplicationTriggerCoordinator",
                        () => $"window-focus-routine-evaluated | routineId={LogPrivacy.Id(routine.Id)} routineName={LogPrivacy.Label(routine.Name)} processMatch={processMatch} titleMatch={titleMatch} target={LogPrivacy.Id(routine.TriggerAppPath)} pattern={LogPrivacy.Label(routine.ApplicationTriggerTitlePattern)}");

                    if (processMatch && titleMatch)
                    {
                        matchingRoutines.Add(routine);
                    }
                }

                if (matchingRoutines.Count > 0)
                {
                    _executingProcessIds.Add(e.ProcessId);
                }

                CleanupDeadProcessIds();
            }

            if (matchingRoutines.Count == 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (AudioRoutine routine in matchingRoutines)
                    {
                        try
                        {
                            await ExecuteRoutine(routine, e.ProcessId);
                            _logger.Info(
                                "ApplicationTriggerCoordinator",
                                () => $"window-focus-routine-executed | routineId={LogPrivacy.Id(routine.Id)} processId={LogPrivacy.Id(e.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))} processName={LogPrivacy.Process(e.ProcessName)}");
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(
                                "ApplicationTriggerCoordinator",
                                () => $"window-focus-routine-failed | routineId={LogPrivacy.Id(routine.Id)} processId={LogPrivacy.Id(e.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))}",
                                nameof(OnWindowFocused),
                                ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(
                        "ApplicationTriggerCoordinator",
                        () => $"window-focus-routine-batch-failed | processId={LogPrivacy.Id(e.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))}",
                        nameof(OnWindowFocused),
                        ex);
                }
                finally
                {
                    lock (_lock)
                    {
                        _executingProcessIds.Remove(e.ProcessId);
                    }
                }
            }, _shutdownCts.Token);
        }

        private static bool MatchesProcess(AudioRoutine routine, WindowFocusEventArgs e)
        {
            string targetPath = routine.TriggerAppPath;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return false;
            }

            if (RoutineTriggerPathHelper.LooksLikeExecutablePath(targetPath))
            {
                if (RoutineTriggerPathHelper.IsExecutableProcessMatch(e.ExecutablePath, targetPath, e.ProcessName))
                {
                    return true;
                }

                string targetName = Path.GetFileNameWithoutExtension(targetPath);
                return string.Equals(targetName, e.ProcessName, StringComparison.OrdinalIgnoreCase);
            }

            if (RoutineTriggerPathHelper.LooksLikePackagedAppId(targetPath))
            {
                return !string.IsNullOrWhiteSpace(e.ExecutablePath) &&
                    RoutineTriggerPathHelper.IsPackagedAppExecutablePathMatch(targetPath, e.ExecutablePath);
            }

            string fallbackTargetName = Path.GetFileNameWithoutExtension(targetPath);
            return string.Equals(fallbackTargetName, e.ProcessName, StringComparison.OrdinalIgnoreCase);
        }

        private bool MatchesTitlePattern(AudioRoutine routine, string windowTitle)
        {
            string pattern = routine.ApplicationTriggerTitlePattern;

            if (string.IsNullOrWhiteSpace(pattern))
            {
                return true;
            }

            return routine.ApplicationTriggerTitleMatchMode switch
            {
                ApplicationTriggerTitleMatchMode.Exact =>
                    string.Equals(windowTitle, pattern, StringComparison.OrdinalIgnoreCase),
                ApplicationTriggerTitleMatchMode.Contains =>
                    windowTitle.Contains(pattern, StringComparison.OrdinalIgnoreCase),
                ApplicationTriggerTitleMatchMode.Wildcard =>
                    WildcardMatch(windowTitle, pattern),
                ApplicationTriggerTitleMatchMode.Regex =>
                    RegexMatchWithCache(windowTitle, pattern),
                _ => true
            };
        }

        private bool WildcardMatch(string input, string pattern)
        {
            string regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            try
            {
                return Regex.IsMatch(
                    input,
                    regexPattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    TitleRegexTimeout);
            }
            catch (RegexMatchTimeoutException)
            {
                LogTitlePatternTimeout("wildcard", pattern);
                return false;
            }
        }

        private bool RegexMatchWithCache(string input, string pattern)
        {
            try
            {
                if (!_compiledRegexCache.TryGetValue(pattern, out Regex? regex))
                {
                    regex = new Regex(
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                        TitleRegexTimeout);
                    _compiledRegexCache[pattern] = regex;
                }

                return regex.IsMatch(input);
            }
            catch (RegexMatchTimeoutException)
            {
                LogTitlePatternTimeout("regex", pattern);
                return false;
            }
            catch (ArgumentException ex)
            {
                _logger.Debug(
                    "ApplicationTriggerCoordinator",
                    () => $"window-title-pattern-invalid | mode=regex pattern={LogPrivacy.Label(pattern)} reason={ex.GetType().Name}");
                return false;
            }
        }

        private void LogTitlePatternTimeout(string mode, string pattern)
        {
            _logger.Warning(
                "ApplicationTriggerCoordinator",
                () => $"window-title-pattern-timeout | mode={mode} timeoutMs={TitleRegexTimeout.TotalMilliseconds:F0} pattern={LogPrivacy.Label(pattern)}");
        }

        internal bool MatchesTitlePatternForTests(AudioRoutine routine, string windowTitle)
        {
            return MatchesTitlePattern(routine, windowTitle);
        }

        private void CleanupDeadProcessIds()
        {
            lock (_lock)
            {
                if (_executingProcessIds.Count == 0)
                {
                    return;
                }

                List<int> deadPids = [];
                foreach (int pid in _executingProcessIds)
                {
                    try
                    {
                        using var _ = Process.GetProcessById(pid);
                    }
                    catch
                    {
                        deadPids.Add(pid);
                    }
                }

                foreach (int deadPid in deadPids)
                {
                    _executingProcessIds.Remove(deadPid);
                }

                if (deadPids.Count > 0)
                {
                    _logger.Debug("ApplicationTriggerCoordinator", $"Cleaned up {deadPids.Count} dead process IDs from tracking set");
                }
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

                _shutdownCts.Cancel();
                _shutdownCts.Dispose();
                Stop();
                _focusMonitor.Dispose();
                _disposed = true;
            }
        }
    }
}
