using AudioPilot.Constants;
using AudioPilot.Logging;
using Microsoft.Win32;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class StartupServiceTests : IDisposable
{
    private readonly string _registryPath = $"SOFTWARE\\AudioPilot.Tests\\{Guid.NewGuid():N}";
    private readonly string _valueName = $"AudioPilotTest_{Guid.NewGuid():N}";

    [Fact]
    public void AddToStartup_WritesRegistryValue_AndIsInStartupReturnsTrue()
    {
        EnsureRegistryKeyExists();
        var service = new StartupService(_registryPath, _valueName);

        service.AddToStartup();

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_registryPath, writable: false);
        string? stored = key?.GetValue(_valueName)?.ToString();

        Assert.False(string.IsNullOrWhiteSpace(stored));
        Assert.Contains("-startup", stored, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.IsInStartup());
    }

    [Fact]
    public void IsInStartup_EmitsCorrelatedProbeLog()
    {
        EnsureRegistryKeyExists();
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(_registryPath, writable: true))
        {
            Assert.NotNull(key);
            key!.SetValue(_valueName, "dummy");
        }

        using var logger = Logger.CreateInMemoryForTests("startup-service-probe.log");
        logger.MinimumLevel = LogLevel.Debug;

        var service = new StartupService(_registryPath, _valueName);
        SetLogger(service, logger);

        bool inStartup = service.IsInStartup("startup-registry:test-probe");

        string logText = logger.DisposeAndReadLogTextForTests();

        Assert.True(inStartup);
        Assert.True(string.IsNullOrWhiteSpace(logText), $"Did not expect success-path startup probe logs, but found:{Environment.NewLine}{logText}");
    }

    [Fact]
    public void RemoveFromStartup_DeletesExistingRegistryValue()
    {
        EnsureRegistryKeyExists();
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(_registryPath, writable: true))
        {
            Assert.NotNull(key);
            key!.SetValue(_valueName, "dummy");
        }

        var service = new StartupService(_registryPath, _valueName);
        service.RemoveFromStartup();

        using RegistryKey? readKey = Registry.CurrentUser.OpenSubKey(_registryPath, writable: false);
        Assert.Null(readKey?.GetValue(_valueName));
        Assert.False(service.IsInStartup());
    }

    [Fact]
    public void ValidateAndUpdateStartupPath_RewritesMismatchedValue()
    {
        EnsureRegistryKeyExists();
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(_registryPath, writable: true))
        {
            Assert.NotNull(key);
            key!.SetValue(_valueName, "\"C:\\path\\to\\missing.exe\" -startup");
        }

        var service = new StartupService(_registryPath, _valueName);
        service.ValidateAndUpdateStartupPath();

        using RegistryKey? readKey = Registry.CurrentUser.OpenSubKey(_registryPath, writable: false);
        string? updated = readKey?.GetValue(_valueName)?.ToString();

        Assert.False(string.IsNullOrWhiteSpace(updated));
        Assert.Contains("-startup", updated, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing.exe", updated, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsInStartupWithValidPath_EmitsCorrelatedProbeLogs()
    {
        EnsureRegistryKeyExists();
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(_registryPath, writable: true))
        {
            Assert.NotNull(key);
            key!.SetValue(_valueName, "\"C:\\path\\to\\missing.exe\" -startup");
        }

        using var logger = Logger.CreateInMemoryForTests("startup-service-validity.log");
        logger.MinimumLevel = LogLevel.Trace;

        var service = new StartupService(_registryPath, _valueName);
        SetLogger(service, logger);

        bool valid = service.IsInStartupWithValidPath("startup-registry:test-validity");

        string logText = logger.DisposeAndReadLogTextForTests();

        Assert.False(valid);
        Assert.Contains($"{AppConstants.Audio.LogEvents.Startup.IsInStartupValidPath} | opId=startup-registry:test-validity result=false reason=path-mismatch", logText, StringComparison.Ordinal);
        Assert.DoesNotContain(AppConstants.Audio.LogEvents.Startup.IsInStartupValidPathValues, logText, StringComparison.Ordinal);
    }

    [Fact]
    public void AddToStartup_EmitsCorrelatedLifecycleLogs()
    {
        EnsureRegistryKeyExists();
        using var logger = Logger.CreateInMemoryForTests("startup-service-add.log");
        logger.MinimumLevel = LogLevel.Trace;

        var service = new StartupService(_registryPath, _valueName);
        SetLogger(service, logger);

        service.AddToStartup("startup-registry:test-add");

        string logText = logger.DisposeAndReadLogTextForTests();

        Assert.Contains("add-startup-start | opId=startup-registry:test-add", logText, StringComparison.Ordinal);
        Assert.Contains($"{AppConstants.Audio.LogEvents.Startup.AddStartupPath} | opId=startup-registry:test-add", logText, StringComparison.Ordinal);
        Assert.Contains("opId=startup-registry:test-add", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAndUpdateStartupPath_EmitsCorrelatedLifecycleLogs()
    {
        EnsureRegistryKeyExists();
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(_registryPath, writable: true))
        {
            Assert.NotNull(key);
            key!.SetValue(_valueName, "\"C:\\path\\to\\missing.exe\" -startup");
        }

        using var logger = Logger.CreateInMemoryForTests("startup-service-validate.log");
        logger.MinimumLevel = LogLevel.Trace;

        var service = new StartupService(_registryPath, _valueName);
        SetLogger(service, logger);

        service.ValidateAndUpdateStartupPath("startup-registry:test-validate");

        string logText = logger.DisposeAndReadLogTextForTests();

        Assert.Contains("validate-startup-path-update | opId=startup-registry:test-validate", logText, StringComparison.Ordinal);
        Assert.Contains($"{AppConstants.Audio.LogEvents.Startup.ValidateStartupPathValues} | opId=startup-registry:test-validate", logText, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_registryPath, throwOnMissingSubKey: false);
        }
        catch
        {
        }
    }

    private void EnsureRegistryKeyExists()
    {
        using RegistryKey? key = Registry.CurrentUser.CreateSubKey(_registryPath, writable: true);
        Assert.NotNull(key);
    }

    private static void SetLogger(StartupService service, Logger logger)
    {
        typeof(StartupService)
            .GetField("_logger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(service, logger);
    }

}

