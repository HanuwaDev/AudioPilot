using System.IO.Pipes;
using System.Reflection;
using System.Text;
using AudioPilot.Cli;

namespace AudioPilot.Tests.Helpers;

public sealed class SingleInstanceHelperTests
{
    [Fact]
    public async Task TryAcquire_WhenSecondInstanceStarts_RaisesActivationOnFirst()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        using var first = new SingleInstanceHelper(mutexName, pipeName);
        Assert.True(first.TryAcquire());

        var activationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ActivationRequested += (_, _) => activationSignal.TrySetResult(true);

        await first.ActivationListenerReadyForTests;

        using var second = new SingleInstanceHelper(mutexName, pipeName);
        Assert.False(second.TryAcquire());

        bool activated = await activationSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(activated);
    }

    [Fact]
    public void TryAcquire_WithDifferentScopes_AllowsBothInstances()
    {
        string leftScope = Guid.NewGuid().ToString("N");
        string rightScope = Guid.NewGuid().ToString("N");

        using var left = new SingleInstanceHelper($"AudioPilot.Tests.Mutex.{leftScope}", $"AudioPilot.Tests.Pipe.{leftScope}");
        using var right = new SingleInstanceHelper($"AudioPilot.Tests.Mutex.{rightScope}", $"AudioPilot.Tests.Pipe.{rightScope}");

        Assert.True(left.TryAcquire());
        Assert.True(right.TryAcquire());
    }

    [Fact]
    public async Task ActivationListener_IgnoresInvalidPayload()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        using var first = new SingleInstanceHelper(mutexName, pipeName);
        Assert.True(first.TryAcquire());

        var activationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ActivationRequested += (_, _) => activationSignal.TrySetResult(true);

        await first.ActivationListenerReadyForTests;

        using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.None))
        {
            await client.ConnectAsync(3000);
            byte[] invalidPayload = Encoding.UTF8.GetBytes("INVALID");
            byte[] length = BitConverter.GetBytes(invalidPayload.Length);
            await client.WriteAsync(length);
            await client.WriteAsync(invalidPayload);
            await client.FlushAsync();
        }

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await activationSignal.Task.WaitAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task TryAcquire_CommandForwarding_ReceivesExitCodeAndOutput()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        using var first = new SingleInstanceHelper(mutexName, pipeName);
        Assert.True(first.TryAcquire());

        first.CommandRequested += payload =>
        {
            if (payload == new CliCommand { Action = CliAction.SwitchOutput, Reverse = true }.ToPipePayload())
            {
                return Task.FromResult(new SingleInstanceCommandResult(5, "precondition-failed", ProtocolVersion: 1));
            }

            return Task.FromResult(new SingleInstanceCommandResult(2, "invalid", ProtocolVersion: 1));
        };

        await first.ActivationListenerReadyForTests;

        using var second = new SingleInstanceHelper(mutexName, pipeName);
        Assert.False(second.TryAcquire(new CliCommand { Action = CliAction.SwitchOutput, Reverse = true }.ToPipePayload()));
        Assert.True(second.LastSignalExistingSucceeded);
        Assert.Equal(5, second.LastSignalExitCode);
        Assert.Equal("precondition-failed", second.LastSignalOutput);
    }

    [Fact]
    public async Task TryAcquire_StartupStatusForwarding_ReceivesOutput()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        using var first = new SingleInstanceHelper(mutexName, pipeName);
        Assert.True(first.TryAcquire());

        first.CommandRequested += _ => Task.FromResult(new SingleInstanceCommandResult(0, "enabled", ProtocolVersion: 1));

        await first.ActivationListenerReadyForTests;

        var statusCommand = new CliCommand { Action = CliAction.StartupStatus };
        using var second = new SingleInstanceHelper(mutexName, pipeName);

        Assert.False(second.TryAcquire(statusCommand.ToPipePayload()));
        Assert.True(second.LastSignalExistingSucceeded);
        Assert.Equal(0, second.LastSignalExitCode);
        Assert.Equal("enabled", second.LastSignalOutput);
    }

    [Fact]
    public void TryAcquire_CommandForwarding_WhenExistingInstanceUnreachable_SetsFailureState()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        using var orphanedMutex = new Mutex(true, mutexName, out bool createdNew);
        Assert.True(createdNew);

        using var helper = new SingleInstanceHelper(mutexName, pipeName);
        Assert.False(helper.TryAcquire(new CliCommand { Action = CliAction.SwitchOutput }.ToPipePayload()));
        Assert.True(helper.ExistingInstanceDetected);
        Assert.False(helper.LastSignalExistingSucceeded);
        Assert.Null(helper.LastSignalExitCode);
        Assert.Null(helper.LastSignalOutput);
    }

    [Fact]
    public async Task TryAcquireDetailed_WhenExistingInstanceHealthy_ReturnsHealthyDisposition()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        using var first = new SingleInstanceHelper(mutexName, pipeName);
        Assert.True(first.TryAcquire());
        await first.ActivationListenerReadyForTests;

        using var second = new SingleInstanceHelper(mutexName, pipeName);

        SingleInstanceAcquireResult result = second.TryAcquireDetailed(showUserErrors: false);

        Assert.False(result.Acquired);
        Assert.True(result.ExistingHealthy);
        Assert.Equal(SingleInstanceSignalFailureKind.None, result.FailureKind);
    }

    [Fact]
    public void TryAcquireDetailed_WhenExistingInstanceUnreachable_ReturnsUnresponsiveConnectionFailure()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        using var orphanedMutex = new Mutex(true, mutexName, out bool createdNew);
        Assert.True(createdNew);

        using var helper = new SingleInstanceHelper(mutexName, pipeName);

        SingleInstanceAcquireResult result = helper.TryAcquireDetailed(showUserErrors: false);

        Assert.False(result.Acquired);
        Assert.True(result.ExistingUnresponsive);
        Assert.Equal(SingleInstanceSignalFailureKind.ConnectionFailed, result.FailureKind);
        Assert.Equal(SingleInstanceSignalFailureKind.ConnectionFailed, helper.LastSignalFailureKind);
    }

    [Fact]
    public async Task TryAcquireDetailed_WhenExistingInstanceReturnsInvalidResponse_ReturnsUnresponsiveInvalidResponse()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        using var orphanedMutex = new Mutex(true, mutexName, out bool createdNew);
        Assert.True(createdNew);
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();

            byte[] lengthBuffer = new byte[sizeof(int)];
            int bytesRead = await server.ReadAsync(lengthBuffer);
            Assert.Equal(sizeof(int), bytesRead);

            int payloadLength = BitConverter.ToInt32(lengthBuffer, 0);
            byte[] payloadBuffer = new byte[payloadLength];
            int offset = 0;
            while (offset < payloadLength)
            {
                offset += await server.ReadAsync(payloadBuffer.AsMemory(offset, payloadLength - offset));
            }

            byte[] invalidPayload = Encoding.UTF8.GetBytes("not-json");
            byte[] invalidLength = BitConverter.GetBytes(invalidPayload.Length);
            await server.WriteAsync(invalidLength);
            await server.WriteAsync(invalidPayload);
            await server.FlushAsync();
        });

        using var helper = new SingleInstanceHelper(mutexName, pipeName);
        SingleInstanceAcquireResult result = helper.TryAcquireDetailed(showUserErrors: false);

        Assert.False(result.Acquired);
        Assert.True(result.ExistingUnresponsive);
        Assert.Equal(SingleInstanceSignalFailureKind.InvalidResponse, result.FailureKind);
        Assert.Equal(SingleInstanceSignalFailureKind.InvalidResponse, helper.LastSignalFailureKind);
        await serverTask;
    }

    [Fact]
    public async Task TryAcquireDetailed_WhenExistingInstanceStallsAfterConnect_ReturnsConnectionFailure()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        using var orphanedMutex = new Mutex(true, mutexName, out bool createdNew);
        Assert.True(createdNew);
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();

            byte[] lengthBuffer = new byte[sizeof(int)];
            int bytesRead = await server.ReadAsync(lengthBuffer);
            Assert.Equal(sizeof(int), bytesRead);

            int payloadLength = BitConverter.ToInt32(lengthBuffer, 0);
            byte[] payloadBuffer = new byte[payloadLength];
            int offset = 0;
            while (offset < payloadLength)
            {
                offset += await server.ReadAsync(payloadBuffer.AsMemory(offset, payloadLength - offset));
            }

            await Task.Delay(500);
        });

        using var helper = new SingleInstanceHelper(mutexName, pipeName, responseTimeoutMs: 100);
        SingleInstanceAcquireResult result = helper.TryAcquireDetailed(showUserErrors: false);

        Assert.False(result.Acquired);
        Assert.True(result.ExistingUnresponsive);
        Assert.Equal(SingleInstanceSignalFailureKind.ConnectionFailed, result.FailureKind);
        Assert.Equal(SingleInstanceSignalFailureKind.ConnectionFailed, helper.LastSignalFailureKind);
        await serverTask;
    }

    [Fact]
    public async Task TryAcquire_CommandForwarding_MalformedPayload_ReceivesExitCodeTwo()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        using var first = new SingleInstanceHelper(mutexName, pipeName);
        Assert.True(first.TryAcquire());

        first.CommandRequested += payload =>
        {
            return Task.FromResult(
                CliCommand.TryFromPipePayload(payload, out _)
                    ? new SingleInstanceCommandResult(0, "ok", ProtocolVersion: 1)
                    : new SingleInstanceCommandResult(6, ErrorCode: "forwarded-protocol-mismatch", ErrorMessage: "The running AudioPilot instance uses an incompatible CLI forwarding protocol.", ProtocolVersion: 1));
        };

        await first.ActivationListenerReadyForTests;

        using var second = new SingleInstanceHelper(mutexName, pipeName);
        Assert.False(second.TryAcquire("not-json"));
        Assert.True(second.LastSignalExistingSucceeded);
        Assert.Equal(6, second.LastSignalExitCode);
        Assert.Equal("forwarded-protocol-mismatch", second.LastSignalErrorCode);
        Assert.Equal("The running AudioPilot instance uses an incompatible CLI forwarding protocol.", second.LastSignalErrorMessage);
    }

    [Fact]
    public async Task TryAcquire_CommandForwarding_WhenHandlerThrows_ReceivesExitCodeThree()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        using var first = new SingleInstanceHelper(mutexName, pipeName);
        Assert.True(first.TryAcquire());

        first.CommandRequested += _ => throw new InvalidOperationException("handler-failed");

        await first.ActivationListenerReadyForTests;

        using var second = new SingleInstanceHelper(mutexName, pipeName);
        Assert.False(second.TryAcquire(new CliCommand { Action = CliAction.Status }.ToPipePayload()));
        Assert.True(second.LastSignalExistingSucceeded);
        Assert.Equal(7, second.LastSignalExitCode);
        Assert.Equal("forwarded-runtime-failed", second.LastSignalErrorCode);
        Assert.Equal("Command execution failed in the running UI host.", second.LastSignalErrorMessage);
    }

    [Fact]
    public async Task ActivationListener_WhenClientConnectsAndSendsNothing_TimesOutAndAcceptsNextClient()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        var activationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new SingleInstanceHelper(mutexName, pipeName, requestReadTimeoutMs: 100);
        first.ActivationRequested += (_, _) => activationSignal.TrySetResult(true);
        Assert.True(first.TryAcquire());

        await first.ActivationListenerReadyForTests;

        await using var stalledClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stalledClient.ConnectAsync(3000);
        await Task.Delay(350);

        using var second = new SingleInstanceHelper(mutexName, pipeName);
        Assert.False(second.TryAcquire());
        Assert.True(second.LastSignalExistingSucceeded);

        bool activated = await activationSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(activated);
    }

    [Fact]
    public async Task Dispose_AfterAcquire_ClearsActivationListenerState()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        var helper = new SingleInstanceHelper(mutexName, pipeName);
        Assert.True(helper.TryAcquire());

        await helper.ActivationListenerReadyForTests;

        var ctsField = typeof(SingleInstanceHelper).GetField("_activationListenerCts", BindingFlags.Instance | BindingFlags.NonPublic);
        var taskField = typeof(SingleInstanceHelper).GetField("_activationListenerTask", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(ctsField);
        Assert.NotNull(taskField);

        var listenerTask = taskField!.GetValue(helper) as Task;
        Assert.NotNull(listenerTask);

        helper.Dispose();

        Assert.Null(ctsField!.GetValue(helper));
        Assert.Null(taskField.GetValue(helper));

        await listenerTask!;
    }

    [Fact]
    public async Task Dispose_AfterAcquire_AllowsNextInstanceToBecomePrimary()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        var firstActivation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new SingleInstanceHelper(mutexName, pipeName);
        first.ActivationRequested += (_, _) => firstActivation.TrySetResult(true);
        Assert.True(first.TryAcquire());
        await first.ActivationListenerReadyForTests;

        first.Dispose();

        var secondActivation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var second = new SingleInstanceHelper(mutexName, pipeName);
        second.ActivationRequested += (_, _) => secondActivation.TrySetResult(true);
        Assert.True(second.TryAcquire());
        await second.ActivationListenerReadyForTests;

        using var third = new SingleInstanceHelper(mutexName, pipeName);
        Assert.False(third.TryAcquire());

        bool activated = await secondActivation.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(activated);
        Assert.False(firstActivation.Task.IsCompleted);
    }

    [Fact]
    public void TryAcquire_WhenAlreadyOwned_IsIdempotent()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";

        using var helper = new SingleInstanceHelper(mutexName, pipeName);
        Assert.True(helper.TryAcquire());

        var ctsField = typeof(SingleInstanceHelper).GetField("_activationListenerCts", BindingFlags.Instance | BindingFlags.NonPublic);
        var taskField = typeof(SingleInstanceHelper).GetField("_activationListenerTask", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(ctsField);
        Assert.NotNull(taskField);

        object? firstCts = ctsField!.GetValue(helper);
        object? firstTask = taskField!.GetValue(helper);

        Assert.True(helper.TryAcquire());
        Assert.Same(firstCts, ctsField.GetValue(helper));
        Assert.Same(firstTask, taskField.GetValue(helper));
    }
}

