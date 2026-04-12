using System.Reflection;

namespace AudioPilot.Tests.Helpers;

[Collection("CoreAudioWorkerIsolation")]
public sealed class ComThreadingHelperTests
{
    [Fact]
    public async Task ForceCoreAudioWorkerFailure_RestartsWorker_OnNextInvocation()
    {
        int first = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => 41);
        Assert.Equal(41, first);

        await ComThreadingHelper.ForceCoreAudioWorkerFailureForTestsAsync();

        await ComThreadingHelper.WaitForCoreAudioWorkerReadyForTestsAsync();

        int afterRestart = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => 42);

        Assert.Equal(42, afterRestart);
    }

    [Fact]
    public async Task ForceCoreAudioWorkerFailure_RestartsWorker_AcrossRepeatedFailures()
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            await ComThreadingHelper.ForceCoreAudioWorkerFailureForTestsAsync();
            await ComThreadingHelper.WaitForCoreAudioWorkerReadyForTestsAsync();

            int value = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => iteration + 100);
            Assert.Equal(iteration + 100, value);
        }
    }

    [Fact]
    public async Task CoreAudioComExecutor_Invoke_UnblocksWhenDisposeStartsDuringHungWorkItem()
    {
        object executor = CreatePrivateExecutor();
        MethodInfo invokeMethod = GetPrivateExecutorMethod("Invoke", [typeof(Action), typeof(CancellationToken)]);
        MethodInfo disposeMethod = GetPrivateExecutorMethod(nameof(IDisposable.Dispose), Type.EmptyTypes);

        using var blockerStarted = new ManualResetEventSlim(false);
        using var releaseBlocker = new ManualResetEventSlim(false);

        Task invokeTask = Task.Run(() =>
        {
            try
            {
                invokeMethod.Invoke(executor,
                [
                    (Action)(() =>
                    {
                        blockerStarted.Set();
                        Assert.True(releaseBlocker.Wait(TimeSpan.FromSeconds(5)));
                    }),
                    CancellationToken.None,
                ]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        });

        Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(5)));

        disposeMethod.Invoke(executor, []);

        ObjectDisposedException exception = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await invokeTask.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains("CoreAudioComExecutor", exception.ObjectName, StringComparison.Ordinal);

        releaseBlocker.Set();
    }

    private static object CreatePrivateExecutor()
    {
        Type? executorType = typeof(ComThreadingHelper)
            .GetNestedType("CoreAudioComExecutor", BindingFlags.NonPublic);

        Assert.NotNull(executorType);

        object? executor = Activator.CreateInstance(
            executorType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["AudioPilot.CoreAudioCOM.Test"],
            culture: null);

        Assert.NotNull(executor);
        return executor;
    }

    private static MethodInfo GetPrivateExecutorMethod(string name, Type[] parameterTypes)
    {
        Type executorType = typeof(ComThreadingHelper)
            .GetNestedType("CoreAudioComExecutor", BindingFlags.NonPublic)!;

        MethodInfo? method = executorType.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: parameterTypes,
            modifiers: null);

        Assert.NotNull(method);
        return method;
    }

}

