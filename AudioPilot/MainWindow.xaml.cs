using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.ViewModels;
using Microsoft.Win32;
using Microsoft.Xaml.Behaviors;
using NAudio.CoreAudioApi;

namespace AudioPilot
{
    internal readonly record struct MainWindowRuntimeDependencies(
        SingleInstanceHelper SingleInstance,
        AppRuntimeServiceBundle RuntimeServices,
        AudioDeviceService AudioService,
        SettingsService SettingsService,
        HotkeyService HotkeyService,
        StartupService StartupService);

    internal readonly record struct MainWindowUiServices(
        AppShellService Shell,
        OverlayService OverlayService,
        MediaOverlayCommandService MediaOverlayCommands);

    internal readonly record struct MainWindowComposition(
        AppViewModel AppViewModel,
        MainWindowHotkeyBindings HotkeyBindings,
        MainWindowLifecycleCoordinator LifecycleCoordinator,
        MainWindowHotplugOverlayCoordinator HotplugOverlayCoordinator,
        MainWindowStartupResumeCoordinator StartupResumeCoordinator,
        MainWindowVisibilityCoordinator VisibilityCoordinator);

    internal readonly record struct RestoreMixerScrollState(
        double HeaderOffsetY,
        double TargetOffset,
        double ScrollableHeight);

    public partial class MainWindow : Window
    {
        private const int WM_DPICHANGED = 0x02E0;

        private readonly SingleInstanceHelper _singleInstance;
        private readonly AppRuntimeServiceBundle _runtimeServices;
        private readonly AudioDeviceService _audioService;
        private readonly SettingsService _settingsService;
        private readonly HotkeyService _hotkeyService;
        private readonly StartupService _startupService;
        private readonly AppShellService _shell;
        private readonly OverlayService _overlayService;
        private readonly MediaOverlayCommandService _mediaOverlayCommands;
        private readonly AppViewModel _appVm;
        private readonly Logger _logger;
        private readonly MainWindowHotkeyBindings _hotkeyBindings;
        private readonly MainWindowLifecycleCoordinator _lifecycleCoordinator;
        private readonly MainWindowHotplugOverlayCoordinator _hotplugOverlayCoordinator;
        private readonly MainWindowStartupResumeCoordinator _startupResumeCoordinator;
        private readonly MainWindowVisibilityCoordinator _visibilityCoordinator;
        private bool _sourceInitialized;
        private int _shutdownStarted;
        private int _hotplugIgnoredDuringShutdownLogged;
        private int _pendingHotplugSignals;
        private int _hotplugSuppressedRefreshes;
        private int _hotplugCoalescedEvents;
        private int _hotplugAppliedRefreshes;
        private CancellationTokenSource? _hotplugRefreshDebounceCts;
        private CancellationTokenSource? _restoreMixerScrollCts;
        private int _shutdownCleanupStarted;
        private int _shutdownCleanupCompleted;
        private bool _systemEventHandlersRegistered;
        private HwndSource? _windowSource;
        internal AppViewModel AppViewModel => _appVm;

        public MainWindow()
        {
            Logger logger = Logger.Instance;
            bool infoEnabled = logger.IsEnabled(LogLevel.Info);
            long constructorStartTimestamp = 0;
            if (infoEnabled)
            {
                constructorStartTimestamp = Stopwatch.GetTimestamp();
            }

            InitializeComponent();

            MainWindowRuntimeDependencies runtimeDependencies = CreateRuntimeDependencies();
            _singleInstance = runtimeDependencies.SingleInstance;
            _runtimeServices = runtimeDependencies.RuntimeServices;
            _audioService = runtimeDependencies.AudioService;
            _settingsService = runtimeDependencies.SettingsService;
            _hotkeyService = runtimeDependencies.HotkeyService;
            _startupService = runtimeDependencies.StartupService;

            MainWindowUiServices uiServices = CreateUiServices();
            _shell = uiServices.Shell;
            _overlayService = uiServices.OverlayService;
            _mediaOverlayCommands = uiServices.MediaOverlayCommands;
            _logger = logger;

            MainWindowComposition composition = CreateMainWindowComposition();
            _appVm = composition.AppViewModel;
            _hotkeyBindings = composition.HotkeyBindings;
            _lifecycleCoordinator = composition.LifecycleCoordinator;
            _hotplugOverlayCoordinator = composition.HotplugOverlayCoordinator;
            _startupResumeCoordinator = composition.StartupResumeCoordinator;
            _visibilityCoordinator = composition.VisibilityCoordinator;

            DataContext = _appVm;

            RegisterGlobalEventHandlers();
            RegisterWindowEventHandlers();

            if (infoEnabled)
            {
                _logger.Info("MainWindow", () => $"main-window-constructor-completed | elapsedMs={Stopwatch.GetElapsedTime(constructorStartTimestamp).TotalMilliseconds:F1}");
            }
        }

        private static MainWindowRuntimeDependencies CreateRuntimeDependencies()
        {
            SingleInstanceHelper singleInstance = ApplicationBootstrapper.GetSingleInstance();
            AppRuntimeServiceBundle runtimeServices = AppRuntimeServiceBundle.CreateDefault();
            AudioDeviceService audioService = runtimeServices.AudioService;
            DeviceCacheHelper.Initialize(audioService);

            return new MainWindowRuntimeDependencies(
                singleInstance,
                runtimeServices,
                audioService,
                runtimeServices.SettingsService,
                new HotkeyService(),
                runtimeServices.StartupService);
        }

