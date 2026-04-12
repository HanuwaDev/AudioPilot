using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppWindowVisibilityCoordinatorTests
{
    [Fact]
    public void ShowWindow_DefersUntilStartupVisibilityResolves()
    {
        var windowState = new AppWindowStateCoordinator();
        bool showCalled = false;
        bool refreshCacheCalled = false;
        bool refreshMixerCalled = false;
        bool updateMuteCalled = false;
        using var loggerScope = new TestLoggerScope(nameof(AppWindowVisibilityCoordinatorTests), "window-show-deferred.log");

        AppWindowVisibilityCoordinator.ShowWindow(
            windowState,
            () => showCalled = true,
            static () => { },
            () => refreshCacheCalled = true,
            () =>
            {
                refreshMixerCalled = true;
                return Task.CompletedTask;
            },
            () =>
            {
                updateMuteCalled = true;
                return Task.CompletedTask;
            },
            loggerScope.Logger,
            DateTime.UtcNow);

        Assert.True(windowState.HasInteractiveShowRequest);
        Assert.False(showCalled);
        Assert.False(refreshCacheCalled);
        Assert.False(refreshMixerCalled);
        Assert.False(updateMuteCalled);
    }

    [Fact]
    public void BuildMinimizePlan_CombinesFirstRunAndSaveBalloonState()
    {
        var windowState = new AppWindowStateCoordinator
        {
            ShowBalloonOnFirstMinimize = true,
        };

        windowState.MarkShown(DateTime.UtcNow.AddMilliseconds(-AppConstants.Timing.ShowCooldownMs - 10));

        MinimizeWindowPlan plan = AppWindowVisibilityCoordinator.BuildMinimizePlan(
            windowState,
            showBalloonAfterSave: true,
            DateTime.UtcNow);

        Assert.Equal(MinimizeAttemptResult.Started, plan.AttemptResult);
        Assert.True(plan.ShowBalloon);
        Assert.True(plan.ConsumeFirstRunBalloon);
        Assert.True(plan.ConsumeSaveBalloon);
    }

    [Fact]
    public void ApplyMinimizePlan_ClearsConsumedBalloonFlags_AndCompletesMinimize()
    {
        var windowState = new AppWindowStateCoordinator
        {
            ShowBalloonOnFirstMinimize = true,
        };

        bool saveBalloonCleared = false;
        bool minimizeCalled = false;
        Action? afterHide = null;
        using var loggerScope = new TestLoggerScope(nameof(AppWindowVisibilityCoordinatorTests), "window-visibility.log");

        AppWindowVisibilityCoordinator.ApplyMinimizePlan(
            windowState,
            new MinimizeWindowPlan(MinimizeAttemptResult.Started, ShowBalloon: true, ConsumeFirstRunBalloon: true, ConsumeSaveBalloon: true),
            (callback, _, _) =>
            {
                minimizeCalled = true;
                afterHide = callback;
            },
            () => saveBalloonCleared = true,
            loggerScope.Logger);

        Assert.True(minimizeCalled);
        Assert.False(windowState.ShowBalloonOnFirstMinimize);
        Assert.True(saveBalloonCleared);

        afterHide?.Invoke();

        Assert.Equal(MinimizeAttemptResult.Started, windowState.TryBeginMinimize(DateTime.UtcNow.AddMilliseconds(-AppConstants.Timing.ShowCooldownMs - 10)));
    }

    [Fact]
    public void ShowWindow_ShowsImmediatelyAfterStartupVisibilityResolves()
    {
        var windowState = new AppWindowStateCoordinator();
        windowState.MarkStartupVisibilityResolved();
        bool showCalled = false;
        bool refreshCollectionsCalled = false;
        bool refreshCacheCalled = false;
        bool refreshMixerCalled = false;
        bool updateMuteCalled = false;
        using var loggerScope = new TestLoggerScope(nameof(AppWindowVisibilityCoordinatorTests), "window-show-ready.log");

        AppWindowVisibilityCoordinator.ShowWindow(
            windowState,
            () => showCalled = true,
            () => refreshCollectionsCalled = true,
            () => refreshCacheCalled = true,
            () =>
            {
                refreshMixerCalled = true;
                return Task.CompletedTask;
            },
            () =>
            {
                updateMuteCalled = true;
                return Task.CompletedTask;
            },
            loggerScope.Logger,
            DateTime.UtcNow);

        Assert.True(showCalled);
        Assert.True(refreshCollectionsCalled);
        Assert.True(refreshCacheCalled);
        Assert.True(refreshMixerCalled);
        Assert.True(updateMuteCalled);
    }
}
