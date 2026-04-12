using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Platform;

public sealed class MediaKeyHelperTests
{
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
