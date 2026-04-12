using System.Runtime.CompilerServices;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

internal static class TestAssemblyInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        TestExecutionGuards.EnsureNoRunningUiProcess();
    }
}
