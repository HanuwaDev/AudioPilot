using AudioPilot.Helpers;

namespace AudioPilot.Tests.Helpers;

public sealed class RoutineTriggerPathHelperTests
{
    [Fact]
    public void NormalizeExecutablePath_FileUri_ReturnsLocalWindowsPath()
    {
        string normalized = RoutineTriggerPathHelper.NormalizeExecutablePath("file:///C:/Apps/Spotify/Spotify.exe");

        Assert.Equal(@"C:\Apps\Spotify\Spotify.exe", normalized);
    }

    [Fact]
    public void IsExecutablePathMatch_TreatsFileUriAndNativePathAsEquivalent()
    {
        bool matches = RoutineTriggerPathHelper.IsExecutablePathMatch(
            "file:///C:/Apps/Spotify/Spotify.exe",
            @"C:\Apps\Spotify\Spotify.exe");

        Assert.True(matches);
    }
}
