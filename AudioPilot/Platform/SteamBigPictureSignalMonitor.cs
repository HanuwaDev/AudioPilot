using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AudioPilot.Platform;

internal readonly record struct SteamBigPictureSignalMonitorStartResult(bool Success, string Status, string? FailureReason = null);

internal enum SteamBigPictureSignalKind
{
    Unknown,
    Foreground,
    Create,
    Destroy,
    Show,
    Hide,
    NameChange,
}

internal readonly record struct SteamBigPictureSignal(
    SteamBigPictureSignalKind Kind,
    nint Hwnd,
    int ProcessId,
    string ProcessExecutablePath,
    string Title,
    string ClassName);

internal interface ISteamBigPictureSignalMonitor : IDisposable
{
    event Action<SteamBigPictureSignal>? Signaled;

    bool IsRunning { get; }

    SteamBigPictureSignalMonitorStartResult Start();
    void Stop();
}

internal sealed partial class WinEventSteamBigPictureSignalMonitor : ISteamBigPictureSignalMonitor
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectNameChange = 0x800C;
    private const int ObjidWindow = 0;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;

    private readonly Lock _sync = new();
    private readonly WinEventProc _callback;
    private readonly List<nint> _hooks = [];
    private bool _running;
    private bool _disposed;

    private delegate void WinEventProc(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventProc lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hWinEventHook);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

#pragma warning disable SYSLIB1054
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, char[] lpClassName, int nMaxCount);
#pragma warning restore SYSLIB1054

    public WinEventSteamBigPictureSignalMonitor()
    {
        _callback = OnWinEvent;
    }

    public event Action<SteamBigPictureSignal>? Signaled;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _running;
            }
        }
    }

    public SteamBigPictureSignalMonitorStartResult Start()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return new SteamBigPictureSignalMonitorStartResult(false, "inactive", "monitor-disposed");
            }

            if (_running)
            {
                return new SteamBigPictureSignalMonitorStartResult(true, "active");
            }

            try
            {
                AddHook(EventSystemForeground, EventSystemForeground);
                AddHook(EventObjectCreate, EventObjectNameChange);
                _running = true;
                return new SteamBigPictureSignalMonitorStartResult(true, "active");
            }
            catch (Exception ex)
            {
                DisposeHooksUnderLock();
                return new SteamBigPictureSignalMonitorStartResult(false, "inactive", ex.GetType().Name);
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            DisposeHooksUnderLock();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeHooksUnderLock();
        }
    }

    internal static bool ShouldSignal(uint eventType, nint hwnd, int idObject, int idChild)
    {
        if (hwnd == nint.Zero)
        {
            return false;
        }

        if (eventType == EventSystemForeground)
        {
            return true;
        }

        if (idObject != ObjidWindow || idChild != 0)
        {
            return false;
        }

        return eventType is EventObjectCreate or EventObjectDestroy or EventObjectShow or EventObjectHide or EventObjectNameChange;
    }

    internal static SteamBigPictureSignalKind GetSignalKind(uint eventType)
    {
        return eventType switch
        {
            EventSystemForeground => SteamBigPictureSignalKind.Foreground,
            EventObjectCreate => SteamBigPictureSignalKind.Create,
            EventObjectDestroy => SteamBigPictureSignalKind.Destroy,
            EventObjectShow => SteamBigPictureSignalKind.Show,
            EventObjectHide => SteamBigPictureSignalKind.Hide,
            EventObjectNameChange => SteamBigPictureSignalKind.NameChange,
            _ => SteamBigPictureSignalKind.Unknown,
        };
    }

    private void AddHook(uint eventMin, uint eventMax)
    {
        nint hook = SetWinEventHook(
            eventMin,
            eventMax,
            0,
            _callback,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
        if (hook == nint.Zero)
        {
            throw new InvalidOperationException($"Failed to register WinEvent hook for range {eventMin}-{eventMax}");
        }

        _hooks.Add(hook);
    }

    private void DisposeHooksUnderLock()
    {
        foreach (nint hook in _hooks)
        {
            try
            {
                UnhookWinEvent(hook);
            }
            catch
            {
            }
        }

        _hooks.Clear();
        _running = false;
    }

    private void OnWinEvent(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        bool shouldRaise;
        lock (_sync)
        {
            shouldRaise = _running && !_disposed && ShouldSignal(eventType, hwnd, idObject, idChild);
        }

        if (shouldRaise)
        {
            Signaled?.Invoke(BuildSignal(eventType, hwnd));
        }
    }

    private static SteamBigPictureSignal BuildSignal(uint eventType, nint hwnd)
    {
        int processId = 0;
        string executablePath = string.Empty;
        string title = TryGetWindowTitle(hwnd) ?? string.Empty;
        string className = TryGetWindowClassName(hwnd) ?? string.Empty;

        try
        {
            GetWindowThreadProcessId(hwnd, out uint rawProcessId);
            if (rawProcessId > 0 && rawProcessId <= int.MaxValue)
            {
                processId = (int)rawProcessId;
                try
                {
                    using Process process = Process.GetProcessById(processId);
                    executablePath = process.MainModule?.FileName ?? string.Empty;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return new SteamBigPictureSignal(
            GetSignalKind(eventType),
            hwnd,
            processId,
            executablePath,
            title,
            className);
    }

    private static string? TryGetWindowTitle(nint hwnd)
    {
        try
        {
            int titleLength = GetWindowTextLength(hwnd);
            if (titleLength <= 0)
            {
                return null;
            }

            StringBuilder titleBuilder = new(titleLength + 1);
            int copiedLength = GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
            if (copiedLength <= 0)
            {
                return null;
            }

            string title = titleBuilder.ToString().Trim();
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetWindowClassName(nint hwnd)
    {
        try
        {
            char[] classBuffer = new char[256];
            int copiedLength = GetClassName(hwnd, classBuffer, classBuffer.Length);
            if (copiedLength <= 0)
            {
                return null;
            }

            string className = new string(classBuffer, 0, copiedLength).Trim();
            return string.IsNullOrWhiteSpace(className) ? null : className;
        }
        catch
        {
            return null;
        }
    }
}
