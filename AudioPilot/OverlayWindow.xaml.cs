using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using Microsoft.Win32;

namespace AudioPilot
{
    public partial class OverlayWindow : Window
    {
        private const uint MonitorDefaultToNearest = 2;
        private const int WmDpiChanged = 0x02E0;
        private const int UserDefaultScreenDpi = 96;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private static readonly Logger _logger = Logger.Instance;
        private readonly DispatcherTimer _closeTimer;
        private readonly Storyboard? _fadeOutStoryboard;
        private readonly OverlayInlineBuilder _inlineBuilder;
        private readonly double _baseOverlayTextFontSize;
        private readonly double _baseMinHeightDip;
        private OverlayPosition _position = OverlayPosition.TopRight;
        private int _stackIndex;
        private double _durationSeconds = AudioPilot.Constants.AppConstants.Timing.OverlayAutoHideSeconds;
        private bool _repositionQueued;
        private bool _refreshTargetMonitorQueued;
        private HwndSource? _hwndSource;
        private nint _overlayWindowHandle;
        private nint _targetMonitor;
        private string _targetMonitorSource = "none";
        private bool _isFadeInRunning;
        private DoubleAnimation? _fadeInAnimation;
        private bool _isFadeInCompletionHooked;
        private bool _isFadeOutCompletionHooked;

        [LibraryImport("user32.dll")]
        private static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll")]
        private static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

        [LibraryImport("user32.dll")]
        private static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out POINT lpPoint);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

        [LibraryImport("Shcore.dll", SetLastError = true)]
        private static partial int GetScaleFactorForMonitor(nint hmonitor, out int scaleFactor);

        [LibraryImport("user32.dll")]
        private static partial uint GetDpiForWindow(nint hwnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        public OverlayWindow(string text)
        {
            InitializeComponent();

            ShowActivated = false;
            _inlineBuilder = new OverlayInlineBuilder(new ColorEmojiImageSourceFactory());
            SetOverlayText(text);

            Opacity = 1.0;

            _closeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(_durationSeconds)
            };
            _closeTimer.Tick += OnCloseTimerTick;

            _fadeOutStoryboard = Resources["FadeOutStoryboard"] as Storyboard;
            _baseOverlayTextFontSize = OverlayText.FontSize;
            _baseMinHeightDip = MinHeight;

            SourceInitialized += OnSourceInitialized;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            _overlayWindowHandle = _hwndSource?.Handle ?? nint.Zero;
            _hwndSource?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmDpiChanged)
            {
                if (lParam != IntPtr.Zero)
                {
                    RECT suggestedRect = Marshal.PtrToStructure<RECT>(lParam);
                    POINT suggestedCenter = new()
                    {
                        x = suggestedRect.left + ((suggestedRect.right - suggestedRect.left) / 2),
                        y = suggestedRect.top + ((suggestedRect.bottom - suggestedRect.top) / 2)
                    };

                    nint suggestedMonitor = MonitorFromPoint(suggestedCenter, MonitorDefaultToNearest);
                    if (suggestedMonitor != nint.Zero)
                    {
                        _targetMonitor = suggestedMonitor;
                        _targetMonitorSource = "dpi-suggested-rect";
                    }
                }

                QueueReposition();
            }

