using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;
using Microsoft.Win32;

namespace AudioPilot.Services.Configuration
{
    public class StartupService
    {
        private readonly Logger _logger;
        private readonly string _startupRegistryPath;
        private readonly string _startupValueName;

        public StartupService()
            : this(AppConstants.Registry.StartupPath, AppConstants.Identity.AppName)
        {
        }

        internal StartupService(string startupRegistryPath, string startupValueName)
            : this(startupRegistryPath, startupValueName, logger: null)
        {
        }

        internal StartupService(string startupRegistryPath, string startupValueName, Logger? logger)
        {
            _logger = logger ?? Logger.Instance;
            _startupRegistryPath = startupRegistryPath;
            _startupValueName = startupValueName;
        }

        public void AddToStartup(string? startupRegistryOpId = null)
        {
            string opId = string.IsNullOrWhiteSpace(startupRegistryOpId)
                ? $"startup-registry:{Guid.NewGuid():N}"
                : startupRegistryOpId;
            string exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? Environment.ProcessPath
                ?? string.Empty;

            _logger.Info("StartupService", () => $"add-startup-start | opId={opId}");
            _logger.Trace("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.AddStartupPath} | opId={opId} exeFile={GetFileNameForLog(exePath)}");

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    _startupRegistryPath, writable: true);
                string expectedValue = $"\"{exePath}\" -startup";
                object? currentValue = key?.GetValue(_startupValueName);

                if (currentValue == null)
                {
                    key?.SetValue(_startupValueName, expectedValue);
                    _logger.Info("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.AddStartupSuccess} | opId={opId} mode=create");
                    _logger.Trace("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.AddStartupValue} | opId={opId} {DescribeStartupValueForLog(expectedValue)}");
                }
                else if (currentValue.ToString() != expectedValue)
                {
                    _logger.Info("StartupService", () => $"add-startup-update | opId={opId}");
                    _logger.Trace("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.AddStartupUpdateValues} | opId={opId} old={DescribeStartupValueForLog(currentValue?.ToString())} new={DescribeStartupValueForLog(expectedValue)}");
                    key?.SetValue(_startupValueName, expectedValue);
                }
                else
                {
                    _logger.Debug("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.AddStartupSkip} | opId={opId} reason=already-matching");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Error("StartupService", "Access denied when adding to startup registry", nameof(AddToStartup), ex);
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("StartupService", "Failed to add to startup registry", nameof(AddToStartup), ex);
                throw;
            }
        }

        public void RemoveFromStartup(string? startupRegistryOpId = null)
        {
            string opId = string.IsNullOrWhiteSpace(startupRegistryOpId)
                ? $"startup-registry:{Guid.NewGuid():N}"
                : startupRegistryOpId;
            _logger.Info("StartupService", () => $"remove-startup-start | opId={opId}");

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    _startupRegistryPath, writable: true);

