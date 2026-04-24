namespace AudioPilot.Tests.Helpers;

public sealed class TestLogFileAssertTests : IDisposable
{
    private readonly string _tempDirectory;

    public TestLogFileAssertTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "AudioPilot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task WaitForLogText_ReturnsWhenFragmentAppears()
    {
        string logPath = Path.Combine(_tempDirectory, "single.log");

        Task<string> waitTask = Task.Run(() =>
        {
            return TestLogFileAssert.WaitForLogText(logPath, "ready", timeoutMs: 3000);
        });

        await Task.Yield();
        await File.WriteAllTextAsync(logPath, "ready");

        string logText = await waitTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains("ready", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForLogText_ReturnsWhenAllFragmentsAppear()
    {
        string logPath = Path.Combine(_tempDirectory, "multi.log");

        Task<string> waitTask = Task.Run(() =>
        {
            return TestLogFileAssert.WaitForLogText(logPath, timeoutMs: 3000, "alpha", "beta");
        });

        await Task.Yield();
        await File.WriteAllTextAsync(logPath, "alpha");
        await File.AppendAllTextAsync(logPath, " beta");

        string logText = await waitTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains("alpha", logText, StringComparison.Ordinal);
        Assert.Contains("beta", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForLogText_ReturnsWhenFileAppearsAfterWaitStarts()
    {
        string logPath = Path.Combine(_tempDirectory, "created-late.log");

        Task<string> waitTask = Task.Run(() =>
        {
            return TestLogFileAssert.WaitForLogText(logPath, "created", timeoutMs: 3000);
        });

        await Task.Yield();
        await File.WriteAllTextAsync(logPath, "created");
        await Task.Delay(100); // Give FileSystemWatcher time to detect the file creation

        string logText = await waitTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains("created", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForLogText_ReturnsWhenFileIsReplacedDuringWait()
    {
        string logPath = Path.Combine(_tempDirectory, "rotated.log");
        await File.WriteAllTextAsync(logPath, "before");

        Task<string> waitTask = Task.Run(() =>
        {
            return TestLogFileAssert.WaitForLogText(logPath, "after", timeoutMs: 3000);
        });

        await Task.Yield();
        File.Delete(logPath);
        await File.WriteAllTextAsync(logPath, "after");

        string logText = await waitTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains("after", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitForLogText_ReturnsAvailableTextOnTimeout()
    {
        string logPath = Path.Combine(_tempDirectory, "timeout.log");
        File.WriteAllText(logPath, "partial");

        string logText = TestLogFileAssert.WaitForLogText(logPath, "missing", timeoutMs: 50);

        Assert.Equal("partial", logText);
    }

    [Fact]
    public void TrySignal_ReturnsFalse_WhenWaitHandleIsDisposed()
    {
        var waitHandle = new AutoResetEvent(false);
        waitHandle.Dispose();

        bool signaled = TestLogFileAssert.TrySignal(waitHandle);

        Assert.False(signaled);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
