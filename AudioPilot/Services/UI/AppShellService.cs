using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Resources;
using AudioPilot.Constants;
using AudioPilot.Logging;
using Hardcodet.Wpf.TaskbarNotification;

namespace AudioPilot.Services.UI
{
    public partial class AppShellService : IDisposable
    {
        internal sealed class WindowInteractionStrategy(
            Action<Window> centerWindow,
            Action<Window> prepareForShow,
            Action<Window, IntPtr> bringToForeground)
        {
            public static WindowInteractionStrategy Interactive { get; } = new(
                CenterWindowCore,
                static window =>
                {
                    window.WindowState = WindowState.Normal;
                    window.Opacity = 1;
                },
                BringWindowToForegroundCore);

            public static WindowInteractionStrategy NonInteractive { get; } = new(
                static _ => { },
                static window =>
                {
                    window.WindowState = WindowState.Minimized;
                    window.Opacity = 0;
                },
                static (_, _) => { });

            public void Center(Window window) => centerWindow(window);

            public void PrepareForShow(Window window) => prepareForShow(window);

            public void BringToForeground(Window window, IntPtr handle) => bringToForeground(window, handle);
        }

        private const uint WM_SETICON = 0x0080;
        private const int SW_RESTORE = 9;
        private static readonly IntPtr ICON_SMALL = IntPtr.Zero;
        private static readonly IntPtr ICON_BIG = new(1);

