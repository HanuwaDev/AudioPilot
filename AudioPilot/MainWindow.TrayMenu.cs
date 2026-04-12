using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AudioPilot.Models;

namespace AudioPilot
{
    public partial class MainWindow
    {
        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_RIGHTALIGN = 0x0008;
        private const uint TPM_BOTTOMALIGN = 0x0020;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint MF_STRING = 0x0000;
        private const uint MF_SEPARATOR = 0x0800;
        private const uint MIIM_STATE = 0x00000001;
        private const uint MFS_DEFAULT = 0x00001000;
        private const uint MonitorDefaultToNearest = 2;
        private const uint WM_NULL = 0x0000;
        private const uint NativeTrayCommandShow = 1001;
        private const uint NativeTrayCommandHide = 1002;
        private const uint NativeTrayCommandSwitchOutput = 1003;
        private const uint NativeTrayCommandSwitchInput = 1004;
        private const uint NativeTrayCommandSettings = 1005;
        private const uint NativeTrayCommandExit = 1006;
        private const uint NativeTrayCommandRoutineBase = 2000;
        private const int EstimatedNativeTrayMenuWidthPx = 220;
        private const int EstimatedNativeTrayMenuItemHeightPx = 28;
        private const int EstimatedNativeTrayMenuSeparatorHeightPx = 8;

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out POINT lpPoint);

        [LibraryImport("user32.dll")]
        private static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial nint CreatePopupMenu();

