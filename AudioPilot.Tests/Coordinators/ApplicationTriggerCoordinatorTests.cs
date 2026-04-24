using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class ApplicationTriggerCoordinatorTests
{
    [Fact]
    public void Start_DoesNotStartWithoutProcessFocusRoutines()
    {
        var monitor = new FakeWindowFocusMonitor();
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Spotify Launch",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                ApplicationTriggerMode = ApplicationTriggerMode.AppLaunch,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers"
            }
        ];

        using var coordinator = new ApplicationTriggerCoordinator(routines, (_, _) => Task.CompletedTask, Logger.Instance, monitor);

        coordinator.Start();

        Assert.Equal(0, monitor.StartCallCount);
    }

    [Fact]
    public async Task WindowFocus_MatchesExecutablePath_AndSkipsDuplicateSameFocusEvent()
    {
        var monitor = new FakeWindowFocusMonitor();
        var executions = new List<(string RoutineId, int ProcessId)>();
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Spotify Focus",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
                TriggerAppPath = @"C:\Users\arman\AppData\Roaming\Spotify\Spotify.exe",
                ApplicationTriggerTitlePattern = "playlist",
                ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Contains,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers"
            }
        ];

        using var coordinator = new ApplicationTriggerCoordinator(
            routines,
            (routine, processId) =>
            {
                lock (executions)
                {
                    executions.Add((routine.Id, processId));
                }

                executed.TrySetResult();
                return Task.CompletedTask;
            },
            Logger.Instance,
            monitor);

        coordinator.Start();
        Assert.Equal(1, monitor.StartCallCount);

        monitor.RaiseFocused(new WindowFocusEventArgs(
            4242,
            string.Empty,
            @"C:\Users\arman\AppData\Roaming\Spotify\Spotify.exe",
            "Spotify Premium - playlist"));

        await executed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        monitor.RaiseFocused(new WindowFocusEventArgs(
            4242,
            "Spotify",
            @"C:\Users\arman\AppData\Roaming\Spotify\Spotify.exe",
            "Spotify Premium - playlist"));

        await Task.Delay(100);

        lock (executions)
        {
            Assert.Single(executions);
            Assert.Equal(("routine-1", 4242), executions[0]);
        }
    }

    [Fact]
    public async Task WindowFocus_WhenProcessFocusReturnsToSameProcess_TriggersAgain()
    {
        var monitor = new FakeWindowFocusMonitor();
        var executions = new List<(string RoutineId, int ProcessId)>();
        var executedTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Discord Focus",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
                TriggerAppPath = @"C:\Users\arman\AppData\Local\Discord\app-1.0.9235\Discord.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                SwitchOutputPerApp = true,
            }
        ];

        using var coordinator = new ApplicationTriggerCoordinator(
            routines,
            (routine, processId) =>
            {
                lock (executions)
                {
                    executions.Add((routine.Id, processId));
                    if (executions.Count == 2)
                    {
                        executedTwice.TrySetResult();
                    }
                }

                return Task.CompletedTask;
            },
            Logger.Instance,
            monitor);

        coordinator.Start();

        monitor.RaiseFocused(new WindowFocusEventArgs(
            28776,
            "Discord",
            @"C:\Users\arman\AppData\Local\Discord\app-1.0.9235\Discord.exe",
            "Friends - Discord"));

        await WaitForExecutionCountAsync(executions, 1);
        await Task.Delay(50);

        monitor.RaiseFocused(new WindowFocusEventArgs(
            23136,
            "explorer",
            @"C:\Windows\explorer.exe",
            string.Empty));

        monitor.RaiseFocused(new WindowFocusEventArgs(
            28776,
            "Discord",
            @"C:\Users\arman\AppData\Local\Discord\app-1.0.9235\Discord.exe",
            "Friends - Discord"));

        await executedTwice.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lock (executions)
        {
            Assert.Equal([("routine-1", 28776), ("routine-1", 28776)], executions);
        }
    }

    [Fact]
    public async Task WindowFocus_WhenSameProcessFocusesDifferentTitle_TriggersAgain()
    {
        var monitor = new FakeWindowFocusMonitor();
        var executions = new List<(string RoutineId, int ProcessId)>();
        var executedTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Discord Focus",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
                TriggerAppPath = @"C:\Users\arman\AppData\Local\Discord\app-1.0.9235\Discord.exe",
                ApplicationTriggerTitlePattern = "Discord",
                ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Contains,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                SwitchOutputPerApp = true,
            }
        ];

        using var coordinator = new ApplicationTriggerCoordinator(
            routines,
            (routine, processId) =>
            {
                lock (executions)
                {
                    executions.Add((routine.Id, processId));
                    if (executions.Count == 2)
                    {
                        executedTwice.TrySetResult();
                    }
                }

                return Task.CompletedTask;
            },
            Logger.Instance,
            monitor);

        coordinator.Start();

        monitor.RaiseFocused(new WindowFocusEventArgs(
            28776,
            "Discord",
            @"C:\Users\arman\AppData\Local\Discord\app-1.0.9235\Discord.exe",
            "Friends - Discord"));

        await WaitForExecutionCountAsync(executions, 1);
        await Task.Delay(50);

        monitor.RaiseFocused(new WindowFocusEventArgs(
            28776,
            "Discord",
            @"C:\Users\arman\AppData\Local\Discord\app-1.0.9235\Discord.exe",
            "Voice - Discord"));

        await executedTwice.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lock (executions)
        {
            Assert.Equal([("routine-1", 28776), ("routine-1", 28776)], executions);
        }
    }

    [Fact]
    public async Task WindowFocus_MatchesPackagedAppByExecutablePath()
    {
        var monitor = new FakeWindowFocusMonitor();
        var executed = new TaskCompletionSource<(string RoutineId, int ProcessId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Spotify Store",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
                TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers"
            }
        ];

        using var coordinator = new ApplicationTriggerCoordinator(
            routines,
            (routine, processId) =>
            {
                executed.TrySetResult((routine.Id, processId));
                return Task.CompletedTask;
            },
            Logger.Instance,
            monitor);

        coordinator.Start();

        monitor.RaiseFocused(new WindowFocusEventArgs(
            3131,
            "Spotify",
            @"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.0.0.0_x64__zpdnekdrzrea0\Spotify.exe",
            "Spotify"));

        (string RoutineId, int ProcessId) result = await executed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(("routine-1", 3131), result);
    }

    [Fact]
    public async Task WindowFocus_MatchesSteamWebHelper_WhenTargetIsSteamExe()
    {
        var monitor = new FakeWindowFocusMonitor();
        var executed = new TaskCompletionSource<(string RoutineId, int ProcessId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Steam Focus",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
                TriggerAppPath = @"C:\Program Files (x86)\Steam\steam.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers"
            }
        ];

        using var coordinator = new ApplicationTriggerCoordinator(
            routines,
            (routine, processId) =>
            {
                executed.TrySetResult((routine.Id, processId));
                return Task.CompletedTask;
            },
            Logger.Instance,
            monitor);

        coordinator.Start();

        monitor.RaiseFocused(new WindowFocusEventArgs(
            6672,
            "steamwebhelper",
            @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe",
            "Steam"));

        (string RoutineId, int ProcessId) result = await executed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(("routine-1", 6672), result);
    }

    [Fact]
    public async Task WindowFocus_MatchesSquirrelAppExe_WhenTargetIsUpdateExe()
    {
        var monitor = new FakeWindowFocusMonitor();
        var executed = new TaskCompletionSource<(string RoutineId, int ProcessId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Discord Focus",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
                TriggerAppPath = @"C:\Users\Jetix\AppData\Local\Discord\Update.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers"
            }
        ];

        using var coordinator = new ApplicationTriggerCoordinator(
            routines,
            (routine, processId) =>
            {
                executed.TrySetResult((routine.Id, processId));
                return Task.CompletedTask;
            },
            Logger.Instance,
            monitor);

        coordinator.Start();

        monitor.RaiseFocused(new WindowFocusEventArgs(
            3528,
            "Discord",
            @"C:\Users\Jetix\AppData\Local\Discord\app-1.0.9236\Discord.exe",
            "@Hanuwa - Discord"));

        (string RoutineId, int ProcessId) result = await executed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(("routine-1", 3528), result);
    }

    private sealed class FakeWindowFocusMonitor : IWindowFocusMonitor
    {
        public event EventHandler<WindowFocusEventArgs>? WindowFocused;

        public int StartCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public void Start()
        {
            StartCallCount++;
        }

        public void Stop()
        {
            StopCallCount++;
        }

        public void RaiseFocused(WindowFocusEventArgs args)
        {
            WindowFocused?.Invoke(this, args);
        }

        public void Dispose()
        {
        }
    }

    private static async Task WaitForExecutionCountAsync(List<(string RoutineId, int ProcessId)> executions, int expectedCount)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (executions)
            {
                if (executions.Count >= expectedCount)
                {
                    return;
                }
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {expectedCount} execution(s).");
    }
}
