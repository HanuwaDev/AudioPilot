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
                ThrowIfSuperseded(commandSequence, cancellationToken);
                GlobalSystemMediaTransportControlsSessionManager manager = await _commandSnapshotCache.GetManagerAsync(commandSequence);
                ThrowIfSuperseded(commandSequence, cancellationToken);

                TaskCompletionSource<MediaEventAssistOutcome> eventObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

                void SignalEvent(MediaEventAssistKind eventKind, string? sourceAppUserModelId)
                {
                    eventObserved.TrySetResult(new MediaEventAssistOutcome(true, sourceAppUserModelId, eventKind));
                }

                void OnCurrentSessionChanged(
                    GlobalSystemMediaTransportControlsSessionManager changedManager,
                    CurrentSessionChangedEventArgs _args)
                {
                    SignalEvent(
                        MediaEventAssistKind.CurrentSessionChanged,
                        CleanValue(changedManager.GetCurrentSession()?.SourceAppUserModelId));
                }

                void OnSessionsChanged(
                    GlobalSystemMediaTransportControlsSessionManager changedManager,
                    SessionsChangedEventArgs _args)
                {
                    SignalEvent(
                        MediaEventAssistKind.SessionsChanged,
                        CleanValue(changedManager.GetCurrentSession()?.SourceAppUserModelId));
                }

                void OnMediaPropertiesChanged(
                    GlobalSystemMediaTransportControlsSession? session,
                    MediaPropertiesChangedEventArgs _args)
                {
                    SignalEvent(
                        MediaEventAssistKind.MediaPropertiesChanged,
                        CleanValue(session?.SourceAppUserModelId));
                }

                void OnPlaybackInfoChanged(
                    GlobalSystemMediaTransportControlsSession? session,
                    PlaybackInfoChangedEventArgs _args)
                {
                    SignalEvent(
                        MediaEventAssistKind.PlaybackInfoChanged,
                        CleanValue(session?.SourceAppUserModelId));
                }

                void OnTimelinePropertiesChanged(
                    GlobalSystemMediaTransportControlsSession? session,
                    TimelinePropertiesChangedEventArgs _args)
                {
                    SignalEvent(
                        MediaEventAssistKind.TimelinePropertiesChanged,
                        CleanValue(session?.SourceAppUserModelId));
                }

                TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs> currentSessionChangedHandler =
                    OnCurrentSessionChanged;
                TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> sessionsChangedHandler =
                    OnSessionsChanged;
                TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> mediaPropertiesChangedHandler =
                    OnMediaPropertiesChanged;
                TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> playbackInfoChangedHandler =
                    OnPlaybackInfoChanged;
                TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> timelinePropertiesChangedHandler =
                    OnTimelinePropertiesChanged;

                List<GlobalSystemMediaTransportControlsSession> sessions = [.. manager.GetSessions()];

                manager.CurrentSessionChanged += currentSessionChangedHandler;
                manager.SessionsChanged += sessionsChangedHandler;
                foreach (GlobalSystemMediaTransportControlsSession session in sessions)
                {
                    session.MediaPropertiesChanged += mediaPropertiesChangedHandler;
                    session.PlaybackInfoChanged += playbackInfoChangedHandler;
                    session.TimelinePropertiesChanged += timelinePropertiesChangedHandler;
                }

                try
                {
                    Task completed = await Task.WhenAny(
                        eventObserved.Task,
                        Task.Delay(maxWaitMs, cancellationToken));

                    ThrowIfSuperseded(commandSequence, cancellationToken);
                    if (completed == eventObserved.Task && eventObserved.Task.IsCompletedSuccessfully)
                    {
                        return eventObserved.Task.Result;
                    }

                    return new MediaEventAssistOutcome(false, null);
                }
                finally
                {
                    manager.CurrentSessionChanged -= currentSessionChangedHandler;
                    manager.SessionsChanged -= sessionsChangedHandler;
                    foreach (GlobalSystemMediaTransportControlsSession session in sessions)
                    {
                        session.MediaPropertiesChanged -= mediaPropertiesChangedHandler;
                        session.PlaybackInfoChanged -= playbackInfoChangedHandler;
                        session.TimelinePropertiesChanged -= timelinePropertiesChangedHandler;
                    }
                }
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
    }
}
