using System.ComponentModel;

namespace AudioPilot.Models
{
    public class AudioSessionItem(
        string displayName,
        float volume,
        bool isMaster,
        bool isMic,
        bool isSystemSounds = false,
        uint? processId = null) : INotifyPropertyChanged
    {
        private float _volume = volume;
        private bool _suppressVolumeChanged;

        public string DisplayName { get; } = displayName;
        public bool IsMaster { get; } = isMaster;
        public bool IsMic { get; } = isMic;
        public bool IsSystemSounds { get; } = isSystemSounds;
        public uint? ProcessId { get; } = processId;

        public float Volume
        {
            get => _volume;
            set
            {
                if (Math.Abs(_volume - value) < 0.01f) return;
                _volume = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Volume)));
                if (!_suppressVolumeChanged)
                {
                    VolumeChanged?.Invoke(this);
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<AudioSessionItem>? VolumeChanged;

        public void SetVolumeFromSystem(float value)
        {
            if (Math.Abs(_volume - value) < 0.01f)
            {
                return;
            }

            _suppressVolumeChanged = true;
            try
            {
                Volume = value;
            }
            finally
            {
                _suppressVolumeChanged = false;
            }
        }
    }
}
