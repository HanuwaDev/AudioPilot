namespace AudioPilot.Tests.Helpers;

[CollectionDefinition("AudioDeviceHelperCacheIsolation", DisableParallelization = true)]
public sealed class AudioDeviceHelperCacheIsolationCollection
{
}

[CollectionDefinition("AudioHardwareStressIsolation", DisableParallelization = true)]
public sealed class AudioHardwareStressIsolationCollection
{
}

[CollectionDefinition("DeviceCacheHelperIsolation", DisableParallelization = true)]
public sealed class DeviceCacheHelperIsolationCollection
{
}

[CollectionDefinition("LoggerFileIsolation", DisableParallelization = true)]
public sealed class LoggerFileIsolationCollection
{
}

[CollectionDefinition("MessageBoxServiceIsolation", DisableParallelization = true)]
public sealed class MessageBoxServiceIsolationCollection
{
}

[CollectionDefinition("RuntimeTuningConfigIsolation", DisableParallelization = true)]
public sealed class RuntimeTuningConfigIsolationCollection
{
}

[CollectionDefinition("CoreAudioWorkerIsolation", DisableParallelization = true)]
public sealed class CoreAudioWorkerIsolationCollection
{
}

[CollectionDefinition("WpfApplicationIsolation", DisableParallelization = true)]
public sealed class WpfApplicationIsolationCollection
{
}
