using System.Windows.Threading;
using AudioPilot.Logging;

namespace AudioPilot.Services.Hotkeys
{
    internal static class MainWindowMediaHotkeyHandler
    {
        public static Task DispatchAsync(
            Dispatcher dispatcher,
            Logger logger,
            MediaOverlayCommandService mediaOverlayCommands,
            OverlayService overlayService,
            MediaOverlayCommand command,
            Func<bool> mediaAction,
            string methodName)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(mediaOverlayCommands);
            ArgumentNullException.ThrowIfNull(overlayService);
            ArgumentNullException.ThrowIfNull(mediaAction);
            ArgumentException.ThrowIfNullOrWhiteSpace(methodName);

            return MainWindowHotkeyDispatchHelper.InvokeAsync(
                dispatcher,
                logger,
                async () =>
                {
                    MediaOverlayResult overlay = await mediaOverlayCommands.SendWithBestEffortOverlayAsync(command, mediaAction);
                    ApplyOverlayResult(overlayService, overlay);
                },
                "Hotkey async action error",
                methodName);
        }

        internal static void ApplyOverlayResult(OverlayService overlayService, MediaOverlayResult overlay)
        {
            ArgumentNullException.ThrowIfNull(overlayService);

            if (overlay.Kind == MediaOverlayResultKind.Hidden)
            {
                return;
            }

            if (overlay.Kind == MediaOverlayResultKind.TrackMessage && !string.IsNullOrWhiteSpace(overlay.Title))
            {
                overlayService.ShowMediaTrack(overlay.Header, overlay.Title, overlay.Artist);
                return;
            }

            if (!string.IsNullOrWhiteSpace(overlay.Message))
            {
                overlayService.Show(overlay.Message);
            }
        }
    }
}