            return IntPtr.Zero;
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            QueueReposition(refreshTargetMonitor: true);
        }

        private void QueueReposition(bool refreshTargetMonitor = false)
        {
            if (refreshTargetMonitor)
            {
                _refreshTargetMonitorQueued = true;
            }

            if (!IsVisible || _repositionQueued)
            {
                return;
            }

            _repositionQueued = true;
            if (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(Dispatcher))
            {
                _repositionQueued = false;
                return;
            }

            try
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    _repositionQueued = false;
                    if (!IsVisible)
                    {
                        return;
                    }

                    if (_refreshTargetMonitorQueued)
                    {
                        _refreshTargetMonitorQueued = false;
                        RefreshTargetMonitor();
                    }

                    PositionWindow();
                }, DispatcherPriority.Loaded);
            }
            catch (InvalidOperationException ex) when (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(Dispatcher))
            {
                _repositionQueued = false;
                TryLogQueueRepositionShutdownWarning(ex);
            }
        }

        private void TryLogQueueRepositionShutdownWarning(Exception ex)
        {
            const string message = "Skipping queued reposition because dispatcher shutdown is in progress";

            try
            {
                _logger.Warning("OverlayWindow", message, nameof(QueueReposition), ex);
            }
            catch (Exception loggingEx)
            {
                LifecycleFallbackDiagnostics.Write("OverlayWindow", message, nameof(QueueReposition), ex, loggingEx);
            }
        }

        private void RefreshTargetMonitor()
        {
            if (TryGetTargetMonitor(out nint monitor, out string source))
            {
                _targetMonitor = monitor;
                _targetMonitorSource = source;
            }
        }

        public void UpdateContent(string text)
        {
            SetOverlayText(text);
            Opacity = 1.0;
            StopFadeOut();
            UpdateLayout();
        }

        public void UpdateContent(OverlayActionStateKind stateKind, string text)
        {
            ResetOverlayText();

            string brushKey = stateKind switch
            {
                OverlayActionStateKind.Enabled => "OverlaySuccessTextBrush",
                _ => "OverlayErrorDeviceBrush"
            };

            AppendOverlayText(text, FontWeights.Bold, brushKey);

            Opacity = 1.0;
            StopFadeOut();
            UpdateLayout();
        }

        public void ApplyDisplayOptions(OverlayPosition position, double durationSeconds, int stackIndex)
        {
            _position = position;
            _stackIndex = Math.Max(0, stackIndex);
            _durationSeconds = Math.Clamp(durationSeconds, 0.5, 10.0);
            _closeTimer.Interval = TimeSpan.FromSeconds(_durationSeconds);
        }

        public void UpdateContent(string header, string deviceName)
        {
            UpdateContent(OverlayDeviceKind.Output, header, deviceName);
        }

        public void UpdateContent(OverlayDeviceKind kind, string header, string deviceName)
        {
            ResetOverlayText();
            AppendOverlayText(header + "\n", FontWeights.Bold, "OverlayPrimaryTextBrush");

            string deviceBrushKey = kind switch
            {
                OverlayDeviceKind.Input => "OverlayInputDeviceBrush",
                OverlayDeviceKind.Error => "OverlayErrorDeviceBrush",
                _ => "OverlayOutputDeviceBrush"
            };

            if (kind == OverlayDeviceKind.Input &&
                deviceName.Contains('\n', StringComparison.Ordinal) &&
                TrySplitListenOverlayDeviceLines(deviceName, out string inputLine, out string outputLine))
            {
                AppendOverlayText(inputLine, FontWeights.Bold, "OverlayInputDeviceBrush");
                OverlayText.Inlines.Add(new System.Windows.Documents.LineBreak());
                AppendOverlayText(outputLine, FontWeights.Bold, "OverlayOutputDeviceBrush");
            }
            else
            {
                AppendOverlayText(deviceName, FontWeights.Bold, deviceBrushKey);
            }

            Opacity = 1.0;
            StopFadeOut();
            UpdateLayout();
        }

        public void UpdateRoutineContent(string header, string? outputDeviceName, string? inputDeviceName)
        {
            ResetOverlayText();
            AppendOverlayText(header + "\n", FontWeights.Bold, "OverlayPrimaryTextBrush");

            bool wroteLine = false;
            if (!string.IsNullOrWhiteSpace(outputDeviceName))
            {
                AppendOverlayText(outputDeviceName, FontWeights.Bold, "OverlayOutputDeviceBrush");
                wroteLine = true;
            }

            if (!string.IsNullOrWhiteSpace(inputDeviceName))
            {
                if (wroteLine)
                {
                    OverlayText.Inlines.Add(new System.Windows.Documents.LineBreak());
                }

                AppendOverlayText(inputDeviceName, FontWeights.Bold, "OverlayInputDeviceBrush");
            }

            Opacity = 1.0;
            StopFadeOut();
            UpdateLayout();
        }

        public void UpdateRoutinePartialContent(string header, string? outputDeviceName, string? inputDeviceName, string? failedOutputDeviceName, string? failedInputDeviceName)
        {
            ResetOverlayText();
            AppendOverlayText(header + "\n", FontWeights.Bold, "OverlayPrimaryTextBrush");

            bool wroteLine = false;

            if (!string.IsNullOrWhiteSpace(outputDeviceName))
            {
                AddRoutinePartialLine("Output: ", outputDeviceName, "OverlayOutputDeviceBrush", wroteLine);
                wroteLine = true;
            }

            if (!string.IsNullOrWhiteSpace(failedOutputDeviceName))
            {
                AddRoutinePartialLine("Output failed: ", failedOutputDeviceName, "OverlayErrorDeviceBrush", wroteLine);
                wroteLine = true;
            }

            if (!string.IsNullOrWhiteSpace(inputDeviceName))
            {
                AddRoutinePartialLine("Input: ", inputDeviceName, "OverlayInputDeviceBrush", wroteLine);
                wroteLine = true;
            }

            if (!string.IsNullOrWhiteSpace(failedInputDeviceName))
            {
                AddRoutinePartialLine("Input failed: ", failedInputDeviceName, "OverlayErrorDeviceBrush", wroteLine);
            }

            Opacity = 1.0;
            StopFadeOut();
            UpdateLayout();
        }

        private void AddRoutinePartialLine(string label, string deviceName, string brushKey, bool insertLineBreak)
        {
            if (insertLineBreak)
            {
                OverlayText.Inlines.Add(new System.Windows.Documents.LineBreak());
            }

            AppendOverlayText(label, FontWeights.SemiBold, "OverlayPrimaryTextBrush");
            AppendOverlayText(deviceName, FontWeights.Bold, brushKey);
        }

        private static bool TrySplitListenOverlayDeviceLines(string deviceName, out string inputLine, out string outputLine)
        {
            inputLine = string.Empty;
            outputLine = string.Empty;

            string[] parts = deviceName.Split('\n', 2, StringSplitOptions.None);
            if (parts.Length != 2)
            {
                return false;
            }

            inputLine = parts[0];
            outputLine = parts[1];

            return !string.IsNullOrWhiteSpace(inputLine) &&
                   outputLine.StartsWith("To: ", StringComparison.OrdinalIgnoreCase);
        }

        private void SetOverlayText(string text)
        {
            ResetOverlayText();
            AppendOverlayText(text, FontWeights.Normal);
        }

        private void ResetOverlayText()
        {
            OverlayText.Text = string.Empty;
            OverlayText.Inlines.Clear();
        }

        private void AppendOverlayText(string text, FontWeight fontWeight, string? brushKey = null)
        {
            _inlineBuilder.Append(OverlayText, text, fontWeight, brushKey);
        }

        public void UpdateContent(string header, string title, string? artist)
        {
            ResetOverlayText();
            AppendOverlayText(header + "\n", FontWeights.Bold, "OverlayPrimaryTextBrush");
            AppendOverlayText(title, FontWeights.Bold, "OverlayOutputDeviceBrush");

            if (!string.IsNullOrWhiteSpace(artist))
            {
                OverlayText.Inlines.Add(new System.Windows.Documents.LineBreak());

                AppendOverlayText(artist, FontWeights.Bold, "OverlayInputDeviceBrush");
            }

            Opacity = 1.0;
            StopFadeOut();
            UpdateLayout();
        }

        private void OnCloseTimerTick(object? sender, EventArgs e)
        {
            _closeTimer.Stop();
            BeginFadeOutAndClose();
        }

        private void StopFadeOut()
        {
            if (_fadeInAnimation != null && _isFadeInCompletionHooked)
            {
                _fadeInAnimation.Completed -= OnFadeInCompleted;
                _isFadeInCompletionHooked = false;
            }

            _fadeInAnimation = null;

            if (_fadeOutStoryboard != null && _isFadeOutCompletionHooked)
            {
                _fadeOutStoryboard.Completed -= OnFadeCompleted;
                _isFadeOutCompletionHooked = false;
            }

            if (_fadeOutStoryboard != null)
            {
                _fadeOutStoryboard.Stop(this);
                _fadeOutStoryboard.Remove(this);
            }

            BeginAnimation(OpacityProperty, null);
            _isFadeInRunning = false;
        }

        private void BeginFadeIn()
        {
            _isFadeInRunning = true;

            _fadeInAnimation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(AppConstants.Overlay.FadeInDurationMs),
                FillBehavior = FillBehavior.Stop
            };

            _fadeInAnimation.Completed += OnFadeInCompleted;
            _isFadeInCompletionHooked = true;

            BeginAnimation(OpacityProperty, _fadeInAnimation, HandoffBehavior.SnapshotAndReplace);
        }

        private void OnFadeInCompleted(object? sender, EventArgs e)
        {
            if (_fadeInAnimation != null && _isFadeInCompletionHooked)
            {
                _fadeInAnimation.Completed -= OnFadeInCompleted;
                _isFadeInCompletionHooked = false;
            }

            _fadeInAnimation = null;
            _isFadeInRunning = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1.0;
        }

        private void BeginFadeOutAndClose()
        {
            if (_fadeOutStoryboard != null)
            {
                if (!_isFadeOutCompletionHooked)
                {
                    _fadeOutStoryboard.Completed += OnFadeCompleted;
                    _isFadeOutCompletionHooked = true;
                }

                _fadeOutStoryboard.Begin(this, HandoffBehavior.SnapshotAndReplace);
            }
            else
            {
                Hide();
            }
        }

        private void OnFadeCompleted(object? sender, EventArgs e)
        {
            if (_fadeOutStoryboard != null && _isFadeOutCompletionHooked)
            {
                _fadeOutStoryboard.Completed -= OnFadeCompleted;
                _isFadeOutCompletionHooked = false;
            }

            Hide();
        }

        private void PositionWindow()
        {
            nint overlayWindowHandle = _overlayWindowHandle;
            if (overlayWindowHandle == nint.Zero)
            {
                return;
            }

            if (_targetMonitor == nint.Zero)
            {
                RefreshTargetMonitor();
            }

            nint monitor = _targetMonitor;
            string source = _targetMonitorSource;

            if (monitor == nint.Zero ||
                !TryGetMonitorRectsPixels(monitor, out Rect workAreaPixels, out Rect monitorBoundsPixels))
            {
                return;
            }

            if (!TryGetMonitorScale(monitor, overlayWindowHandle, out double targetScaleX, out double targetScaleY))
            {
                return;
            }

            OverlayText.FontSize = _baseOverlayTextFontSize;
            MinHeight = _baseMinHeightDip;

            double workLeftPx = workAreaPixels.Left;
            double workTopPx = workAreaPixels.Top;
            double workRightPx = workAreaPixels.Right;
            double workBottomPx = workAreaPixels.Bottom;
            int workWidthPx = Math.Max(0, (int)Math.Round(workAreaPixels.Width));
            int workHeightPx = Math.Max(0, (int)Math.Round(workAreaPixels.Height));
            if (workWidthPx <= 0 || workHeightPx <= 0)
            {
                return;
            }

            double monitorLeftPx = monitorBoundsPixels.Left;
            double monitorTopPx = monitorBoundsPixels.Top;
            double monitorRightPx = monitorBoundsPixels.Right;
            double monitorBottomPx = monitorBoundsPixels.Bottom;
            int monitorWidthPx = Math.Max(0, (int)Math.Round(monitorBoundsPixels.Width));
            int monitorHeightPx = Math.Max(0, (int)Math.Round(monitorBoundsPixels.Height));
            if (monitorWidthPx <= 0 || monitorHeightPx <= 0)
            {
                return;
            }

            int overlayMarginPxX = Math.Max(1, (int)Math.Round(AppConstants.Overlay.MarginDip * targetScaleX));
            int overlayMarginPxY = Math.Max(1, (int)Math.Round(AppConstants.Overlay.MarginDip * targetScaleY));
            int overlayStackGapPx = Math.Max(1, (int)Math.Round(AppConstants.Overlay.StackGapDip * targetScaleY));
            int overlayTargetWidthPx = AppConstants.Overlay.TargetWidthPx;
            int overlayMinAvailableWidthPx = Math.Max(1, (int)Math.Round(AppConstants.Overlay.MinAvailableWidthDip * targetScaleX));
            int overlayMinAvailableHeightPx = Math.Max(1, (int)Math.Round(AppConstants.Overlay.MinAvailableHeightDip * targetScaleY));

            int availableWidthPx = Math.Max(overlayMinAvailableWidthPx, workWidthPx - (overlayMarginPxX * 2));
            int availableHeightPx = Math.Max(overlayMinAvailableHeightPx, workHeightPx - (overlayMarginPxY * 2));
            int targetWidthPx = Math.Min(overlayTargetWidthPx, availableWidthPx);

            double widthDip = targetWidthPx / targetScaleX;
            MaxWidth = availableWidthPx / targetScaleX;
            MaxHeight = availableHeightPx / targetScaleY;
            Width = widthDip;
            UpdateLayout();

            double measuredHeightDip = ActualHeight > 0 ? ActualHeight : Math.Max(MinHeight, 1d);
            int measuredHeightPx = Math.Max(1, (int)Math.Ceiling(measuredHeightDip * targetScaleY));
            int targetHeightPx = Math.Min(Math.Max(1, measuredHeightPx), availableHeightPx);

            double leftPx;
            double topPx;

            switch (_position)
            {
                case OverlayPosition.TopLeft:
                    leftPx = workLeftPx + overlayMarginPxX;
                    topPx = workTopPx + overlayMarginPxY;
                    break;
                case OverlayPosition.TopCenter:
                    leftPx = monitorLeftPx + ((monitorWidthPx - targetWidthPx) / 2.0);
                    topPx = workTopPx + overlayMarginPxY;
                    break;
                case OverlayPosition.TopRight:
                    leftPx = workRightPx - targetWidthPx - overlayMarginPxX;
                    topPx = workTopPx + overlayMarginPxY;
                    break;
                case OverlayPosition.BottomLeft:
                    leftPx = workLeftPx + overlayMarginPxX;
                    topPx = workBottomPx - targetHeightPx - overlayMarginPxY;
                    break;
                case OverlayPosition.BottomCenter:
                    leftPx = monitorLeftPx + ((monitorWidthPx - targetWidthPx) / 2.0);
                    topPx = workBottomPx - targetHeightPx - overlayMarginPxY;
                    break;
                case OverlayPosition.Center:
                    leftPx = monitorLeftPx + ((monitorWidthPx - targetWidthPx) / 2.0);
                    topPx = monitorTopPx + ((monitorHeightPx - targetHeightPx) / 2.0);
                    break;
                default:
                    leftPx = workRightPx - targetWidthPx - overlayMarginPxX;
                    topPx = workBottomPx - targetHeightPx - overlayMarginPxY;
                    break;
            }

            bool centeredHorizontally = _position is OverlayPosition.TopCenter or OverlayPosition.BottomCenter or OverlayPosition.Center;
            bool centeredVertically = _position is OverlayPosition.Center;

            double minLeftPx = centeredHorizontally ? monitorLeftPx : workLeftPx + overlayMarginPxX;
            double maxLeftPx = centeredHorizontally ? monitorRightPx - targetWidthPx : workRightPx - targetWidthPx - overlayMarginPxX;
            double minTopPx = centeredVertically ? monitorTopPx : workTopPx + overlayMarginPxY;
            double maxTopPx = centeredVertically ? monitorBottomPx - targetHeightPx : workBottomPx - targetHeightPx - overlayMarginPxY;

            double stackOffsetPx = _stackIndex * (targetHeightPx + overlayStackGapPx);
            bool anchorFromBottom = _position is OverlayPosition.BottomLeft or OverlayPosition.BottomCenter or OverlayPosition.BottomRight;
            topPx = anchorFromBottom ? topPx - stackOffsetPx : topPx + stackOffsetPx;

            int finalLeftPx = (int)Math.Round(ClampToRange(leftPx, minLeftPx, maxLeftPx));
            int finalTopPx = (int)Math.Round(ClampToRange(topPx, minTopPx, maxTopPx));

            SetWindowPos(
                overlayWindowHandle,
                nint.Zero,
                finalLeftPx,
                finalTopPx,
                targetWidthPx,
                targetHeightPx,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace(
                    "OverlayWindow",
                    $"{AppConstants.Audio.LogEvents.Overlay.Position} | source={source} anchor={_position} monitor=0x{monitor.ToInt64():X} dpiScale={targetScaleX:F2},{targetScaleY:F2} monitorPx={monitorLeftPx:F0},{monitorTopPx:F0},{monitorWidthPx},{monitorHeightPx} workPx={workLeftPx:F0},{workTopPx:F0},{workWidthPx},{workHeightPx} windowPx={targetWidthPx}x{targetHeightPx} finalPx={finalLeftPx},{finalTopPx}",
                    nameof(PositionWindow));
            }
        }

        private static bool TryGetMonitorScale(nint monitor, nint overlayWindowHandle, out double scaleX, out double scaleY)
        {
            scaleX = 1.0;
            scaleY = 1.0;

            if (GetScaleFactorForMonitor(monitor, out int scaleFactor) == 0 && scaleFactor >= 100 && scaleFactor <= 500)
            {
                scaleX = scaleFactor / 100.0;
                scaleY = scaleX;
                return true;
            }

            if (overlayWindowHandle != nint.Zero)
            {
                uint windowDpi = GetDpiForWindow(overlayWindowHandle);
                if (windowDpi > 0)
                {
                    scaleX = windowDpi / (double)UserDefaultScreenDpi;
                    scaleY = windowDpi / (double)UserDefaultScreenDpi;
                    return true;
                }
            }

            return false;
        }

        private static double ClampToRange(double value, double min, double max)
        {
            if (max < min)
            {
                return min;
            }

            return Math.Min(Math.Max(value, min), max);
        }

        private bool TryGetTargetMonitor(out nint monitor, out string source)
        {
            monitor = nint.Zero;
            source = "none";

            nint overlayWindowHandle = _overlayWindowHandle;

            if (GetCursorPos(out POINT cursorPoint))
            {
                monitor = MonitorFromPoint(cursorPoint, MonitorDefaultToNearest);
                if (monitor != nint.Zero)
                {
                    source = "cursor";
                    return true;
                }
            }

            nint foregroundWindow = GetForegroundWindow();
            if (foregroundWindow != nint.Zero && foregroundWindow != overlayWindowHandle)
            {
                monitor = MonitorFromWindow(foregroundWindow, MonitorDefaultToNearest);
                if (monitor != nint.Zero)
                {
                    source = "foreground-window";
                    return true;
                }
            }

            if (overlayWindowHandle != nint.Zero)
            {
                monitor = MonitorFromWindow(overlayWindowHandle, MonitorDefaultToNearest);
                if (monitor != nint.Zero)
                {
                    source = "overlay-window";
                    return true;
                }
            }

            nint originMonitor = MonitorFromPoint(new POINT { x = 0, y = 0 }, MonitorDefaultToNearest);
            if (originMonitor != nint.Zero)
            {
                monitor = originMonitor;
                source = "origin-fallback";
                return true;
            }

            return false;
        }

        private static bool TryGetMonitorRectsPixels(nint monitor, out Rect workArea, out Rect monitorBounds)
        {
            workArea = Rect.Empty;
            monitorBounds = Rect.Empty;

            if (monitor == nint.Zero)
            {
                return false;
            }

            MONITORINFO info = new()
            {
                cbSize = (uint)Marshal.SizeOf<MONITORINFO>()
            };

            if (!GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            double workWidth = info.rcWork.right - info.rcWork.left;
            double workHeight = info.rcWork.bottom - info.rcWork.top;
            if (workWidth <= 0 || workHeight <= 0)
            {
                return false;
            }

            double monitorWidth = info.rcMonitor.right - info.rcMonitor.left;
            double monitorHeight = info.rcMonitor.bottom - info.rcMonitor.top;
            if (monitorWidth <= 0 || monitorHeight <= 0)
            {
                return false;
            }

            workArea = new Rect(info.rcWork.left, info.rcWork.top, workWidth, workHeight);
            monitorBounds = new Rect(info.rcMonitor.left, info.rcMonitor.top, monitorWidth, monitorHeight);
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public void ShowOverlay()
        {
            _closeTimer?.Stop();
            StopFadeOut();
            RefreshTargetMonitor();

            bool wasHidden = !IsVisible || Visibility != Visibility.Visible;

            if (wasHidden)
            {
                Opacity = 0.0;
            }

            if (!IsVisible)
            {
                Show();
            }
            else if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
            }

            PositionWindow();

            if (wasHidden && !_isFadeInRunning)
            {
                BeginFadeIn();
            }
            else if (!wasHidden)
            {
                Opacity = 1.0;
            }

            _closeTimer?.Start();
        }

        public void Cleanup()
        {
            SourceInitialized -= OnSourceInitialized;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _hwndSource?.RemoveHook(WndProc);
            _hwndSource = null;

            if (_closeTimer != null)
            {
                _closeTimer.Stop();
                _closeTimer.Tick -= OnCloseTimerTick;
            }

            StopFadeOut();
        }

        protected override void OnClosed(EventArgs e)
        {
            Cleanup();
            base.OnClosed(e);
        }
    }
}
