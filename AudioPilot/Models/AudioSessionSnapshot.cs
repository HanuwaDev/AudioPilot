namespace AudioPilot.Models
{
    internal readonly record struct AudioSessionSnapshot(
        string DisplayName,
        float Volume,
        string DeviceName,
        string? ProcessName,
        string? MainWindowTitle,
        uint? ProcessId);
}
