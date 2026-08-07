using System.Runtime.InteropServices;

namespace IdleShutdown.AgentApp;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    public static TimeSpan GetIdleTime()
    {
        if (!TryGetLastInputTick(out var lastInputTick))
        {
            return TimeSpan.Zero;
        }

        var elapsed = unchecked(
            (uint)Environment.TickCount - lastInputTick);

        return TimeSpan.FromMilliseconds(elapsed);
    }

    public static bool TryGetLastInputTick(out uint lastInputTick)
    {
        var info = new LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
        };

        if (!GetLastInputInfo(ref info))
        {
            lastInputTick = 0;
            return false;
        }

        lastInputTick = info.dwTime;
        return true;
    }

    public static bool IsWorkstationLocked()
    {
        var desktop = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
        if (desktop == IntPtr.Zero) return true;
        CloseDesktop(desktop);
        return false;
    }

    public static bool IsForegroundWindowFullscreen()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return false;

        var screen = Screen.FromHandle(hwnd).Bounds;
        const int tolerance = 2;
        return Math.Abs(rect.Left - screen.Left) <= tolerance &&
               Math.Abs(rect.Top - screen.Top) <= tolerance &&
               Math.Abs(rect.Right - screen.Right) <= tolerance &&
               Math.Abs(rect.Bottom - screen.Bottom) <= tolerance;
    }
}
