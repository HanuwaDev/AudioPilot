using System.Windows;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Services.UI
{
    public enum OverlayDeviceKind
    {
        Output = 0,
        Input = 1,
        Error = 2
    }

    public enum OverlayActionStateKind
    {
        Enabled = 0,
        Disabled = 1,
    }

    public class OverlayService : IDisposable
    {
        public readonly record struct OverlayStackItem(OverlayDeviceKind Kind, string Header, string DeviceName);

        internal interface IOverlayPresenter : IDisposable
        {
            void UpdateContent(string message);
            void UpdateContent(OverlayActionStateKind stateKind, string message);
            void UpdateContent(OverlayDeviceKind kind, string header, string deviceName);
            void UpdateRoutineContent(string header, string? outputDeviceName, string? inputDeviceName);
            void UpdateRoutinePartialContent(string header, string? outputDeviceName, string? inputDeviceName, string? failedOutputDeviceName, string? failedInputDeviceName);
            void UpdateContent(string header, string title, string? artist);
            void ApplyDisplayOptions(OverlayPosition position, double durationSeconds, int stackIndex);
            void ShowOverlay();
        }

        private sealed class OverlayWindowPresenter(string initialMessage) : IOverlayPresenter
        {
            private readonly OverlayWindow _window = new(initialMessage);

            public void UpdateContent(string message) => _window.UpdateContent(message);
            public void UpdateContent(OverlayActionStateKind stateKind, string message) => _window.UpdateContent(stateKind, message);
            public void UpdateContent(OverlayDeviceKind kind, string header, string deviceName) => _window.UpdateContent(kind, header, deviceName);
            public void UpdateRoutineContent(string header, string? outputDeviceName, string? inputDeviceName) => _window.UpdateRoutineContent(header, outputDeviceName, inputDeviceName);
            public void UpdateRoutinePartialContent(string header, string? outputDeviceName, string? inputDeviceName, string? failedOutputDeviceName, string? failedInputDeviceName) => _window.UpdateRoutinePartialContent(header, outputDeviceName, inputDeviceName, failedOutputDeviceName, failedInputDeviceName);
            public void UpdateContent(string header, string title, string? artist) => _window.UpdateContent(header, title, artist);
            public void ApplyDisplayOptions(OverlayPosition position, double durationSeconds, int stackIndex) => _window.ApplyDisplayOptions(position, durationSeconds, stackIndex);
            public void ShowOverlay() => _window.ShowOverlay();

            public void Dispose()
            {
                try
                {
                    if (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(_window.Dispatcher))
                    {
                        _window.Cleanup();
                        return;
                    }

                    if (_window.Dispatcher.CheckAccess())
                    {
                        CloseWindow();
                    }
                    else
                    {
                        _window.Dispatcher.Invoke(CloseWindow);
                    }
                }
                catch (InvalidOperationException) when (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(_window.Dispatcher))
                {
                    _window.Cleanup();
                }
            }

            private void CloseWindow()
            {
                if (_window.IsLoaded)
                {
                    _window.Close();
                    return;
                }

                _window.Cleanup();
            }
        }

        private readonly Logger _logger;
        private readonly Action<Action> _dispatch;
        private readonly Func<string, IOverlayPresenter> _presenterFactory;
        private readonly List<IOverlayPresenter> _overlayPresenters = [];
        private bool _overlayEnabled = true;
        private OverlayPosition _overlayPosition = OverlayPosition.BottomRight;
        private double _overlayDurationSeconds = AudioPilot.Constants.AppConstants.Timing.OverlayAutoHideSeconds;

        public OverlayService()
            : this(DispatchOnCurrentApplication, initialMessage => new OverlayWindowPresenter(initialMessage))
        {
        }

        internal OverlayService(Action<Action> dispatch, Func<string, IOverlayPresenter> presenterFactory)
        {
            _logger = Logger.Instance;
            _dispatch = dispatch;
            _presenterFactory = presenterFactory;
        }

        internal bool HasPresenterForTests => _overlayPresenters.Count > 0;

        public void UpdateDisplayOptions(OverlayPosition position, double durationSeconds)
        {
            _overlayPosition = position;
            _overlayDurationSeconds = Math.Clamp(durationSeconds, 0.5, 10.0);
            for (int i = 0; i < _overlayPresenters.Count; i++)
            {
                _overlayPresenters[i].ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, i);
            }
        }

        public void UpdateEnabled(bool enabled)
        {
            _overlayEnabled = enabled;
        }

        public void Dispose()
        {
            foreach (IOverlayPresenter presenter in _overlayPresenters)
            {
                try
                {
                    presenter.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Debug("OverlayService", () => $"overlay-presenter-dispose-failed | scope=all error={ex.GetType().Name}", nameof(Dispose));
                }
            }

            _overlayPresenters.Clear();
            GC.SuppressFinalize(this);
        }

        public void Show(string message)
        {
            if (!_overlayEnabled)
            {
                return;
            }

            if (!TryDispatch(() =>
            {
                try
                {
                    IOverlayPresenter presenter = EnsurePresenter(0, message);
                    presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, 0);

                    presenter.UpdateContent(message);
                    presenter.ShowOverlay();
                    ReleaseUnusedPresenters(1);
                }
                catch (Exception ex)
                {
                    _logger.Error("OverlayService", () => $"overlay-show-failed | mode=plain error={ex.GetType().Name}", nameof(Show), ex);
                }
            }))
            {
                return;
            }
        }

        public void Show(OverlayActionStateKind stateKind, string message)
        {
            if (!_overlayEnabled)
            {
                return;
            }

            if (!TryDispatch(() =>
            {
                try
                {
                    IOverlayPresenter presenter = EnsurePresenter(0, message);
                    presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, 0);

                    presenter.UpdateContent(stateKind, message);
                    presenter.ShowOverlay();
                    ReleaseUnusedPresenters(1);
                }
                catch (Exception ex)
                {
                    _logger.Error("OverlayService", () => $"overlay-show-failed | mode=action stateKind={stateKind} error={ex.GetType().Name}", nameof(Show), ex);
                }
            }))
            {
                return;
            }
        }

        public void Show(string header, string deviceName)
        {
            Show(OverlayDeviceKind.Output, header, deviceName);
        }

        public void Show(OverlayDeviceKind kind, string header, string deviceName)
        {
            if (!_overlayEnabled)
            {
                return;
            }

            if (!TryDispatch(() =>
            {
                try
                {
                    IOverlayPresenter presenter = EnsurePresenter(0, $"{header}\n{deviceName}");
                    presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, 0);

                    presenter.UpdateContent(kind, header, deviceName);
                    presenter.ShowOverlay();
                    ReleaseUnusedPresenters(1);
                }
                catch (Exception ex)
                {
                    _logger.Error("OverlayService", () => $"overlay-show-failed | mode=device kind={kind} error={ex.GetType().Name}", nameof(Show), ex);
                }
            }))
            {
                return;
            }
        }

        public void ShowRoutine(string header, string? outputDeviceName, string? inputDeviceName)
        {
            if (!_overlayEnabled)
            {
                return;
            }

            if (!TryDispatch(() =>
            {
                try
                {
                    string initialMessage = string.Join(
                        "\n",
                        new[] { header, outputDeviceName, inputDeviceName }.Where(static value => !string.IsNullOrWhiteSpace(value)));

                    IOverlayPresenter presenter = EnsurePresenter(0, initialMessage);
                    presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, 0);
                    presenter.UpdateRoutineContent(header, outputDeviceName, inputDeviceName);
                    presenter.ShowOverlay();
                    ReleaseUnusedPresenters(1);
                }
                catch (Exception ex)
                {
                    _logger.Error("OverlayService", () => $"overlay-show-failed | mode=routine error={ex.GetType().Name}", nameof(ShowRoutine), ex);
                }
            }))
            {
                return;
            }
        }

        public void ShowRoutinePartial(string header, string? outputDeviceName, string? inputDeviceName, string? failedOutputDeviceName, string? failedInputDeviceName)
        {
            if (!_overlayEnabled)
            {
                return;
            }

            if (!TryDispatch(() =>
            {
                try
                {
                    string initialMessage = string.Join(
                        "\n",
                        new[] { header, outputDeviceName, inputDeviceName, failedOutputDeviceName, failedInputDeviceName }.Where(static value => !string.IsNullOrWhiteSpace(value)));

                    IOverlayPresenter presenter = EnsurePresenter(0, initialMessage);
                    presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, 0);
                    presenter.UpdateRoutinePartialContent(header, outputDeviceName, inputDeviceName, failedOutputDeviceName, failedInputDeviceName);
                    presenter.ShowOverlay();
                    ReleaseUnusedPresenters(1);
                }
                catch (Exception ex)
                {
                    _logger.Error("OverlayService", () => $"overlay-show-failed | mode=routine-partial error={ex.GetType().Name}", nameof(ShowRoutinePartial), ex);
                }
            }))
            {
                return;
            }
        }

        public void ShowMediaTrack(string header, string title, string? artist)
        {
            if (!_overlayEnabled)
            {
                return;
            }

            if (!TryDispatch(() =>
            {
                try
                {
                    IOverlayPresenter presenter = EnsurePresenter(0, $"{header}\n{title}");
                    presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, 0);

                    presenter.UpdateContent(header, title, artist);
                    presenter.ShowOverlay();
                    ReleaseUnusedPresenters(1);
                }
                catch (Exception ex)
                {
                    _logger.Error("OverlayService", () => $"overlay-show-failed | mode=media-track error={ex.GetType().Name}", nameof(ShowMediaTrack), ex);
                }
            }))
            {
                return;
            }
        }

        public void ShowStacked(IReadOnlyList<OverlayStackItem> items)
        {
            if (!_overlayEnabled || items.Count == 0)
            {
                return;
            }

            if (!TryDispatch(() =>
            {
                try
                {
                    for (int index = 0; index < items.Count; index++)
                    {
                        OverlayStackItem item = items[index];
                        IOverlayPresenter presenter = EnsurePresenter(index, $"{item.Header}\n{item.DeviceName}");
                        presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, index);
                        presenter.UpdateContent(item.Kind, item.Header, item.DeviceName);
                        presenter.ShowOverlay();
                    }

                    ReleaseUnusedPresenters(items.Count);
                }
                catch (Exception ex)
                {
                    _logger.Error("OverlayService", () => $"overlay-show-failed | mode=stacked count={items.Count} error={ex.GetType().Name}", nameof(ShowStacked), ex);
                }
            }))
            {
                return;
            }
        }

        private IOverlayPresenter EnsurePresenter(int index, string initialMessage)
        {
            while (_overlayPresenters.Count <= index)
            {
                _overlayPresenters.Add(_presenterFactory(initialMessage));
            }

            return _overlayPresenters[index];
        }

        private void ReleaseUnusedPresenters(int keepCount)
        {
            while (_overlayPresenters.Count > keepCount)
            {
                int lastIndex = _overlayPresenters.Count - 1;
                IOverlayPresenter presenter = _overlayPresenters[lastIndex];
                _overlayPresenters.RemoveAt(lastIndex);

                try
                {
                    presenter.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Debug("OverlayService", () => $"overlay-presenter-dispose-failed | scope=unused error={ex.GetType().Name}", nameof(ReleaseUnusedPresenters));
                }
            }
        }

        private bool TryDispatch(Action action)
        {
            try
            {
                _dispatch(action);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug("OverlayService", () => $"overlay-dispatch-failed | error={ex.GetType().Name}", nameof(TryDispatch));
                return false;
            }
        }

        private static void DispatchOnCurrentApplication(Action action)
        {
            if (Application.Current?.Dispatcher == null)
            {
                throw new InvalidOperationException("Application dispatcher is not available.");
            }

            var dispatcher = Application.Current.Dispatcher;
            if (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(dispatcher))
            {
                throw new InvalidOperationException("Application dispatcher is shutting down.");
            }

            try
            {
                _ = dispatcher.BeginInvoke(action);
            }
            catch (InvalidOperationException ex) when (MainWindowHotkeyDispatchHelper.IsDispatcherUnavailable(dispatcher))
            {
                throw new InvalidOperationException("Application dispatcher is shutting down.", ex);
            }
        }
    }
}
