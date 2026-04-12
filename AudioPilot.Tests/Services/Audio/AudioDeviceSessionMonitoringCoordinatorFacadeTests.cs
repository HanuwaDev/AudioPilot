using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceSessionMonitoringCoordinatorFacadeTests
{
    [Fact]
    public void AcquireAndRelease_TrackConsumerCounts()
    {
        using var sessionService = new AudioSessionService(new StubEnumerator());
        var facade = CreateFacade(sessionService, _ => { });

        facade.Acquire(AudioMixerMode.Output);
        Assert.Equal(1, facade.GetConsumerCountForTests(AudioMixerMode.Output));

        facade.Release(AudioMixerMode.Output);
        Assert.Equal(0, facade.GetConsumerCountForTests(AudioMixerMode.Output));
    }

    [Fact]
    public void OnEndpointVolumeChanged_EmitsLifecycleSignal()
    {
        using var sessionService = new AudioSessionService(new StubEnumerator());
        AudioSessionLifecycleSignal? received = null;
        var facade = CreateFacade(sessionService, signal => received = signal);

        facade.OnEndpointVolumeChanged(AudioMixerMode.Input, "capture-1", 44f);

        Assert.NotNull(received);
        Assert.Equal(AudioSessionLifecycleSignalKind.EndpointVolumeChanged, received.Value.Kind);
        Assert.Equal("capture-1", received.Value.EndpointId);
    }

    private static AudioDeviceSessionMonitoringCoordinatorFacade CreateFacade(
        AudioSessionService sessionService,
        Action<AudioSessionLifecycleSignal> onLifecycleChanged)
    {
        var logger = Logger.Instance;
        var playbackCoordinator = new SessionMonitorCoordinator(
            logger,
            AudioMixerMode.Output,
            static () => [],
            static (_, _, _) => { },
            static (_, _, _) => { },
            static _ => { },
            static (_, _) => { },
            static () => false);
        var recordingCoordinator = new SessionMonitorCoordinator(
            logger,
            AudioMixerMode.Input,
            static () => [],
            static (_, _, _) => { },
            static (_, _, _) => { },
            static _ => { },
            static (_, _) => { },
            static () => false);

        return new AudioDeviceSessionMonitoringCoordinatorFacade(
            logger,
            sessionService,
            playbackCoordinator,
            recordingCoordinator,
            () => false,
            onLifecycleChanged);
    }

    private sealed class StubEnumerator : IAudioDeviceEnumerator
    {
        public MMDeviceCollection GetActivePlaybackDevices() => throw new NotSupportedException();
        public IReadOnlyList<MMDevice> GetPlaybackDevicesById(IReadOnlyCollection<string> deviceIds) => throw new NotSupportedException();
        public MMDevice GetDefaultPlaybackDevice() => throw new NotSupportedException();
        public MMDevice? GetDefaultRecordingDevice() => throw new NotSupportedException();
        public List<MMDevice?> GetAllDefaultPlaybackDevices() => throw new NotSupportedException();
        public List<MMDevice?> GetAllDefaultRecordingDevices() => throw new NotSupportedException();
    }
}
