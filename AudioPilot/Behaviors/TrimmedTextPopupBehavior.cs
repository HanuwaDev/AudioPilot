using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AudioPilot.Constants;
using AudioPilot.Logging;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors
{
    public sealed class TrimmedTextPopupBehavior : Behavior<TextBlock>
    {
        private static readonly Logger _logger = Logger.Instance;
        private static InfoPopupService PopupService => InfoPopupService.Instance;
        private CancellationTokenSource? _hoverDelayCts;

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.MouseEnter += OnMouseEnter;
            AssociatedObject.MouseLeave += OnMouseLeave;
            AssociatedObject.Unloaded += OnUnloaded;
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);
            AssociatedObject.MouseEnter -= OnMouseEnter;
            AssociatedObject.MouseLeave -= OnMouseLeave;
            AssociatedObject.Unloaded -= OnUnloaded;
            PopupService.Hide(AssociatedObject);
        }

        private async void OnMouseEnter(object sender, MouseEventArgs e)
        {
            CancellationToken hoverDelayToken = HoverDelayCoordinator.StartOrRestart(ref _hoverDelayCts);

            await HoverDelayCoordinator.ExecuteAfterDelayAsync(
                AppConstants.Timing.TooltipHoverDelayMs,
                () => AssociatedObject.IsMouseOver && IsTextTrimmed(AssociatedObject),
                () => PopupService.ShowText(AssociatedObject, AssociatedObject.Text),
                ex => _logger.Warning("TrimmedTextPopupBehavior", "Failed to process trimmed-text hover popup", nameof(OnMouseEnter), ex),
                hoverDelayToken);
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);
            PopupService.Hide(AssociatedObject);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);
            PopupService.Hide(AssociatedObject);
        }

        private static bool IsTextTrimmed(TextBlock textBlock)
        {
            if (textBlock == null) return false;

            Typeface typeface = new(
                textBlock.FontFamily,
                textBlock.FontStyle,
                textBlock.FontWeight,
                textBlock.FontStretch);

            FormattedText formattedText = new(
                textBlock.Text,
                System.Globalization.CultureInfo.CurrentCulture,
                textBlock.FlowDirection,
                typeface,
                textBlock.FontSize,
                textBlock.Foreground,
                VisualTreeHelper.GetDpi(textBlock).PixelsPerDip);

            return formattedText.Width > textBlock.ActualWidth;
        }
    }
}
