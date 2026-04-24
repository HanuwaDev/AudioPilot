using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Tests.Services.Internal;

public sealed class HotkeyDispatchCoordinatorTests
{
    [Fact]
    public async Task ExecuteCallback_DebouncesConcurrentDuplicateDeliveries()
    {
        long now = AppConstants.Timing.HotkeyDebounceTicks + 1;
        var coordinator = new HotkeyDispatchCoordinator(Logger.Instance, () => now);
        int callbackCount = 0;

        Task[] dispatches =
        [
            .. Enumerable.Range(0, 64)
                .Select(_ => Task.Run(() => coordinator.ExecuteCallback(10000, "media-next", () => Interlocked.Increment(ref callbackCount)))),
        ];

        await Task.WhenAll(dispatches);

        await AssertEventuallyAsync(() => Volatile.Read(ref callbackCount) > 0);
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref callbackCount));
    }

    [Fact]
    public void ExecuteCallback_TrimsExpiredDebounceEntries()
    {
        long now = AppConstants.Timing.HotkeyDebounceTicks + 1;
        var coordinator = new HotkeyDispatchCoordinator(Logger.Instance, () => now);

        coordinator.ExecuteCallback(10000, "first", static () => { });
        now += AppConstants.Timing.HotkeyDebounceRetentionTicks + 1;
        coordinator.ExecuteCallback(10001, "second", static () => { });

        Assert.Equal(1, coordinator.DebounceTimestampCountForTests);
    }

    [Fact]
    public void ExecuteCallback_BoundsDebounceEntryCount()
    {
        long now = AppConstants.Timing.HotkeyDebounceTicks + 1;
        var coordinator = new HotkeyDispatchCoordinator(Logger.Instance, () => now);

        for (int hotkeyId = 10000; hotkeyId < 10000 + AppConstants.Limits.MaxHotkeyDebounceEntries + 64; hotkeyId++)
        {
            coordinator.ExecuteCallback(hotkeyId, $"hotkey-{hotkeyId}", static () => { });
            now += TimeSpan.TicksPerMillisecond;
        }

        Assert.True(coordinator.DebounceTimestampCountForTests <= AppConstants.Limits.MaxHotkeyDebounceEntries);
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }
}
