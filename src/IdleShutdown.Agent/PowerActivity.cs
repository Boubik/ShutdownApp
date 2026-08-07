using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IdleShutdown.AgentApp;

internal static class PowerActivity
{
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int GwlStyle = -16;
    private const long WsChild = 0x40000000L;

    private static readonly HashSet<string> IgnoredProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer",
            "dwm",
            "ShellExperienceHost",
            "StartMenuExperienceHost",
            "SearchHost",
            "LockApp",
            "LogonUI",
            "ApplicationFrameHost"
        };

    public static bool ShouldPauseForPresentation()
    {
        // Preferred signal: an application or driver explicitly tells
        // Windows that the display/system must remain active.
        if (HasActiveWindowsPowerRequest())
        {
            return true;
        }

        // Fallback for applications that do not create a Windows power
        // request but really occupy the complete foreground monitor.
        return IsForegroundWindowTrulyFullscreen();
    }

    private static bool HasActiveWindowsPowerRequest()
    {
        uint executionState = 0;

        var status = CallNtPowerInformation(
            PowerInformationLevel.SystemExecutionState,
            IntPtr.Zero,
            0,
            out executionState,
            sizeof(uint));

        if (status != 0)
        {
            return false;
        }

        return (
            executionState &
            (EsSystemRequired | EsDisplayRequired)
        ) != 0;
    }

    private static bool IsForegroundWindowTrulyFullscreen()
    {
        var window = GetForegroundWindow();

        if (
            window == IntPtr.Zero ||
            !IsWindowVisible(window) ||
            IsIconic(window))
        {
            return false;
        }

        var style = GetWindowLongPtr(window, GwlStyle).ToInt64();

        if ((style & WsChild) != 0)
        {
            return false;
        }

        GetWindowThreadProcessId(
            window,
            out var processId);

        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process =
                Process.GetProcessById(
                    unchecked((int)processId));

            if (
                IgnoredProcesses.Contains(
                    process.ProcessName))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        if (!GetWindowRect(window, out var windowRect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(
            window,
            MonitorDefaultToNearest);

        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };

        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        const int tolerance = 2;

        return
            Math.Abs(
                windowRect.Left -
                monitorInfo.Monitor.Left) <= tolerance &&
            Math.Abs(
                windowRect.Top -
                monitorInfo.Monitor.Top) <= tolerance &&
            Math.Abs(
                windowRect.Right -
                monitorInfo.Monitor.Right) <= tolerance &&
            Math.Abs(
                windowRect.Bottom -
                monitorInfo.Monitor.Bottom) <= tolerance;
    }

    private enum PowerInformationLevel
    {
        SystemExecutionState = 16
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        PowerInformationLevel informationLevel,
        IntPtr inputBuffer,
        uint inputBufferSize,
        out uint outputBuffer,
        uint outputBufferSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(
        IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(
        IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr window,
        out Rect rectangle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(
        IntPtr window,
        uint flags);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitor,
        ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(
        IntPtr window,
        int index);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetWindowLongW")]
    private static extern IntPtr GetWindowLong32(
        IntPtr window,
        int index);

    private static IntPtr GetWindowLongPtr(
        IntPtr window,
        int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index)
            : GetWindowLong32(window, index);
    }
}
