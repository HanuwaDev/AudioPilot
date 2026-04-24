using System.Runtime.InteropServices;
using System.Windows;

namespace AudioPilot.Services.UI
{
    public static partial class MessageBoxService
    {
        internal interface IMessageBoxNative
        {
            IntPtr FindWindow(string? className, string? windowName);
            IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? className, string? windowName);
            IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);
            bool SetForegroundWindow(IntPtr hWnd);
            bool SetWindowText(IntPtr hWnd, string text);
            uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
            MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage icon);
        }

        private sealed class Win32MessageBoxNative : IMessageBoxNative
        {
            public IntPtr FindWindow(string? className, string? windowName) => MessageBoxService.FindWindow(className, windowName);
            public IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? className, string? windowName) => MessageBoxService.FindWindowEx(hwndParent, hwndChildAfter, className, windowName);
            public IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam) => MessageBoxService.SendMessage(hWnd, msg, wParam, lParam);
            public bool SetForegroundWindow(IntPtr hWnd) => MessageBoxService.SetForegroundWindow(hWnd);
            public bool SetWindowText(IntPtr hWnd, string text) => MessageBoxService.SetWindowText(hWnd, text);
            public uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId) => MessageBoxService.GetWindowThreadProcessId(hWnd, out processId);
            public MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage icon) => MessageBox.Show(message, caption, buttons, icon);
        }

        private static readonly Lock _lock = new();
        private static int? _currentProcessId;
        private static readonly IMessageBoxNative DefaultNative = new Win32MessageBoxNative();
        private static readonly AsyncLocal<IMessageBoxNative?> NativeOverride = new();

        [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

        [LibraryImport("user32.dll", EntryPoint = "SendMessageW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(IntPtr hWnd);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowText(IntPtr hWnd, string lpString);

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const uint WM_SETTEXT = 0x000C;

        private static int CurrentProcessId => _currentProcessId ??= Environment.ProcessId;
        private static IMessageBoxNative CurrentNative => NativeOverride.Value ?? DefaultNative;

        internal static bool ShouldUpdateExistingWindow(MessageBoxButton buttons, bool existingUpdated)
        {
            return buttons == MessageBoxButton.OK && existingUpdated;
        }

        internal static void SetNativeForTests(IMessageBoxNative native)
        {
            NativeOverride.Value = native;
            _currentProcessId = null;
        }

        internal static void ResetNativeForTests()
        {
            NativeOverride.Value = null;
            _currentProcessId = null;
        }

        private static bool UpdateExistingMessageBox(string message, string caption)
        {
            IMessageBoxNative native = CurrentNative;
            IntPtr hWnd = native.FindWindow("#32770", null);

            while (hWnd != IntPtr.Zero)
            {
                if (IsWindowOwnedByCurrentProcess(hWnd, native))
                {
                    IntPtr hText = native.FindWindowEx(hWnd, IntPtr.Zero, "Static", null);
                    IntPtr hButton = native.FindWindowEx(hWnd, IntPtr.Zero, "Button", null);

                    if (hText != IntPtr.Zero && hButton != IntPtr.Zero)
                    {
                        native.SendMessage(hText, WM_SETTEXT, IntPtr.Zero, message);
                        native.SetWindowText(hWnd, caption);
                        native.SetForegroundWindow(hWnd);
                        return true;
                    }
                }

                hWnd = native.FindWindowEx(IntPtr.Zero, hWnd, "#32770", null);
            }

            return false;
        }

        private static bool IsWindowOwnedByCurrentProcess(IntPtr hWnd, IMessageBoxNative native)
        {
            native.GetWindowThreadProcessId(hWnd, out uint processId);
            return processId == CurrentProcessId;
        }

        public static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage icon)
        {
            lock (_lock)
            {
                bool updatedExisting = buttons == MessageBoxButton.OK && UpdateExistingMessageBox(message, caption);
                if (ShouldUpdateExistingWindow(buttons, updatedExisting))
                {
                    return MessageBoxResult.OK;
                }

                return CurrentNative.Show(message, caption, buttons, icon);
            }
        }

        public static void ShowError(string message, string caption = DialogText.Captions.Error)
        {
            Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public static void ShowWarning(string message, string caption = DialogText.Captions.Warning)
        {
            Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        public static void ShowInfo(string message, string caption = DialogText.Captions.Information)
        {
            Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public static void ShowSuccess(string message, string caption = DialogText.Captions.Success)
        {
            Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public static MessageBoxResult ShowYesNo(string message, string caption = DialogText.Captions.Confirm, MessageBoxImage icon = MessageBoxImage.Question)
        {
            return Show(message, caption, MessageBoxButton.YesNo, icon);
        }
    }
}
