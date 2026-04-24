using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace AudioPilot.Platform
{
    internal static partial class NativeTrayIconRectResolver
    {
        private static readonly FieldInfo? IconDataField =
            typeof(TaskbarIcon).GetField("iconData", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Type? NotifyIconDataType =
            typeof(TaskbarIcon).Assembly.GetType("Hardcodet.Wpf.TaskbarNotification.Interop.NotifyIconData");

        private static readonly FieldInfo? WindowHandleField =
            NotifyIconDataType?.GetField("WindowHandle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly FieldInfo? TaskbarIconIdField =
            NotifyIconDataType?.GetField("TaskbarIconId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly FieldInfo? TaskbarIconGuidField =
            NotifyIconDataType?.GetField("TaskbarIconGuid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        [LibraryImport("shell32.dll", SetLastError = false)]
        private static partial int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        internal static bool TryResolveIconRect(TaskbarIcon? taskbarIcon, out Int32Rect iconRect)
        {
            iconRect = default;

            if (taskbarIcon == null || IconDataField == null)
            {
                return false;
            }

            object? iconData = IconDataField.GetValue(taskbarIcon);
            if (!TryCreateNotifyIconIdentifier(iconData, out NOTIFYICONIDENTIFIER identifier))
            {
                return false;
            }

            if (Shell_NotifyIconGetRect(ref identifier, out RECT nativeRect) != 0)
            {
                return false;
            }

            int width = nativeRect.right - nativeRect.left;
            int height = nativeRect.bottom - nativeRect.top;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            iconRect = new Int32Rect(nativeRect.left, nativeRect.top, width, height);
            return true;
        }

        internal static bool TryCreateNotifyIconIdentifier(object? iconData, out NOTIFYICONIDENTIFIER identifier)
        {
            identifier = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            };

            if (iconData == null
                || WindowHandleField == null
                || TaskbarIconIdField == null
                || TaskbarIconGuidField == null)
            {
                return false;
            }

            if (WindowHandleField.GetValue(iconData) is not nint windowHandle)
            {
                return false;
            }

            if (TaskbarIconIdField.GetValue(iconData) is not uint taskbarIconId)
            {
                return false;
            }

            if (TaskbarIconGuidField.GetValue(iconData) is not Guid taskbarIconGuid)
            {
                return false;
            }

            identifier.hWnd = windowHandle;
            identifier.uID = taskbarIconId;
            identifier.guidItem = taskbarIconGuid;

            return taskbarIconGuid != Guid.Empty || (windowHandle != nint.Zero && taskbarIconId != 0);
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
        internal struct NOTIFYICONIDENTIFIER
        {
            public uint cbSize;
            public nint hWnd;
            public uint uID;
            public Guid guidItem;
        }
    }
}
