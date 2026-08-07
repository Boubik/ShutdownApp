using System.Runtime.InteropServices;

namespace IdleShutdown.AgentApp;

internal static class PopupWindowManager
{
    private static readonly IntPtr HwndTopmost =
        new(-1);

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;

    private const int SwRestore = 9;

    public static void BringToForeground(
        Form form)
    {
        if (
            form.IsDisposed ||
            !form.IsHandleCreated)
        {
            return;
        }

        var handle = form.Handle;

        ShowWindow(
            handle,
            SwRestore);

        SetWindowPos(
            handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove |
            SwpNoSize |
            SwpShowWindow);

        BringWindowToTop(handle);

        var foregroundWindow =
            GetForegroundWindow();

        var currentThread =
            GetCurrentThreadId();

        var foregroundThread =
            foregroundWindow == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(
                    foregroundWindow,
                    out _);

        var targetThread =
            GetWindowThreadProcessId(
                handle,
                out _);

        var attachedToForeground = false;
        var attachedToTarget = false;

        try
        {
            if (
                foregroundThread != 0 &&
                foregroundThread != currentThread)
            {
                attachedToForeground =
                    AttachThreadInput(
                        currentThread,
                        foregroundThread,
                        true);
            }

            if (
                targetThread != 0 &&
                targetThread != currentThread)
            {
                attachedToTarget =
                    AttachThreadInput(
                        currentThread,
                        targetThread,
                        true);
            }

            SetForegroundWindow(handle);
            SetActiveWindow(handle);
            SetFocus(handle);

            form.TopMost = true;
            form.Activate();
            form.BringToFront();
        }
        finally
        {
            if (attachedToTarget)
            {
                AttachThreadInput(
                    currentThread,
                    targetThread,
                    false);
            }

            if (attachedToForeground)
            {
                AttachThreadInput(
                    currentThread,
                    foregroundThread,
                    false);
            }
        }
    }

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(
        IntPtr window,
        int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(
        IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(
        IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(
        IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(
        IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(
        uint sourceThread,
        uint targetThread,
        bool attach);
}
