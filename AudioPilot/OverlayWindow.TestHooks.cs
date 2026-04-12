using AudioPilot.Models;

namespace AudioPilot
{
    public partial class OverlayWindow
    {
        internal readonly record struct OverlayDisplayStateForTests(
            OverlayPosition Position,
            int StackIndex,
            double DurationSeconds,
            TimeSpan CloseTimerInterval,
            bool HasFadeOutStoryboard,
            bool IsFadeOutCompletionHooked,
            bool IsFadeInRunning,
            bool IsFadeInCompletionHooked);

        internal OverlayDisplayStateForTests GetDisplayStateForTests()
        {
            return new OverlayDisplayStateForTests(
                _position,
                _stackIndex,
                _durationSeconds,
                _closeTimer.Interval,
                _fadeOutStoryboard != null,
                _isFadeOutCompletionHooked,
                _isFadeInRunning,
                _isFadeInCompletionHooked);
        }

        internal void BeginFadeInForTests() => BeginFadeIn();

        internal void BeginFadeOutAndCloseForTests() => BeginFadeOutAndClose();

        internal void StopFadeOutForTests() => StopFadeOut();

        internal static bool TrySplitListenOverlayDeviceLinesForTests(string deviceName, out string inputLine, out string outputLine)
            => TrySplitListenOverlayDeviceLines(deviceName, out inputLine, out outputLine);
    }
}
