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

    [Theory]
    [InlineData("Spotify.exe", "Spotify", @"C:\Users\Arman\AppData\Roaming\Spotify\Spotify.exe")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify", @"C:\Program Files\WindowsApps\Spotify.exe")]
    [InlineData("Chrome", "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData("Microsoft.MicrosoftEdge_8wekyb3d8bbwe!MicrosoftEdge", "msedge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    public void DoesSessionSourceMatchFocusedProcess_ReturnsTrue_ForMatchingMediaSource(
        string source,
        string processName,
        string executablePath)
    {
        bool matches = MediaKeyHelper.DoesSessionSourceMatchFocusedProcess(
            source,
            new MediaKeyHelper.FocusedProcessInfo(1234, processName, executablePath));

        Assert.True(matches);
    }

    [Theory]
    [InlineData("Chrome", "Spotify", @"C:\Users\Arman\AppData\Roaming\Spotify\Spotify.exe")]
    [InlineData("Spotify.exe", "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData("Music", "mus", @"C:\Apps\mus.exe")]
    public void DoesSessionSourceMatchFocusedProcess_ReturnsFalse_ForDifferentMediaSource(
        string source,
        string processName,
        string executablePath)
    {
        bool matches = MediaKeyHelper.DoesSessionSourceMatchFocusedProcess(
            source,
            new MediaKeyHelper.FocusedProcessInfo(1234, processName, executablePath));

        Assert.False(matches);
    }

}