        [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AppendMenu(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyMenu(nint hMenu);

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial uint TrackPopupMenuEx(nint hmenu, uint fuFlags, int x, int y, nint hwnd, nint lptpm);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(nint hWnd);

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        private static partial nint PostMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetMenuDefaultItem(nint hMenu, uint uItem, [MarshalAs(UnmanagedType.Bool)] bool fByPos);

        [LibraryImport("user32.dll", EntryPoint = "SetMenuItemInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetMenuItemInfo(nint hMenu, uint item, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, ref MENUITEMINFO lpmii);

        private void TaskbarIcon_TrayRightMouseUp(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            try
            {
                ShowNativeTrayMenu();
            }
            catch (Exception ex)
            {
                _logger.Error("MainWindow", "Native tray menu failed", nameof(TaskbarIcon_TrayRightMouseUp), ex);
            }
        }

        private void ShowNativeTrayMenu()
        {
            if (!Dispatcher.CheckAccess())
            {
                return;
            }

            nint menu = CreatePopupMenu();
            if (menu == nint.Zero)
            {
                return;
            }

            Dictionary<uint, string> routineCommands = [];
            try
            {
                int estimatedMenuHeightPx = BuildNativeTrayMenu(menu, routineCommands);
                SetDefaultNativeMenuItem(menu, NativeTrayCommandShow);
                _ = SetMenuDefaultItem(menu, NativeTrayCommandShow, fByPos: false);

                uint alignmentFlags = TPM_RETURNCMD | TPM_RIGHTBUTTON;
                int anchorX;
                int anchorY;
                if (TryResolveNativeTrayPopupPlacement(estimatedMenuHeightPx, out NativeTrayPopupPlacement placement))
                {
                    alignmentFlags |= placement.AlignmentFlags;
                    anchorX = placement.X;
                    anchorY = placement.Y;
                }
                else if (GetCursorPos(out POINT cursorPoint))
                {
                    nint monitor = MonitorFromPoint(cursorPoint, MonitorDefaultToNearest);
                    var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                    bool hasMonitorInfo = monitor != nint.Zero && GetMonitorInfo(monitor, ref monitorInfo);
                    alignmentFlags |= hasMonitorInfo
                        ? ResolveNativeTrayAlignmentFlags(
                            cursorPoint,
                            monitorInfo,
                            EstimatedNativeTrayMenuWidthPx,
                            estimatedMenuHeightPx)
                        : TPM_LEFTALIGN;
                    anchorX = cursorPoint.x;
                    anchorY = cursorPoint.y;
                }
                else
                {
                    return;
                }

                nint windowHandle = new WindowInteropHelper(this).Handle;
                SetForegroundWindow(windowHandle);
                uint command = TrackPopupMenuEx(menu, alignmentFlags, anchorX, anchorY, windowHandle, nint.Zero);
                PostMessage(windowHandle, WM_NULL, nint.Zero, nint.Zero);

                if (command != 0)
                {
                    ExecuteNativeTrayCommand(command, routineCommands);
                }
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        private int BuildNativeTrayMenu(nint menu, Dictionary<uint, string> routineCommands)
        {
            int itemCount = 0;
            int separatorCount = 0;

            AppendNativeMenuItem(menu, NativeTrayCommandShow, FormatNativeMenuItemLabel("Show", _appVm.GetTrayShowAppHotkey()));
            AppendNativeMenuItem(menu, NativeTrayCommandHide, "Hide");
            itemCount += 2;

            bool hasOutputCycle = ShouldShowNativeSwitchMenuItem(_appVm.OutputCycleDevices.Count, _appVm.OutputHotkeysEnabled);
            bool hasInputCycle = ShouldShowNativeSwitchMenuItem(_appVm.InputCycleDevices.Count, _appVm.InputHotkeysEnabled);
            if (hasOutputCycle || hasInputCycle)
            {
                AppendNativeSeparator(menu);
                separatorCount++;

                if (hasOutputCycle)
                {
                    AppendNativeMenuItem(menu, NativeTrayCommandSwitchOutput, $"Switch Output ({GetCurrentDefaultPlaybackDeviceName()})");
                    itemCount++;
                }

                if (hasInputCycle)
                {
                    AppendNativeMenuItem(menu, NativeTrayCommandSwitchInput, $"Switch Input ({GetCurrentDefaultRecordingDeviceName()})");
                    itemCount++;
                }
            }

            IReadOnlyList<AudioRoutine> routines = _appVm.GetTrayMenuRoutines();
            if (routines.Count > 0)
            {
                AppendNativeSeparator(menu);
                separatorCount++;

                uint nextRoutineCommand = NativeTrayCommandRoutineBase;
                foreach (AudioRoutine routine in routines)
                {
                    string label = string.IsNullOrWhiteSpace(routine.Hotkey)
                        ? routine.Name
                        : $"{routine.Name}\t{routine.Hotkey}";
                    AppendNativeMenuItem(menu, nextRoutineCommand, label);
                    routineCommands[nextRoutineCommand] = routine.Id;
                    nextRoutineCommand++;
                    itemCount++;
                }
            }

            AppendNativeSeparator(menu);
            AppendNativeMenuItem(menu, NativeTrayCommandSettings, "Settings");
            AppendNativeMenuItem(menu, NativeTrayCommandExit, "Exit");
            separatorCount++;
            itemCount += 2;

            return (itemCount * EstimatedNativeTrayMenuItemHeightPx)
                + (separatorCount * EstimatedNativeTrayMenuSeparatorHeightPx);
        }

        private bool TryResolveNativeTrayPopupPlacement(int estimatedMenuHeightPx, out NativeTrayPopupPlacement placement)
        {
            placement = default;

            if (!NativeTrayIconRectResolver.TryResolveIconRect(taskbarIcon, out Int32Rect iconRect))
            {
                return false;
            }

            POINT iconCenter = new()
            {
                x = iconRect.X + (iconRect.Width / 2),
                y = iconRect.Y + (iconRect.Height / 2),
            };

            nint monitor = MonitorFromPoint(iconCenter, MonitorDefaultToNearest);
            if (monitor == nint.Zero)
            {
                return false;
            }

            var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
            {
                return false;
            }

            placement = ResolveNativeTrayPopupPlacement(
                new RECT
                {
                    left = iconRect.X,
                    top = iconRect.Y,
                    right = iconRect.X + iconRect.Width,
                    bottom = iconRect.Y + iconRect.Height,
                },
                monitorInfo.rcWork,
                EstimatedNativeTrayMenuWidthPx,
                estimatedMenuHeightPx);
            return true;
        }

        internal static NativeTrayPopupPlacement ResolveNativeTrayPopupPlacement(
            RECT iconRect,
            RECT workArea,
            int estimatedMenuWidthPx,
            int estimatedMenuHeightPx)
        {
            int aboveSpace = iconRect.top - workArea.top;
            int belowSpace = workArea.bottom - iconRect.bottom;
            int leftSpace = iconRect.right - workArea.left;
            int rightSpace = workArea.right - iconRect.left;

            bool openAbove = aboveSpace >= estimatedMenuHeightPx || aboveSpace >= belowSpace;
            bool openToRight = rightSpace >= estimatedMenuWidthPx || rightSpace >= leftSpace;

            uint alignmentFlags = openToRight ? TPM_LEFTALIGN : TPM_RIGHTALIGN;
            if (openAbove)
            {
                alignmentFlags |= TPM_BOTTOMALIGN;
            }

            return new NativeTrayPopupPlacement(
                openToRight ? iconRect.left : iconRect.right,
                openAbove ? iconRect.top : iconRect.bottom,
                alignmentFlags);
        }

        internal static uint ResolveNativeTrayAlignmentFlags(
            POINT cursorPoint,
            MONITORINFO monitorInfo,
            int estimatedMenuWidthPx,
            int estimatedMenuHeightPx)
        {
            uint flags = TPM_LEFTALIGN;
            int spaceToRight = monitorInfo.rcMonitor.right - cursorPoint.x;
            int spaceToBottom = monitorInfo.rcMonitor.bottom - cursorPoint.y;

            if (spaceToRight < estimatedMenuWidthPx)
            {
                flags |= TPM_RIGHTALIGN;
            }

            if (spaceToBottom < estimatedMenuHeightPx)
            {
                flags |= TPM_BOTTOMALIGN;
            }

            return flags;
        }

        internal static bool ShouldShowNativeSwitchMenuItem(int cycleDeviceCount, bool hotkeysEnabled)
        {
            return hotkeysEnabled && cycleDeviceCount > 0;
        }

        internal readonly record struct NativeTrayPopupPlacement(int X, int Y, uint AlignmentFlags);

        private void ExecuteNativeTrayCommand(uint command, Dictionary<uint, string> routineCommands)
        {
            switch (command)
            {
                case NativeTrayCommandShow:
                    TrayMenu_Show_Click(this, new RoutedEventArgs());
                    return;
                case NativeTrayCommandHide:
                    TrayMenu_Hide_Click(this, new RoutedEventArgs());
                    return;
                case NativeTrayCommandSwitchOutput:
                    TrayMenu_SwitchOutput_Click(this, new RoutedEventArgs());
                    return;
                case NativeTrayCommandSwitchInput:
                    TrayMenu_SwitchInput_Click(this, new RoutedEventArgs());
                    return;
                case NativeTrayCommandSettings:
                    TrayMenu_Settings_Click(this, new RoutedEventArgs());
                    return;
                case NativeTrayCommandExit:
                    TrayMenu_Exit_Click(this, new RoutedEventArgs());
                    return;
                default:
                    if (routineCommands.TryGetValue(command, out string? routineId) && routineId != null)
                    {
                        ExecuteTrayRoutine(routineId);
                    }

                    return;
            }
        }

        private void ExecuteTrayRoutine(string routineId)
        {
            _ = MainWindowHotkeyDispatchHelper.ExecuteAsync(
                () => _appVm.RunRoutineFromTrayAsync(routineId),
                _logger,
                Dispatcher,
                "Tray routine failed",
                "Error running AudioPilot routine.",
                nameof(ExecuteTrayRoutine));
        }

        private static void AppendNativeMenuItem(nint menu, uint commandId, string text)
        {
            if (!AppendMenu(menu, MF_STRING, commandId, text))
            {
                throw new InvalidOperationException($"Failed to append native tray menu item '{text}'.");
            }
        }

        internal static string FormatNativeMenuItemLabel(string text, string? shortcut)
        {
            string trimmedShortcut = shortcut?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(trimmedShortcut)
                ? text
                : $"{text}\t{trimmedShortcut}";
        }

        private static void AppendNativeSeparator(nint menu)
        {
            if (!AppendMenu(menu, MF_SEPARATOR, 0, null))
            {
                throw new InvalidOperationException("Failed to append native tray menu separator.");
            }
        }

        private static void SetDefaultNativeMenuItem(nint menu, uint commandId)
        {
            var menuItemInfo = new MENUITEMINFO
            {
                cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
                fMask = MIIM_STATE,
                fState = MFS_DEFAULT,
            };

            _ = SetMenuItemInfo(menu, commandId, fByPosition: false, ref menuItemInfo);
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MENUITEMINFO
        {
            public uint cbSize;
            public uint fMask;
            public uint fType;
            public uint fState;
            public uint wID;
            public nint hSubMenu;
            public nint hbmpChecked;
            public nint hbmpUnchecked;
            public nuint dwItemData;
            public nint dwTypeData;
            public uint cch;
            public nint hbmpItem;
        }
    }
}
