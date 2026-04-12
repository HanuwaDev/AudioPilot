using System.Runtime.InteropServices;
using AudioPilot.Logging;

namespace AudioPilot.Platform
{
    public static partial class MediaKeyHelper
    {
        private const uint ExpectedInputCount = 2;

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
            public static int Size => Marshal.SizeOf<INPUT>();
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg; public ushort wParamL; public ushort wParamH;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
        private const ushort VK_MEDIA_PREV_TRACK = 0xB1;
        private const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

        private static readonly Lock _lock = new();

        internal static Func<ushort, (uint Result, int ErrorCode)>? SendInputOverrideForTests { get; set; }
        internal static ILogger? LoggerOverrideForTests { get; set; }

        public static void PressPlayPause() => _ = TryPressPlayPause();
        public static void PressNextTrack() => _ = TryPressNextTrack();
        public static void PressPreviousTrack() => _ = TryPressPreviousTrack();
        public static bool TryPressPlayPause() => SendKey(VK_MEDIA_PLAY_PAUSE, "PlayPause");
        public static bool TryPressNextTrack() => SendKey(VK_MEDIA_NEXT_TRACK, "NextTrack");
        public static bool TryPressPreviousTrack() => SendKey(VK_MEDIA_PREV_TRACK, "PreviousTrack");

        private static bool SendKey(ushort vk, string keyName)
        {
            lock (_lock)
            {
                try
                {
                    var (result, errorCode) = SendKeyCore(vk);

                    if (result != ExpectedInputCount)
                    {
                        Exception failure = errorCode != 0
                            ? new System.ComponentModel.Win32Exception(errorCode)
                            : new InvalidOperationException($"SendInput returned {result} instead of {ExpectedInputCount}.");
                        GetLogger()?.Error("MediaKeyHelper", $"media-key-send-failed:{keyName}", nameof(SendKey), failure);
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    GetLogger()?.Error("MediaKeyHelper", $"media-key-send-exception:{keyName}", nameof(SendKey), ex);
                    return false;
                }
            }
        }

        internal static void ResetTestHooks()
        {
            SendInputOverrideForTests = null;
            LoggerOverrideForTests = null;
        }

        private static (uint Result, int ErrorCode) SendKeyCore(ushort vk)
        {
            if (SendInputOverrideForTests != null)
            {
                return SendInputOverrideForTests(vk);
            }

            INPUT[] inputs =
            [
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = vk,
                            dwFlags = KEYEVENTF_EXTENDEDKEY
                        }
                    }
                },
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = vk,
                            dwFlags = KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP
                        }
                    }
                }
            ];

            uint result = SendInput(ExpectedInputCount, inputs, INPUT.Size);
            int errorCode = result == ExpectedInputCount ? 0 : Marshal.GetLastWin32Error();
            return (result, errorCode);
        }

        private static ILogger? GetLogger()
        {
            return LoggerOverrideForTests ?? Logger.Instance;
        }
    }
}
