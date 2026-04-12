using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

public sealed class OverlayWindowTests
{
    [Fact]
    public void TrySplitListenOverlayDeviceLines_ReturnsTrue_ForExpectedInput()
    {
        bool result = OverlayWindow.TrySplitListenOverlayDeviceLinesForTests(
            "Desk Mic\nTo: Headphones",
            out string inputLine,
            out string outputLine);

        Assert.True(result);
        Assert.Equal("Desk Mic", inputLine);
        Assert.Equal("To: Headphones", outputLine);
    }

    [Fact]
    public void TrySplitListenOverlayDeviceLines_ReturnsFalse_ForInvalidOutputPrefix()
    {
        bool result = OverlayWindow.TrySplitListenOverlayDeviceLinesForTests(
            "Desk Mic\nHeadphones",
            out string inputLine,
            out string outputLine);

        Assert.False(result);
        Assert.Equal("Desk Mic", inputLine);
        Assert.Equal("Headphones", outputLine);
    }

    [Theory]
    [InlineData(OverlayPosition.TopLeft, 0.2, -3, 0.5, 0)]
    [InlineData(OverlayPosition.BottomCenter, 12.0, 4, 10.0, 4)]
    public void ApplyDisplayOptions_ClampsDurationAndStackIndex(
        OverlayPosition position,
        double durationSeconds,
        int stackIndex,
        double expectedDurationSeconds,
        int expectedStackIndex)
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.ApplyDisplayOptions(position, durationSeconds, stackIndex);
                OverlayWindow.OverlayDisplayStateForTests displayState = window.GetDisplayStateForTests();

                Assert.Equal(position, displayState.Position);
                Assert.Equal(expectedStackIndex, displayState.StackIndex);
                Assert.Equal(expectedDurationSeconds, displayState.DurationSeconds);
                Assert.Equal(TimeSpan.FromSeconds(expectedDurationSeconds), displayState.CloseTimerInterval);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void BeginFadeOutAndClose_AttachesCompletionHandlerIdempotently()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                Assert.True(window.GetDisplayStateForTests().HasFadeOutStoryboard);

                window.BeginFadeOutAndCloseForTests();
                Assert.True(window.GetDisplayStateForTests().IsFadeOutCompletionHooked);

                window.BeginFadeOutAndCloseForTests();
                Assert.True(window.GetDisplayStateForTests().IsFadeOutCompletionHooked);

                window.StopFadeOutForTests();
                Assert.False(window.GetDisplayStateForTests().IsFadeOutCompletionHooked);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void StopFadeOut_DetachesFadeInCompletionHandler_AndClearsRunningState()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.BeginFadeInForTests();
                Assert.True(window.GetDisplayStateForTests().IsFadeInRunning);
                Assert.True(window.GetDisplayStateForTests().IsFadeInCompletionHooked);

                window.StopFadeOutForTests();

                Assert.False(window.GetDisplayStateForTests().IsFadeInRunning);
                Assert.False(window.GetDisplayStateForTests().IsFadeInCompletionHooked);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    private static OverlayWindow CreateOverlayWindow()
    {
        return new OverlayWindow("Overlay Test");
    }
}
