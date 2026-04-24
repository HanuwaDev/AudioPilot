using System.Reflection;

namespace AudioPilot.Tests.Platform;

public sealed class SteamBigPictureSignalMonitorTests
{
    [Theory]
    [InlineData(0x0003u, 1, 1, 1, true)]
    [InlineData(0x8000u, 1, 0, 0, true)]
    [InlineData(0x8001u, 1, 0, 0, true)]
    [InlineData(0x8002u, 1, 0, 0, true)]
    [InlineData(0x8003u, 1, 0, 0, true)]
    [InlineData(0x800Cu, 1, 0, 0, true)]
    [InlineData(0x8002u, 0, 0, 0, false)]
    [InlineData(0x8002u, 1, 1, 0, false)]
    [InlineData(0x8002u, 1, 0, 1, false)]
    [InlineData(0x8005u, 1, 0, 0, false)]
    public void ShouldSignal_ReturnsExpectedValue(uint eventType, int hwndValue, int idObject, int idChild, bool expected)
    {
        bool result = WinEventSteamBigPictureSignalMonitor.ShouldSignal(eventType, (nint)hwndValue, idObject, idChild);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0x0003u, "Foreground")]
    [InlineData(0x8000u, "Create")]
    [InlineData(0x8001u, "Destroy")]
    [InlineData(0x8002u, "Show")]
    [InlineData(0x8003u, "Hide")]
    [InlineData(0x800Cu, "NameChange")]
    [InlineData(0x9999u, "Unknown")]
    public void GetSignalKind_ReturnsExpectedKind(uint eventType, string expected)
    {
        SteamBigPictureSignalKind result = WinEventSteamBigPictureSignalMonitor.GetSignalKind(eventType);

        Assert.Equal(expected, result.ToString());
    }

    [Fact]
    public void Dispose_IgnoresStaleWinEventCallback()
    {
        var monitor = new WinEventSteamBigPictureSignalMonitor();
        int signalCount = 0;
        monitor.Signaled += _ => signalCount++;

        SetPrivateField(monitor, "_running", true);
        monitor.Dispose();

        InvokeWinEventCallback(monitor, 0x0003u, (nint)1, 1, 1);

        Assert.Equal(0, signalCount);
    }

    [Fact]
    public void Stop_IgnoresStaleWinEventCallback()
    {
        var monitor = new WinEventSteamBigPictureSignalMonitor();
        int signalCount = 0;
        monitor.Signaled += _ => signalCount++;

        SetPrivateField(monitor, "_running", true);
        monitor.Stop();

        InvokeWinEventCallback(monitor, 0x0003u, (nint)1, 1, 1);

        Assert.Equal(0, signalCount);
    }

    private static void InvokeWinEventCallback(WinEventSteamBigPictureSignalMonitor monitor, uint eventType, nint hwnd, int idObject, int idChild)
    {
        MethodInfo? method = typeof(WinEventSteamBigPictureSignalMonitor).GetMethod("OnWinEvent", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(monitor, [nint.Zero, eventType, hwnd, idObject, idChild, 0u, 0u]);
    }

    private static void SetPrivateField(WinEventSteamBigPictureSignalMonitor monitor, string fieldName, bool value)
    {
        FieldInfo? field = typeof(WinEventSteamBigPictureSignalMonitor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(monitor, value);
    }
}
