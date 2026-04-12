using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;
using Newtonsoft.Json.Linq;

namespace AudioPilot.Tests.Cli;

public sealed partial class LocalHeadlessCommandRunnerTests
{


    [Fact]
    public async Task ExecuteAsync_RoutineExport_WritesJsonFile()
    {
        string exportPath = Path.Combine(Path.GetTempPath(), $"audiopilot-routines-{Guid.NewGuid():N}.json");

        try
        {
            using var scope = new HeadlessRunnerScope(
                new Settings
                {
                    Routines = new RoutinesSettings
                    {
                        Items =
                        [
                            new AudioRoutine
                            {
                                Id = "routine-1",
                                Name = "Desk",
                                OutputDeviceId = "out-1",
                                OutputDeviceName = "Speakers",
                            },
                        ]
                    }
                });

            CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
            {
                Action = CliAction.RoutineExport,
                Key = exportPath,
                AllowAnyPath = true,
                JsonOutput = true,
            });

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(exportPath));

            JObject parsed = JObject.Parse(result.Output!);
            Assert.True(parsed["data"]?["success"]?.Value<bool>());
            Assert.Equal(1, parsed["data"]?["routineCount"]?.Value<int>());

            JObject exported = JObject.Parse(File.ReadAllText(exportPath));
            Assert.Equal("1.0.0", exported["SchemaVersion"]?.Value<string>());
            Assert.Equal("routine-1", exported["Routines"]?.First?["Id"]?.Value<string>());
        }
        finally
        {
            if (File.Exists(exportPath))
            {
                File.Delete(exportPath);
            }
        }
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_WithAmbiguousName_ReturnsPreconditionFailure()
    {
        using var scope = new HeadlessRunnerScope(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
            [
                new AudioRoutine
                {
                    Id = "routine-1",
                    Name = "Desk",
                    Enabled = true,
                    OutputDeviceId = "out-1",
                    OutputDeviceName = "Speakers"
                },
                new AudioRoutine
                {
                    Id = "routine-2",
                    Name = "Desk",
                    Enabled = true,
                    OutputDeviceId = "out-2",
                    OutputDeviceName = "Headset"
                }
            ]
            }
        });

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:routine-selector-ambiguous] Multiple routines match 'Desk'. Use the routine id instead.", result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_WhenRoutineDisabled_ReturnsPreconditionFailure()
    {
        using var scope = new HeadlessRunnerScope(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
            [
                new AudioRoutine
                {
                    Id = "routine-1",
                    Name = "Desk",
                    Enabled = false,
                    OutputDeviceId = "out-1",
                    OutputDeviceName = "Speakers"
                }
            ]
            }
        });

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:routine-disabled] Routine 'Desk' is disabled.", result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_WhenRoutineHasNoTargets_ReturnsPreconditionFailure()
    {
        using var scope = new HeadlessRunnerScope(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
            [
                new AudioRoutine
                {
                    Id = "routine-1",
                    Name = "Desk",
                    Enabled = true
                }
            ]
            }
        });

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:routine-has-no-targets] Routine 'Desk' has no configured targets.", result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_WhenAppStartRoutineTargetNotRunning_ReturnsPreconditionFailure()
    {
        using var scope = new HeadlessRunnerScope(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
            [
                new AudioRoutine
                {
                    Id = "routine-1",
                    Name = "Spotify",
                    Enabled = true,
                    OutputDeviceId = "out-1",
                    OutputDeviceName = "Speakers",
                    TriggerOnAppStart = true,
                    TriggerAppPath = @"C:\DefinitelyMissing\MissingApp.exe",
                    SwitchOutputPerApp = true,
                }
            ]
            }
        });

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Spotify",
        });

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:routine-trigger-app-not-running] Routine 'Spotify' requires the target application 'MissingApp' to be running.", result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_WhenAppStartRoutineTargetNotRunning_JsonIncludesRoutineMetadata()
    {
        using var scope = new HeadlessRunnerScope(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
            [
                new AudioRoutine
                {
                    Id = "routine-1",
                    Name = "Spotify",
                    Enabled = true,
                    OutputDeviceId = "out-1",
                    OutputDeviceName = "Speakers",
                    TriggerOnAppStart = true,
                    TriggerAppPath = @"C:\DefinitelyMissing\MissingApp.exe",
                    SwitchOutputPerApp = true,
                }
            ]
            }
        });

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Spotify",
            JsonOutput = true,
        });

        Assert.Equal(5, result.ExitCode);
        Assert.NotNull(result.Output);

        JObject root = JObject.Parse(result.Output!);
        Assert.Equal("routine-trigger-app-not-running", root["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal("routine-1", root["data"]?["error"]?["routineId"]?.Value<string>());
        Assert.Equal("Spotify", root["data"]?["error"]?["routineName"]?.Value<string>());
        Assert.Equal("Application startup", root["data"]?["error"]?["triggerMode"]?.Value<string>());
        Assert.Equal("MissingApp", root["data"]?["error"]?["triggerApplicationName"]?.Value<string>());
        Assert.True(root["data"]?["error"]?["requiresRunningTriggerProcess"]?.Value<bool>());
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_AttemptsInputReconnect_AfterOutputFailure()
    {
        var fakeReconnectService = new FakeBluetoothReconnectService { NextResult = false };
        using var scope = new HeadlessRunnerScope(new Settings
        {
            Miscellaneous = new MiscellaneousSettings
            {
                BluetoothReconnectEnabled = true
            },
            Routines = new RoutinesSettings
            {
                Items =
            [
                new AudioRoutine
                {
                    Id = "routine-1",
                    Name = "Desk",
                    Enabled = true,
                    OutputDeviceId = "missing-output-device",
                    OutputDeviceName = "Bluetooth Headset",
                    InputDeviceId = "missing-input-device",
                    InputDeviceName = "Bluetooth Microphone",
                }
            ]
            }
        }, fakeReconnectService);

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("[diag-code:routine-run-failed] Failed to run routine 'Desk'.", result.Output, StringComparison.Ordinal);
        Assert.Contains("Output failure:", result.Output, StringComparison.Ordinal);
        Assert.Contains("Input failure:", result.Output, StringComparison.Ordinal);
        Assert.Equal(2, fakeReconnectService.Calls);
        Assert.Equal(["output", "input"], fakeReconnectService.Kinds);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_IncludesOutputExceptionFailureDetail()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings
            {
                Routines = new RoutinesSettings
                {
                    Items =
                    [
                        new AudioRoutine
                        {
                            Id = "routine-1",
                            Name = "Desk",
                            Enabled = true,
                            OutputDeviceId = "out-2",
                            OutputDeviceName = "Headset"
                        }
                    ]
                }
            },
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetDefaultPlaybackDeviceSnapshot: static () => ("out-1", "Speakers"),
                SwitchAudioDeviceAsync: static (_, _, _, _, _, _, _) => throw new InvalidOperationException("boom")));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("[diag-code:routine-run-failed] Failed to run routine 'Desk'. Output failure: Output switch threw InvalidOperationException.", result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_IncludesInputExceptionFailureDetail()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings
            {
                Routines = new RoutinesSettings
                {
                    Items =
                    [
                        new AudioRoutine
                        {
                            Id = "routine-1",
                            Name = "Desk",
                            Enabled = true,
                            InputDeviceId = "in-2",
                            InputDeviceName = "Microphone"
                        }
                    ]
                }
            },
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                SwitchInputDeviceToAsync: static (_, _, _) => throw new InvalidOperationException("boom")));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("[diag-code:routine-run-failed] Failed to run routine 'Desk'. Input failure: Input switch threw InvalidOperationException.", result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_IncludesPerAppDeferredFailureDetail()
    {
        string currentProcessPath = Environment.ProcessPath ?? throw new InvalidOperationException("Current process path is unavailable.");

        using var scope = new HeadlessRunnerScope(
            new Settings
            {
                Routines = new RoutinesSettings
                {
                    Items =
                    [
                        new AudioRoutine
                        {
                            Id = "routine-1",
                            Name = "Desk",
                            Enabled = true,
                            TriggerOnAppStart = true,
                            TriggerAppPath = currentProcessPath,
                            SwitchOutputPerApp = true,
                            OutputDeviceId = "out-2",
                            OutputDeviceName = "Headset"
                        }
                    ]
                }
            },
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                SwitchApplicationOutputDeviceDetailedAsync: static (_, _, _, _) => new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.DeferredNoAudio, null)));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("[diag-code:routine-run-failed] Failed to run routine 'Desk'. Output failure: Per-app output routing is pending until the application produces audio.", result.Output);
    }


    [Fact]
    public void FormatRoutineError_Json_IncludesPartialSuccessMetadata()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-desk",
            Name = "Desk",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            InputDeviceId = "in-1",
            InputDeviceName = "Microphone",
        };

        string json = CliOutputFormatter.FormatRoutineError(
            3,
            "routine-run-failed",
            "Failed to run routine 'Desk'.",
            jsonOutput: true,
            routine: routine,
            outputSucceeded: true,
            appliedOutputDeviceName: "Speakers",
            inputSucceeded: false,
            appliedInputDeviceName: null);

        JObject parsed = JObject.Parse(json);
        JToken error = Assert.IsType<JObject>(parsed["data"]?["error"]);
        Assert.True(error["partialFailure"]?.Value<bool>());
        Assert.True(error["outputSucceeded"]?.Value<bool>());
        Assert.Equal("Speakers", error["appliedOutputDeviceName"]?.Value<string>());
        Assert.False(error["inputSucceeded"]?.Value<bool>());
        Assert.Null(error["appliedInputDeviceName"]?.Value<string>());
    }


    [Fact]
    public void FormatRoutineError_Text_IncludesPartialSuccessMetadata()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-desk",
            Name = "Desk",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            InputDeviceId = "in-1",
            InputDeviceName = "Microphone",
        };

        string text = CliOutputFormatter.FormatRoutineError(
            3,
            "routine-run-failed",
            "Failed to run routine 'Desk'.",
            jsonOutput: false,
            routine: routine,
            outputSucceeded: true,
            appliedOutputDeviceName: "Speakers",
            inputSucceeded: false,
            appliedInputDeviceName: null);

        Assert.Equal("[diag-code:routine-run-failed] Failed to run routine 'Desk'. Partial result: succeeded output 'Speakers'; failed input 'Microphone'.", text);
    }


    [Fact]
    public void FormatRoutineError_Json_IncludesFailureDetails()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-desk",
            Name = "Desk",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            InputDeviceId = "in-1",
            InputDeviceName = "Microphone",
        };

        string json = CliOutputFormatter.FormatRoutineError(
            3,
            "routine-run-failed",
            "Failed to run routine 'Desk'.",
            jsonOutput: true,
            routine: routine,
            outputSucceeded: false,
            appliedOutputDeviceName: null,
            outputFailureDetail: "Per-app output routing is pending until the application produces audio.",
            inputSucceeded: false,
            appliedInputDeviceName: null,
            inputFailureDetail: "Input switch threw InvalidOperationException.");

        JObject parsed = JObject.Parse(json);
        JToken error = Assert.IsType<JObject>(parsed["data"]?["error"]);
        Assert.Equal("Per-app output routing is pending until the application produces audio.", error["outputFailureDetail"]?.Value<string>());
        Assert.Equal("Input switch threw InvalidOperationException.", error["inputFailureDetail"]?.Value<string>());
    }


    [Fact]
    public void FormatRoutineError_Text_IncludesFailureDetails()
    {
        string text = CliOutputFormatter.FormatRoutineError(
            3,
            "routine-run-failed",
            "Failed to run routine 'Desk'.",
            jsonOutput: false,
            outputFailureDetail: "Per-app output routing is pending until the application produces audio.",
            inputFailureDetail: "Input switch threw InvalidOperationException.");

        Assert.Equal("[diag-code:routine-run-failed] Failed to run routine 'Desk'. Output failure: Per-app output routing is pending until the application produces audio. Input failure: Input switch threw InvalidOperationException.", text);
    }


    [Fact]
    public void FormatRoutineError_Text_Redact_ReplacesSensitiveValues()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-desk",
            Name = "Desk",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            InputDeviceId = "in-1",
            InputDeviceName = "Microphone",
            TriggerOnAppStart = true,
            TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
        };

        string text = CliOutputFormatter.FormatRoutineError(
            3,
            "routine-run-failed",
            "Failed to run routine 'Desk'.",
            jsonOutput: false,
            routine: routine,
            triggerApplicationName: "Discord",
            outputSucceeded: true,
            appliedOutputDeviceName: "Speakers",
            inputSucceeded: false,
            appliedInputDeviceName: null,
            redactOutput: true);

        Assert.DoesNotContain("Desk", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Speakers", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Discord", text, StringComparison.Ordinal);
        Assert.Contains("value[", text);
        Assert.Contains("device[", text);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineListJson_IncludesTriggerMetadata()
    {
        using var scope = new HeadlessRunnerScope(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
            [
                new AudioRoutine
                {
                    Id = "routine-discord",
                    Name = "Discord",
                    Enabled = true,
                    OutputDeviceId = "out-1",
                    OutputDeviceName = "Headset",
                    TriggerOnAppStart = true,
                    TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
                    SwitchOutputPerApp = true,
                    ShowInTrayMenu = true,
                    ExecutionDelayMs = 250,
                    CooldownSeconds = 15,
                    TriggerAppStableForMs = 800,
                },
                new AudioRoutine
                {
                    Id = "routine-discord-alt",
                    Name = "Discord Alt",
                    Enabled = true,
                    OutputDeviceId = "out-2",
                    OutputDeviceName = "Speakers",
                    TriggerOnAppStart = true,
                    TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
                }
            ]
            }
        });

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineList,
            JsonOutput = true,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Output);

        JObject parsed = JObject.Parse(result.Output!);
        JArray routines = Assert.IsType<JArray>(parsed["data"]?["routines"]);
        JToken routine = routines.Single(static item => string.Equals(item["id"]?.Value<string>(), "routine-discord", StringComparison.Ordinal));
        Assert.Equal("routine-discord", routine["id"]?.Value<string>());
        Assert.Equal("Application startup", routine["triggerMode"]?.Value<string>());
        Assert.True(routine["triggerOnAppStart"]?.Value<bool>());
        Assert.Equal(@"C:\Apps\Discord\Discord.exe", routine["triggerAppPath"]?.Value<string>());
        Assert.True(routine["switchOutputPerApp"]?.Value<bool>());
        Assert.False(routine["showInTrayMenu"]?.Value<bool>());
        Assert.Equal("Application start: Discord | Application audio only", routine["triggerSummary"]?.Value<string>());
        Assert.Equal(250, routine["executionDelayMs"]?.Value<int>());
        Assert.Equal(15, routine["cooldownSeconds"]?.Value<int>());
        Assert.Equal(800, routine["triggerAppStableForMs"]?.Value<int>());
        Assert.Equal("Balanced", routine["timingPreset"]?.Value<string>());
        Assert.Equal("Delay: 250 ms | Cooldown: 15 s | App stable: 800 ms", routine["timingSummary"]?.Value<string>());
        Assert.Contains("conflicts with 1 other enabled routine", routine["conflictSummary"]?.Value<string>(), StringComparison.Ordinal);
    }


    [Fact]
    public void FormatRoutineRunResult_Json_IncludesTriggerMetadata()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-desk",
            Name = "Desk",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            TriggerKind = RoutineTriggerKind.DeviceChange,
            ShowInTrayMenu = true,
            ExecutionDelayMs = 300,
            CooldownSeconds = 20,
        };

        string json = CliOutputFormatter.FormatRoutineRunResult(routine, "Speakers", null, jsonOutput: true);

        JObject parsed = JObject.Parse(json);
        JToken data = Assert.IsType<JObject>(parsed["data"]);
        Assert.Equal("routine-desk", data["id"]?.Value<string>());
        Assert.Equal("Device change", data["triggerMode"]?.Value<string>());
        Assert.False(data["triggerOnAppStart"]?.Value<bool>());
        Assert.Equal(string.Empty, data["triggerAppPath"]?.Value<string>());
        Assert.False(data["restorePreviousAudioOnDeactivate"]?.Value<bool>());
        Assert.False(data["switchOutputPerApp"]?.Value<bool>());
        Assert.False(data["showInTrayMenu"]?.Value<bool>());
        Assert.Equal("Device change", data["triggerSummary"]?.Value<string>());
        Assert.Equal(150, data["executionDelayMs"]?.Value<int>());
        Assert.Equal(5, data["cooldownSeconds"]?.Value<int>());
        Assert.Equal(0, data["triggerAppStableForMs"]?.Value<int>());
        Assert.Equal("Automatic", data["timingPreset"]?.Value<string>());
        Assert.Equal("Delay: 150 ms | Cooldown: 5 s", data["timingSummary"]?.Value<string>());
        Assert.Equal("Speakers", data["appliedOutputDeviceName"]?.Value<string>());
        Assert.Equal("routine-run-success", data["diagCode"]?.Value<string>());
    }


    [Fact]
    public void FormatRoutineList_Text_IncludesTimingAndConflictSummary()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                TriggerKind = RoutineTriggerKind.DeviceChange,
                ExecutionDelayMs = 125,
                CooldownSeconds = 10,
                ShowInTrayMenu = true,
            },
            new()
            {
                Id = "routine-2",
                Name = "Headset",
                Enabled = true,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
                TriggerKind = RoutineTriggerKind.DeviceChange,
                ShowInTrayMenu = true,
            }
        ];

        string text = CliOutputFormatter.FormatRoutineList(routines, jsonOutput: false);

        Assert.Contains("Timing: Automatic | Delay: 150 ms | Cooldown: 5 s", text, StringComparison.Ordinal);
        Assert.Contains("Conflict: Device change conflicts with 1 other enabled routine: different output targets.", text, StringComparison.Ordinal);
    }


    [Fact]
    public void FormatRoutineRunResult_Text_IncludesTimingSummary()
    {
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Name = "Desk",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            ExecutionDelayMs = 200,
            CooldownSeconds = 8,
        };

        string text = CliOutputFormatter.FormatRoutineRunResult(routine, "Speakers", null, jsonOutput: false);

        Assert.Contains("Timing: Custom | Delay: 200 ms | Cooldown: 8 s.", text, StringComparison.Ordinal);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineListJson_Redact_RedactsNamesAndPaths()
    {
        using var scope = new HeadlessRunnerScope(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
            [
                new AudioRoutine
                {
                    Id = "routine-discord",
                    Name = "Discord",
                    Enabled = true,
                    OutputDeviceId = "out-1",
                    OutputDeviceName = "Headset",
                    TriggerOnAppStart = true,
                    TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
                    SwitchOutputPerApp = true,
                    ShowInTrayMenu = true,
                }
            ]
            }
        });

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineList,
            JsonOutput = true,
            RedactOutput = true,
        });

        Assert.Equal(0, result.ExitCode);
        JObject parsed = JObject.Parse(result.Output!);
        JToken routine = Assert.Single(Assert.IsType<JArray>(parsed["data"]?["routines"]));
        Assert.Equal("routine-discord", routine["id"]?.Value<string>());
        Assert.StartsWith("routine[", routine["name"]?.Value<string>(), StringComparison.Ordinal);
        Assert.StartsWith("path[", routine["triggerAppPath"]?.Value<string>(), StringComparison.Ordinal);
        Assert.StartsWith("device[", routine["outputDeviceName"]?.Value<string>(), StringComparison.Ordinal);
    }

}
