using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using AudioPilot.Constants;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace AudioPilot.Platform
{
    public static partial class AudioDeviceHelper
    {
        public readonly record struct PackagedAppIdentity(string DisplayName, string AppUserModelId, string PackageFamilyName, string AppId);
        public readonly record struct VisibleWindowMetadata(string Title, string ClassName);
        public readonly record struct VisibleWindowHandleMetadata(nint WindowHandle, int ProcessId, string Title, string ClassName);

        [LibraryImport("ntdll.dll")]
        private static partial int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation,
            int processInformationLength,
            out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public int BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        private const int MAX_PARENT_DEPTH = 20;

        private static readonly NativePropertyKey PKEY_AppUserModel_ID = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

        [LibraryImport("shell32.dll")]
        private static partial int SHGetPropertyStoreForWindow(
            IntPtr hwnd,
            ref Guid riid,
            out IntPtr ppv);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

        [LibraryImport("user32.dll")]
        private static partial int GetWindowTextLengthA(IntPtr hWnd);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
        private static partial int GetWindowTextLength(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private static readonly FrozenSet<string> KnownHelperCache =
            AppConstants.Audio.KnownHelperProcesses.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        private static readonly FrozenSet<string> IgnoredProcessCache =
            AppConstants.Audio.IgnoredProcesses.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        private static readonly FrozenDictionary<int, string> HResultLookup =
            new Dictionary<int, string>
            {
                { unchecked((int)0x80070490), "Element not found (ERROR_NOT_FOUND)" },
                { unchecked((int)0x80070005), "Access denied (E_ACCESSDENIED)" },
                { unchecked((int)0x8007000E), "Out of memory (E_OUTOFMEMORY)" },
                { unchecked((int)0x800706BA), "RPC server unavailable (RPC_S_SERVER_UNAVAILABLE)" },
                { unchecked((int)0x800706BF), "RPC call failed (RPC_S_CALL_FAILED)" },
                { unchecked((int)0x800401F0), "Not initialized (CO_E_NOTINITIALIZED)" },
                { unchecked((int)0x800401F3), "Invalid class string (REGDB_E_CLASSNOTREG)" },
                { unchecked((int)0x800401F5), "Object not registered (CO_E_OBJNOTREG)" },
                { unchecked((int)0x800704A6), "Device not connected (ERROR_DEVICE_NOT_CONNECTED)" },
                { unchecked((int)0x8007045B), "System shutdown (ERROR_SHUTDOWN_IN_PROGRESS)" },
                { unchecked((int)0x80040201), "Invalid stream (AUDCLNT_E_INVALID_STREAM_FLAG)" },
                { unchecked((int)0x88890003), "Device in use (AUDCLNT_E_DEVICE_IN_USE)" },
                { unchecked((int)0x88890004), "Engine already initialized (AUDCLNT_E_ENGINE_ALREADY_INITIALIZED)" },
                { unchecked((int)0x88890006), "Engine not initialized (AUDCLNT_E_ENGINE_NOT_INITIALIZED)" },
                { unchecked((int)0x8889000A), "Incorrect buffer size (AUDCLNT_E_INCORRECT_BUFFER_SIZE)" },
                { unchecked((int)0x8889000F), "CPU usage exhausted (AUDCLNT_E_CPUUSAGE_EXCEEDED)" }
            }.ToFrozenDictionary();

        private readonly record struct CacheEntry(string? Value, long Expiry);
        private readonly record struct ProcessNameResolution(string? Name, ProcessNameResolutionSource Source);

        private enum ProcessNameResolutionSource
        {
            Window,
            FileDescription,
            SanitizedProcessName,
        }

        private static readonly long CacheExpiryTicks =
            (long)TimeSpan.FromMinutes(AppConstants.Timing.CacheEntryExpiryMinutes).TotalMilliseconds;

        private static readonly long WindowPidMapCacheDuration =
            (long)TimeSpan.FromSeconds(AppConstants.Timing.SessionCacheShortTtlSeconds).TotalMilliseconds;
        private static readonly long PackagedAppInventoryCacheDuration =
            (long)TimeSpan.FromMinutes(AppConstants.Timing.PackagedAppInventoryCacheMinutes).TotalMilliseconds;
        private const int MaxWindowPidMapCacheEntries = AppConstants.Limits.MaxPidProcessMapEntries;
        private const int MaxPackagedAppInventoryCacheEntries = AppConstants.Limits.MaxPackagedAppInventoryEntries;
        private const int MaxFileDescriptionCacheEntries = AppConstants.Limits.MaxFileDescriptionCacheEntries;
        private const int MaxAumidCacheEntries = AppConstants.Limits.MaxAumidCacheEntries;
        private const int CacheTrimIntervalMask = 0x3F;

        private static ConcurrentDictionary<int, CacheEntry> FileDescriptionCache = new();
        private static ConcurrentDictionary<string, CacheEntry> AumidCache = new();
        private static ConcurrentDictionary<IntPtr, uint>? WindowPidMapCache;
        private static IReadOnlyList<PackagedAppIdentity>? PackagedAppInventoryCache;
        private static long WindowPidMapCacheExpiry;
        private static long PackagedAppInventoryCacheExpiry;
        private static int CacheTrimCounter;

        internal static IReadOnlyDictionary<IntPtr, uint>? WindowPidMapCacheForTests => WindowPidMapCache;
        internal static long WindowPidMapCacheExpiryForTests => Volatile.Read(ref WindowPidMapCacheExpiry);
        internal static IReadOnlyList<PackagedAppIdentity>? PackagedAppInventoryCacheForTests => PackagedAppInventoryCache;
        internal static long PackagedAppInventoryCacheExpiryForTests => Volatile.Read(ref PackagedAppInventoryCacheExpiry);

        internal static void SeedFileDescriptionCacheForTests(int pid, string? value, long expiry)
        {
            FileDescriptionCache[pid] = new CacheEntry(value, expiry);
        }

        internal static bool TryGetFileDescriptionCacheEntryForTests(int pid, out string? value, out long expiry)
        {
            if (FileDescriptionCache.TryGetValue(pid, out CacheEntry entry))
            {
                value = entry.Value;
                expiry = entry.Expiry;
                return true;
            }

            value = null;
            expiry = 0;
            return false;
        }

        internal static void SeedAumidCacheForTests(string aumid, string? value, long expiry)
        {
            AumidCache[aumid] = new CacheEntry(value, expiry);
        }

        internal static bool TryGetAumidCacheEntryForTests(string aumid, out string? value, out long expiry)
        {
            if (AumidCache.TryGetValue(aumid, out CacheEntry entry))
            {
                value = entry.Value;
                expiry = entry.Expiry;
                return true;
            }

            value = null;
            expiry = 0;
            return false;
        }

        internal static void SeedWindowPidMapCacheForTests(IReadOnlyDictionary<IntPtr, uint> cache, long expiry)
        {
            WindowPidMapCache = new ConcurrentDictionary<IntPtr, uint>(cache);
            Volatile.Write(ref WindowPidMapCacheExpiry, expiry);
        }

        internal static void SeedPackagedAppInventoryCacheForTests(IReadOnlyList<PackagedAppIdentity> cache, long expiry)
        {
            PackagedAppInventoryCache = cache;
            Volatile.Write(ref PackagedAppInventoryCacheExpiry, expiry);
        }

        private static void Trace(string message)
        {
            var logger = Logger.Instance;
            if (!logger.IsEnabled(LogLevel.Trace))
                return;

            logger.Trace("AudioDeviceHelper", message);
        }

        private static void Trace(Func<string> messageFactory)
        {
            var logger = Logger.Instance;
            if (!logger.IsEnabled(LogLevel.Trace))
                return;

            logger.Trace("AudioDeviceHelper", messageFactory());
        }

        private static void StoreCacheEntry<TKey>(ConcurrentDictionary<TKey, CacheEntry> cache, TKey key, string? value, long now)
            where TKey : notnull
        {
            cache[key] = new CacheEntry(value, now + CacheExpiryTicks);
            TrimCachesIfNeeded(now);
        }

        private static bool TryGetCachedValue<TKey>(ConcurrentDictionary<TKey, CacheEntry> cache, TKey key, long now, out string? value)
            where TKey : notnull
        {
            if (cache.TryGetValue(key, out CacheEntry cached) && cached.Expiry > now)
            {
                value = cached.Value;
                return true;
            }

            cache.TryRemove(key, out _);
            value = null;
            return false;
        }

        private static string? StoreCachedLookupResult<TKey>(ConcurrentDictionary<TKey, CacheEntry> cache, TKey key, string? value, long now)
            where TKey : notnull
        {
            StoreCacheEntry(cache, key, value, now);
            return value;
        }

        private static bool TryGetExpiringSnapshot<T>(T? cachedValue, long expiry, long now, [NotNullWhen(true)] out T? value)
            where T : class
        {
            if (cachedValue != null && expiry > now)
            {
                value = cachedValue;
                return true;
            }

            value = null;
            return false;
        }

        private static T PublishExpiringSnapshot<T>(ref T? cache, ref long expiry, T value, long duration, long now)
            where T : class
        {
            Interlocked.Exchange(ref cache, value);
            Interlocked.Exchange(ref expiry, now + duration);
            return value;
        }

        public static void ClearCaches()
        {
            Interlocked.Exchange(ref FileDescriptionCache, new ConcurrentDictionary<int, CacheEntry>());
            Interlocked.Exchange(ref AumidCache, new ConcurrentDictionary<string, CacheEntry>());
            Interlocked.Exchange(ref WindowPidMapCache, null);
            Interlocked.Exchange(ref PackagedAppInventoryCache, null);
            Interlocked.Exchange(ref WindowPidMapCacheExpiry, 0);
            Interlocked.Exchange(ref PackagedAppInventoryCacheExpiry, 0);
            Logger.Instance.Info("AudioDeviceHelper", "All caches cleared");
        }

        internal static void ClearPackagedAppInventoryCache()
        {
            Interlocked.Exchange(ref PackagedAppInventoryCache, null);
            Interlocked.Exchange(ref PackagedAppInventoryCacheExpiry, 0);
        }

        internal static void TrimCachesForHiddenMode()
        {
            long now = Environment.TickCount64;
            TrimCache(FileDescriptionCache, MaxFileDescriptionCacheEntries, now);
            TrimCache(AumidCache, MaxAumidCacheEntries, now);
            Interlocked.Exchange(ref WindowPidMapCache, null);
            Interlocked.Exchange(ref WindowPidMapCacheExpiry, 0);
            Interlocked.Exchange(ref PackagedAppInventoryCache, null);
            Interlocked.Exchange(ref PackagedAppInventoryCacheExpiry, 0);
        }

        public static IReadOnlyDictionary<IntPtr, uint> BuildWindowPidMap()
        {
            var now = Environment.TickCount64;
            if (TryGetWindowPidMapSnapshot(now, out ConcurrentDictionary<IntPtr, uint>? cached))
                return cached;

            var map = new ConcurrentDictionary<IntPtr, uint>();
            EnumWindows((hWnd, _) =>
            {
                uint threadId = GetWindowThreadProcessId(hWnd, out uint pid);
                if (threadId == 0 || pid == 0)
                {
                    return true;
                }

                map[hWnd] = pid;
                return true;
            }, IntPtr.Zero);

            return PublishWindowPidMapSnapshot(map, now);
        }

        private static bool TryGetWindowPidMapSnapshot(long now, [NotNullWhen(true)] out ConcurrentDictionary<IntPtr, uint>? cached)
        {
            if (!TryGetExpiringSnapshot(WindowPidMapCache, WindowPidMapCacheExpiry, now, out cached))
            {
                cached = null;
                return false;
            }

            if (cached.Count <= MaxWindowPidMapCacheEntries)
            {
                return true;
            }

            int oversizedCount = cached.Count;
            Interlocked.Exchange(ref WindowPidMapCache, null);
            Interlocked.Exchange(ref WindowPidMapCacheExpiry, 0);
            Trace($"Discarded oversized window PID cache snapshot count={oversizedCount} max={MaxWindowPidMapCacheEntries}");
            cached = null;
            return false;
        }

        private static ConcurrentDictionary<IntPtr, uint> PublishWindowPidMapSnapshot(ConcurrentDictionary<IntPtr, uint> map, long now)
        {
            if (map.Count > MaxWindowPidMapCacheEntries)
            {
                Interlocked.Exchange(ref WindowPidMapCache, null);
                Interlocked.Exchange(ref WindowPidMapCacheExpiry, 0);
                Trace(() => $"Skipped caching oversized window PID snapshot count={map.Count} max={MaxWindowPidMapCacheEntries}");
                return map;
            }

            return PublishExpiringSnapshot(ref WindowPidMapCache, ref WindowPidMapCacheExpiry, map, WindowPidMapCacheDuration, now);
        }

        public static IReadOnlyDictionary<int, IReadOnlyList<string>> GetVisibleWindowTitlesByProcessIds(IEnumerable<int>? processIds)
        {
            IReadOnlyDictionary<int, IReadOnlyList<VisibleWindowMetadata>> windowsByProcessId = GetVisibleWindowMetadataByProcessIds(processIds);

            return windowsByProcessId.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<string>)[.. pair.Value.Select(static window => window.Title)],
                EqualityComparer<int>.Default);
        }

        public static IReadOnlyDictionary<int, IReadOnlyList<VisibleWindowMetadata>> GetVisibleWindowMetadataByProcessIds(IEnumerable<int>? processIds)
        {
            if (processIds == null)
            {
                return new Dictionary<int, IReadOnlyList<VisibleWindowMetadata>>();
            }

            HashSet<uint> targetProcessIds = [.. processIds
                .Where(static processId => processId > 0)
                .Select(static processId => (uint)processId)];

            if (targetProcessIds.Count == 0)
            {
                return new Dictionary<int, IReadOnlyList<VisibleWindowMetadata>>();
            }

            IReadOnlyDictionary<IntPtr, uint> windowPidMap = BuildWindowPidMap();
            Dictionary<int, List<VisibleWindowMetadata>> windowsByProcessId = [];

            foreach ((IntPtr windowHandle, uint windowProcessId) in windowPidMap)
            {
                if (!targetProcessIds.Contains(windowProcessId) || !IsWindowVisible(windowHandle))
                {
                    continue;
                }

                string? title = TryGetWindowTitle(windowHandle);
                if (string.IsNullOrWhiteSpace(title) || IsInternalWindowTitle(title))
                {
                    continue;
                }

                string className = TryGetWindowClassName(windowHandle) ?? string.Empty;

                int processId = unchecked((int)windowProcessId);
                if (!windowsByProcessId.TryGetValue(processId, out List<VisibleWindowMetadata>? processWindows))
                {
                    processWindows = [];
                    windowsByProcessId[processId] = processWindows;
                }

                bool duplicateExists = processWindows.Any(existing =>
                    string.Equals(existing.Title, title, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.ClassName, className, StringComparison.OrdinalIgnoreCase));
                if (!duplicateExists)
                {
                    processWindows.Add(new VisibleWindowMetadata(title, className));
                }
            }

            return windowsByProcessId.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<VisibleWindowMetadata>)pair.Value,
                EqualityComparer<int>.Default);
        }

        public static IReadOnlyList<VisibleWindowHandleMetadata> GetVisibleWindowHandleMetadataByProcessIds(IEnumerable<int>? processIds)
        {
            if (processIds == null)
            {
                return [];
            }

            HashSet<uint> targetProcessIds = [..
                processIds
                    .Where(static processId => processId > 0)
                    .Select(static processId => (uint)processId)];

            if (targetProcessIds.Count == 0)
            {
                return [];
            }

            IReadOnlyDictionary<IntPtr, uint> windowPidMap = BuildWindowPidMap();
            var windows = new List<VisibleWindowHandleMetadata>();

            foreach ((IntPtr windowHandle, uint windowProcessId) in windowPidMap)
            {
                if (!targetProcessIds.Contains(windowProcessId) || !IsWindowVisible(windowHandle))
                {
                    continue;
                }

                string? title = TryGetWindowTitle(windowHandle);
                if (string.IsNullOrWhiteSpace(title) || IsInternalWindowTitle(title))
                {
                    continue;
                }

                string className = TryGetWindowClassName(windowHandle) ?? string.Empty;
                int processId = unchecked((int)windowProcessId);
                bool duplicateExists = windows.Any(existing =>
                    existing.WindowHandle == windowHandle ||
                    (existing.ProcessId == processId &&
                     string.Equals(existing.Title, title, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(existing.ClassName, className, StringComparison.OrdinalIgnoreCase)));
                if (!duplicateExists)
                {
                    windows.Add(new VisibleWindowHandleMetadata(windowHandle, processId, title, className));
                }
            }

            return windows;
        }

        private static string? TryGetWindowTitle(IntPtr windowHandle)
        {
            int titleLength = GetWindowTextLength(windowHandle);
            if (titleLength <= 0)
            {
                return null;
            }

            StringBuilder titleBuilder = new(titleLength + 1);
            int copiedLength = GetWindowText(windowHandle, titleBuilder, titleBuilder.Capacity);
            if (copiedLength <= 0)
            {
                return null;
            }

            string title = titleBuilder.ToString().Trim();
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }

        private static string? TryGetWindowClassName(IntPtr windowHandle)
        {
            char[] classBuffer = new char[256];
            int copiedLength = GetClassName(windowHandle, classBuffer, classBuffer.Length);
            if (copiedLength <= 0)
            {
                return null;
            }

            string className = new string(classBuffer, 0, copiedLength).Trim();
            return string.IsNullOrWhiteSpace(className) ? null : className;
        }

        public static string? GetProcessRegisteredName(uint pid, int depth = 0)
        {
            return GetProcessRegisteredName(pid, depth, BuildWindowPidMap());
        }

        public static string? GetProcessRegisteredName(
            uint pid,
            int depth,
            IReadOnlyDictionary<IntPtr, uint> windowPidMap)
        {
            if (depth >= MAX_PARENT_DEPTH)
                return null;

            if (!TryGetProcessName((int)pid, out string? processName))
            {
                return null;
            }

            if (IsKnownHelperProcessName(processName))
            {
                var parentName = TraverseParentChain((int)pid, depth + 1, windowPidMap);
                if (!string.IsNullOrEmpty(parentName))
                {
                    Trace(() => $"Resolved helper process={LogPrivacy.Process(processName)} pid={LogPrivacy.Id(pid.ToString())} to parent={LogPrivacy.Process(parentName)}");
                    return parentName;
                }
            }

            ProcessNameResolution resolution = ResolveProcessName((int)pid, processName, windowPidMap);
            if (resolution.Source == ProcessNameResolutionSource.Window)
            {
                Trace(() => $"Resolved process={LogPrivacy.Process(processName)} pid={LogPrivacy.Id(pid.ToString())} via window={LogPrivacy.Process(resolution.Name)}");
            }
            else if (resolution.Source == ProcessNameResolutionSource.FileDescription)
            {
                Trace(() => $"Resolved process={LogPrivacy.Process(processName)} pid={LogPrivacy.Id(pid.ToString())} via file-description={LogPrivacy.Process(resolution.Name)}");
            }

            return resolution.Name;
        }

        public static Task<string?> GetProcessRegisteredNameAsync(uint pid, int depth = 0)
        {
            return ComThreadingHelper.RunOnCoreAudioThreadAsync(
                () => GetProcessRegisteredName(pid, depth));
        }

        public static Task<string?> GetProcessNameFromWindowAsync(int pid)
        {
            return ComThreadingHelper.RunOnCoreAudioThreadAsync(
                () => GetProcessNameFromWindow(pid, BuildWindowPidMap()));
        }

        public static string? GetProcessAppUserModelId(int pid)
        {
            return GetProcessAppUserModelId(pid, BuildWindowPidMap());
        }

        public static string? GetAppUserModelDisplayName(string? appUserModelId)
        {
            if (string.IsNullOrWhiteSpace(appUserModelId))
            {
                return null;
            }

            return ResolveAumidToName(appUserModelId);
        }

        public static IReadOnlyList<PackagedAppIdentity> GetInstalledPackagedApps()
        {
            long now = Environment.TickCount64;
            if (TryGetPackagedAppInventorySnapshot(now, out IReadOnlyList<PackagedAppIdentity>? cachedApps))
            {
                return cachedApps;
            }

            try
            {
                List<PackagedAppIdentity> apps = GetInstalledPackagedAppsFromPackageManager();
                if (apps.Count == 0)
                {
                    apps = GetInstalledPackagedAppsFromRegistry();
                }

                IReadOnlyList<PackagedAppIdentity> cachedResult = [.. apps];
                return PublishPackagedAppInventorySnapshot(cachedResult, now);
            }
            catch (Exception ex)
            {
                Trace(() => $"GetInstalledPackagedApps failed: {ex.GetType().Name}");
                return [];
            }
        }

        private static bool TryGetPackagedAppInventorySnapshot(long now, [NotNullWhen(true)] out IReadOnlyList<PackagedAppIdentity>? cachedApps)
        {
            if (!TryGetExpiringSnapshot(PackagedAppInventoryCache, PackagedAppInventoryCacheExpiry, now, out cachedApps))
            {
                cachedApps = null;
                return false;
            }

            if (cachedApps.Count <= MaxPackagedAppInventoryCacheEntries)
            {
                return true;
            }

            int oversizedCount = cachedApps.Count;
            Interlocked.Exchange(ref PackagedAppInventoryCache, null);
            Interlocked.Exchange(ref PackagedAppInventoryCacheExpiry, 0);
            Trace($"Discarded oversized packaged app inventory cache count={oversizedCount} max={MaxPackagedAppInventoryCacheEntries}");
            cachedApps = null;
            return false;
        }

        private static IReadOnlyList<PackagedAppIdentity> PublishPackagedAppInventorySnapshot(IReadOnlyList<PackagedAppIdentity> cachedResult, long now)
        {
            if (cachedResult.Count > MaxPackagedAppInventoryCacheEntries)
            {
                Interlocked.Exchange(ref PackagedAppInventoryCache, null);
                Interlocked.Exchange(ref PackagedAppInventoryCacheExpiry, 0);
                Trace(() => $"Skipped caching oversized packaged app inventory snapshot count={cachedResult.Count} max={MaxPackagedAppInventoryCacheEntries}");
                return cachedResult;
            }

            return PublishExpiringSnapshot(ref PackagedAppInventoryCache, ref PackagedAppInventoryCacheExpiry, cachedResult, PackagedAppInventoryCacheDuration, now);
        }

        private static List<PackagedAppIdentity> GetInstalledPackagedAppsFromPackageManager()
        {
            try
            {
                var packageManager = new PackageManager();
                var apps = new List<PackagedAppIdentity>();
                var seenAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (Package package in packageManager.FindPackagesForUser(string.Empty))
                {
                    string packageFamilyName = package.Id.FamilyName;
                    if (string.IsNullOrWhiteSpace(packageFamilyName))
                    {
                        continue;
                    }

                    IReadOnlyList<Windows.ApplicationModel.Core.AppListEntry>? appEntries;
                    try
                    {
                        appEntries = package.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Trace(() => $"GetAppListEntriesAsync failed for {packageFamilyName}: {ex.GetType().Name}");
                        continue;
                    }

                    if (appEntries == null || appEntries.Count == 0)
                    {
                        continue;
                    }

                    bool includeAppId = appEntries.Count > 1;
                    foreach (Windows.ApplicationModel.Core.AppListEntry appEntry in appEntries)
                    {
                        TryAddPackagedAppIdentity(
                            apps,
                            seenAumids,
                            packageFamilyName,
                            appEntry.AppUserModelId,
                            appEntry.DisplayInfo?.DisplayName,
                            includeAppId);
                    }
                }

                return OrderPackagedApps(apps);
            }
            catch (Exception ex)
            {
                Trace(() => $"GetInstalledPackagedAppsFromPackageManager failed: {ex.GetType().Name}");
                return [];
            }
        }

        private static List<PackagedAppIdentity> GetInstalledPackagedAppsFromRegistry()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\ActivatableClasses\Package");

            if (key == null)
            {
                return [];
            }

            var rawEntries = new List<(string PackageFamilyName, string AppId, string AppUserModelId)>();
            var seenAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string packageFamilyName in key.GetSubKeyNames())
            {
                if (string.IsNullOrWhiteSpace(packageFamilyName))
                {
                    continue;
                }

                using var packageKey = key.OpenSubKey(packageFamilyName);
                if (packageKey == null)
                {
                    continue;
                }

                foreach (string appId in packageKey.GetSubKeyNames())
                {
                    if (string.IsNullOrWhiteSpace(appId))
                    {
                        continue;
                    }

                    string appUserModelId = $"{packageFamilyName}!{appId}";
                    if (seenAumids.Add(appUserModelId))
                    {
                        rawEntries.Add((packageFamilyName, appId, appUserModelId));
                    }
                }
            }

            HashSet<string> multiEntryPackages = rawEntries
                .GroupBy(static entry => entry.PackageFamilyName, StringComparer.OrdinalIgnoreCase)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return OrderPackagedApps(rawEntries.Select(entry =>
                CreatePackagedAppIdentity(
                    entry.PackageFamilyName,
                    entry.AppUserModelId,
                    preferredDisplayName: null,
                    multiEntryPackages.Contains(entry.PackageFamilyName))));
        }

        private static bool TryAddPackagedAppIdentity(
            List<PackagedAppIdentity> apps,
            HashSet<string> seenAumids,
            string packageFamilyName,
            string? appUserModelId,
            string? preferredDisplayName,
            bool includeAppId)
        {
            string normalizedAppUserModelId = appUserModelId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(packageFamilyName) ||
                string.IsNullOrWhiteSpace(normalizedAppUserModelId) ||
                !seenAumids.Add(normalizedAppUserModelId))
            {
                return false;
            }

            apps.Add(CreatePackagedAppIdentity(packageFamilyName, normalizedAppUserModelId, preferredDisplayName, includeAppId));
            return true;
        }

        private static PackagedAppIdentity CreatePackagedAppIdentity(
            string packageFamilyName,
            string appUserModelId,
            string? preferredDisplayName,
            bool includeAppId)
        {
            string appId = ExtractPackagedAppId(appUserModelId, packageFamilyName);
            string displayName = ResolvePackagedAppDisplayName(packageFamilyName, appId, preferredDisplayName, includeAppId);
            return new PackagedAppIdentity(displayName, appUserModelId, packageFamilyName, appId);
        }

        private static string ResolvePackagedAppDisplayName(
            string packageFamilyName,
            string appId,
            string? preferredDisplayName,
            bool includeAppId)
        {
            string displayName = preferredDisplayName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return BuildPackagedAppDisplayName(packageFamilyName, appId, includeAppId);
            }

            if (includeAppId &&
                !string.IsNullOrWhiteSpace(appId) &&
                !string.Equals(appId, "App", StringComparison.OrdinalIgnoreCase) &&
                !displayName.Contains(appId, StringComparison.OrdinalIgnoreCase))
            {
                return $"{displayName} ({appId})";
            }

            return displayName;
        }

        private static List<PackagedAppIdentity> OrderPackagedApps(IEnumerable<PackagedAppIdentity> apps)
        {
            return
            [
                .. apps
                    .OrderBy(static entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static entry => entry.AppUserModelId, StringComparer.OrdinalIgnoreCase)
            ];
        }

        private static string ExtractPackagedAppId(string appUserModelId, string packageFamilyName)
        {
            if (string.IsNullOrWhiteSpace(appUserModelId))
            {
                return string.Empty;
            }

            int separatorIndex = appUserModelId.IndexOf('!');
            if (separatorIndex >= 0 && separatorIndex < appUserModelId.Length - 1)
            {
                return appUserModelId[(separatorIndex + 1)..].Trim();
            }

            if (!string.IsNullOrWhiteSpace(packageFamilyName) &&
                appUserModelId.StartsWith(packageFamilyName, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return appUserModelId.Trim();
        }

        internal static string ExtractPackagedAppIdForTests(string appUserModelId, string packageFamilyName)
        {
            return ExtractPackagedAppId(appUserModelId, packageFamilyName);
        }

        public static Task<string?> GetFileDescriptionAsync(int pid)
        {
            return Task.Run(() => GetFileDescription(pid));
        }

        private static string? TraverseParentChain(
            int pid,
            int currentDepth,
            IReadOnlyDictionary<IntPtr, uint> windowPidMap)
        {
            if (currentDepth >= MAX_PARENT_DEPTH)
                return null;

            var parentPid = GetParentPid(pid);
            if (parentPid == 0 || parentPid == pid)
                return null;

            if (!TryGetProcessName(parentPid, out string? parentName))
            {
                return null;
            }

            if (IsKnownHelperProcessName(parentName))
                return TraverseParentChain(parentPid, currentDepth + 1, windowPidMap);

            Trace(() => $"Parent chain traversal found parent={LogPrivacy.Process(parentName)} pid={LogPrivacy.Id(parentPid.ToString())}");

            return ResolveProcessName(parentPid, parentName, windowPidMap).Name;
        }

        private static bool TryGetProcessName(int pid, [NotNullWhen(true)] out string? processName)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                processName = process.ProcessName;
                return !string.IsNullOrEmpty(processName);
            }
            catch (ArgumentException)
            {
                processName = null;
                return false;
            }
            catch (InvalidOperationException)
            {
                processName = null;
                return false;
            }
        }

        private static ProcessNameResolution ResolveProcessName(
            int pid,
            string processName,
            IReadOnlyDictionary<IntPtr, uint> windowPidMap)
        {
            string? windowName = GetProcessNameFromWindow(pid, windowPidMap);
            if (!string.IsNullOrEmpty(windowName))
            {
                return new ProcessNameResolution(windowName, ProcessNameResolutionSource.Window);
            }

            string? fileDescription = GetFileDescription(pid);
            if (!string.IsNullOrEmpty(fileDescription))
            {
                return new ProcessNameResolution(fileDescription, ProcessNameResolutionSource.FileDescription);
            }

            return new ProcessNameResolution(SanitizeProcessName(processName), ProcessNameResolutionSource.SanitizedProcessName);
        }

        private static IntPtr FindPreferredMainWindow(int pid, IReadOnlyDictionary<IntPtr, uint> windowPidMap)
        {
            IntPtr windowHandle = FindMainWindow(pid, windowPidMap, requireVisible: true);
            return windowHandle != IntPtr.Zero
                ? windowHandle
                : FindMainWindow(pid, windowPidMap, requireVisible: false);
        }

        private static string? GetProcessNameFromWindow(
            int pid,
            IReadOnlyDictionary<IntPtr, uint> windowPidMap)
        {
            IntPtr hWnd = FindPreferredMainWindow(pid, windowPidMap);

            if (hWnd == IntPtr.Zero)
                return null;

            string? aumid = GetWindowAppUserModelId(hWnd);
            if (!string.IsNullOrEmpty(aumid))
            {
                string? friendlyName = ResolveAumidToName(aumid);
                if (!string.IsNullOrEmpty(friendlyName))
                {
                    Trace(() => $"Resolved PID {pid} via AUMID mapping");
                    return friendlyName;
                }
            }

            int length = GetWindowTextLength(hWnd);
            if (length > 0)
            {
                StringBuilder title = new(length + 1);
                int copiedLength = GetWindowText(hWnd, title, title.Capacity);
                if (copiedLength <= 0)
                {
                    return null;
                }

                string titleText = title.ToString();
                if (!string.IsNullOrEmpty(titleText) && !IsInternalWindowTitle(titleText))
                {
                    Trace(() => $"Resolved PID {pid} via window title heuristic (length={titleText.Length})");
                    return titleText;
                }
            }

            return null;
        }

        private static string? GetProcessAppUserModelId(
            int pid,
            IReadOnlyDictionary<IntPtr, uint> windowPidMap)
        {
            IntPtr hWnd = FindPreferredMainWindow(pid, windowPidMap);

            return hWnd == IntPtr.Zero ? null : GetWindowAppUserModelId(hWnd);
        }

        internal static bool IsInternalWindowTitle(string title)
        {
            foreach (var prefix in AppConstants.Audio.InternalWindowPrefixes)
            {
                if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (title.Length > AppConstants.Audio.WindowTitleHeuristics.MinHexLength &&
                (title.Contains('.') || title.Contains('_')) &&
                char.IsDigit(title[^1]))
            {
                int hexCount = 0;
                for (int i = title.Length - 1; i >= 0 && (char.IsDigit(title[i]) ||
                    (title[i] >= 'a' && title[i] <= 'f') ||
                    (title[i] >= 'A' && title[i] <= 'F') ||
                    title[i] == '.'); i--)
                {
                    hexCount++;
                }
                if (hexCount > AppConstants.Audio.WindowTitleHeuristics.MaxHexCount)
                    return true;
            }

            if (title.Length > AppConstants.Audio.WindowTitleHeuristics.MinDashLength && title.Contains('-'))
            {
                int dashCount = 0;
                foreach (char c in title)
                    if (c == '-') dashCount++;
                if (dashCount >= AppConstants.Audio.WindowTitleHeuristics.MinDashCount)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Reads the AppUserModelID from a top-level window using the shared native property-store projection.
        /// </summary>
        private static string? GetWindowAppUserModelId(IntPtr hWnd)
        {
            try
            {
                Guid iid = typeof(IPropertyStoreNativeInterop).GUID;
                if (SHGetPropertyStoreForWindow(hWnd, ref iid, out IntPtr storePtr) != 0 || storePtr == IntPtr.Zero)
                {
                    return null;
                }

                if (!NativeAudioInteropHelper.ComActivator.TryWrapTyped(storePtr, out IActivatedNativeComObject<IPropertyStoreNativeInterop>? storeHandle, out _))
                {
                    return null;
                }

                using (storeHandle!)
                {
                    NativePropertyKey key = PKEY_AppUserModel_ID;
                    int hResult = storeHandle.Interface.GetValue(ref key, out NativePropVariant variant);
                    if (hResult != 0)
                    {
                        return null;
                    }

                    using (variant)
                    {
                        if (!variant.TryGetString(out string? appUserModelId))
                        {
                            return null;
                        }

                        return appUserModelId;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace(() => $"GetWindowAppUserModelId failed: {ex.GetType().Name}");
                return null;
            }
        }

        public static string? GetFileDescription(int pid)
        {
            var now = Environment.TickCount64;

            if (TryGetCachedValue(FileDescriptionCache, pid, now, out string? cachedDescription))
                return cachedDescription;

            try
            {
                using var process = Process.GetProcessById(pid);
                var mainModule = process.MainModule;
                if (mainModule?.FileName != null)
                {
                    var info = FileVersionInfo.GetVersionInfo(mainModule.FileName);
                    var description = info.FileDescription;

                    if (!string.IsNullOrEmpty(description))
                    {
                        return StoreCachedLookupResult(FileDescriptionCache, pid, description, now);
                    }
                }

                return StoreCachedLookupResult(FileDescriptionCache, pid, null, now);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                Trace(() => $"GetFileDescription failed for PID {pid}: Access is denied (Expected for elevated/system processes)");
                return StoreCachedLookupResult(FileDescriptionCache, pid, null, now);
            }
            catch (Exception ex)
            {
                Trace(() => $"GetFileDescription failed for PID {LogPrivacy.Id(pid.ToString())}: {ex.GetType().Name}");
                return StoreCachedLookupResult(FileDescriptionCache, pid, null, now);
            }
        }

        private static string? FindPackageFamilyNameForAumid(Microsoft.Win32.RegistryKey rootKey, string aumid)
        {
            int exclamationIndex = aumid.IndexOf('!');
            if (exclamationIndex > 0)
            {
                string packageFamilyName = aumid[..exclamationIndex];
                using var directMatch = rootKey.OpenSubKey(packageFamilyName);
                if (directMatch != null)
                {
                    return packageFamilyName;
                }
            }

            foreach (string subKeyName in rootKey.GetSubKeyNames())
            {
                using var subKey = rootKey.OpenSubKey(subKeyName);
                if (subKey == null)
                    continue;

                foreach (string appId in subKey.GetSubKeyNames())
                {
                    if (string.Equals($"{subKeyName}!{appId}", aumid, StringComparison.OrdinalIgnoreCase))
                    {
                        return subKeyName;
                    }
                }
            }

            return null;
        }

        private static string? ResolveAumidToName(string aumid)
        {
            var now = Environment.TickCount64;

            if (TryGetCachedValue(AumidCache, aumid, now, out string? cachedName))
                return cachedName;

            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\ActivatableClasses\Package");

                if (key == null)
                {
                    return StoreCachedLookupResult(AumidCache, aumid, null, now);
                }

                string? packageFamilyName = FindPackageFamilyNameForAumid(key, aumid);
                if (!string.IsNullOrWhiteSpace(packageFamilyName))
                {
                    string cleaned = CleanPackageName(packageFamilyName);
                    Trace("Resolved AUMID to package display name");
                    return StoreCachedLookupResult(AumidCache, aumid, cleaned, now);
                }

                return StoreCachedLookupResult(AumidCache, aumid, null, now);
            }
            catch (Exception ex)
            {
                Trace(() => $"ResolveAumidToName failed: {ex.GetType().Name}");
                return StoreCachedLookupResult(AumidCache, aumid, null, now);
            }
        }

        private static void TrimCachesIfNeeded(long now)
        {
            if ((Interlocked.Increment(ref CacheTrimCounter) & CacheTrimIntervalMask) != 0)
                return;

            TrimCache(FileDescriptionCache, MaxFileDescriptionCacheEntries, now);
            TrimCache(AumidCache, MaxAumidCacheEntries, now);
        }

        private static void TrimCache<TKey>(ConcurrentDictionary<TKey, CacheEntry> cache, int maxEntries, long now)
            where TKey : notnull
        {
            if (cache.Count <= maxEntries)
                return;

            foreach (var kvp in cache)
            {
                if (kvp.Value.Expiry <= now)
                    cache.TryRemove(kvp.Key, out _);
            }

            int countAfterExpiryTrim = cache.Count;
            if (countAfterExpiryTrim <= maxEntries)
                return;

            int overflow = countAfterExpiryTrim - maxEntries;
            var oldestByExpiry = new List<(TKey Key, long Expiry)>(countAfterExpiryTrim);
            foreach (var kvp in cache)
            {
                oldestByExpiry.Add((kvp.Key, kvp.Value.Expiry));
            }

            oldestByExpiry.Sort(static (left, right) => left.Expiry.CompareTo(right.Expiry));
            int removals = Math.Min(overflow, oldestByExpiry.Count);
            for (int i = 0; i < removals; i++)
            {
                cache.TryRemove(oldestByExpiry[i].Key, out _);
            }
        }

        private static string CleanPackageName(string packageFamilyName)
        {
            int underscoreIndex = packageFamilyName.IndexOf('_');
            if (underscoreIndex > 0)
                return packageFamilyName[..underscoreIndex].Replace('.', ' ');

            return packageFamilyName;
        }

        internal static string CleanPackageNameForTests(string packageFamilyName)
        {
            return CleanPackageName(packageFamilyName);
        }

        private static string BuildPackagedAppDisplayName(string packageFamilyName, string appId, bool includeAppId)
        {
            string packageDisplayName = CleanPackageName(packageFamilyName).Trim();
            if (!includeAppId)
            {
                return string.IsNullOrWhiteSpace(packageDisplayName) ? appId : packageDisplayName;
            }

            string cleanedAppId = appId.Replace('.', ' ').Trim();
            if (string.IsNullOrWhiteSpace(cleanedAppId) || string.Equals(cleanedAppId, "App", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(packageDisplayName) ? packageFamilyName : packageDisplayName;
            }

            if (string.IsNullOrWhiteSpace(packageDisplayName))
            {
                return cleanedAppId;
            }

            return $"{packageDisplayName} ({cleanedAppId})";
        }

        internal static string BuildPackagedAppDisplayNameForTests(string packageFamilyName, string appId, bool includeAppId)
        {
            return BuildPackagedAppDisplayName(packageFamilyName, appId, includeAppId);
        }

        private static IntPtr FindMainWindow(
            int pid,
            IReadOnlyDictionary<IntPtr, uint> windowPidMap,
            bool requireVisible)
        {
            IntPtr bestWindow = IntPtr.Zero;
            int bestTitleLength = 0;

            foreach (var kvp in windowPidMap)
            {
                if (kvp.Value != pid)
                    continue;

                if (requireVisible && !IsWindowVisible(kvp.Key))
                    continue;

                int titleLength = GetWindowTextLength(kvp.Key);
                if (titleLength > bestTitleLength)
                {
                    bestTitleLength = titleLength;
                    bestWindow = kvp.Key;
                }
                else if (bestWindow == IntPtr.Zero)
                {
                    bestWindow = kvp.Key;
                }
            }

            return bestWindow;
        }

        public static void LogComException(ILogger logger, string methodName, COMException ex)
        {
            logger.Error(
                "AudioDeviceService",
                () => $"COM Exception - HRESULT: 0x{ex.ErrorCode:X8}, Type: {ex.GetType().Name}",
                methodName,
                ex);

            if (HResultLookup.TryGetValue(ex.ErrorCode, out var message))
                logger.Trace("AudioDeviceService", () => $"  -> {message}", methodName);
            else
                logger.Trace(
                    "AudioDeviceService",
                    "  -> Unknown HRESULT - You may need to look up this code",
                    methodName);
        }

        public static void LogException(ILogger logger, string methodName, Exception ex)
        {
            logger.Error(
                "AudioDeviceService",
                () => $"Exception - Type: {ex.GetType().Name}",
                methodName,
                ex);

            if (ex.InnerException != null)
            {
                logger.Trace(
                    "AudioDeviceService",
                    () => $"  -> Inner Exception: {ex.InnerException.GetType().Name}",
                    methodName);
            }
        }

        public static string CapitalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return name;

            return string.Create(name.Length, name, (span, source) =>
            {
                source.CopyTo(span);
                span[0] = char.ToUpperInvariant(span[0]);
            });
        }

        public static string SanitizeProcessName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return processName;

            if (processName.Equals("msedge", StringComparison.OrdinalIgnoreCase))
                return "Microsoft Edge";

            ReadOnlySpan<char> result = processName.AsSpan();

            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var suffix in AppConstants.Audio.SuffixesToRemove)
                {
                    if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        result = result[..^suffix.Length];
                        changed = true;
                        break;
                    }
                }
            }

            if (result.Length > 3 &&
                (result.EndsWith("64", StringComparison.Ordinal) ||
                 result.EndsWith("32", StringComparison.Ordinal)))
            {
                result = result[..^2];
            }

            if (result.Length == processName.Length)
                return processName;

            return result.ToString();
        }

        public static Process? FindRealAppProcess(Process? webViewProcess)
        {
            if (webViewProcess == null)
                return null;

            var visitedPids = new HashSet<int>(MAX_PARENT_DEPTH);
            Process? currentProcess = webViewProcess;
            bool isCurrentOwned = false;

            try
            {
                while (currentProcess != null && visitedPids.Count < MAX_PARENT_DEPTH)
                {
                    visitedPids.Add(currentProcess.Id);
                    string processName = currentProcess.ProcessName;

                    Trace(() => $"Checking process={LogPrivacy.Process(processName)} pid={LogPrivacy.Id(currentProcess.Id.ToString())}");

                    if (!IsKnownHelperProcessName(processName))
                    {
                        var result = currentProcess;
                        currentProcess = null;
                        return result;
                    }

                    var parentPid = GetParentPid(currentProcess.Id);
                    if (parentPid == 0 || visitedPids.Contains(parentPid))
                        break;

                    if (isCurrentOwned)
                        currentProcess.Dispose();

                    try
                    {
                        currentProcess = Process.GetProcessById(parentPid);
                        isCurrentOwned = true;
                    }
                    catch (ArgumentException)
                    {
                        currentProcess = null;
                        isCurrentOwned = false;
                    }
                    catch (InvalidOperationException)
                    {
                        currentProcess = null;
                        isCurrentOwned = false;
                    }
                }

                return null;
            }
            finally
            {
                if (currentProcess != null && isCurrentOwned)
                    currentProcess.Dispose();
            }
        }

        public static int GetParentPid(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                PROCESS_BASIC_INFORMATION pbi = new();
                int status = NtQueryInformationProcess(
                    process.Handle,
                    0,
                    ref pbi,
                    Marshal.SizeOf(pbi),
                    out _);

                if (status == 0)
                    return pbi.InheritedFromUniqueProcessId.ToInt32();
            }
            catch (Exception ex)
            {
                Trace(() => $"GetParentPid failed for PID {LogPrivacy.Id(pid.ToString())}: {ex.GetType().Name}");
            }

            return 0;
        }

        public static Process? GetParentProcessSafe(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                PROCESS_BASIC_INFORMATION pbi = new();
                int status = NtQueryInformationProcess(
                    process.Handle,
                    0,
                    ref pbi,
                    Marshal.SizeOf(pbi),
                    out _);

                if (status == 0)
                {
                    int parentPid = pbi.InheritedFromUniqueProcessId.ToInt32();
                    if (parentPid > 0)
                    {
                        var parentProcess = Process.GetProcessById(parentPid);
                        Trace(() => $"Got parent process for PID {pid}: parent PID = {parentPid}");
                        return parentProcess;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Trace(() => $"GetParentProcessSafe failed for PID {LogPrivacy.Id(pid.ToString())}: {ex.GetType().Name}");
                return null;
            }
        }

        public static string GetFriendlyProcessName(Process? process)
        {
            if (process == null)
                return string.Empty;

            string processName = process.ProcessName;

            if (processName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var parentProcess = GetParentProcessSafe(process.Id);
                    if (parentProcess != null)
                    {
                        Trace(() => $"Resolved msedgewebview2 to parent={LogPrivacy.Process(parentProcess.ProcessName)}");
                        return parentProcess.ProcessName;
                    }
                }
                catch (Exception ex)
                {
                    Trace(() => $"Failed to get parent process for {LogPrivacy.Process(processName)}: {ex.GetType().Name}");
                }
            }

            return processName;
        }

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
