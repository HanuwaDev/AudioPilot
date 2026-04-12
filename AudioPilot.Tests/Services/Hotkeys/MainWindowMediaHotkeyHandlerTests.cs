using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using Windows.Media.Control;

namespace AudioPilot.Tests.Services.Hotkeys;

public sealed class MainWindowMediaHotkeyHandlerTests
{
    [Fact]
    public void DispatchAsync_ShowsTrackOverlay_WhenMediaCommandReturnsTrackMessage()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var presenter = new RecordingOverlayPresenter();
            var overlayService = new OverlayService(dispatch: action => action(), presenterFactory: _ => presenter);
            bool commandSent = false;
            var engine = new MediaOverlayEngine(
                currentSnapshotOverride: CreateQueuedCurrentSnapshotOverride(
                    new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Track A", "Artist A", null, "spotify", 84),
                    new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Track B", "Artist B", null, "spotify", 85)),
                snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["spotify"] = new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Track A", "Artist A", null, "spotify", 84),
                }),
                sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

            Task task = MainWindowMediaHotkeyHandler.DispatchAsync(
                Dispatcher.CurrentDispatcher,
                Logger.Instance,
                new MediaOverlayCommandService(engine),
                overlayService,
                MediaOverlayCommand.NextTrack,
                () =>
                {
                    commandSent = true;
                    return true;
                },
                nameof(MainWindowMediaHotkeyHandlerTests));

            TestPrivateAccess.RunTaskOnDispatcher(task);

            Assert.True(commandSent);
            Assert.Equal(1, presenter.MediaUpdateCount);
            Assert.Equal(1, presenter.ShowCount);
            Assert.Contains(presenter.Messages, message => string.Equals(message.deviceName, "Track B", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void DispatchAsync_DoesNotShowOverlay_WhenMediaCommandReturnsHidden()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var presenter = new RecordingOverlayPresenter();
            var overlayService = new OverlayService(dispatch: action => action(), presenterFactory: _ => presenter);
            bool commandSent = false;
            var engine = new MediaOverlayEngine(
                currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
                snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
                sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

            Task task = MainWindowMediaHotkeyHandler.DispatchAsync(
                Dispatcher.CurrentDispatcher,
                Logger.Instance,
                new MediaOverlayCommandService(engine),
                overlayService,
                MediaOverlayCommand.PlayPause,
                () =>
                {
                    commandSent = true;
                    return true;
                },
                nameof(MainWindowMediaHotkeyHandlerTests));

            TestPrivateAccess.RunTaskOnDispatcher(task);

            Assert.True(commandSent);
            Assert.Equal(0, presenter.ShowCount);
            Assert.Equal(0, presenter.MediaUpdateCount);
            Assert.Equal(0, presenter.MessageUpdateCount);
        });
    }

    [Fact]
    public void DispatchAsync_ShowsFailureOverlay_WhenMediaCommandSendFails()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var presenter = new RecordingOverlayPresenter();
            var overlayService = new OverlayService(dispatch: action => action(), presenterFactory: _ => presenter);
            bool commandSent = false;

            Task task = MainWindowMediaHotkeyHandler.DispatchAsync(
                Dispatcher.CurrentDispatcher,
                Logger.Instance,
                new MediaOverlayCommandService(),
                overlayService,
                MediaOverlayCommand.NextTrack,
                () =>
                {
                    commandSent = true;
                    return false;
                },
                nameof(MainWindowMediaHotkeyHandlerTests));

            TestPrivateAccess.RunTaskOnDispatcher(task);

            Assert.True(commandSent);
            Assert.Equal(1, presenter.ShowCount);
            Assert.Equal(1, presenter.MessageUpdateCount);
            Assert.Contains(presenter.Messages, message => string.Equals(message.header, "Next track failed", StringComparison.Ordinal));
        });
    }

    private static Func<string?, long, CancellationToken, Task<MediaOverlaySessionSnapshot>> CreateQueuedCurrentSnapshotOverride(
        params MediaOverlaySessionSnapshot[] snapshots)
    {
        Queue<MediaOverlaySessionSnapshot> queue = new(snapshots);
        MediaOverlaySessionSnapshot fallback = snapshots.Length > 0 ? snapshots[^1] : MediaOverlaySessionSnapshot.Empty;
        Lock gate = new();

        return (_, _, _) =>
        {
            lock (gate)
            {
                MediaOverlaySessionSnapshot snapshot = queue.Count > 0 ? queue.Dequeue() : fallback;
                return Task.FromResult(snapshot);
            }
        };
    }
}
