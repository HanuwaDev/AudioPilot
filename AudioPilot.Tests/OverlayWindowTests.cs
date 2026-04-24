using System.Windows;
using System.Windows.Media;
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

    [Fact]
    public void UpdateContent_ForMediaTrack_UsesDedicatedWrappedTextBlocks()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.UpdateContent("Next track", "A very long title that should stay in the title text block", "Artist");

                OverlayWindow.MediaOverlayTextStateForTests state = window.GetMediaOverlayTextStateForTests();
                Assert.Equal(Visibility.Collapsed, state.OverlayTextVisibility);
                Assert.Equal(Visibility.Collapsed, state.StructuredPanelVisibility);
                Assert.Equal(Visibility.Visible, state.MediaPanelVisibility);
                Assert.Equal("Next track", state.Header);
                Assert.Equal("A very long title that should stay in the title text block", state.Title);
                Assert.Equal("Artist", state.Artist);
                Assert.Equal(Visibility.Visible, state.ArtistVisibility);
                Assert.Equal(63, state.TitleMaxHeight);
                Assert.Equal("MediaOverlayInlineTextBlock", state.TitleElementType);
                Assert.Equal(3, state.TitleMaxLines);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void UpdateContent_AfterMediaTrack_RestoresPlainOverlayText()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.UpdateContent("Current track", "Song", null);
                window.UpdateContent("Plain message");

                OverlayWindow.MediaOverlayTextStateForTests state = window.GetMediaOverlayTextStateForTests();
                Assert.Equal(Visibility.Visible, state.OverlayTextVisibility);
                Assert.Equal(Visibility.Collapsed, state.StructuredPanelVisibility);
                Assert.Equal(Visibility.Collapsed, state.MediaPanelVisibility);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void UpdateContent_ForDevice_UsesStructuredTrimmedRows()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.UpdateContent(OverlayDeviceKind.Output, "Switched output device", "Very Long Speakers Device Name");

                OverlayWindow.StructuredOverlayTextStateForTests state = window.GetStructuredOverlayTextStateForTests();
                Assert.Equal(Visibility.Collapsed, state.OverlayTextVisibility);
                Assert.Equal(Visibility.Visible, state.StructuredPanelVisibility);
                Assert.Equal(Visibility.Collapsed, state.MediaPanelVisibility);
                Assert.Equal("Switched output device", state.Header);
                Assert.Equal(Visibility.Visible, state.Rows[0].Visibility);
                Assert.Equal(Visibility.Collapsed, state.Rows[0].LabelVisibility);
                Assert.Equal("Very Long Speakers Device Name", state.Rows[0].Value);
                Assert.Equal(TextAlignment.Center, state.Rows[0].ValueTextAlignment);
                Assert.Equal(60, state.Rows[0].ValueMaxHeight);
                Assert.Equal(TextTrimming.CharacterEllipsis, state.Rows[0].ValueTextTrimming);
                Assert.Equal(TextWrapping.Wrap, state.Rows[0].ValueTextWrapping);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void UpdateContent_ForListenInput_SplitsInputAndOutputRows()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.UpdateContent(OverlayDeviceKind.Input, "Listen to input enabled", "Desk Mic\nTo: Headphones");

                OverlayWindow.StructuredOverlayTextStateForTests state = window.GetStructuredOverlayTextStateForTests();
                Assert.Equal("Listen to input enabled", state.Header);
                Assert.Equal("Desk Mic", state.Rows[0].Value);
                Assert.Equal(Visibility.Collapsed, state.Rows[0].LabelVisibility);
                Assert.Equal("To: ", state.Rows[1].Label);
                Assert.Equal("Headphones", state.Rows[1].Value);
                Assert.Equal(TextAlignment.Left, state.Rows[1].ValueTextAlignment);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void UpdateRoutinePartialContent_UsesLabeledStructuredRows()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.UpdateRoutinePartialContent(
                    "Desk - Partial",
                    "Speakers",
                    null,
                    null,
                    "Microphone With A Long Name");

                OverlayWindow.StructuredOverlayTextStateForTests state = window.GetStructuredOverlayTextStateForTests();
                Assert.Equal("Desk - Partial", state.Header);
                Assert.Equal("Output: ", state.Rows[0].Label);
                Assert.Equal("Speakers", state.Rows[0].Value);
                Assert.Equal("Input failed: ", state.Rows[1].Label);
                Assert.Equal("Microphone With A Long Name", state.Rows[1].Value);
                Assert.Equal(60, state.Rows[1].ValueMaxHeight);
                Assert.Equal(TextTrimming.CharacterEllipsis, state.Rows[1].ValueTextTrimming);
                Assert.Equal(TextWrapping.Wrap, state.Rows[1].ValueTextWrapping);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void UpdateContent_ForMediaTrack_RendersTitleThroughInlineBuilder()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.UpdateContent("Next track", "Launch day 😀 highlights", "Artist");

                OverlayWindow.MediaOverlayTextStateForTests state = window.GetMediaOverlayTextStateForTests();
                Assert.Contains("Launch day", state.Title, StringComparison.Ordinal);
                Assert.Contains("highlights", state.Title, StringComparison.Ordinal);
                Assert.Equal("MediaOverlayInlineTextBlock", state.TitleElementType);
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