                if (key?.GetValue(_startupValueName) != null)
                {
                    key?.DeleteValue(_startupValueName, false);
                    _logger.Info("StartupService", () => $"remove-startup-success | opId={opId}");
                }
                else
                {
                    _logger.Debug("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.RemoveStartupSkip} | opId={opId} reason=entry-missing");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Error("StartupService", "Access denied when removing from startup registry", nameof(RemoveFromStartup), ex);
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("StartupService", "Failed to remove from startup registry", nameof(RemoveFromStartup), ex);
                throw;
            }
        }

        public bool IsInStartup(string? _startupRegistryOpId = null)
        {
            try
            {
                _ = _startupRegistryOpId;
                using var key = Registry.CurrentUser.OpenSubKey(
                    _startupRegistryPath, writable: false);
                object? value = key?.GetValue(_startupValueName);
                return value != null;
            }
            catch (Exception ex)
            {
                _logger.Error("StartupService", "Failed to check startup registry", nameof(IsInStartup), ex);
                return false;
            }
        }

        public bool IsInStartupWithValidPath(string? startupRegistryOpId = null)
        {
            try
            {
                string opIdPrefix = FormatStartupRegistryOpIdPrefix(startupRegistryOpId);
                using var key = Registry.CurrentUser.OpenSubKey(
                    _startupRegistryPath, writable: false);
                object? value = key?.GetValue(_startupValueName);
                if (value == null)
                {
                    _logger.Debug("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.IsInStartupValidPath} | {opIdPrefix}result=false reason=entry-missing");
                    return false;
                }

                string registryValue = value.ToString() ?? string.Empty;
                string registryExePath = ExtractExePathFromRegistryValue(registryValue);
                string currentExePath = Process.GetCurrentProcess().MainModule?.FileName
                    ?? Environment.ProcessPath
                    ?? string.Empty;

                bool pathMatches = string.Equals(
                    System.IO.Path.GetFullPath(registryExePath),
                    System.IO.Path.GetFullPath(currentExePath),
                    StringComparison.OrdinalIgnoreCase);

                if (pathMatches)
                {
                    return true;
                }
                else
                {
                    _logger.Warning("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.IsInStartupValidPath} | {opIdPrefix}result=false reason=path-mismatch");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("StartupService", "Failed to check startup registry with path validation", nameof(IsInStartupWithValidPath), ex);
                return false;
            }
        }

        private static string ExtractExePathFromRegistryValue(string registryValue)
        {
            string trimmed = registryValue.Trim();

            if (trimmed.StartsWith('"'))
            {
                int endQuote = trimmed.IndexOf('\"', 1);
                if (endQuote > 0)
                {
                    return trimmed[1..endQuote];
                }
            }

            int spaceIndex = trimmed.IndexOf(' ');
            if (spaceIndex > 0)
            {
                return trimmed[..spaceIndex];
            }

            return trimmed;
        }

        public void ValidateAndUpdateStartupPath(string? startupRegistryOpId = null)
        {
            try
            {
                string opId = string.IsNullOrWhiteSpace(startupRegistryOpId)
                    ? $"startup-registry:{Guid.NewGuid():N}"
                    : startupRegistryOpId;
                string exePath = Process.GetCurrentProcess().MainModule?.FileName
                    ?? Environment.ProcessPath
                    ?? string.Empty;

                using var key = Registry.CurrentUser.OpenSubKey(
                    _startupRegistryPath, writable: true);
                {
                    if (key != null)
                    {
                        object? currentValue = key.GetValue(_startupValueName);
                        if (currentValue != null)
                        {
                            string expectedValue = $"\"{exePath}\" -startup";
                            string existingValue = currentValue.ToString() ?? string.Empty;
                            if (existingValue != expectedValue)
                            {
                                _logger.Info("StartupService", () => $"validate-startup-path-update | opId={opId}");
                                _logger.Trace("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.ValidateStartupPathValues} | opId={opId} old={DescribeStartupValueForLog(existingValue)} new={DescribeStartupValueForLog(expectedValue)}");
                                key.SetValue(_startupValueName, expectedValue);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("StartupService", "Failed to validate or update startup path", nameof(ValidateAndUpdateStartupPath), ex);
            }
        }

        public void RemoveIfPresent(string? startupRegistryOpId = null)
        {
            if (IsInStartup(startupRegistryOpId))
            {
                string opId = string.IsNullOrWhiteSpace(startupRegistryOpId)
                    ? $"startup-registry:{Guid.NewGuid():N}"
                    : startupRegistryOpId;
                _logger.Info("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.RemoveIfPresent} | opId={opId} action=remove");
                RemoveFromStartup(opId);
            }
        }

        private static string GetFileNameForLog(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "<empty>";
            }

            return System.IO.Path.GetFileName(path);
        }

        private static string DescribeStartupValueForLog(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "valueState=empty";
            }

            string trimmed = value.Trim();
            bool hasStartupArg = trimmed.Contains("-startup", StringComparison.OrdinalIgnoreCase);
            return $"valueState=present length={trimmed.Length} hasStartupArg={hasStartupArg}";
        }

        private static string FormatStartupRegistryOpIdPrefix(string? startupRegistryOpId)
        {
            return string.IsNullOrWhiteSpace(startupRegistryOpId)
                ? string.Empty
                : $"opId={startupRegistryOpId} ";
        }
    }
}