        [LibraryImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
        private static partial IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial IntPtr CopyIcon(IntPtr hIcon);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool BringWindowToTop(IntPtr hWnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(IntPtr hWnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

        [LibraryImport("kernel32.dll")]
        private static partial uint GetCurrentThreadId();

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyIcon(IntPtr hIcon);

        private readonly Window _window;
        private readonly TaskbarIcon _tray;
        private readonly Logger _logger = Logger.Instance;
        private readonly WindowInteractionStrategy _windowInteraction;
        private bool _hasShownOnce;
        private bool _trayInitialized;
        private bool _windowTaskbarIconRefreshedOnce;
        private bool _usesSharedIcon;
        private IntPtr _nativeIconHandle = IntPtr.Zero;
        private ImageSource? _icon;

        public AppShellService(Window window, TaskbarIcon tray)
            : this(window, tray, WindowInteractionStrategy.Interactive)
        {
        }

        internal AppShellService(Window window, TaskbarIcon tray, WindowInteractionStrategy? windowInteraction)
        {
            _window = window;
            _tray = tray;
            _windowInteraction = windowInteraction ?? WindowInteractionStrategy.Interactive;
        }

        /// <summary>
        /// Returns whether the primary window is interactively visible (shown and not minimized).
        /// </summary>
        public bool IsWindowVisible
        {
            get
            {
                if (!_window.Dispatcher.CheckAccess())
                {
                    return _window.Dispatcher.Invoke(() => IsWindowVisible);
                }
                return _window.Visibility == Visibility.Visible && _window.WindowState != WindowState.Minimized;
            }
        }

        public void InitializeIcons(ImageSource? icon = null)
        {
            if (!_window.Dispatcher.CheckAccess())
            {
                _window.Dispatcher.Invoke(() => InitializeIcons(icon));
                return;
            }

            _usesSharedIcon = icon == null;
            _icon = icon ?? AppIconImageProvider.GetSharedIconFrameForDpi(VisualTreeHelper.GetDpi(_window).DpiScaleX);
            EnsureIconsApplied();

            _logger.Debug("AppShellService", "app-shell-icons-initialized | scope=window-tray");
        }

        public void RefreshIconsForCurrentDpi()
        {
            if (!_window.Dispatcher.CheckAccess())
            {
                _window.Dispatcher.Invoke(RefreshIconsForCurrentDpi);
                return;
            }

            if (!_usesSharedIcon)
            {
                return;
            }

            _icon = AppIconImageProvider.GetSharedIconFrameForDpi(VisualTreeHelper.GetDpi(_window).DpiScaleX);
            EnsureIconsApplied();
            _logger.Debug("AppShellService", "app-shell-icons-refreshed | reason=dpi-change");
        }

        private void EnsureIconsApplied()
        {
            if (_icon == null)
            {
                return;
            }

            _window.Icon = _icon;
            _tray.IconSource = _icon;

            _tray.ToolTipText = AppConstants.Identity.DisplayName;

            ApplyWindowIconWin32();
        }

        private void ApplyWindowIconWin32()
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(_window).Handle;
                if (handle == IntPtr.Zero)
                {
                    return;
                }

                if (_nativeIconHandle == IntPtr.Zero)
                {
                    StreamResourceInfo resource = Application.GetResourceStream(new Uri(AppConstants.Files.IconPath, UriKind.Absolute));
                    if (resource is null)
                    {
                        _logger.Warning("AppShellService", "Win32 icon resource missing");
                        return;
                    }

                    using var icon = new Icon(resource.Stream);
                    _nativeIconHandle = CopyIcon(icon.Handle);
                    if (_nativeIconHandle == IntPtr.Zero)
                    {
                        _logger.Warning("AppShellService", "Win32 icon copy failed");
                        return;
                    }
                }

                SendMessage(handle, WM_SETICON, ICON_SMALL, _nativeIconHandle);
                SendMessage(handle, WM_SETICON, ICON_BIG, _nativeIconHandle);
            }
            catch (Exception ex)
            {
                _logger.Warning("AppShellService", "Win32 icon apply failed", nameof(ApplyWindowIconWin32), ex);
            }
        }

        private void RefreshWindowTaskbarIconOnce()
        {
            if (_windowTaskbarIconRefreshedOnce)
            {
                return;
            }

            if (_window.Icon == null)
            {
                return;
            }

            _windowTaskbarIconRefreshedOnce = true;

            var currentIcon = _window.Icon;
            _window.Icon = null;
            _window.Icon = currentIcon;

            ApplyWindowIconWin32();
        }

        private static void BringWindowToForegroundCore(Window window, IntPtr handle)
        {
            IntPtr foregroundHandle = GetForegroundWindow();
            uint currentThreadId = GetCurrentThreadId();
            uint foregroundThreadId = foregroundHandle != IntPtr.Zero
                ? GetWindowThreadProcessId(foregroundHandle, out _)
                : 0;

            bool attachedToForegroundThread = false;

            try
            {
                if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                {
                    attachedToForegroundThread = AttachThreadInput(currentThreadId, foregroundThreadId, true);
                }

                ShowWindow(handle, SW_RESTORE);
                BringWindowToTop(handle);
                SetForegroundWindow(handle);

                window.Activate();
                if (!window.IsActive)
                {
                    window.Topmost = true;
                    window.Topmost = false;
                }

                window.Focus();
            }
            finally
            {
                if (attachedToForegroundThread)
                {
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
            }
        }

        private static void CenterWindowCore(Window window)
        {
            double targetLeft = (SystemParameters.WorkArea.Width - window.Width) / 2 + SystemParameters.WorkArea.Left;
            double targetTop = (SystemParameters.WorkArea.Height - window.Height) / 2 + SystemParameters.WorkArea.Top;

            const double tolerance = 1.0;

            if (Math.Abs(window.Left - targetLeft) < tolerance && Math.Abs(window.Top - targetTop) < tolerance)
            {
                return;
            }

            window.Left = targetLeft;
            window.Top = targetTop;
        }

        private void EnsureTrayInitialized()
        {
            if (_trayInitialized) return;

            try
            {
                if (_icon == null) return;

                _tray.IconSource = _icon;
                _tray.ToolTipText = AppConstants.Identity.DisplayName;
                _trayInitialized = true;
                _logger.Debug("AppShellService", "app-shell-tray-initialized | mode=lazy");
            }
            catch (Exception ex)
            {
                _logger.Error("AppShellService", "Error initializing tray icon", nameof(EnsureTrayInitialized), ex);
            }
        }

        public void CenterWindow()
        {
            if (!_window.Dispatcher.CheckAccess())
            {
                _window.Dispatcher.Invoke(CenterWindow);
                return;
            }

            CenterWindowCore(_window);
        }

        /// <summary>
        /// Shows and activates the main window, centering on first show.
        /// </summary>
        public void ShowWindowFrontAndCenter()
        {
            if (!_window.Dispatcher.CheckAccess())
            {
                _window.Dispatcher.Invoke(ShowWindowFrontAndCenter);
                return;
            }

            try
            {
                IntPtr handle = new WindowInteropHelper(_window).Handle;
                if (handle == IntPtr.Zero)
                {
                    _logger.Error("AppShellService", "Window handle is invalid, cannot show window");
                    return;
                }

                _logger.Info(
                    "AppShellService",
                    () => $"app-shell-window-show-start | firstShow={!_hasShownOnce} trayInitialized={_trayInitialized} isVisible={IsWindowVisible}",
                    nameof(ShowWindowFrontAndCenter));

                if (!_hasShownOnce)
                {
                    _windowInteraction.Center(_window);
                    _hasShownOnce = true;
                }

                _window.ShowInTaskbar = true;
                _windowInteraction.PrepareForShow(_window);
                _window.Show();
                EnsureIconsApplied();
                RefreshWindowTaskbarIconOnce();

                _windowInteraction.BringToForeground(_window, handle);
                _logger.Info(
                    "AppShellService",
                    () => $"app-shell-window-show-complete | result=success windowState={_window.WindowState} showInTaskbar={_window.ShowInTaskbar}",
                    nameof(ShowWindowFrontAndCenter));
            }
            catch (Exception ex)
            {
                _logger.Error("AppShellService", "Error showing window", nameof(ShowWindowFrontAndCenter), ex);

                try
                {
                    _logger.Info("AppShellService", "app-shell-window-recovery-started | reason=show-window-failed");
                    _window.WindowState = WindowState.Normal;
                    _window.Opacity = 1;
                    _window.ShowInTaskbar = true;
                    _window.Show();

                    IntPtr recoveryHandle = new WindowInteropHelper(_window).Handle;
                    if (recoveryHandle != IntPtr.Zero)
                    {
                        _windowInteraction.BringToForeground(_window, recoveryHandle);
                    }

                    _logger.Info(
                        "AppShellService",
                        () => $"app-shell-window-recovery-complete | result=success windowState={_window.WindowState} showInTaskbar={_window.ShowInTaskbar}",
                        nameof(ShowWindowFrontAndCenter));
                }
                catch (Exception recoveryEx)
                {
                    _logger.Fatal("AppShellService", "Window recovery failed", nameof(ShowWindowFrontAndCenter), recoveryEx);
                    MessageBoxService.ShowError("The application window cannot be displayed. Please restart the app.");
                }
            }
        }

        public void StartHiddenToTray()
        {
            if (!_window.Dispatcher.CheckAccess())
            {
                _window.Dispatcher.Invoke(StartHiddenToTray);
                return;
            }

            try
            {
                EnsureTrayInitialized();
                EnsureIconsApplied();

                _window.Opacity = 0;
                _window.ShowInTaskbar = false;
                _window.WindowState = WindowState.Normal;
                _window.Hide();

                _tray.Visibility = Visibility.Visible;
                _logger.Debug("AppShellService", "app-shell-start-hidden-to-tray | result=success visibility=tray-only");
            }
            catch (Exception ex)
            {
                _logger.Error("AppShellService", "Error starting hidden in tray", nameof(StartHiddenToTray), ex);
            }
        }

        /// <summary>
        /// Hides the main window to tray and optionally shows a balloon tip.
        /// </summary>
        public void MinimizeToTray(Action? afterHide = null, bool showBalloon = false, string? appName = null)
        {
            if (!_window.Dispatcher.CheckAccess())
            {
                _window.Dispatcher.Invoke(() => MinimizeToTray(afterHide, showBalloon, appName));
                return;
            }

            try
            {
                EnsureTrayInitialized();

                _window.Opacity = 0;
                _window.ShowInTaskbar = false;
                _window.WindowState = WindowState.Minimized;
                _window.Hide();
                afterHide?.Invoke();

                _tray.Visibility = Visibility.Visible;

                if (showBalloon && appName != null)
                {
                    _tray.ShowBalloonTip(appName, "The application is still running in the background.", BalloonIcon.Info);
                    _logger.Debug("AppShellService", "app-shell-minimized-to-tray | result=success balloonShown=true");
                }
                else
                {
                    _logger.Debug("AppShellService", "app-shell-minimized-to-tray | result=success balloonShown=false");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("AppShellService", "Error minimizing to tray", nameof(MinimizeToTray), ex);
            }
        }

        public void Dispose()
        {
            if (!_window.Dispatcher.CheckAccess())
            {
                _window.Dispatcher.Invoke(Dispose);
                return;
            }

            try
            {
                if (_trayInitialized)
                {
                    _tray.Visibility = Visibility.Collapsed;
                }

                _tray.Dispose();
                _trayInitialized = false;
                if (_nativeIconHandle != IntPtr.Zero)
                {
                    DestroyIcon(_nativeIconHandle);
                    _nativeIconHandle = IntPtr.Zero;
                }
                _icon = null;
                _logger.Info("AppShellService", "app-shell-tray-disposed");
            }
            catch (Exception ex)
            {
                _logger.Error("AppShellService", "Error disposing TaskbarIcon", nameof(Dispose), ex);
            }

            GC.SuppressFinalize(this);
        }
    }
}
