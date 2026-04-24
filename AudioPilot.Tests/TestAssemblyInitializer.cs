using System.Runtime.CompilerServices;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

internal static class TestAssemblyInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        string testDataRoot = Path.Combine(
            Path.GetTempPath(),
            "AudioPilot.Tests",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        AppDataPaths.UserDataRootProviderOverride = () => Path.Combine(testDataRoot, "appdata");
        AppDataPaths.BaseDirectoryProviderOverride = () => Path.Combine(testDataRoot, "portable");
        AppDataPaths.InstallerRegistrationProviderOverride = () => (null, null);

        TestExecutionGuards.EnsureNoRunningUiProcess();
    }
}
