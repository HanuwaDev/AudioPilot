using System.Collections.Concurrent;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using Windows.Media.Control;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppCliOverlayCoordinatorTests
{
    [Fact]
    public void SetMuteMic_AppliesMuteState_AndShowsOverlay()
    {
        var presenter = new RecordingOverlayPresenter();
        AppCliOverlayCoordinator coordinator = CreateCoordinator(presenter);
        bool? applied = null;

        bool result = coordinator.SetMuteMic(true, value => applied = value);

        Assert.True(result);
        Assert.True(applied);
        var (stateKind, message) = Assert.Single(presenter.ActionMessages);
        Assert.Equal(OverlayActionStateKind.Disabled, stateKind);
        Assert.Equal("Microphone muted", message);
    }

    [Fact]
    public void ToggleMuteSound_UsesCurrentValueProvider_AndShowsUnmutedOverlay()
    {
        var presenter = new RecordingOverlayPresenter();
        AppCliOverlayCoordinator coordinator = CreateCoordinator(presenter);
        bool? applied = null;

        bool result = coordinator.ToggleMuteSound(() => true, value => applied = value);

        Assert.True(result);
        Assert.False(applied);
        var (stateKind, message) = Assert.Single(presenter.ActionMessages);
        Assert.Equal(OverlayActionStateKind.Enabled, stateKind);
        Assert.Equal("Sound unmuted", message);
    }

    [Fact]
    public void ToggleDeafen_UsesCurrentValueProvider_AndShowsDeafenedOverlay()
    {
        var presenter = new RecordingOverlayPresenter();
        AppCliOverlayCoordinator coordinator = CreateCoordinator(presenter);
        bool? applied = null;

        bool result = coordinator.ToggleDeafen(() => false, value => applied = value);

        Assert.True(result);
        Assert.True(applied);
        var (stateKind, message) = Assert.Single(presenter.ActionMessages);
        Assert.Equal(OverlayActionStateKind.Disabled, stateKind);
        Assert.Equal("Deafened", message);
    }

    [Theory]
    [InlineData(true, "Input listen enabled")]
    [InlineData(false, "Input listen disabled")]
    public void GetListenToInputOverlayHeader_ReturnsExpectedHeader(bool enabled, string expected)
    {
        string header = AppCliOverlayCoordinator.GetListenToInputOverlayHeader(enabled);

        Assert.Equal(expected, header);
    }

    [Theory]
    [InlineData(null, "Current input device")]
    [InlineData("", "Current input device")]
    [InlineData("   ", "Current input device")]
    [InlineData("Desk Mic", "Desk Mic")]
    public void NormalizeListenToInputOverlayDeviceName_ReturnsExpectedName(string? friendlyName, string expected)
    {
        string normalized = AppCliOverlayCoordinator.NormalizeListenToInputOverlayDeviceName(friendlyName);

        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void ComposeListenToInputOverlayDeviceText_WhenEnabled_IncludesMonitorTarget()
    {
        string text = AppCliOverlayCoordinator.ComposeListenToInputOverlayDeviceText(
            enabled: true,
            inputDeviceName: "Desk Mic",
            monitorTargetOutputDeviceName: "Headphones");

        Assert.Equal("Desk Mic\nTo: Headphones", text);
    }

    [Fact]
    public void ComposeListenToInputOverlayDeviceText_WhenEnabledWithoutTarget_UsesDefaultOutputFallback()
    {
        string text = AppCliOverlayCoordinator.ComposeListenToInputOverlayDeviceText(
            enabled: true,
            inputDeviceName: "Desk Mic",
            monitorTargetOutputDeviceName: null);

        Assert.Equal("Desk Mic\nTo: Default output", text);
    }

    [Fact]
    public void ComposeListenToInputOverlayDeviceText_WhenDisabled_ReturnsInputNameOnly()
    {
        string text = AppCliOverlayCoordinator.ComposeListenToInputOverlayDeviceText(
            enabled: false,
            inputDeviceName: "Desk Mic",
            monitorTargetOutputDeviceName: "Headphones");

        Assert.Equal("Desk Mic", text);
    }

    [Theory]
    [InlineData(40f, 5, true, 45f)]
    [InlineData(40f, 5, false, 35f)]
    [InlineData(98f, 5, true, 100f)]
    [InlineData(2f, 5, false, 0f)]
    [InlineData(50f, 0, true, 55f)]
    public void ComputeSteppedVolumePercent_ReturnsExpectedValue(float currentPercent, int stepPercent, bool increase, float expected)
    {
        float updated = AppCliOverlayCoordinator.ComputeSteppedVolumePercent(currentPercent, stepPercent, increase);

        Assert.Equal(expected, updated);
    }

    [Theory]
    [InlineData("Master volume", 72.2f, "Master volume 72%")]
    [InlineData("Microphone volume", 48.8f, "Microphone volume 49%")]
    public void BuildVolumeOverlayMessage_ReturnsExpectedMessage(string label, float percent, string expected)
    {
        string message = AppCliOverlayCoordinator.BuildVolumeOverlayMessage(label, percent);

        Assert.Equal(expected, message);
    }

    [Theory]
    [InlineData(true, OverlayActionStateKind.Enabled)]
    [InlineData(false, OverlayActionStateKind.Disabled)]
    public void GetVolumeOverlayStateKind_ReturnsExpectedState(bool increase, OverlayActionStateKind expected)
    {
        OverlayActionStateKind stateKind = AppCliOverlayCoordinator.GetVolumeOverlayStateKind(increase);

        Assert.Equal(expected, stateKind);
    }

    [Fact]
    public async Task MediaNextTrack_WhenCaptureAlreadyInFlight_SendsCommandAndSchedulesTrailingCapture()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        int captureCount = 0;
        int snapshotCallCount = 0;
        int sendCount = 0;
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                int currentCall = Interlocked.Increment(ref snapshotCallCount);
                if (currentCall == 1)
                {
                    Interlocked.Increment(ref captureCount);
                    captureStarted.TrySetResult();
                    await releaseCapture.Task.WaitAsync(token);
                    return new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track A",
                        "Artist A",
                        null,
                        "spotify",
                        42);
                }

                return new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track B",
                    "Artist B",
                    null,
                    "spotify",
                    1);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppCliOverlayCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommand: () =>
            {
                Interlocked.Increment(ref sendCount);
                return true;
            },
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack();
        await captureStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout());
        coordinator.MediaNextTrack();

        Assert.True(coordinator.IsMediaOverlayCaptureInFlightForTests);
        Assert.Equal(1, Volatile.Read(ref captureCount));
        await AssertEventuallyAsync(
            () => Volatile.Read(ref sendCount) == 1,
            GetMediaOverlayCaptureTimeout());
        Assert.Equal(0, presenter.MessageUpdateCount);

        releaseCapture.TrySetResult();
        await AssertEventuallyAsync(
            () => !coordinator.IsMediaOverlayCaptureInFlightForTests && Volatile.Read(ref sendCount) == 2,
            GetMediaOverlayCaptureTimeout());

        await AssertEventuallyAsync(
            () => presenter.MediaUpdateCount >= 1 && history.Count == 1,
            GetMediaOverlayCaptureTimeout());
        Assert.Contains(presenter.Messages, message => string.Equals(message.deviceName, "Track B", StringComparison.Ordinal));
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.Equal("media-overlay-trailing-track", entry.DiagCode);
    }

    [Fact]
    public async Task MediaNextTrack_UsesAsyncCommandDelegate_WhenProvided()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool commandCompleted = false;
        int snapshotCallCount = 0;

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) =>
            {
                int currentCall = Interlocked.Increment(ref snapshotCallCount);
                return Task.FromResult(currentCall == 1
                    ? new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track A",
                        "Artist A",
                        null,
                        "spotify",
                        42)
                    : new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track B",
                        "Artist B",
                        null,
                        "spotify",
                        1));
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppCliOverlayCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommandAsync: async () =>
            {
                commandStarted.TrySetResult();
                await completeCommand.Task;
                commandCompleted = true;
                return true;
            },
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack();
        await commandStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout());

        Assert.False(commandCompleted);
        Assert.Equal(0, presenter.ShowCount);

        completeCommand.TrySetResult();

        await AssertEventuallyAsync(
            () => presenter.MediaUpdateCount == 1,
            GetMediaOverlayCaptureTimeout());
        Assert.True(commandCompleted);
        Assert.Contains(presenter.Messages, message => string.Equals(message.deviceName, "Track B", StringComparison.Ordinal));

        await AssertEventuallyAsync(
            () => history.Count == 1,
            GetMediaOverlayCaptureTimeout());
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.Equal("media-overlay-track-changed", entry.DiagCode);
        Assert.True(entry.Success);
        Assert.False(entry.Skipped);
        Assert.NotNull(entry.ElapsedMs);
        Assert.Equal("NextTrack", entry.Details?["command"]);
        Assert.Equal("changed", entry.Details?["outcome"]);
        Assert.False(string.IsNullOrWhiteSpace(entry.Details?["finalPhase"]));
        Assert.Equal("track-changed", entry.Details?["finalChangeKind"]);
    }

    [Fact]
    public async Task MediaNextTrack_FromHotkey_RecordsHotkeySourceAndDetailedHistory()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        int snapshotCallCount = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) =>
            {
                int currentCall = Interlocked.Increment(ref snapshotCallCount);
                return Task.FromResult(currentCall == 1
                    ? new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track A",
                        "Artist A",
                        null,
                        "spotify",
                        42)
                    : new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track B",
                        "Artist B",
                        null,
                        "spotify",
                        1));
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppCliOverlayCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommand: () => true,
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack("hotkey");

        await AssertEventuallyAsync(
            () => presenter.MediaUpdateCount == 1 && history.Count == 1,
            GetMediaOverlayCaptureTimeout());
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.Equal("hotkey", entry.Source);
        Assert.Equal("media-overlay-track-changed", entry.DiagCode);
        Assert.Equal("hotkey", entry.Details?["source"]);
        Assert.Equal("changed", entry.Details?["outcome"]);
        Assert.False(string.IsNullOrWhiteSpace(entry.Details?["finalPhase"]));
    }

    [Fact]
    public async Task MediaNextTrack_DetailedCommandOutcome_RecordsRouteAndCandidateSource()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        int snapshotCallCount = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) =>
            {
                int currentCall = Interlocked.Increment(ref snapshotCallCount);
                return Task.FromResult(currentCall == 1
                    ? new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track A",
                        "Artist A",
                        null,
                        "spotify",
                        42)
                    : new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track B",
                        "Artist B",
                        null,
                        "spotify",
                        1));
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppCliOverlayCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommandDetailedAsync: () => Task.FromResult(new MediaKeyHelper.MediaCommandSendOutcome(
                Sent: true,
                MediaKeyHelper.MediaCommandRouteKind.CurrentGsmc,
                CandidateSourceAppUserModelId: "Spotify.exe",
                ElapsedMs: 3.4)),
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack("hotkey");

        await AssertEventuallyAsync(
            () => presenter.MediaUpdateCount == 1 && history.Count == 1,
            GetMediaOverlayCaptureTimeout());
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.Equal("media-overlay-track-changed", entry.DiagCode);
        Assert.Equal("CurrentGsmc", entry.Details?["sendRoute"]);
        Assert.Equal("true", entry.Details?["sendSent"]);
        Assert.Equal("false", entry.Details?["sendUsedInputFallback"]);
        Assert.True(entry.Details?.ContainsKey("sendCandidateSource"));
        Assert.DoesNotContain(
            entry.Details?.Keys ?? [],
            key => key.Contains("Focused", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MediaNextTrack_WhenCommandRoutesToPlayingSource_UsesRoutedSourceForOverlay()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        const string targetSource = "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4";
        MediaOverlaySessionSnapshot braveBaseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "NEW TRYHARD ACC",
            "humzh",
            null,
            "Brave",
            1724);
        MediaOverlaySessionSnapshot spotifyPreCommand = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "90210",
            "Travis Scott",
            "Rodeo",
            targetSource,
            42);
        MediaOverlaySessionSnapshot spotifyChanged = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "20 Min",
            "Lil Uzi Vert",
            "Luv Is Rage 2 (Deluxe)",
            targetSource,
            0);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                return Task.FromResult(string.Equals(preferredSource, targetSource, StringComparison.OrdinalIgnoreCase)
                    ? spotifyChanged
                    : braveBaseline);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["Brave"] = braveBaseline,
                [targetSource] = spotifyPreCommand,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { braveBaseline, spotifyChanged }),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());

        AppCliOverlayCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommandDetailedAsync: () => Task.FromResult(new MediaKeyHelper.MediaCommandSendOutcome(
                Sent: true,
                MediaKeyHelper.MediaCommandRouteKind.PlayingGsmc,
                CandidateSourceAppUserModelId: targetSource,
                ElapsedMs: 1.2)),
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack("hotkey");

        await AssertEventuallyAsync(
            () => presenter.MediaUpdateCount == 1 && history.Count == 1,
            GetMediaOverlayCaptureTimeout());
        Assert.Contains(presenter.Messages, message =>
            string.Equals(message.header, "Next track", StringComparison.Ordinal)
            && string.Equals(message.deviceName, "20 Min", StringComparison.Ordinal));
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.Equal("media-overlay-track-changed", entry.DiagCode);
        Assert.Equal("PlayingGsmc", entry.Details?["sendRoute"]);
        Assert.True(entry.Details?.ContainsKey("sendCandidateSource"));
    }

    [Fact]
    public async Task MediaPreviousTrack_WhenMetadataNeverChanges_RecordsUnchangedDiagnostics()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            84);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(baseline),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = baseline,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { baseline }),
            eventWaitOverride: (_, _, _, _) => Task.FromResult(new MediaEventAssistOutcome(false, null)),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());

        AppCliOverlayCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaPreviousTrackCommand: () => true,
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaPreviousTrack();

        await AssertEventuallyAsync(
            () => presenter.MessageUpdateCount == 1 && history.Count == 1,
            GetMediaOverlayCaptureTimeout());
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.Equal("media-overlay-track-unchanged", entry.DiagCode);
        Assert.Equal("unchanged", entry.Details?["outcome"]);
        Assert.Equal("unchanged", entry.Details?["finalFallbackClassification"]);
    }

    [Fact]
    public async Task MediaNextTrack_WhenNewerPressArrives_WaitsForTrailingOverlay()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        int snapshotCallCount = 0;
        int sendCount = 0;
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                int currentCall = Interlocked.Increment(ref snapshotCallCount);
                if (currentCall == 1)
                {
                    captureStarted.TrySetResult();
                    await releaseCapture.Task.WaitAsync(token);
                    return new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track A",
                        "Artist A",
                        null,
                        "spotify",
                        42);
                }

                return new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track B",
                    "Artist B",
                    null,
                    "spotify",
                    1);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppCliOverlayCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaPlayPauseCommand: () =>
            {
                Interlocked.Increment(ref sendCount);
                return true;
            },
            mediaNextTrackCommand: () =>
            {
                Interlocked.Increment(ref sendCount);
                return true;
            },
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack();
        await captureStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout());
        coordinator.MediaPlayPause();

        Assert.True(coordinator.IsMediaOverlayCaptureInFlightForTests);
        await AssertEventuallyAsync(
            () => Volatile.Read(ref sendCount) == 1,
            GetMediaOverlayCaptureTimeout());
        Assert.Equal(0, presenter.MessageUpdateCount);

        releaseCapture.TrySetResult();

        await AssertEventuallyAsync(
            () => !coordinator.IsMediaOverlayCaptureInFlightForTests && Volatile.Read(ref sendCount) == 2,
            GetMediaOverlayCaptureTimeout());

        Assert.Contains(presenter.Messages, message => string.Equals(message.deviceName, "Track B", StringComparison.Ordinal));
        Assert.True(presenter.MediaUpdateCount >= 1);
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.Equal("media-overlay-trailing-track", entry.DiagCode);
        Assert.Equal(0, presenter.ActionUpdateCount);
        Assert.Equal(0, presenter.DeviceUpdateCount);
        Assert.Equal(0, presenter.RoutineUpdateCount);
        Assert.Equal(0, presenter.RoutinePartialUpdateCount);
    }

    [Fact]
    public async Task MediaNextTrack_WhenCaptureAlreadyInFlightAndSendFails_ShowsFailureOverlay()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                captureStarted.TrySetResult();
                await releaseCapture.Task.WaitAsync(token);
                return new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppCliOverlayCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommand: () => false,
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack();
        await captureStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout());
        coordinator.MediaNextTrack();

        Assert.True(coordinator.IsMediaOverlayCaptureInFlightForTests);
        Assert.Equal(1, presenter.ShowCount);
        Assert.Equal(1, presenter.MessageUpdateCount);
        Assert.Contains(presenter.Messages, message => string.Equals(message.header, "Next track failed", StringComparison.Ordinal));

        releaseCapture.TrySetResult();
        await AssertEventuallyAsync(
            () => !coordinator.IsMediaOverlayCaptureInFlightForTests,
            GetMediaOverlayCaptureTimeout());
        Assert.Contains(history, entry => entry.DiagCode == "media-command-send-failed" && !entry.Success);
    }

    [Fact]
    public async Task MediaNextTrack_WhenTrailingCaptureHasNoMetadata_RecordsHiddenHistoryWithoutGenericOverlay()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        int snapshotCallCount = 0;
        int sendCount = 0;
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                int currentCall = Interlocked.Increment(ref snapshotCallCount);
                if (currentCall == 1)
                {
                    captureStarted.TrySetResult();
                    await releaseCapture.Task.WaitAsync(token);
                    return new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track A",
                        "Artist A",
                        null,
                        "spotify",
                        42);
                }

                if (currentCall == 2)
                {
                    return new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track B",
                        "Artist B",
                        null,
                        "spotify",
                        1);
                }

                return MediaOverlaySessionSnapshot.Empty;
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppCliOverlayCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommand: () =>
            {
                Interlocked.Increment(ref sendCount);
                return true;
            },
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack();
        await captureStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout());
        coordinator.MediaNextTrack();

        await AssertEventuallyAsync(
            () => Volatile.Read(ref sendCount) == 1,
            GetMediaOverlayCaptureTimeout());
        Assert.Equal(0, presenter.MessageUpdateCount);

        releaseCapture.TrySetResult();

        await AssertEventuallyAsync(
            () => !coordinator.IsMediaOverlayCaptureInFlightForTests
                && Volatile.Read(ref sendCount) == 2
                && history.Any(entry => entry.DiagCode == "media-overlay-trailing-no-metadata"),
            GetMediaOverlayCaptureTimeout());

        Assert.Equal(0, presenter.ShowCount);
        ExecutionHistoryEntry entry = Assert.Single(history, entry => entry.DiagCode == "media-overlay-trailing-no-metadata");
        Assert.True(entry.Success);
        Assert.True(entry.Skipped);
        Assert.Equal("trailing", entry.Details?["overlayCapture"]);
    }

    [Fact]
    public async Task ShowCurrentTrack_WhenTrackMetadataExists_ShowsTrackOverlay()
    {
        var presenter = new RecordingOverlayPresenter();
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>
            {
                new(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Fixture Title",
                    "Fixture Artist",
                    "Fixture Album",
                    "fixture-source",
                    33),
            }));

        AppCliOverlayCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine));

        coordinator.ShowCurrentTrack();

        await AssertEventuallyAsync(() => presenter.MediaUpdateCount == 1, GetMediaOverlayCaptureTimeout());
        Assert.Contains(presenter.Messages, message => string.Equals(message.header, "Current track", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShowCurrentTrack_WhenPausedTrackMetadataExists_ShowsPausedTrackOverlay()
    {
        var presenter = new RecordingOverlayPresenter();
        var pausedSnapshot = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Fixture Title",
            "Fixture Artist",
            "Fixture Album",
            "fixture-source",
            33);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(pausedSnapshot),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>
            {
                pausedSnapshot,
            }));

        AppCliOverlayCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine));

        coordinator.ShowCurrentTrack();

        await AssertEventuallyAsync(() => presenter.MediaUpdateCount == 1, GetMediaOverlayCaptureTimeout());
        Assert.Contains(presenter.Messages, message => string.Equals(message.header, "Current track paused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShowCurrentTrack_WhenNoTrackMetadataExists_ShowsNoCurrentTrack()
    {
        var presenter = new RecordingOverlayPresenter();
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(new MediaOverlaySessionSnapshot(
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                null,
                null,
                null,
                null,
                null)),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppCliOverlayCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine));

        coordinator.ShowCurrentTrack();

        await AssertEventuallyAsync(() => presenter.MessageUpdateCount == 1, GetMediaOverlayCaptureTimeout());
        Assert.Contains(presenter.Messages, message => string.Equals(message.header, "No current track", StringComparison.Ordinal));
    }

    private static AppCliOverlayCoordinator CreateCoordinator(
        RecordingOverlayPresenter presenter,
        MediaOverlayCommandService? mediaOverlayCommands = null,
        Func<bool>? mediaPlayPauseCommand = null,
        Func<bool>? mediaNextTrackCommand = null,
        Func<bool>? mediaPreviousTrackCommand = null,
        Func<Task<bool>>? mediaPlayPauseCommandAsync = null,
        Func<Task<bool>>? mediaNextTrackCommandAsync = null,
        Func<Task<bool>>? mediaPreviousTrackCommandAsync = null,
        Action<ExecutionHistoryEntry>? mediaHistoryRecorder = null,
        Func<Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaPlayPauseCommandDetailedAsync = null,
        Func<Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaNextTrackCommandDetailedAsync = null,
        Func<Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaPreviousTrackCommandDetailedAsync = null)
    {
        var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        var overlay = new OverlayService(action => action(), _ => presenter);
        return new AppCliOverlayCoordinator(
            audio,
            overlay,
            mediaOverlayCommands ?? new MediaOverlayCommandService(),
            Logger.Instance,
            () => new Settings(),
            mediaPlayPauseCommand,
            mediaNextTrackCommand,
            mediaPreviousTrackCommand,
            mediaPlayPauseCommandAsync,
            mediaNextTrackCommandAsync,
            mediaPreviousTrackCommandAsync,
            mediaHistoryRecorder,
            mediaPlayPauseCommandDetailedAsync,
            mediaNextTrackCommandDetailedAsync,
            mediaPreviousTrackCommandDetailedAsync);
    }

    private static TimeSpan GetMediaOverlayCaptureTimeout()
    {
        return TimeSpan.FromMilliseconds(AppConstants.MediaOverlay.MaxCaptureDurationMs + 1000);
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadlineUtc = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(2));
        while (DateTime.UtcNow < deadlineUtc)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition());
    }
}
