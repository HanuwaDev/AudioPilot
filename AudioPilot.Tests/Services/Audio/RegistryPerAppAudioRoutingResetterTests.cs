using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using Microsoft.Win32;

namespace AudioPilot.Tests.Services.Audio;

public sealed class RegistryPerAppAudioRoutingResetterTests : IDisposable
{
    private readonly TestScopedDirectory _scope;
    private readonly Logger _logger;
    private readonly string _registryRootPath;

    public RegistryPerAppAudioRoutingResetterTests()
    {
        _scope = new TestScopedDirectory(nameof(RegistryPerAppAudioRoutingResetterTests));
        _logger = new Logger(_scope.Root, "registry-per-app-reset.log");
        _registryRootPath = $@"Software\AudioPilot.Tests\{Guid.NewGuid():N}";
    }

    [Fact]
    public void TryResetAll_WhenNoAssignmentsExist_ReturnsSuccessWithoutAssignments()
    {
        using RegistryKey rootKey = Registry.CurrentUser.CreateSubKey(_registryRootPath, writable: true)!;
        var resetter = new RegistryPerAppAudioRoutingResetter(rootKey, "PolicyConfig\\PropertyStore", _logger);

        PerAppAudioRoutingResetResult result = resetter.TryResetAll();

        Assert.True(result.Success);
        Assert.False(result.HadAssignments);
    }

    [Fact]
    public void TryResetAll_WhenAssignmentsExist_DeletesPropertyStore()
    {
        using RegistryKey rootKey = Registry.CurrentUser.CreateSubKey(_registryRootPath, writable: true)!;
        using (RegistryKey propertyStore = rootKey.CreateSubKey("PolicyConfig\\PropertyStore", writable: true)!)
        {
            propertyStore.SetValue("value-1", "assigned");
            using RegistryKey child = propertyStore.CreateSubKey("child", writable: true)!;
            child.SetValue("value-2", 1);
        }

        var resetter = new RegistryPerAppAudioRoutingResetter(rootKey, "PolicyConfig\\PropertyStore", _logger);

        PerAppAudioRoutingResetResult result = resetter.TryResetAll();

        Assert.True(result.Success);
        Assert.True(result.HadAssignments);
        Assert.Null(rootKey.OpenSubKey("PolicyConfig\\PropertyStore", writable: false));
    }

    [Fact]
    public void TryResetAll_WhenRegistryAccessFails_ReturnsFailure()
    {
        RegistryKey rootKey = Registry.CurrentUser.CreateSubKey(_registryRootPath, writable: true)!;
        rootKey.Dispose();

        var resetter = new RegistryPerAppAudioRoutingResetter(rootKey, "PolicyConfig\\PropertyStore", _logger);

        PerAppAudioRoutingResetResult result = resetter.TryResetAll();

        Assert.False(result.Success);
        Assert.False(result.HadAssignments);
    }

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_registryRootPath, throwOnMissingSubKey: false);
        }
        catch
        {
        }

        _logger.Dispose();
        _scope.Dispose();
    }
}