        private MainWindowUiServices CreateUiServices()
        {
            return new MainWindowUiServices(
                new AppShellService(this, taskbarIcon),
                new OverlayService(),
                new MediaOverlayCommandService());
        }

        private MainWindowComposition CreateMainWindowComposition()
        {
            AppViewModel? appViewModel = null;
            var visibilityCoordinator = new MainWindowVisibilityCoordinator(_logger);
            var cliOverlayCoordinator = new AppCliOverlayCoordinator(
                _audioService,
                _overlayService,
                _mediaOverlayCommands,
                _logger,
                () => appViewModel?.CurrentSettings);
            var switchCoordinator = new AppSwitchCommandCoordinator(
                _audioService,
                _overlayService,
                _logger,
                _runtimeServices.BluetoothReconnectCoordinator,
                (output, suppressMs) => appViewModel?.SuppressConnectedHotplugOverlay(output, suppressMs));

            AppViewModel composedAppViewModel = appViewModel = new AppViewModel(
                settings: _settingsService,
                startup: _startupService,
                audio: _audioService,
                hotkeys: _hotkeyService,
                cliOverlayCoordinator: cliOverlayCoordinator,
                switchCoordinator: switchCoordinator,
                shell: _shell,
                mixerFactory: () => new MixerViewModel(_audioService, Dispatcher, AudioMixerMode.Output, _logger, DeviceCacheHelper.Instance),
                inputMixerFactory: () => new MixerViewModel(_audioService, Dispatcher, AudioMixerMode.Input, _logger, DeviceCacheHelper.Instance),
                overlay: _overlayService,
                dispatcher: Dispatcher,
                routineBluetoothReconnectCoordinator: _runtimeServices.BluetoothReconnectCoordinator,
                deviceCache: DeviceCacheHelper.Instance);

            var startupCoordinator = new AppStartupCoordinator(
                composedAppViewModel,
                _hotkeyService,
                onStartHiddenToTray: visibilityCoordinator.MarkPendingAutoScrollOnNextShow);
            var hotplugOverlayCoordinator = new MainWindowHotplugOverlayCoordinator(_settingsService, composedAppViewModel, _overlayService);

            return new MainWindowComposition(
                composedAppViewModel,
                CreateHotkeyBindings(composedAppViewModel),
                new MainWindowLifecycleCoordinator(_logger, Dispatcher, AppConstants.Timing.ShutdownStepTimeoutMs),
                hotplugOverlayCoordinator,
                new MainWindowStartupResumeCoordinator(
                    _logger,
                    _audioService,
                    _settingsService,
                    startupCoordinator,
                    composedAppViewModel,
                    hotplugOverlayCoordinator,
                    message => MessageBoxService.ShowError(message, DialogText.Captions.StartupError),
                    () => Application.Current.Shutdown()),
                visibilityCoordinator);
        }

        private MainWindowHotkeyBindings CreateHotkeyBindings(AppViewModel appViewModel)
        {
            return new MainWindowHotkeyBindings(
                _hotkeyService,
                onShowApp: OnShowAppHotkey,
                onMediaShowCurrentTrack: () => RunBackgroundHotkeyAction(
                    () => appViewModel.ShowCurrentTrackFromCli(),
                    "Show current track hotkey action error",
                    nameof(appViewModel.ShowCurrentTrackFromCli)),
                onMediaPlayPause: () => SendMediaHotkeyWithOverlay(MediaOverlayCommand.PlayPause, MediaKeyHelper.TryPressPlayPause),
                onMediaNextTrack: () => SendMediaHotkeyWithOverlay(MediaOverlayCommand.NextTrack, MediaKeyHelper.TryPressNextTrack),
                onMediaPreviousTrack: () => SendMediaHotkeyWithOverlay(MediaOverlayCommand.PreviousTrack, MediaKeyHelper.TryPressPreviousTrack),
                onMuteMic: () => ToggleFlagWithOverlay(
                    toggleAction: () => appViewModel.MuteMic = !appViewModel.MuteMic,
                    readState: () => appViewModel.MuteMic,
                    enabledMessage: "Microphone muted",
                    disabledMessage: "Microphone unmuted",
                    enabledStateKind: OverlayActionStateKind.Disabled,
                    disabledStateKind: OverlayActionStateKind.Enabled),
                onMuteSound: () => ToggleFlagWithOverlay(
                    toggleAction: () => appViewModel.MuteSound = !appViewModel.MuteSound,
                    readState: () => appViewModel.MuteSound,
                    enabledMessage: "Sound muted",
                    disabledMessage: "Sound unmuted",
                    enabledStateKind: OverlayActionStateKind.Disabled,
                    disabledStateKind: OverlayActionStateKind.Enabled),
                onDeafen: () => ToggleFlagWithOverlay(
                    toggleAction: () => appViewModel.Deafen = !appViewModel.Deafen,
                    readState: () => appViewModel.Deafen,
                    enabledMessage: "Deafened",
                    disabledMessage: "Undeafened",
                    enabledStateKind: OverlayActionStateKind.Disabled,
                    disabledStateKind: OverlayActionStateKind.Enabled),
                onListenToInput: () => RunBackgroundHotkeyAction(
                    () => _ = appViewModel.ToggleListenToInputFromCli(),
                    "Listen hotkey action error",
                    nameof(appViewModel.ToggleListenToInputFromCli)),
                onMasterVolumeUp: () => RunBackgroundHotkeyAction(
                    () => _ = appViewModel.StepMasterVolumeUpFromCli(),
                    "Master volume up hotkey action error",
                    nameof(appViewModel.StepMasterVolumeUpFromCli)),
                onMasterVolumeDown: () => RunBackgroundHotkeyAction(
                    () => _ = appViewModel.StepMasterVolumeDownFromCli(),
                    "Master volume down hotkey action error",
                    nameof(appViewModel.StepMasterVolumeDownFromCli)),
                onMicVolumeUp: () => RunBackgroundHotkeyAction(
                    () => _ = appViewModel.StepMicVolumeUpFromCli(),
                    "Microphone volume up hotkey action error",
                    nameof(appViewModel.StepMicVolumeUpFromCli)),
                onMicVolumeDown: () => RunBackgroundHotkeyAction(
                    () => _ = appViewModel.StepMicVolumeDownFromCli(),
                    "Microphone volume down hotkey action error",
                    nameof(appViewModel.StepMicVolumeDownFromCli)),
                onInputSwitch: OnInputSwitchHotkey,
                onOutputSwitch: OnSwitchHotkey,
                onInputReverseSwitch: OnInputSwitchHotkeyReverse,
                onOutputReverseSwitch: OnSwitchHotkeyReverse);
        }

