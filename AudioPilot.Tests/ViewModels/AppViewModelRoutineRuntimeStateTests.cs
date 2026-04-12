using System.Collections;
using System.Reflection;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineRuntimeStateTests
{
    [Fact]
    public void ApplyRoutinesFromSettings_PrunesStaleRoutineRuntimeStateEntries()
    {
        TestExecutionGuards.RunSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelRoutineRuntimeStateTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            AppViewModel viewModel = harness.ViewModel;

            viewModel.ApplyRoutinesFromSettings(
            [
                new AudioRoutine { Id = "keep", Name = "Keep" },
                new AudioRoutine { Id = "drop", Name = "Drop" },
            ]);

            SetRoutineLastRunState(viewModel, "keep");
            SetRoutineLastRunState(viewModel, "drop");
            SetRoutineLastRunState(viewModel, "stale");

            IDictionary runtimeStates = GetRoutineRuntimeStates(viewModel);
            Assert.Equal(3, runtimeStates.Count);

            viewModel.ApplyRoutinesFromSettings(
            [
                new AudioRoutine { Id = "keep", Name = "Keep" },
            ]);

            Assert.Single(runtimeStates);
            Assert.True(runtimeStates.Contains("keep"));
        });
    }

    private static IDictionary GetRoutineRuntimeStates(AppViewModel viewModel)
    {
        FieldInfo? field = typeof(AppViewModel).GetField("_routineRuntimeStates", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<IDictionary>(field!.GetValue(viewModel), exactMatch: false);
    }

    private static void SetRoutineLastRunState(AppViewModel viewModel, string routineId)
    {
        MethodInfo? method = typeof(AppViewModel).GetMethod(
            "SetRoutineLastRunState",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(RoutineLastRunState), typeof(string), typeof(DateTimeOffset?)],
            modifiers: null);
        Assert.NotNull(method);

        _ = method!.Invoke(viewModel, [routineId, RoutineLastRunState.Succeeded, null, null]);
    }
}
