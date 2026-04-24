using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Platform
{
    public static partial class AudioDeviceHelper
    {
        public static string GetSessionDisplayName(AudioSessionControl session, Process? process)
        {
            string displayName = session.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = GetFriendlyProcessName(process);

            if (string.IsNullOrWhiteSpace(displayName))
                return "Unknown";

            return CapitalizeName(displayName);
        }

        public static string GetSessionDisplayNameFromCache(
            string processName,
            string? cachedDisplayName)
        {
            string displayName = !string.IsNullOrWhiteSpace(cachedDisplayName)
                ? cachedDisplayName
                : SanitizeProcessName(processName);

            if (string.IsNullOrWhiteSpace(displayName))
                return "Unknown";

            return CapitalizeName(displayName);
        }

        internal static bool IsKnownHelperProcessName(string processName)
        {
            return !string.IsNullOrWhiteSpace(processName)
                && KnownHelperCache.Contains(processName);
        }

        internal static bool IsIgnoredProcessName(string processName)
        {
            return !string.IsNullOrWhiteSpace(processName)
                && IgnoredProcessCache.Contains(processName);
        }

        public static bool ShouldIgnoreSession(ILogger logger, string displayName, Process? process)
        {
            if (displayName.Contains("@%SystemRoot%", StringComparison.OrdinalIgnoreCase))
            {
                logger.Trace("AudioDeviceService", () => $"  Skipping system sounds: {LogPrivacy.Session(displayName)}");
                return true;
            }

            if (process != null && IsIgnoredProcessName(process.ProcessName))
            {
                logger.Trace(
                    "AudioDeviceService",
                    () => $"  Skipping ignored process: process={LogPrivacy.Process(process.ProcessName)} display={LogPrivacy.Session(displayName)}");
                return true;
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                logger.Trace("AudioDeviceService", "  Skipping session with empty display name");
                return true;
            }

            return false;
        }

        public static bool ShouldIgnoreSessionFromCache(string displayName, string processName)
        {
            return displayName.Contains("@%SystemRoot%", StringComparison.OrdinalIgnoreCase)
                || IsIgnoredProcessName(processName)
                || string.IsNullOrWhiteSpace(displayName);
        }

        public static bool IsSessionValid(ILogger logger, AudioSessionControl session)
        {
            if (session == null) return false;
            try
            {
                _ = session.GetProcessID;
                return true;
            }
            catch (Exception ex)
            {
                logger.Trace("AudioDeviceService", () => $"  Session validation failed: {ex.GetType().Name}");
                return false;
            }
        }

        public static bool TryGetSessionVolume(
            ILogger logger,
            AudioSessionControl session,
            out float volume)
        {
            volume = 0f;
            if (session == null) return false;

            if (!IsSessionValid(logger, session))
                return false;

            try
            {
                volume = session.SimpleAudioVolume.Volume;
                return true;
            }
            catch (Exception ex)
            {
                logger.Trace(
                    "AudioDeviceService",
                    () => $"TryGetSessionVolume failed: {ex.GetType().Name}");
                return false;
            }
        }

        public static bool TryGetSessionVolumeAndMute(
            ILogger logger,
            AudioSessionControl session,
            out float volume,
            out bool isMuted)
        {
            volume = 0f;
            isMuted = false;
            if (session == null) return false;

            if (!IsSessionValid(logger, session))
                return false;

            try
            {
                var simpleAudioVolume = session.SimpleAudioVolume;
                volume = simpleAudioVolume.Volume;
                isMuted = simpleAudioVolume.Mute;
                return true;
            }
            catch (Exception ex)
            {
                logger.Trace(
                    "AudioDeviceService",
                    () => $"TryGetSessionVolumeAndMute failed: {ex.GetType().Name}");
                return false;
            }
        }

        public static bool TrySetSessionVolume(
            ILogger logger,
            AudioSessionControl session,
            float volume)
        {
            if (!IsSessionValid(logger, session))
                return false;

            try
            {
                var scalar = Math.Clamp(volume, 0f, 1f);
                session.SimpleAudioVolume.Volume = scalar;
                return true;
            }
            catch (Exception ex)
            {
                logger.Trace(
                    "AudioDeviceService",
                    () => $"TrySetSessionVolume failed: {ex.GetType().Name}");
                return false;
            }
        }

        public static bool TrySetSessionMute(
            ILogger logger,
            AudioSessionControl session,
            bool isMuted)
        {
            if (!IsSessionValid(logger, session))
                return false;

            try
            {
                session.SimpleAudioVolume.Mute = isMuted;
                return true;
            }
            catch (Exception ex)
            {
                logger.Trace(
                    "AudioDeviceService",
                    () => $"TrySetSessionMute failed: {ex.GetType().Name}");
                return false;
            }
        }

        public static bool TryGetSessionMute(
            ILogger logger,
            AudioSessionControl session,
            out bool isMuted)
        {
            isMuted = false;
            if (session == null) return false;

            if (!IsSessionValid(logger, session))
                return false;

            try
            {
                isMuted = session.SimpleAudioVolume.Mute;
                return true;
            }
            catch (Exception ex)
            {
                logger.Trace(
                    "AudioDeviceService",
                    () => $"TryGetSessionMute failed: {ex.GetType().Name}");
                return false;
            }
        }

        public static AudioSessionControl? FindSystemSoundsSession(
            AudioDeviceService audioService)
        {
            Logger logger = Logger.Instance;
            MMDevice? defaultDevice = null;
            try
            {
                defaultDevice = audioService.GetDefaultPlaybackDevice();
                if (defaultDevice?.AudioSessionManager == null)
                    return null;

                var sessions = defaultDevice.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session != null)
                        {
                            if (session.GetProcessID == 0)
                                return session;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            logger.Trace("AudioDeviceService", () => $"FindSystemSoundsSession skipped session index {i}: {ex.GetType().Name}");
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.Trace("AudioDeviceService", () => $"FindSystemSoundsSession failed: {ex.GetType().Name}");
                }
                return null;
            }
            finally
            {
                defaultDevice?.Dispose();
            }
        }

        /// <summary>
        /// Finds a session for a process id across active playback devices.
        /// </summary>
        /// <remarks>
        /// Returns the first valid match and prefers sessions whose display name matches the expected name when
        /// provided.
        /// </remarks>
        public static AudioSessionControl? FindSessionByPid(
            AudioDeviceService audioService,
            uint pid,
            string? expectedDisplayName,
            ConcurrentDictionary<uint, string> pidToProcessName,
            Logger logger)
        {
            AudioSessionControl? firstMatch = null;
            try
            {
                var activeDevices = audioService.GetActivePlaybackDevices();
                if (activeDevices == null || activeDevices.Count == 0)
                    return null;

                for (int di = 0; di < activeDevices.Count; di++)
                {
                    var device = activeDevices[di];
                    try
                    {
                        if (device?.AudioSessionManager == null)
                            continue;

                        var sessions = device.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            AudioSessionControl? session = null;
                            try
                            {
                                session = sessions[i];
                                if (session == null) continue;

                                if (session.GetProcessID != pid)
                                    continue;

                                if (!TryGetSessionVolume(logger, session, out _))
                                    continue;

                                string? displayName = session.DisplayName;
                                if (!string.IsNullOrWhiteSpace(displayName))
                                    pidToProcessName[pid] = displayName;

                                if (firstMatch == null)
                                {
                                    firstMatch = session;
                                    session = null;
                                }

                                if (!string.IsNullOrWhiteSpace(expectedDisplayName) &&
                                    !string.IsNullOrWhiteSpace(displayName) &&
                                    displayName.Equals(expectedDisplayName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return firstMatch;
                                }
                            }
                            catch (Exception ex)
                            {
                                if (logger.IsEnabled(LogLevel.Trace))
                                {
                                    logger.Trace("AudioDeviceService", () => $"FindSessionByPid skipped session index {i}: {ex.GetType().Name}");
                                }
                            }
                            finally
                            {
                                session?.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        device?.Dispose();
                    }
                }

                return firstMatch;
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.Trace("AudioDeviceService", () => $"FindSessionByPid failed for pid={LogPrivacy.Id(pid.ToString())} reason={ex.GetType().Name}");
                }
                return null;
            }
        }

        /// <summary>
        /// Applies a volume value to all valid sessions that belong to a process id.
        /// </summary>
        public static int SetVolumeForSessionsByPid(
            AudioDeviceService audioService,
            uint pid,
            float scalar,
            ConcurrentDictionary<uint, string> pidToProcessName,
            Logger logger)
        {
            return SetVolumeForSessionsByPid(audioService, pid, scalar, pidToProcessName, logger, DataFlow.Render);
        }

        public static int SetVolumeForSessionsByPid(
            AudioDeviceService audioService,
            uint pid,
            float scalar,
            ConcurrentDictionary<uint, string> pidToProcessName,
            Logger logger,
            DataFlow dataFlow)
        {
            int updated = 0;

            try
            {
                var activeDevices = dataFlow == DataFlow.Capture
                    ? audioService.GetActiveCaptureDevices()
                    : audioService.GetActivePlaybackDevices();
                if (activeDevices == null || activeDevices.Count == 0)
                    return 0;

                for (int di = 0; di < activeDevices.Count; di++)
                {
                    var device = activeDevices[di];
                    try
                    {
                        if (device?.AudioSessionManager == null)
                            continue;

                        var sessions = device.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            AudioSessionControl? session = null;
                            try
                            {
                                session = sessions[i];
                                if (session == null) continue;

                                if (session.GetProcessID != pid)
                                    continue;

                                if (!IsSessionValid(logger, session))
                                    continue;

                                string? displayName = session.DisplayName;
                                if (!string.IsNullOrWhiteSpace(displayName))
                                    pidToProcessName[pid] = displayName;

                                if (TrySetSessionVolume(logger, session, scalar))
                                {
                                    updated++;
                                }
                            }
                            catch (Exception ex)
                            {
                                if (logger.IsEnabled(LogLevel.Trace))
                                {
                                    logger.Trace("AudioDeviceService", () => $"SetVolumeForSessionsByPid skipped session index {i}: {ex.GetType().Name}");
                                }
                            }
                            finally
                            {
                                session?.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        device?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.Trace("AudioDeviceService", () => $"SetVolumeForSessionsByPid failed for pid={LogPrivacy.Id(pid.ToString())} flow={dataFlow} reason={ex.GetType().Name}");
                }
                return updated;
            }

            return updated;
        }

        /// <summary>
        /// Applies volume to sessions identified as system sounds (PID 0 or system-sounds naming patterns).
        /// </summary>
        public static int SetVolumeForSystemSounds(
            AudioDeviceService audioService,
            float scalar,
            Logger logger)
        {
            int updated = 0;

            try
            {
                var activeDevices = audioService.GetActivePlaybackDevices();
                if (activeDevices == null || activeDevices.Count == 0)
                    return 0;

                for (int di = 0; di < activeDevices.Count; di++)
                {
                    var device = activeDevices[di];
                    try
                    {
                        if (device?.AudioSessionManager == null)
                            continue;

                        var sessions = device.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            AudioSessionControl? session = null;
                            try
                            {
                                session = sessions[i];
                                if (session == null)
                                    continue;

                                bool isSystemByPid = false;
                                try
                                {
                                    isSystemByPid = session.GetProcessID == 0;
                                }
                                catch
                                {
                                }

                                bool isSystemByName = false;
                                try
                                {
                                    string? displayName = session.DisplayName;
                                    isSystemByName =
                                        string.Equals(displayName, "System Sounds", StringComparison.OrdinalIgnoreCase) ||
                                        (displayName?.Contains("@%SystemRoot%", StringComparison.OrdinalIgnoreCase) == true);
                                }
                                catch
                                {
                                }

                                if (!isSystemByPid && !isSystemByName)
                                    continue;

                                float clamped = Math.Clamp(scalar, 0f, 1f);
                                try
                                {
                                    session.SimpleAudioVolume.Volume = clamped;

                                    updated++;
                                }
                                catch (Exception ex)
                                {
                                    logger.Trace("AudioDeviceService", () => $"SetVolumeForSystemSounds failed: {ex.GetType().Name}");
                                }
                            }
                            catch
                            {
                            }
                            finally
                            {
                                session?.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        device?.Dispose();
                    }
                }
            }
            catch
            {
                return updated;
            }

            return updated;
        }

        public static int SetMuteForSessionsByPid(
            AudioDeviceService audioService,
            uint pid,
            bool isMuted,
            ConcurrentDictionary<uint, string> pidToProcessName,
            Logger logger,
            DataFlow dataFlow)
        {
            int updated = 0;

            try
            {
                var activeDevices = dataFlow == DataFlow.Capture
                    ? audioService.GetActiveCaptureDevices()
                    : audioService.GetActivePlaybackDevices();
                if (activeDevices == null || activeDevices.Count == 0)
                    return 0;

                for (int di = 0; di < activeDevices.Count; di++)
                {
                    var device = activeDevices[di];
                    try
                    {
                        if (device?.AudioSessionManager == null)
                            continue;

                        var sessions = device.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            AudioSessionControl? session = null;
                            try
                            {
                                session = sessions[i];
                                if (session == null)
                                    continue;

                                if (session.GetProcessID != pid)
                                    continue;

                                if (!IsSessionValid(logger, session))
                                    continue;

                                string? displayName = session.DisplayName;
                                if (!string.IsNullOrWhiteSpace(displayName))
                                    pidToProcessName[pid] = displayName;

                                if (TrySetSessionMute(logger, session, isMuted))
                                {
                                    updated++;
                                }
                            }
                            catch (Exception ex)
                            {
                                if (logger.IsEnabled(LogLevel.Trace))
                                {
                                    logger.Trace("AudioDeviceService", () => $"SetMuteForSessionsByPid skipped session index {i}: {ex.GetType().Name}");
                                }
                            }
                            finally
                            {
                                session?.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        device?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.Trace("AudioDeviceService", () => $"SetMuteForSessionsByPid failed for pid={LogPrivacy.Id(pid.ToString())} flow={dataFlow} reason={ex.GetType().Name}");
                }

                return updated;
            }

            return updated;
        }

        public static int SetMuteForSystemSounds(
            AudioDeviceService audioService,
            bool isMuted,
            Logger logger)
        {
            int updated = 0;

            try
            {
                var activeDevices = audioService.GetActivePlaybackDevices();
                if (activeDevices == null || activeDevices.Count == 0)
                    return 0;

                for (int di = 0; di < activeDevices.Count; di++)
                {
                    var device = activeDevices[di];
                    try
                    {
                        if (device?.AudioSessionManager == null)
                            continue;

                        var sessions = device.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            AudioSessionControl? session = null;
                            try
                            {
                                session = sessions[i];
                                if (session == null)
                                    continue;

                                bool isSystemByPid = false;
                                try
                                {
                                    isSystemByPid = session.GetProcessID == 0;
                                }
                                catch
                                {
                                }

                                bool isSystemByName = false;
                                try
                                {
                                    string? displayName = session.DisplayName;
                                    isSystemByName =
                                        string.Equals(displayName, "System Sounds", StringComparison.OrdinalIgnoreCase) ||
                                        (displayName?.Contains("@%SystemRoot%", StringComparison.OrdinalIgnoreCase) == true);
                                }
                                catch
                                {
                                }

                                if (!isSystemByPid && !isSystemByName)
                                    continue;

                                if (TrySetSessionMute(logger, session, isMuted))
                                {
                                    updated++;
                                }
                            }
                            catch (Exception ex)
                            {
                                if (logger.IsEnabled(LogLevel.Trace))
                                {
                                    logger.Trace("AudioDeviceService", () => $"SetMuteForSystemSounds skipped session index {i}: {ex.GetType().Name}");
                                }
                            }
                            finally
                            {
                                session?.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        device?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.Trace("AudioDeviceService", () => $"SetMuteForSystemSounds failed: {ex.GetType().Name}");
                }

                return updated;
            }

            return updated;
        }

        public static Guid? TryExtractGuid(string naudioId)
        {
            int idx = naudioId.LastIndexOf("}.{", StringComparison.Ordinal);
            if (idx >= 0)
            {
                string guidPart = naudioId[(idx + 2)..].Trim('{', '}');
                if (Guid.TryParse(guidPart, out var g))
                    return g;
            }

            string trimmed = naudioId.Trim('{', '}');
            if (Guid.TryParse(trimmed, out var g2))
                return g2;

            return null;
        }

        public static bool TryGetEndpointVolume(
            ILogger logger,
            MMDevice device,
            [NotNullWhen(true)] out AudioEndpointVolume? volume)
        {
            return TryGetEndpointVolume(logger, device, out volume, reason: null);
        }

        public static bool TryGetEndpointVolume(
            ILogger logger,
            MMDevice device,
            [NotNullWhen(true)] out AudioEndpointVolume? volume,
            string? reason)
        {
            volume = null;
            if (device == null)
            {
                logger.Trace("AudioDeviceHelper", "TryGetEndpointVolume called with null device");
                return false;
            }

            string deviceLabel = LogPrivacy.Device(device.FriendlyName);
            string logReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;

            try
            {
                volume = device.AudioEndpointVolume;
                if (volume != null)
                {
                    return true;
                }
                logger.Warning(
                    "AudioDeviceHelper",
                    $"{deviceLabel} returned null AudioEndpointVolume | reason={logReason}");
                return false;
            }
            catch (InvalidCastException)
            {
                logger.Trace(
                    "AudioDeviceHelper",
                    $"{deviceLabel} does not support IAudioEndpointVolume interface | reason={logReason}");
                return false;
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80004002))
            {
                logger.Trace(
                    "AudioDeviceHelper",
                    $"{deviceLabel} returns E_NOINTERFACE for IAudioEndpointVolume | reason={logReason}");
                return false;
            }
            catch (COMException ex)
            {
                logger.Error(
                    "AudioDeviceHelper",
                    $"COM exception for {deviceLabel}: 0x{ex.HResult:X8} - {ex.GetType().Name} | reason={logReason}",
                    nameof(TryGetEndpointVolume),
                    ex);
                return false;
            }
            catch (Exception ex)
            {
                logger.Error(
                    "AudioDeviceHelper",
                    $"Unexpected exception for {deviceLabel}: {ex.GetType().Name} | reason={logReason}",
                    nameof(TryGetEndpointVolume),
                    ex);
                return false;
            }
        }
    }
}
