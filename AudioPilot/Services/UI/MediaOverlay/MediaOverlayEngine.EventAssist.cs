using AudioPilot.Logging;
using Windows.Foundation;
using Windows.Media.Control;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed partial class MediaOverlayEngine
    {
        private async Task<bool> DelayWithEventAssistIfWithinBudgetAsync(
            int delayMs,
            string? preferredSourceAppUserModelId,
            DateTimeOffset deadlineUtc,
            long commandSequence,
            CancellationToken cancellationToken)
        {
            MediaOverlayDelayAssistResult result = await DelayWithEventAssistOutcomeIfWithinBudgetAsync(
                delayMs,
                preferredSourceAppUserModelId,
                deadlineUtc,
                commandSequence,
                cancellationToken);
            return result.CompletedWithinBudget;
        }

        private async Task<MediaOverlayDelayAssistResult> DelayWithEventAssistOutcomeIfWithinBudgetAsync(
            int delayMs,
            string? preferredSourceAppUserModelId,
            DateTimeOffset deadlineUtc,
            long commandSequence,
            CancellationToken cancellationToken)
        {
            ThrowIfSuperseded(commandSequence, cancellationToken);
            _commandSnapshotCache.InvalidateSnapshots(commandSequence);

            if (DateTimeOffset.UtcNow.AddMilliseconds(delayMs) > deadlineUtc)
            {
                return new MediaOverlayDelayAssistResult(false, false);
            }

            MediaEventAssistOutcome eventAssistOutcome = await WaitForRelevantMediaEventAsync(
                preferredSourceAppUserModelId,
                delayMs,
                commandSequence,
                cancellationToken);
            ThrowIfSuperseded(commandSequence, cancellationToken);

            if (eventAssistOutcome.ObservedEvent)
            {
                MarkRecentlySignaledSource(eventAssistOutcome.SignaledSourceAppUserModelId);
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    $"Observed GSMTC event during post-command wait source={LogPrivacy.Id(preferredSourceAppUserModelId)} signaledSource={LogPrivacy.Id(eventAssistOutcome.SignaledSourceAppUserModelId)} eventKind={eventAssistOutcome.EventKind} waitMs={delayMs}",
                    nameof(DelayWithEventAssistIfWithinBudgetAsync));
            }

            return new MediaOverlayDelayAssistResult(true, eventAssistOutcome.ObservedEvent, eventAssistOutcome);
        }

        private async Task<MediaEventAssistOutcome> WaitForRelevantMediaEventAsync(
            string? preferredSourceAppUserModelId,
            int maxWaitMs,
            long commandSequence,
            CancellationToken cancellationToken)
        {
            if (maxWaitMs <= 0)
            {
                return new MediaEventAssistOutcome(false, null);
            }

            if (_eventWaitOverride != null)
            {
                return await _eventWaitOverride(preferredSourceAppUserModelId, maxWaitMs, commandSequence, cancellationToken);
            }

            try
            {
                GsmtcEventWaiterRegistration registration = await GetOrCreateGsmtcEventWaiterRegistrationAsync(
                    commandSequence,
                    cancellationToken);

                MediaEventAssistOutcome outcome = await registration.Waiter.WaitAsync(
                    preferredSourceAppUserModelId,
                    maxWaitMs,
                    cancellationToken);
                ThrowIfSuperseded(commandSequence, cancellationToken);
                return outcome;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    $"Failed to wait for GSMTC event assist source={LogPrivacy.Id(preferredSourceAppUserModelId)}. {ex.GetType().Name}",
                    nameof(WaitForRelevantMediaEventAsync));
                return new MediaEventAssistOutcome(false, null);
            }
        }

        private async Task<GsmtcEventWaiterRegistration> GetOrCreateGsmtcEventWaiterRegistrationAsync(
            long commandSequence,
            CancellationToken cancellationToken)
        {
            lock (_eventWaiterLock)
            {
                if (_eventWaitersByCommandSequence.TryGetValue(commandSequence, out GsmtcEventWaiterRegistration? existing))
                {
                    return existing;
                }
            }

            ThrowIfSuperseded(commandSequence, cancellationToken);
            GlobalSystemMediaTransportControlsSessionManager manager = await _commandSnapshotCache.GetManagerAsync(commandSequence);
            ThrowIfSuperseded(commandSequence, cancellationToken);

            var created = new GsmtcEventWaiterRegistration(manager);
            lock (_eventWaiterLock)
            {
                if (_eventWaitersByCommandSequence.TryGetValue(commandSequence, out GsmtcEventWaiterRegistration? existing))
                {
                    created.Dispose();
                    return existing;
                }

                _eventWaitersByCommandSequence[commandSequence] = created;
                return created;
            }
        }

        private void ClearGsmtcEventWaiter(long commandSequence)
        {
            GsmtcEventWaiterRegistration? registration;
            lock (_eventWaiterLock)
            {
                if (_eventWaitersByCommandSequence.TryGetValue(commandSequence, out registration))
                {
                    _eventWaitersByCommandSequence.Remove(commandSequence);
                }
            }

            registration?.Dispose();
        }

        private sealed class GsmtcEventWaiterRegistration : IDisposable
        {
            private readonly GlobalSystemMediaTransportControlsSessionManager _manager;
            private readonly List<GlobalSystemMediaTransportControlsSession> _sessions;
            private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs> _currentSessionChangedHandler;
            private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> _sessionsChangedHandler;
            private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _mediaPropertiesChangedHandler;
            private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _playbackInfoChangedHandler;
            private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> _timelinePropertiesChangedHandler;
            private int _disposed;

            public GsmtcEventWaiterRegistration(GlobalSystemMediaTransportControlsSessionManager manager)
            {
                _manager = manager;
                _sessions = [.. manager.GetSessions()];
                Waiter = new MediaOverlayCommandEventWaiter();

                _currentSessionChangedHandler = OnCurrentSessionChanged;
                _sessionsChangedHandler = OnSessionsChanged;
                _mediaPropertiesChangedHandler = OnMediaPropertiesChanged;
                _playbackInfoChangedHandler = OnPlaybackInfoChanged;
                _timelinePropertiesChangedHandler = OnTimelinePropertiesChanged;

                _manager.CurrentSessionChanged += _currentSessionChangedHandler;
                _manager.SessionsChanged += _sessionsChangedHandler;
                foreach (GlobalSystemMediaTransportControlsSession session in _sessions)
                {
                    session.MediaPropertiesChanged += _mediaPropertiesChangedHandler;
                    session.PlaybackInfoChanged += _playbackInfoChangedHandler;
                    session.TimelinePropertiesChanged += _timelinePropertiesChangedHandler;
                }
            }

            public MediaOverlayCommandEventWaiter Waiter { get; }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                TryDetach("manager-current-session", () => _manager.CurrentSessionChanged -= _currentSessionChangedHandler);
                TryDetach("manager-sessions", () => _manager.SessionsChanged -= _sessionsChangedHandler);
                foreach (GlobalSystemMediaTransportControlsSession session in _sessions)
                {
                    TryDetach("session-media-properties", () => session.MediaPropertiesChanged -= _mediaPropertiesChangedHandler);
                    TryDetach("session-playback-info", () => session.PlaybackInfoChanged -= _playbackInfoChangedHandler);
                    TryDetach("session-timeline-properties", () => session.TimelinePropertiesChanged -= _timelinePropertiesChangedHandler);
                }

                Waiter.Dispose();
            }

            private void OnCurrentSessionChanged(
                GlobalSystemMediaTransportControlsSessionManager changedManager,
                CurrentSessionChangedEventArgs _args)
            {
                Signal(MediaEventAssistKind.CurrentSessionChanged, CleanValue(changedManager.GetCurrentSession()?.SourceAppUserModelId));
            }

            private void OnSessionsChanged(
                GlobalSystemMediaTransportControlsSessionManager changedManager,
                SessionsChangedEventArgs _args)
            {
                Signal(MediaEventAssistKind.SessionsChanged, CleanValue(changedManager.GetCurrentSession()?.SourceAppUserModelId));
            }

            private void OnMediaPropertiesChanged(
                GlobalSystemMediaTransportControlsSession session,
                MediaPropertiesChangedEventArgs _args)
            {
                Signal(MediaEventAssistKind.MediaPropertiesChanged, CleanValue(session.SourceAppUserModelId));
            }

            private void OnPlaybackInfoChanged(
                GlobalSystemMediaTransportControlsSession session,
                PlaybackInfoChangedEventArgs _args)
            {
                Signal(MediaEventAssistKind.PlaybackInfoChanged, CleanValue(session.SourceAppUserModelId));
            }

            private void OnTimelinePropertiesChanged(
                GlobalSystemMediaTransportControlsSession session,
                TimelinePropertiesChangedEventArgs _args)
            {
                Signal(MediaEventAssistKind.TimelinePropertiesChanged, CleanValue(session.SourceAppUserModelId));
            }

            private void Signal(MediaEventAssistKind eventKind, string? sourceAppUserModelId)
            {
                Waiter.Signal(new MediaEventAssistOutcome(true, sourceAppUserModelId, eventKind));
            }

            private static void TryDetach(string eventName, Action detach)
            {
                try
                {
                    detach();
                }
                catch (Exception ex)
                {
                    Logger.Instance?.Trace(
                        "MediaOverlayHelper",
                        $"media-event-waiter-detach-failed | event={eventName} reason={ex.GetType().Name}",
                        nameof(GsmtcEventWaiterRegistration));
                }
            }
        }
    }
}
