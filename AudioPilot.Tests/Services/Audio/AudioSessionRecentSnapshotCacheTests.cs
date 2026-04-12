using AudioPilot.Constants;
using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioSessionRecentSnapshotCacheTests
{
    [Fact]
    public void UpdateRecentNoControlsSnapshot_AllowsImmediateCacheReuse()
    {
        var cache = new AudioSessionRecentSnapshotCache();
        List<AudioSessionSnapshot> sessions =
        [
            new AudioSessionSnapshot("Master Volume", 55f, "Speakers", null, null, null),
            new AudioSessionSnapshot("Player", 40f, "Speakers", "player", null, 42),
        ];

        cache.UpdateRecentNoControlsSnapshot(
            AudioMixerMode.Output,
            sessions,
            "fingerprint",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dev-1" },
            useSelectivePlaybackScan: true);

        bool found = cache.TryGetRecentNoControlsSnapshotData(
            AudioMixerMode.Output,
            AppConstants.Timing.SessionSnapshotFastPathCacheMs,
            out var cached);

        Assert.True(found);
        Assert.Equal(2, cached.Count);
    }

    [Fact]
    public void RecordEndpointVolumeNotification_UpdatesCachedSharedRow()
    {
        var cache = new AudioSessionRecentSnapshotCache();
        List<AudioSessionSnapshot> sessions =
        [
            new AudioSessionSnapshot("Master Volume", 10f, "Speakers", null, null, null),
        ];

        cache.UpdateRecentNoControlsSnapshot(AudioMixerMode.Output, sessions);
        cache.RecordEndpointVolumeNotification(AudioMixerMode.Output, 73f);
        cache.TryGetRecentNoControlsSnapshotData(AudioMixerMode.Output, AppConstants.Timing.SessionSnapshotFastPathCacheMs, out var cached);

        Assert.Equal(73f, cached[0].Volume);
    }
}
