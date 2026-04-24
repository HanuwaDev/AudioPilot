using System.Windows;

namespace AudioPilot.Tests.TestDoubles;

internal sealed class RecordingMessageBoxNative : MessageBoxService.IMessageBoxNative
{
    private readonly uint _currentProcessId = (uint)Environment.ProcessId;
    private readonly IntPtr _dialogHandle = new(101);
    private readonly IntPtr _textHandle = new(201);
    private readonly IntPtr _buttonHandle = new(202);

    public int ShowCallCount { get; private set; }
    public int FindWindowCallCount { get; private set; }
    public bool HasDialog { get; set; }
    public MessageBoxResult YesNoResponse { get; set; } = MessageBoxResult.OK;
    public MessageBoxResult DefaultResponse { get; set; } = MessageBoxResult.OK;
    public List<(string message, string caption)> YesNoMessages { get; } = [];
    public List<(string message, string caption)> SuccessMessages { get; } = [];
    public List<(string message, string caption)> WarningMessages { get; } = [];
    public List<(string message, string caption)> ErrorMessages { get; } = [];

    public IntPtr FindWindow(string? className, string? windowName)
    {
        FindWindowCallCount++;
        if (HasDialog && className == "#32770")
        {
            return _dialogHandle;
        }

        return IntPtr.Zero;
    }

    public IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? className, string? windowName)
    {
        if (hwndParent == _dialogHandle && className == "Static")
        {
            return _textHandle;
        }

        if (hwndParent == _dialogHandle && className == "Button")
        {
            return _buttonHandle;
        }

        return IntPtr.Zero;
    }

    public IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam) => IntPtr.Zero;
    public bool SetForegroundWindow(IntPtr hWnd) => true;
    public bool SetWindowText(IntPtr hWnd, string text) => true;

    public uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId)
    {
        processId = _currentProcessId;
        return 1;
    }

    public MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        ShowCallCount++;

        if (buttons == MessageBoxButton.YesNo)
        {
            YesNoMessages.Add((message, caption));
            return YesNoResponse;
        }

        switch (icon)
        {
            case MessageBoxImage.Information:
                SuccessMessages.Add((message, caption));
                break;
            case MessageBoxImage.Warning:
                WarningMessages.Add((message, caption));
                break;
            case MessageBoxImage.Error:
                ErrorMessages.Add((message, caption));
                break;
        }

        return DefaultResponse;
    }
}
