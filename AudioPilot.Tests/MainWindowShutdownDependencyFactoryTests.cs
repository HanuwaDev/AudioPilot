using AudioPilot.Coordinators;

namespace AudioPilot.Tests;

public sealed class MainWindowShutdownDependencyFactoryTests
{
    [Fact]
    public async Task Build_ReturnsDependenciesThatInvokeSuppliedCallbacks()
    {
        List<string> calls = [];

        MainWindowShutdownPreparationDependencies preparation = MainWindowShutdownDependencyFactory.BuildPreparation(
            () => calls.Add("close-owned"),
            () => calls.Add("detach-global"),
            () => calls.Add("detach-audio"),
            () => calls.Add("stop-hotplug"),
            () => calls.Add("detach-system"),
            () => calls.Add("detach-window"));

        MainWindowShutdownDependencies dependencies = MainWindowShutdownDependencyFactory.Build(
            preparation,
            () => calls.Add("unwire-hotkeys"),
            () =>
            {
                calls.Add("cleanup-appvm");
                return Task.CompletedTask;
            },
            () => calls.Add("dispose-hotkey-service"),
            () =>
            {
                calls.Add("dispose-runtime");
                return Task.CompletedTask;
            },
            () => calls.Add("dispose-overlay"),
            () => calls.Add("dispose-shell"),
            () => calls.Add("dispose-single-instance"));

        dependencies.Preparation.CloseOwnedWindows();
        dependencies.Preparation.DetachGlobalExceptionHandlers();
        dependencies.Preparation.DetachAudioEventHandlers();
        dependencies.Preparation.StopHotplugRefreshDebounce();
        dependencies.Preparation.DetachSystemEventHandlers();
        dependencies.Preparation.DetachWindowEventHandlers();
        dependencies.UnwireHotkeys();
        await dependencies.CleanupAppViewModelAsync();
        dependencies.DisposeHotkeyService();
        await dependencies.DisposeRuntimeServicesAsync!();
        dependencies.DisposeOverlayService();
        dependencies.DisposeShell();
        dependencies.DisposeSingleInstance();

        Assert.Equal(
            [
                "close-owned",
                "detach-global",
                "detach-audio",
                "stop-hotplug",
                "detach-system",
                "detach-window",
                "unwire-hotkeys",
                "cleanup-appvm",
                "dispose-hotkey-service",
                "dispose-runtime",
                "dispose-overlay",
                "dispose-shell",
                "dispose-single-instance"
            ],
            calls);
    }
}
