namespace AudioPilot.Services.UI
{
    public sealed class MediaOverlayCommandService
    {
        private readonly MediaOverlayEngine _engine;

        public MediaOverlayCommandService()
            : this(new MediaOverlayEngine())
        {
        }

        internal MediaOverlayCommandService(MediaOverlayEngine engine)
        {
            _engine = engine;
        }

        public Task<MediaOverlayResult> SendWithBestEffortOverlayAsync(MediaOverlayCommand command, Func<bool> sendCommand)
        {
            return _engine.SendWithBestEffortOverlayAsync(command, sendCommand);
        }

        public Task<MediaOverlaySessionSnapshot> GetCurrentMediaSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return _engine.GetCurrentMediaSnapshotAsync(cancellationToken);
        }
    }
}
