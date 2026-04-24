using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioSessionProcessCacheCoordinatorTests
{
    [Fact]
    public async Task StartCleanupTaskAsync_IsIdempotent()
    {
        using var coordinator = new AudioSessionProcessCacheCoordinator(Logger.Instance, TimeSpan.FromMinutes(10));

        await coordinator.StartCleanupTaskAsync(() => false);
        await coordinator.StartCleanupTaskAsync(() => false);

        Assert.True(coordinator.IsCleanupLoopStarted);
        Assert.NotNull(coordinator.CleanupTaskForTests);
    }

    [Fact]
    public void TrimProcessCacheForTests_TrimsToConfiguredLimit()
    {
        using var coordinator = new AudioSessionProcessCacheCoordinator(Logger.Instance, TimeSpan.FromMinutes(10));

        for (int index = 0; index < AppConstants.Limits.MaxProcessCacheEntries + 5; index++)
        {
            coordinator.AddProcessCacheEntryForTests(
                (uint)index + 1,
                $"proc-{index}",
                $"Display {index}",
                null,
                DateTime.UtcNow.AddMinutes(-index).Ticks);
        }

        coordinator.TrimProcessCacheForTests();

        Assert.True(coordinator.ProcessCacheCount <= AppConstants.Limits.MaxProcessCacheEntries);
    }
}
