namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlayCommandSequenceStore
    {
        private long _latestCommandSequence;

        public long GetNextCommandSequence()
        {
            return Interlocked.Increment(ref _latestCommandSequence);
        }

        public bool IsCommandSequenceCurrent(long commandSequence)
        {
            return commandSequence == Volatile.Read(ref _latestCommandSequence);
        }
    }
}
