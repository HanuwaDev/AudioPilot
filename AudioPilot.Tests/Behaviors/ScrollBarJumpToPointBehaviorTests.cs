using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AudioPilot.Behaviors;

namespace AudioPilot.Tests.Behaviors;

public sealed class ScrollBarJumpToPointBehaviorTests
{
    [Fact]
    public void CalculateValueFromClickPosition_ReturnsMinimum_AtTopOfVerticalReversedTrack()
    {
        double value = ScrollBarJumpToPointBehavior.CalculateValueFromClickPosition(
            new Point(0, 0),
            new Size(10, 200),
            Orientation.Vertical,
            0,
            100,
            isDirectionReversed: true);

        Assert.Equal(0, value);
    }

    [Fact]
    public void CalculateValueFromClickPosition_ReturnsMaximum_AtBottomOfVerticalReversedTrack()
    {
        double value = ScrollBarJumpToPointBehavior.CalculateValueFromClickPosition(
            new Point(0, 200),
            new Size(10, 200),
            Orientation.Vertical,
            0,
            100,
            isDirectionReversed: true);

        Assert.Equal(100, value);
    }

    [Fact]
    public void CalculateValueFromClickPosition_ReturnsMidpoint_ForHorizontalTrack()
    {
        double value = ScrollBarJumpToPointBehavior.CalculateValueFromClickPosition(
            new Point(60, 0),
            new Size(120, 10),
            Orientation.Horizontal,
            10,
            30,
            isDirectionReversed: false);

        Assert.Equal(20, value);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(250, 100)]
    public void CalculateValueFromClickPosition_ClampsOutOfRangeCoordinates(double y, double expected)
    {
        double value = ScrollBarJumpToPointBehavior.CalculateValueFromClickPosition(
            new Point(0, y),
            new Size(10, 200),
            Orientation.Vertical,
            0,
            100,
            isDirectionReversed: true);

        Assert.Equal(expected, value);
    }

    [Fact]
    public void CalculateValueFromClickPosition_ReturnsNaN_WhenTrackLengthIsZero()
    {
        double value = ScrollBarJumpToPointBehavior.CalculateValueFromClickPosition(
            new Point(0, 10),
            new Size(0, 0),
            Orientation.Vertical,
            0,
            100,
            isDirectionReversed: true);

        Assert.True(double.IsNaN(value));
    }

    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    public void ShouldHandleTrackClickCore_ReflectsEnabledTrackRangeAndThumbOrigin(
        bool isScrollBarEnabled,
        bool hasTrack,
        bool hasScrollableRange,
        bool isThumbOrigin,
        bool expected)
    {
        bool result = ScrollBarJumpToPointBehavior.ShouldHandleTrackClickCore(
            isScrollBarEnabled,
            hasTrack,
            hasScrollableRange,
            isThumbOrigin);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetScrollCommand_ReturnsVerticalOffsetCommand_ForVerticalOrientation()
    {
        RoutedCommand command = ScrollBarJumpToPointBehavior.GetScrollCommand(Orientation.Vertical);

        Assert.Same(ScrollBar.ScrollToVerticalOffsetCommand, command);
    }

    [Fact]
    public void GetScrollCommand_ReturnsHorizontalOffsetCommand_ForHorizontalOrientation()
    {
        RoutedCommand command = ScrollBarJumpToPointBehavior.GetScrollCommand(Orientation.Horizontal);

        Assert.Same(ScrollBar.ScrollToHorizontalOffsetCommand, command);
    }
}
