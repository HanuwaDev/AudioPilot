using AudioPilot.Coordinators;

namespace AudioPilot
{
    internal static class MainWindowShutdownDependencyFactory
    {
        /// <summary>
        /// Builds the runtime shutdown dependency bundle and injects the shared singleton cleanup steps that
        /// remain owned by the main window rather than the caller.
        /// </summary>
        /// <remarks>
        /// The factory centralizes shutdown wiring so tests can supply narrow delegates for window-owned cleanup
        /// while production always routes device-cache and core-audio executor disposal through the canonical
        /// static helpers.
        /// </remarks>
        public static MainWindowShutdownDependencies Build(
            MainWindowShutdownPreparationDependencies preparation,
            Action unwireHotkeys,
            Func<Task> cleanupAppViewModelAsync,
            Action disposeHotkeyService,
            Func<Task>? disposeRuntimeServicesAsync,
            Action disposeOverlayService,
            Action disposeShell,
            Action disposeSingleInstance)
        {
            return new MainWindowShutdownDependencies(
                preparation,
                unwireHotkeys,
                cleanupAppViewModelAsync,
                disposeHotkeyService,
                disposeRuntimeServicesAsync,
                disposeOverlayService,
                DeviceCacheHelper.DisposeSingleton,
                ComThreadingHelper.DisposeCoreAudioExecutor,
                disposeShell,
                disposeSingleInstance);
        }

        /// <summary>
        /// Builds the front-loaded shutdown preparation bundle that detaches event sources before asynchronous
        /// cleanup begins.
        /// </summary>
        public static MainWindowShutdownPreparationDependencies BuildPreparation(
            Action closeOwnedWindows,
            Action detachGlobalExceptionHandlers,
            Action detachAudioEventHandlers,
            Action stopHotplugRefreshDebounce,
            Action detachSystemEventHandlers,
            Action detachWindowEventHandlers)
        {
            return new MainWindowShutdownPreparationDependencies(
                closeOwnedWindows,
                detachGlobalExceptionHandlers,
                detachAudioEventHandlers,
                stopHotplugRefreshDebounce,
                detachSystemEventHandlers,
                detachWindowEventHandlers);
        }
    }
}