        private void ClearHotkeyText_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetHotkeyTextBoxContext(sender, out TextBox textBox, out IHotkeySink target))
            {
                return;
            }

            target.Reset();
            textBox.Text = target.DisplayText;
            textBox.SelectionLength = 0;
            textBox.SelectionStart = textBox.Text.Length;
        }

        private static bool TryGetHotkeyTextBoxContext(
            object sender,
            out TextBox textBox,
            out IHotkeySink target)
        {
            textBox = null!;
            target = null!;

            if (sender is not FrameworkElement { Parent: ContextMenu { PlacementTarget: TextBox placementTarget } })
            {
                return false;
            }

            textBox = placementTarget;
            foreach (Behavior behavior in Interaction.GetBehaviors(textBox))
            {
                if (behavior is HotkeyCaptureBehavior { Target: not null } hotkeyBehavior)
                {
                    target = hotkeyBehavior.Target;
                    return true;
                }
            }

            return false;
        }

        private void RegisterGlobalEventHandlers()
        {
            Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            _audioService.DeviceStateChanged += OnAudioDeviceStateChanged;
        }

        private void RegisterWindowEventHandlers()
        {
            Loaded += MainWindow_Loaded;
            IsVisibleChanged += MainWindow_IsVisibleChanged;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (_sourceInitialized) return;

            _logger.Debug("MainWindow", "main-window-source-initialized | action=initialize-hotkey-service");

            var windowHelper = new WindowInteropHelper(this);
            _hotkeyService.InitializeInfrastructure();
            _windowSource = HwndSource.FromHwnd(windowHelper.Handle);
            _windowSource?.AddHook(WndProc);

            _shell.InitializeIcons();

            WindowThemeResolver.ApplyWindowTheme(this, _appVm.Theme);
            RegisterSystemEventHandlers();

            _hotkeyBindings.Wire();

            _sourceInitialized = true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DPICHANGED && !_sourceInitialized)
            {
                return IntPtr.Zero;
            }

            if (msg == WM_DPICHANGED)
            {
                _shell.RefreshIconsForCurrentDpi();
            }

            return IntPtr.Zero;
        }

        private void RegisterSystemEventHandlers()
        {
            if (_systemEventHandlersRegistered)
            {
                return;
            }

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _systemEventHandlersRegistered = true;
        }

        private void DispatchUiHotkeyAction(Action action)
        {
            MainWindowHotkeyDispatchHelper.Dispatch(
                Dispatcher,
                _logger,
                action,
                "Hotkey action error",
                nameof(DispatchUiHotkeyAction));
        }

        private void DispatchUiHotkeyActionAsync(Func<Task> action)
        {
            MainWindowHotkeyDispatchHelper.DispatchAsync(
                Dispatcher,
                _logger,
                action,
                "Hotkey async action error",
                nameof(DispatchUiHotkeyActionAsync));
        }

        private void RunBackgroundHotkeyAction(Action action, string errorMessage, string methodName)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.Error("MainWindow", errorMessage, methodName, ex);
            }
        }

        private void OnShowAppHotkey() => DispatchUiHotkeyAction(_appVm.ShowWindow);

        private void ExecuteUiAction(Action action, string logMessage, string userMessage, string methodName)
        {
            _ = MainWindowHotkeyDispatchHelper.ExecuteAsync(
                () =>
                {
                    action();
                    return Task.CompletedTask;
                },
                _logger,
                Dispatcher,
                logMessage,
                userMessage,
                methodName);
        }

        private void TrayMenu_Show_Click(object sender, RoutedEventArgs e)
        {
            ExecuteUiAction(
                _appVm.ShowWindow,
                "Tray show failed",
                "Error showing AudioPilot.",
                nameof(TrayMenu_Show_Click));
        }

        private void SavedRoutinesListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TryGetRoutineListItemFromEventSource(e.OriginalSource, out ListBoxItem? item))
            {
                SelectRoutineListItem(item);
            }
        }

        internal static bool TryGetRoutineListItemFromEventSource(object? source, out ListBoxItem? item)
        {
            item = source as ListBoxItem;
            if (item != null)
            {
                return item.DataContext != null;
            }

            if (source is not DependencyObject dependencyObject)
            {
                return false;
            }

            item = FindVisualAncestor<ListBoxItem>(dependencyObject);
            return item?.DataContext != null;
        }

        internal static bool SelectRoutineListItem(ListBoxItem? item)
        {
            if (item?.DataContext == null)
            {
                return false;
            }

            if (ItemsControl.ItemsControlFromItemContainer(item) is ListBox listBox && !item.IsSelected)
            {
                listBox.UnselectAll();
            }

            item.IsSelected = true;
            item.Focus();
            return true;
        }

        private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match)
                {
                    return match;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void TaskbarIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ExecuteUiAction(
                _appVm.ShowWindow,
                "Tray double-click show failed",
                "Error showing AudioPilot.",
                nameof(TaskbarIcon_TrayMouseDoubleClick));
        }

        private void TrayMenu_Settings_Click(object sender, RoutedEventArgs e)
        {
            ExecuteUiAction(
                () =>
                {
                    _appVm.SelectedSettingsTabIndex = 3;
                    _appVm.ShowWindow();
                    ResetMainContentScrollToTop();
                },
                "Tray settings open failed",
                "Error opening AudioPilot settings.",
                nameof(TrayMenu_Settings_Click));
        }

        private void TrayMenu_Hide_Click(object sender, RoutedEventArgs e)
        {
            ExecuteUiAction(
                _appVm.MinimizeWindow,
                "Tray hide failed",
                "Error hiding AudioPilot.",
                nameof(TrayMenu_Hide_Click));
        }

        private async void TrayMenu_SwitchOutput_Click(object sender, RoutedEventArgs e)
        {
            await MainWindowHotkeyDispatchHelper.ExecuteAsync(
                async () =>
                {
                    await _appVm.SwitchDevicesAsync(_appVm.MuteMic, _appVm.MuteSound, _appVm.Deafen).AsTask();
                },
                _logger,
                Dispatcher,
                "Tray output switch failed",
                "Error switching output devices.",
                nameof(TrayMenu_SwitchOutput_Click));
        }

        private async void TrayMenu_SwitchInput_Click(object sender, RoutedEventArgs e)
        {
            await MainWindowHotkeyDispatchHelper.ExecuteAsync(
                async () =>
                {
                    await _appVm.SwitchInputDevicesAsync().AsTask();
                },
                _logger,
                Dispatcher,
                "Tray input switch failed",
                "Error switching input devices.",
                nameof(TrayMenu_SwitchInput_Click));
        }

        private void TrayMenu_Exit_Click(object sender, RoutedEventArgs e)
        {
            ExecuteUiAction(
                _appVm.ExitApplication,
                "Tray exit failed",
                "Error closing AudioPilot.",
                nameof(TrayMenu_Exit_Click));
        }


        private static string GetCurrentDefaultPlaybackDeviceName()
        {
            if (DeviceCacheHelper.IsInitialized)
            {
                return ResolveDefaultDeviceName(
                    () => DeviceCacheHelper.Instance.GetPlaybackDeviceNameWithoutRefresh(Role.Multimedia));
            }

            return "Unavailable";
        }

        private static string GetCurrentDefaultRecordingDeviceName()
        {
            if (DeviceCacheHelper.IsInitialized)
            {
                return ResolveDefaultDeviceName(
                    () => DeviceCacheHelper.Instance.GetRecordingDeviceNameWithoutRefresh(Role.Console));
            }

            return "Unavailable";
        }

        internal static string ResolveDefaultDeviceName(Func<string?> getDeviceName)
        {
            try
            {
                string? deviceName = getDeviceName();
                return string.IsNullOrWhiteSpace(deviceName) ? "Unavailable" : deviceName.Trim();
            }
            catch
            {
                return "Unavailable";
            }
        }

        private void RepoLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppConstants.Links.RepositoryUrl)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.Warning("MainWindow", () => $"Failed to open repository link: {ex.GetType().Name}", nameof(RepoLink_RequestNavigate), ex);
            }

            e.Handled = true;
        }


        private void SendMediaHotkeyWithOverlay(MediaOverlayCommand command, Func<bool> mediaAction)
        {
            _ = MainWindowMediaHotkeyHandler.DispatchAsync(
                Dispatcher,
                _logger,
                _mediaOverlayCommands,
                _overlayService,
                command,
                mediaAction,
                nameof(DispatchUiHotkeyActionAsync));
        }

        private void ToggleFlagWithOverlay(
            Action toggleAction,
            Func<bool> readState,
            string enabledMessage,
            string disabledMessage,
            OverlayActionStateKind enabledStateKind,
            OverlayActionStateKind disabledStateKind)
        {
            DispatchUiHotkeyAction(() =>
            {
                toggleAction();
                bool enabled = readState();
                _overlayService.Show(
                    enabled ? enabledStateKind : disabledStateKind,
                    enabled ? enabledMessage : disabledMessage);
            });
        }

        private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                await _startupResumeCoordinator.HandleWindowLoadedAsync(nameof(MainWindow_Loaded));
            }
            catch (Exception ex)
            {
                _logger.Fatal("MainWindow", "Unexpected startup initialization failure", nameof(MainWindow_Loaded), ex);
                _lifecycleCoordinator.ShowFatalErrorDialogOnce("A fatal error occurred while starting AudioPilot. Please check AudioPilot.log for details.");
                Application.Current.Shutdown();
            }
        }

        internal Task BootstrapStartupAsync(string ownerMethodName)
        {
            return _startupResumeCoordinator.HandleWindowLoadedAsync(ownerMethodName);
        }

        private void OnSwitchHotkey()
        {
            RunHotkeyActionAsync(
                () => _appVm.SwitchDevicesAsync(_appVm.MuteMic, _appVm.MuteSound, _appVm.Deafen).AsTask(),
                logMessage: "Hotkey handler error",
                userMessage: "Error switching devices.",
                methodName: nameof(OnSwitchHotkey));
        }

        private void OnSwitchHotkeyReverse()
        {
            RunHotkeyActionAsync(
                () => _appVm.SwitchDevicesAsync(_appVm.MuteMic, _appVm.MuteSound, _appVm.Deafen, reverse: true).AsTask(),
                logMessage: "Reverse hotkey handler error",
                userMessage: "Error switching devices.",
                methodName: nameof(OnSwitchHotkeyReverse));
        }

        private void OnInputSwitchHotkey()
        {
            RunHotkeyActionAsync(
                async () =>
                {
                    _ = await _appVm.SwitchInputDevicesAsync();
                },
                logMessage: "Input hotkey handler error",
                userMessage: "Error switching input devices.",
                methodName: nameof(OnInputSwitchHotkey));
        }

        private void OnInputSwitchHotkeyReverse()
        {
            RunHotkeyActionAsync(
                async () =>
                {
                    _ = await _appVm.SwitchInputDevicesAsync(reverse: true);
                },
                logMessage: "Reverse input hotkey handler error",
                userMessage: "Error switching input devices.",
                methodName: nameof(OnInputSwitchHotkeyReverse));
        }

        private void RunHotkeyActionAsync(Func<Task> action, string logMessage, string userMessage, string methodName)
        {
            _ = ExecuteHotkeyActionAsync(action, logMessage, userMessage, methodName);
        }

        private async Task ExecuteHotkeyActionAsync(Func<Task> action, string logMessage, string userMessage, string methodName)
        {
            await MainWindowHotkeyDispatchHelper.ExecuteAsync(
                action,
                _logger,
                Dispatcher,
                logMessage,
                userMessage,
                methodName);
        }

        private void OnAudioDeviceStateChanged()
        {
            if (IsShutdownRequested())
            {
                if (Interlocked.Exchange(ref _hotplugIgnoredDuringShutdownLogged, 1) == 0)
                {
                    _logger.Warning("MainWindow", "Ignoring hotplug signal during shutdown", nameof(OnAudioDeviceStateChanged));
                }

                return;
            }

            bool isWindowVisible = _shell.IsWindowVisible;
            int pendingSignals = Interlocked.Increment(ref _pendingHotplugSignals);
            int debounceDelayMs = ResolveHotplugRefreshDebounceMs(
                pendingSignals,
                RuntimeTuningConfig.HotplugRefreshDebounceMs,
                isWindowVisible);

            CancellationTokenSource nextDebounceCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(ref _hotplugRefreshDebounceCts, out bool replacedPreviousDebounce);
            CancellationToken debounceToken = nextDebounceCts.Token;
            if (replacedPreviousDebounce)
            {
                Interlocked.Increment(ref _hotplugSuppressedRefreshes);
            }

            _ = MainWindowHotkeyDispatchHelper.InvokeAsync(
                Dispatcher,
                _logger,
                () => MainWindowHotplugRefreshHelper.ExecuteAsync(
                    debounceDelayMs,
                    new MainWindowHotplugRefreshDependencies(
                        IsShutdownRequested,
                        DelayAsync: static (delayMs, token) => Task.Delay(delayMs, token),
                        ConsumePendingSignals: () => Interlocked.Exchange(ref _pendingHotplugSignals, 0),
                        AddCoalescedEvents: extras => Interlocked.Add(ref _hotplugCoalescedEvents, extras),
                        RefreshDevicesForHotplugAsync: _appVm.RefreshDevicesForHotplugAsync,
                        WaitForHotplugRefreshSettlementAsync: token => WaitForHotplugRefreshSettlementAsync(
                            innerToken => _appVm.WaitForMixerRefreshSettlementAsync(innerToken),
                            token),
                        ProcessPostRefresh: _hotplugOverlayCoordinator.ProcessPostRefresh,
                        ExecuteDeviceChangeTriggeredRoutinesAsync: _appVm.ExecuteDeviceChangeTriggeredRoutinesAsync,
                        IncrementAppliedRefreshes: () => Interlocked.Increment(ref _hotplugAppliedRefreshes),
                        ReadCoalescedEvents: () => Interlocked.CompareExchange(ref _hotplugCoalescedEvents, 0, 0),
                        ReadSuppressedRefreshes: () => Interlocked.CompareExchange(ref _hotplugSuppressedRefreshes, 0, 0),
                        DiagnosticsInterval: AppConstants.Timing.HotplugDiagnosticsLogEveryNAppliedRefreshes),
                    _logger,
                    nameof(OnAudioDeviceStateChanged),
                    debounceToken),
                "Failed to schedule hotplug refresh",
                nameof(OnAudioDeviceStateChanged));
        }

        /// <summary>
        /// Resolves the effective hotplug debounce window, using the fast path for the first hidden-window signal
        /// while preserving the configured delay for visible-window coalescing bursts.
        /// </summary>
        internal static int ResolveHotplugRefreshDebounceMs(int pendingSignals, int configuredDebounceMs, bool isWindowVisible)
        {
            int normalizedConfiguredDebounceMs = Math.Max(1, configuredDebounceMs);
            if (!isWindowVisible || pendingSignals <= 1)
            {
                return Math.Min(
                    normalizedConfiguredDebounceMs,
                    RuntimeTuningConfig.HotplugRefreshFastPathDebounceMs);
            }

            return normalizedConfiguredDebounceMs;
        }

        /// <summary>
        /// Awaits refresh settlement and yields once more to let dispatcher-posted follow-up work drain before the
        /// hotplug pipeline evaluates post-refresh overlays and routines.
        /// </summary>
        internal static async Task WaitForHotplugRefreshSettlementAsync(
            Func<CancellationToken, Task> waitForRefreshSettlementAsync,
            CancellationToken cancellationToken)
        {
            await waitForRefreshSettlementAsync(cancellationToken);
            await Task.Yield();
        }

        private bool IsShutdownRequested()
        {
            return Interlocked.CompareExchange(ref _shutdownStarted, 0, 0) != 0;
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                _logger.Fatal("MainWindow", "Unhandled exception occurred", nameof(OnUnhandledException), ex);
            }

            if (e.IsTerminating)
            {
                _lifecycleCoordinator.ShowFatalErrorDialogOnce("A fatal error occurred and AudioPilot must close. Please check AudioPilot.log for details.");
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (e.Exception is OperationCanceledException || e.Exception is TaskCanceledException)
            {
                _logger.Warning("MainWindow", "Ignoring recoverable dispatcher cancellation exception", nameof(OnDispatcherUnhandledException), e.Exception);
                e.Handled = true;
                return;
            }

            _logger.Fatal("MainWindow", $"Fatal dispatcher unhandled exception: {e.Exception.GetType().Name}", nameof(OnDispatcherUnhandledException), e.Exception);
            e.Handled = true;

            _lifecycleCoordinator.ShowFatalErrorDialogOnce("A fatal error occurred and the app will close. Please check AudioPilot.log for details.");
            Application.Current.Shutdown();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _shutdownCleanupCompleted, 0, 0) == 0)
            {
                Interlocked.Exchange(ref _shutdownStarted, 1);

                if (Interlocked.CompareExchange(ref _shutdownCleanupStarted, 1, 0) == 0)
                {
                    e.Cancel = true;
                    _logger.Debug("MainWindow", "Window closing requested, starting asynchronous shutdown cleanup");
                    _ = CompleteShutdownAndCloseAsync();
                    return;
                }

                e.Cancel = true;
                return;
            }

            base.OnClosing(e);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            _appVm.HandleWindowVisibilityChanged(_shell.IsWindowVisible);
            _visibilityCoordinator.HandleWindowStateChanged(WindowState, this.Hide, _appVm.MinimizeWindow);
        }

        private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _appVm.HandleWindowVisibilityChanged(e.NewValue is true);

            if (e.NewValue is not true)
            {
                CancelPendingRestoreMixerScroll();
            }

            _visibilityCoordinator.HandleVisibleChanged(
                isVisible: e.NewValue is true,
                isEditorTabActive: () => _appVm.IsEditorTabsActive,
                isAutoScrollEnabled: () => _appVm.Settings!.Overlay!.AutoScrollToMixerOnRestore,
                scheduleScroll: ScheduleScrollToVolumeMixerSection);
        }

        private void ScheduleScrollToVolumeMixerSection()
        {
            if (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(Dispatcher))
            {
                return;
            }

            CancellationTokenSource nextScrollCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(ref _restoreMixerScrollCts);

            try
            {
                _ = MainWindowHotkeyDispatchHelper.InvokeAsync(
                    Dispatcher,
                    _logger,
                    () => ExecuteRestoreMixerScrollAsync(nextScrollCts),
                    "Failed to schedule delayed mixer scroll",
                    nameof(MainWindow_IsVisibleChanged));
            }
            catch (InvalidOperationException ex) when (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(Dispatcher))
            {
                AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _restoreMixerScrollCts, nextScrollCts);
                _logger.Warning("MainWindow", "Skipping delayed mixer scroll because dispatcher shutdown is in progress", nameof(MainWindow_IsVisibleChanged), ex);
            }
        }

        private async Task ExecuteRestoreMixerScrollAsync(CancellationTokenSource ownedScrollCts)
        {
            try
            {
                CancellationToken cancellationToken = ownedScrollCts.Token;

                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Loaded, cancellationToken);
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle, cancellationToken);

                if (_shell.IsWindowVisible
                    && TryScrollToVolumeMixerSection(out RestoreMixerScrollState initialState, forceLayout: true)
                    && IsRestoreMixerScrollComplete(initialState))
                {
                    return;
                }

                if (!_appVm.HasPendingMixerRestoreWork())
                {
                    return;
                }

                await _appVm.WaitForMixerRestoreReadinessAsync(cancellationToken);
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle, cancellationToken);

                RestoreMixerScrollState? previousState = null;
                Stopwatch stopwatch = Stopwatch.StartNew();
                const int maxScrollPasses = 12;
                const int maxWaitMs = 1500;

                for (int pass = 0;
                    pass < maxScrollPasses
                    && stopwatch.ElapsedMilliseconds < maxWaitMs
                    && _shell.IsWindowVisible;
                    pass++)
                {
                    bool observedLayoutUpdate = await AwaitNextMixerLayoutOrIdleAsync(cancellationToken);

                    if (!_shell.IsWindowVisible ||
                        !TryScrollToVolumeMixerSection(
                            out RestoreMixerScrollState currentState,
                            forceLayout: !observedLayoutUpdate))
                    {
                        continue;
                    }

                    if (previousState.HasValue
                        && AreEquivalent(previousState.Value, currentState)
                        && IsRestoreMixerScrollComplete(currentState))
                    {
                        break;
                    }

                    previousState = currentState;
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _restoreMixerScrollCts, ownedScrollCts);
            }
        }

        private void CancelPendingRestoreMixerScroll()
        {
            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _restoreMixerScrollCts);
        }

        private void ResetMainContentScrollToTop()
        {
            if (MainContentScrollViewer == null || MainContentScrollViewer.VerticalOffset <= 0.5)
            {
                return;
            }

            MainContentScrollViewer.ScrollToVerticalOffset(0);
            MainContentScrollViewer.UpdateLayout();
        }

        private async Task<bool> AwaitNextMixerLayoutOrIdleAsync(CancellationToken cancellationToken)
        {
            if (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(Dispatcher) || MainContentScrollViewer == null)
            {
                return false;
            }

            using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task layoutTask = WaitForNextLayoutUpdateAsync(waitCts.Token);
            Task idleTask = Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle, cancellationToken).Task;
            Task completedTask = await Task.WhenAny(layoutTask, idleTask);

            waitCts.Cancel();

            try
            {
                await completedTask;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            if (!ReferenceEquals(completedTask, layoutTask))
            {
                try
                {
                    await layoutTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            return ReferenceEquals(completedTask, layoutTask);
        }

        private Task WaitForNextLayoutUpdateAsync(CancellationToken cancellationToken)
        {
            if (MainContentScrollViewer == null)
            {
                return Task.CompletedTask;
            }

            var layoutUpdatedTcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler? handler = null;
            CancellationTokenRegistration cancellationRegistration = default;

            void Detach()
            {
                MainContentScrollViewer.LayoutUpdated -= handler;
                cancellationRegistration.Dispose();
            }

            handler = (_, _) =>
            {
                Detach();
                layoutUpdatedTcs.TrySetResult(null);
            };

            MainContentScrollViewer.LayoutUpdated += handler;

            if (cancellationToken.CanBeCanceled)
            {
                cancellationRegistration = cancellationToken.Register(() =>
                {
                    if (Dispatcher.CheckAccess())
                    {
                        Detach();
                    }
                    else if (!MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(Dispatcher))
                    {
                        try
                        {
                            Dispatcher.Invoke(Detach);
                        }
                        catch (InvalidOperationException) when (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(Dispatcher))
                        {
                        }
                    }

                    layoutUpdatedTcs.TrySetCanceled(cancellationToken);
                });
            }

            return layoutUpdatedTcs.Task;
        }

        private bool TryScrollToVolumeMixerSection(out RestoreMixerScrollState state, bool forceLayout = false)
        {
            state = default;
            if (MainContentScrollViewer?.Content is not Visual scrollContent || VolumeMixerHeader is null)
            {
                return false;
            }

            if (forceLayout)
            {
                MainContentScrollViewer.UpdateLayout();
            }

            var transform = VolumeMixerHeader.TransformToAncestor(scrollContent);
            var headerPosition = transform.Transform(new Point(0, 0));
            var targetOffset = ClampScrollOffset(
                headerPosition.Y,
                MainContentScrollViewer.ScrollableHeight);

            MainContentScrollViewer.ScrollToVerticalOffset(targetOffset);
            state = new RestoreMixerScrollState(
                headerPosition.Y,
                targetOffset,
                MainContentScrollViewer.ScrollableHeight);
            return true;
        }

        private static bool AreEquivalent(RestoreMixerScrollState left, RestoreMixerScrollState right)
        {
            const double tolerance = 0.5;

            return Math.Abs(left.HeaderOffsetY - right.HeaderOffsetY) < tolerance
                && Math.Abs(left.TargetOffset - right.TargetOffset) < tolerance
                && Math.Abs(left.ScrollableHeight - right.ScrollableHeight) < tolerance;
        }

        private static bool IsRestoreMixerScrollComplete(RestoreMixerScrollState state)
        {
            const double tolerance = 0.5;
            return state.ScrollableHeight + tolerance >= state.HeaderOffsetY;
        }

        internal static double ClampScrollOffset(double headerOffsetY, double scrollableHeight)
        {
            return Math.Max(0, Math.Min(headerOffsetY, scrollableHeight));
        }

        private void RootGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!MainWindowInteractionHelper.ShouldClearRootFocus(
                e.OriginalSource as DependencyObject,
                this))
            {
                return;
            }

            Keyboard.ClearFocus();
            Focus();
            OutputSwitchOrderListBox.UnselectAll();
            InputSwitchOrderListBox.UnselectAll();
            MainWindowInteractionHelper.UnselectListBoxIfSelected(SavedRoutinesListBox);
        }

        private void VolumeMixer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListBox listBox)
            {
                MainWindowInteractionHelper.UnselectListBoxIfSelected(listBox);
            }
        }

        private void DeviceTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, sender) || VolumeMixer == null || !VolumeMixer.IsKeyboardFocusWithin)
            {
                return;
            }

            Keyboard.ClearFocus();
            DeviceTabControl.Focus();
        }

        private void VolumeMixer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (MainContentScrollViewer == null || e.Delta == 0)
            {
                return;
            }

            var targetOffset = MainWindowInteractionHelper.ClampMouseWheelOffset(
                MainContentScrollViewer.VerticalOffset,
                MainContentScrollViewer.ScrollableHeight,
                e.Delta);

            MainContentScrollViewer.ScrollToVerticalOffset(targetOffset);
            e.Handled = true;
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (IsShutdownRequested())
            {
                return;
            }

            if (_appVm.Theme != AppTheme.System ||
                (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color))
            {
                return;
            }

            WindowThemeResolver.ApplyWindowTheme(this, AppTheme.System);
        }

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (IsShutdownRequested())
            {
                return;
            }

            _startupResumeCoordinator.HandlePowerModeChanged(e, nameof(OnPowerModeChanged));
        }

        private MainWindowShutdownDependencies CreateShutdownDependencies()
        {
            return MainWindowShutdownDependencyFactory.Build(
                CreateShutdownPreparationDependencies(),
                _hotkeyBindings.Unwire,
                _appVm.CleanupAsync,
                () => _hotkeyService?.Dispose(),
                _runtimeServices is not null ? DisposeRuntimeServicesAsync : null,
                () => _overlayService?.Dispose(),
                () => _shell?.Dispose(),
                () => _singleInstance?.Dispose());
        }

        private MainWindowShutdownPreparationDependencies CreateShutdownPreparationDependencies()
        {
            return MainWindowShutdownDependencyFactory.BuildPreparation(
                CloseOwnedWindowsForShutdown,
                DetachGlobalExceptionHandlers,
                () => _audioService.DeviceStateChanged -= OnAudioDeviceStateChanged,
                () =>
                {
                    StopHotplugRefreshDebounce();
                    CancelPendingRestoreMixerScroll();
                },
                DetachSystemEventHandlers,
                DetachWindowEventHandlers);
        }

        private void CloseOwnedWindowsForShutdown()
        {
            Window[] ownedWindows = [.. OwnedWindows.Cast<Window>()];
            foreach (Window ownedWindow in ownedWindows)
            {
                try
                {
                    ownedWindow.Close();
                }
                catch (InvalidOperationException ex) when (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(Dispatcher) || IsExpectedCloseDuringShutdown(ex))
                {
                    if (!MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(Dispatcher))
                    {
                        TryLogOwnedWindowCloseDebug(ownedWindow.GetType().Name);
                    }
                }
                catch (Exception ex)
                {
                    TryLogOwnedWindowCloseFailure(ownedWindow, ex);
                }
            }
        }

        private void TryLogOwnedWindowCloseDebug(string windowType)
        {
            try
            {
                _logger.Debug("MainWindow", () => $"owned-window-close-skipped | type={windowType}");
            }
            catch (Exception loggingEx)
            {
                LifecycleFallbackDiagnostics.Write(
                    "MainWindow",
                    $"Failed to log owned window close skip for {windowType}",
                    nameof(CloseOwnedWindowsForShutdown),
                    new InvalidOperationException($"owned-window-close-skipped:{windowType}"),
                    loggingEx);
            }
        }

        private void TryLogOwnedWindowCloseFailure(Window ownedWindow, Exception ex)
        {
            string windowType = ownedWindow.GetType().Name;

            try
            {
                _logger.Warning("MainWindow", () => $"owned-window-close-failed | type={windowType}", nameof(CloseOwnedWindowsForShutdown), ex);
            }
            catch (Exception loggingEx)
            {
                LifecycleFallbackDiagnostics.Write(
                    "MainWindow",
                    $"Failed to close owned window {windowType}",
                    nameof(CloseOwnedWindowsForShutdown),
                    ex,
                    loggingEx);
            }
        }

        private void DetachGlobalExceptionHandlers()
        {
            Application.Current.DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        }

        private void StopHotplugRefreshDebounce()
        {
            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _hotplugRefreshDebounceCts);
        }

        private void DetachSystemEventHandlers()
        {
            if (_systemEventHandlersRegistered)
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                _systemEventHandlersRegistered = false;
            }

            _startupResumeCoordinator.Dispose();
        }

        private void DetachWindowEventHandlers()
        {
            Loaded -= MainWindow_Loaded;
            IsVisibleChanged -= MainWindow_IsVisibleChanged;
            _windowSource?.RemoveHook(WndProc);
            _windowSource = null;
        }

        private Task DisposeRuntimeServicesAsync()
        {
            return _runtimeServices?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        }

        private async Task CompleteShutdownAndCloseAsync()
        {
            try
            {
                await _lifecycleCoordinator.ExecuteShutdownAsync(
                    CreateShutdownDependencies(),
                    nameof(CompleteShutdownAndCloseAsync));
            }
            finally
            {
                Interlocked.Exchange(ref _shutdownCleanupCompleted, 1);
            }

            if (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(Dispatcher))
            {
                return;
            }

            try
            {
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() =>
                    {
                        try
                        {
                            Close();
                        }
                        catch (InvalidOperationException ex) when (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(Dispatcher) || IsExpectedCloseDuringShutdown(ex))
                        {
                            if (!MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(Dispatcher))
                            {
                                _logger.Debug("MainWindow", "close-skipped-while-window-is-already-closing");
                            }
                        }
                    }));
            }
            catch (InvalidOperationException) when (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(Dispatcher))
            {
                _logger.Debug("MainWindow", "close-scheduling-skipped-during-dispatcher-shutdown");
            }
        }

        private static bool IsExpectedCloseDuringShutdown(InvalidOperationException ex)
        {
            return ex.Message.Contains("while a Window is closing", StringComparison.OrdinalIgnoreCase);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _logger.Dispose();
        }
    }
}
