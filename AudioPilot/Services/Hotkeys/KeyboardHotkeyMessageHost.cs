using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Hotkeys
{
    internal interface IKeyboardHotkeyMessageHost : IDisposable
    {
        bool IsRunning { get; }

        bool TryStart();

        HotkeyOsRegistrationResult RegisterHotkey(int id, uint fsModifiers, uint vk);

        void UnregisterHotkey(int id);

        void Stop();
    }

    internal sealed partial class MessageOnlyKeyboardHotkeyHost(
        Logger logger,
        Action<int, long> dispatchHotkeyId,
        Func<IntPtr, int, uint, uint, HotkeyOsRegistrationResult> registerHotKeyInvoker,
        Action<IntPtr, int> unregisterHotKeyInvoker) : IKeyboardHotkeyMessageHost
    {
        private const uint WM_APP = 0x8000;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint SMTO_ERRORONEXIT = 0x0020;
        private const int WM_HOTKEY = 0x0312;
        private const int WM_QUIT = 0x0012;
        private const int WM_NCCREATE = 0x0081;
        private const uint WM_APP_REGISTER_HOTKEY = WM_APP + 1;
        private const uint WM_APP_UNREGISTER_HOTKEY = WM_APP + 2;
        private const int HWND_MESSAGE = -3;
        private const int GWLP_USERDATA = -21;
        private const uint PM_NOREMOVE = 0x0000;
        private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
        private const int ERROR_TIMEOUT = 1460;
        private const nuint RegisterSucceededResult = 0;
        private const nuint RegisterFailedUnknownResult = uint.MaxValue;
        private const string WindowClassName = "AudioPilot.KeyboardHotkeyMessageHost";

        private static readonly WndProc s_wndProc = WindowProc;

#pragma warning disable SYSLIB1054
        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern sbyte GetMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(in NativeMessage lpMsg);

        [DllImport("user32.dll", EntryPoint = "DispatchMessageW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessage(in NativeMessage lpMsg);

        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,
            uint uTimeout,
            out nuint lpdwResult);

        [DllImport("user32.dll", EntryPoint = "PeekMessageW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
#pragma warning restore SYSLIB1054

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr HWnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public NativePoint Pt;
            public uint LPrivate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WndClassEx
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public IntPtr lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CreateStruct
        {
            public IntPtr lpCreateParams;
            public IntPtr hInstance;
            public IntPtr hMenu;
            public IntPtr hwndParent;
            public int cy;
            public int cx;
            public int y;
            public int x;
            public int style;
            public IntPtr lpszName;
            public IntPtr lpszClass;
            public uint dwExStyle;
        }

        private readonly Logger _logger = logger;
        private readonly Action<int, long> _dispatchHotkeyId = dispatchHotkeyId;
        private readonly Func<IntPtr, int, uint, uint, HotkeyOsRegistrationResult> _registerHotKeyInvoker = registerHotKeyInvoker;
        private readonly Action<IntPtr, int> _unregisterHotKeyInvoker = unregisterHotKeyInvoker;
        private readonly Lock _stateLock = new();
        private Thread? _worker;
        private uint _workerThreadId;
        private IntPtr _windowHandle;
        private bool _disposed;
        private GCHandle? _selfHandle;

        public bool IsRunning
        {
            get
            {
                lock (_stateLock)
                {
                    return _windowHandle != IntPtr.Zero;
                }
            }
        }

        public bool TryStart()
        {
            ManualResetEventSlim started = new(false);
            Exception? startupFailure = null;

            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_windowHandle != IntPtr.Zero)
                {
                    return true;
                }

                if (_worker != null && _worker.IsAlive)
                {
                    return false;
                }

                _worker = ThreadedWin32HostHelper.StartBackgroundWorker(
                    () => WorkerLoop(started, ex => startupFailure = ex),
                    "AudioPilot.KeyboardMessages");
            }

            return ThreadedWin32HostHelper.WaitForStartup(
                started,
                startupFailure,
                () => IsRunning,
                _logger,
                "HotkeyService",
                $"keyboard-message-host-start-timeout | timeoutMs={AppConstants.Timing.CleanupWaitMs}");
        }

        public HotkeyOsRegistrationResult RegisterHotkey(int id, uint fsModifiers, uint vk)
        {
            IntPtr windowHandle;
            lock (_stateLock)
            {
                if (_disposed || _windowHandle == IntPtr.Zero)
                {
                    return new HotkeyOsRegistrationResult(Succeeded: false);
                }

                windowHandle = _windowHandle;
            }

            if (SendMessageTimeout(
                    windowHandle,
                    WM_APP_REGISTER_HOTKEY,
                    (IntPtr)id,
                    ToIntPtrChecked(PackRegisterArguments(fsModifiers, vk)),
                    SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT,
                    AppConstants.Timing.CleanupWaitMs,
                    out nuint result) == IntPtr.Zero)
            {
                int errorCode = NormalizeSendMessageTimeoutError();
                if (errorCode == ERROR_TIMEOUT)
                {
                    _logger.Warning(
                        "HotkeyService",
                        () => $"keyboard-message-host-register-timeout | id={id} timeoutMs={AppConstants.Timing.CleanupWaitMs} win32={errorCode}");
                }
                else
                {
                    _logger.Warning(
                        "HotkeyService",
                        () => $"keyboard-message-host-register-request-failed | id={id} win32={errorCode}");
                }

                return new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: errorCode);
            }

            if (result == RegisterSucceededResult)
            {
                return new HotkeyOsRegistrationResult(Succeeded: true);
            }

            int registrationError = result == RegisterFailedUnknownResult ? 0 : unchecked((int)result);
            return new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: registrationError);
        }

        public void UnregisterHotkey(int id)
        {
            IntPtr windowHandle;
            lock (_stateLock)
            {
                if (_disposed || _windowHandle == IntPtr.Zero)
                {
                    return;
                }

                windowHandle = _windowHandle;
            }

            if (SendMessageTimeout(
                    windowHandle,
                    WM_APP_UNREGISTER_HOTKEY,
                    (IntPtr)id,
                    IntPtr.Zero,
                    SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT,
                    AppConstants.Timing.CleanupWaitMs,
                    out _) == IntPtr.Zero)
            {
                int errorCode = NormalizeSendMessageTimeoutError();
                if (errorCode == ERROR_TIMEOUT)
                {
                    _logger.Warning(
                        "HotkeyService",
                        () => $"keyboard-message-host-unregister-timeout | id={id} timeoutMs={AppConstants.Timing.CleanupWaitMs} win32={errorCode}");
                }
                else
                {
                    _logger.Warning(
                        "HotkeyService",
                        () => $"keyboard-message-host-unregister-request-failed | id={id} win32={errorCode}");
                }
            }
        }

        public void Stop()
        {
            Thread? worker;
            uint workerThreadId;

            lock (_stateLock)
            {
                worker = _worker;
                workerThreadId = _workerThreadId;
            }

            if (worker == null)
            {
                return;
            }

            ThreadedWin32HostHelper.RequestStopAndJoin(
                worker,
                workerThreadId,
                id => _ = PostThreadMessage(id, WM_QUIT, IntPtr.Zero, IntPtr.Zero),
                _logger,
                "HotkeyService",
                $"keyboard-message-host-stop-timeout | timeoutMs={AppConstants.Timing.CleanupWaitMs}");
        }

        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            Stop();
        }

        private void WorkerLoop(ManualResetEventSlim started, Action<Exception?> reportStartupFailure)
        {
            IntPtr windowHandle = IntPtr.Zero;
            GCHandle selfHandle = default;

            try
            {
                _workerThreadId = GetCurrentThreadId();
                _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

                RegisterWindowClass();

                selfHandle = GCHandle.Alloc(this);
                IntPtr createParam = GCHandle.ToIntPtr(selfHandle);
                windowHandle = CreateWindowEx(
                    0,
                    WindowClassName,
                    string.Empty,
                    0,
                    0,
                    0,
                    0,
                    0,
                    (IntPtr)HWND_MESSAGE,
                    IntPtr.Zero,
                    GetModuleHandle(null),
                    createParam);

                if (windowHandle == IntPtr.Zero)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    reportStartupFailure(new Win32Exception(errorCode));
                    _logger.Error("HotkeyService", () => $"Failed to create keyboard message host window. Win32 Error Code: {errorCode}");
                    started.Set();
                    return;
                }

                lock (_stateLock)
                {
                    _windowHandle = windowHandle;
                    _selfHandle = selfHandle;
                }

                reportStartupFailure(null);
                started.Set();

                while (true)
                {
                    sbyte result = GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0);
                    if (result == 0)
                    {
                        break;
                    }

                    if (result < 0)
                    {
                        int errorCode = Marshal.GetLastWin32Error();
                        throw new Win32Exception(errorCode);
                    }

                    _ = TranslateMessage(in message);
                    _ = DispatchMessage(in message);
                }
            }
            catch (Exception ex)
            {
                reportStartupFailure(ex);
                started.Set();
                _logger.Error("HotkeyService", "Exception while running keyboard message host", nameof(TryStart), ex);
            }
            finally
            {
                if (windowHandle != IntPtr.Zero)
                {
                    _ = DestroyWindow(windowHandle);
                }

                lock (_stateLock)
                {
                    _windowHandle = IntPtr.Zero;
                    _workerThreadId = 0;
                    _worker = null;

                    if (_selfHandle.HasValue)
                    {
                        _selfHandle.Value.Free();
                        _selfHandle = null;
                    }
                    else if (selfHandle.IsAllocated)
                    {
                        selfHandle.Free();
                    }
                }
            }
        }

        private static void RegisterWindowClass()
        {
            WndClassEx windowClass = new()
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = GetModuleHandle(null),
                lpszClassName = WindowClassName,
            };

            ushort atom = RegisterClassEx(ref windowClass);
            if (atom == 0)
            {
                int errorCode = Marshal.GetLastWin32Error();
                if (errorCode != ERROR_CLASS_ALREADY_EXISTS)
                {
                    throw new Win32Exception(errorCode);
                }
            }
        }

        private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_NCCREATE)
            {
                CreateStruct createStruct = Marshal.PtrToStructure<CreateStruct>(lParam);
                _ = SetWindowLongPtr(hWnd, GWLP_USERDATA, createStruct.lpCreateParams);
                return (IntPtr)1;
            }

            IntPtr userData = GetWindowLongPtr(hWnd, GWLP_USERDATA);
            if (userData != IntPtr.Zero)
            {
                GCHandle handle = GCHandle.FromIntPtr(userData);
                if (handle.Target is MessageOnlyKeyboardHotkeyHost host)
                {
                    if (msg == WM_HOTKEY)
                    {
                        host._dispatchHotkeyId(wParam.ToInt32(), Stopwatch.GetTimestamp());
                        return IntPtr.Zero;
                    }

                    if (msg == WM_APP_REGISTER_HOTKEY)
                    {
                        UnpackRegisterArguments(lParam, out uint fsModifiers, out uint virtualKey);
                        HotkeyOsRegistrationResult result = host._registerHotKeyInvoker(hWnd, wParam.ToInt32(), fsModifiers, virtualKey);
                        return result.Succeeded
                            ? (IntPtr)RegisterSucceededResult
                            : (IntPtr)(result.ErrorCode == 0 ? RegisterFailedUnknownResult : (nuint)result.ErrorCode);
                    }

                    if (msg == WM_APP_UNREGISTER_HOTKEY)
                    {
                        host._unregisterHotKeyInvoker(hWnd, wParam.ToInt32());
                        return IntPtr.Zero;
                    }
                }
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private static long PackRegisterArguments(uint fsModifiers, uint virtualKey)
            => ((long)virtualKey << 32) | fsModifiers;

        private static IntPtr ToIntPtrChecked(long value)
            => checked((IntPtr)value);

        private static void UnpackRegisterArguments(IntPtr packedArgs, out uint fsModifiers, out uint virtualKey)
        {
            ulong raw = unchecked((ulong)packedArgs.ToInt64());
            fsModifiers = unchecked((uint)(raw & uint.MaxValue));
            virtualKey = unchecked((uint)(raw >> 32));
        }

        private static int NormalizeSendMessageTimeoutError()
        {
            int errorCode = Marshal.GetLastWin32Error();
            return errorCode == 0 ? ERROR_TIMEOUT : errorCode;
        }
    }
}
