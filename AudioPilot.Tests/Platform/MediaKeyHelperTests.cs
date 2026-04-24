using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Platform;

[Collection("MediaKeyHelperIsolation")]
public sealed class MediaKeyHelperTests
{
    [Fact]
    public void TryPressNextTrack_UsesSystemMediaCommandBeforeNativeSend()
    {
        try
        {
            MediaKeyHelper.SystemMediaCommandOverrideForTests = static command =>
                command == MediaKeyHelper.SystemMediaCommand.NextTrack;
            MediaKeyHelper.SendInputOverrideForTests = static _ => throw new InvalidOperationException("native fallback should not run");

            bool sent = MediaKeyHelper.TryPressNextTrack();

            Assert.True(sent);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public async Task TryPressNextTrackAsync_UsesSystemMediaCommandBeforeNativeSend()
    {
        try
        {
            MediaKeyHelper.SystemMediaCommandOverrideForTests = static command =>
                command == MediaKeyHelper.SystemMediaCommand.NextTrack;
            MediaKeyHelper.SendInputOverrideForTests = static _ => throw new InvalidOperationException("native fallback should not run");

            bool sent = await MediaKeyHelper.TryPressNextTrackAsync();

            Assert.True(sent);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressNextTrackDetailed_ReportsSystemMediaOverrideRoute()
    {
        try
        {
            MediaKeyHelper.SystemMediaCommandOverrideForTests = static command =>
                command == MediaKeyHelper.SystemMediaCommand.NextTrack;
            MediaKeyHelper.SendInputOverrideForTests = static _ => throw new InvalidOperationException("native fallback should not run");

            MediaKeyHelper.MediaCommandSendOutcome outcome = MediaKeyHelper.TryPressNextTrackDetailed();

            Assert.True(outcome.Sent);
            Assert.Equal(MediaKeyHelper.MediaCommandRouteKind.TestOverride, outcome.Route);
            Assert.False(outcome.UsedSendInputFallback);
            Assert.Null(outcome.FailureReason);
            Assert.True(outcome.ElapsedMs >= 0);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressNextTrackDetailed_HonorsExplicitFallbackSuppression_WhenDetailedCommandRequestsIt()
    {
        try
        {
            MediaKeyHelper.DetailedSystemMediaCommandOverrideForTests = static _ => new MediaKeyHelper.MediaCommandSendOutcome(
                Sent: false,
                MediaKeyHelper.MediaCommandRouteKind.ControllableGsmc,
                SuppressFallback: true,
                CandidateSourceAppUserModelId: "Spotify.exe",
                FailureReason: "fallback-suppressed");
            MediaKeyHelper.SendInputOverrideForTests = static _ => throw new InvalidOperationException("native fallback should not run");

            MediaKeyHelper.MediaCommandSendOutcome outcome = MediaKeyHelper.TryPressNextTrackDetailed();

            Assert.False(outcome.Sent);
            Assert.Equal(MediaKeyHelper.MediaCommandRouteKind.ControllableGsmc, outcome.Route);
            Assert.True(outcome.SuppressFallback);
            Assert.False(outcome.UsedSendInputFallback);
            Assert.Equal("fallback-suppressed", outcome.FailureReason);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressNextTrackDetailed_UsesNativeFallback_WhenNoSystemMediaCandidateExists()
    {
        try
        {
            MediaKeyHelper.DetailedSystemMediaCommandOverrideForTests = static _ => new MediaKeyHelper.MediaCommandSendOutcome(
                Sent: false,
                MediaKeyHelper.MediaCommandRouteKind.None,
                FailureReason: "no-system-media-candidate");
            MediaKeyHelper.SendInputOverrideForTests = static _ => (2u, 0);

            MediaKeyHelper.MediaCommandSendOutcome outcome = MediaKeyHelper.TryPressNextTrackDetailed();

            Assert.True(outcome.Sent);
            Assert.Equal(MediaKeyHelper.MediaCommandRouteKind.SendInputFallback, outcome.Route);
            Assert.True(outcome.UsedSendInputFallback);
            Assert.Null(outcome.FailureReason);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressPlayPauseDetailed_UsesNativeFallback_WhenNoSystemMediaCandidateExists()
    {
        try
        {
            MediaKeyHelper.DetailedSystemMediaCommandOverrideForTests = static _ => new MediaKeyHelper.MediaCommandSendOutcome(
                Sent: false,
                MediaKeyHelper.MediaCommandRouteKind.None,
                FailureReason: "no-system-media-candidate");
            MediaKeyHelper.SendInputOverrideForTests = static _ => (2u, 0);

            MediaKeyHelper.MediaCommandSendOutcome outcome = MediaKeyHelper.TryPressPlayPauseDetailed();

            Assert.True(outcome.Sent);
            Assert.Equal(MediaKeyHelper.MediaCommandRouteKind.SendInputFallback, outcome.Route);
            Assert.True(outcome.UsedSendInputFallback);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressPreviousTrackDetailed_ReportsNativeFallbackFailure()
    {
        try
        {
            MediaKeyHelper.DetailedSystemMediaCommandOverrideForTests = static _ => new MediaKeyHelper.MediaCommandSendOutcome(
                Sent: false,
                MediaKeyHelper.MediaCommandRouteKind.None,
                FailureReason: "no-system-media-candidate");
            MediaKeyHelper.SendInputOverrideForTests = static _ => (1u, 5);

            MediaKeyHelper.MediaCommandSendOutcome outcome = MediaKeyHelper.TryPressPreviousTrackDetailed();

            Assert.False(outcome.Sent);
            Assert.Equal(MediaKeyHelper.MediaCommandRouteKind.SendInputFallback, outcome.Route);
            Assert.True(outcome.UsedSendInputFallback);
            Assert.Equal("sendinput-partial", outcome.FailureReason);
            Assert.Equal(5, outcome.ErrorCode);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public async Task ProbeSystemMediaManagerAsync_ReacquiresManagerAfterCachedManagerBecomesUnusable()
    {
        int managerRequestCount = 0;
        try
        {
            MediaKeyHelper.SystemMediaManagerRequestOverrideForTests = () =>
            {
                managerRequestCount++;
                return Task.FromResult<Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager>(null!);
            };
            bool firstProbeSucceeded = await MediaKeyHelper.ProbeSystemMediaManagerForTestsAsync();
            bool secondProbeSucceeded = await MediaKeyHelper.ProbeSystemMediaManagerForTestsAsync();

            Assert.False(firstProbeSucceeded);
            Assert.False(secondProbeSucceeded);
            Assert.Equal(2, managerRequestCount);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressPlayPause_FallsBackToNativeSend_WhenSystemMediaCommandDoesNotHandle()
    {
        try
        {
            MediaKeyHelper.SystemMediaCommandOverrideForTests = static _ => false;
            MediaKeyHelper.SendInputOverrideForTests = static _ => (2u, 0);

            bool sent = MediaKeyHelper.TryPressPlayPause();

            Assert.True(sent);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressPlayPause_ReturnsTrue_WhenNativeSendSucceeds()
    {
        try
        {
            MediaKeyHelper.SendInputOverrideForTests = static _ => (2u, 0);

            bool sent = MediaKeyHelper.TryPressPlayPause();

            Assert.True(sent);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressNextTrack_LogsFailure_WhenNativeSendReturnsPartialCount()
    {
        using var logScope = new TestLoggerScope(nameof(TryPressNextTrack_LogsFailure_WhenNativeSendReturnsPartialCount), "mediakey.log");

        try
        {
            MediaKeyHelper.LoggerOverrideForTests = logScope.Logger;
            MediaKeyHelper.SendInputOverrideForTests = static _ => (1u, 5);

            bool sent = MediaKeyHelper.TryPressNextTrack();

            Assert.False(sent);

            string logText = logScope.DisposeAndReadLogText();
            Assert.Contains("media-key-send-failed:NextTrack", logText, StringComparison.Ordinal);
            Assert.Contains("Win32Exception", logText, StringComparison.Ordinal);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressPreviousTrack_LogsException_WhenNativeSendThrows()
    {
        using var logScope = new TestLoggerScope(nameof(TryPressPreviousTrack_LogsException_WhenNativeSendThrows), "mediakey.log");

        try
        {
            MediaKeyHelper.LoggerOverrideForTests = logScope.Logger;
            MediaKeyHelper.SendInputOverrideForTests = static _ => throw new InvalidOperationException("boom");

            bool sent = MediaKeyHelper.TryPressPreviousTrack();

            Assert.False(sent);

            string logText = logScope.DisposeAndReadLogText();
            Assert.Contains("media-key-send-exception:PreviousTrack", logText, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", logText, StringComparison.Ordinal);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

}
