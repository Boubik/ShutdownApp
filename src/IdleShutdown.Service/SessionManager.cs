using System.Runtime.InteropServices;

namespace IdleShutdown.ServiceApp;

internal sealed record SessionSnapshot(
    int SessionId,
    bool IsLocked,
    DateTime? LastInputTime);

internal sealed record MachineSessionState(
    IReadOnlyList<SessionSnapshot> LoggedOnSessions,
    int? ConsoleSessionId,
    DateTime? ConsoleLastInputTime);

internal static class SessionManager
{
    private const int WtsUserName = 5;
    private const int WtsSessionInfoEx = 25;
    private const int WtsDisconnected = 4;
    private const int WtsSessionStateLock = 0;
    private const uint NoConsoleSession = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsInfoEx
    {
        public int Level;
        public WtsInfoExData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct WtsInfoExData
    {
        [FieldOffset(0)]
        public WtsInfoExLevel1 Level1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsInfoExLevel1
    {
        public int SessionId;
        public int SessionState;
        public int SessionFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
        public string WinStationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)]
        public string UserName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)]
        public string DomainName;

        public long LogonTime;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long CurrentTime;

        public int IncomingBytes;
        public int OutgoingBytes;
        public int IncomingFrames;
        public int OutgoingFrames;
        public int IncomingCompressedBytes;
        public int OutgoingCompressedBytes;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr serverHandle,
        int reserved,
        int version,
        out IntPtr sessionInfo,
        out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport(
        "wtsapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr serverHandle,
        int sessionId,
        int infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    public static MachineSessionState GetMachineSessionState()
    {
        IntPtr sessions = IntPtr.Zero;
        var consoleSessionValue = WTSGetActiveConsoleSessionId();
        var consoleSessionId =
            consoleSessionValue == NoConsoleSession
                ? (int?) null
                : unchecked((int) consoleSessionValue);

        try
        {
            if (!WTSEnumerateSessions(
                    IntPtr.Zero,
                    0,
                    1,
                    out sessions,
                    out var count))
            {
                throw new InvalidOperationException(
                    $"WTSEnumerateSessions failed: " +
                    $"{Marshal.GetLastWin32Error()}");
            }

            var result = new List<SessionSnapshot>();
            var itemSize = Marshal.SizeOf<WtsSessionInfo>();
            var current = sessions;
            DateTime? consoleLastInputTime = null;

            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<WtsSessionInfo>(current);
                current = IntPtr.Add(current, itemSize);

                var extended = QueryExtendedInfo(item.SessionId);
                var userName = extended?.UserName ??
                               QueryString(item.SessionId, WtsUserName);

                var lastInputTime = GetLastInputTime(extended);

                if (item.SessionId == consoleSessionId)
                {
                    consoleLastInputTime = lastInputTime;
                }

                if (
                    item.SessionId == 0 ||
                    string.IsNullOrWhiteSpace(userName))
                {
                    continue;
                }

                var isLocked =
                    item.State == WtsDisconnected ||
                    extended?.SessionState == WtsDisconnected ||
                    extended?.SessionFlags == WtsSessionStateLock;

                result.Add(
                    new SessionSnapshot(
                        item.SessionId,
                        isLocked,
                        lastInputTime));
            }

            if (
                consoleSessionId.HasValue &&
                !consoleLastInputTime.HasValue)
            {
                consoleLastInputTime = GetLastInputTime(
                    consoleSessionId.Value);
            }

            return new MachineSessionState(
                result,
                consoleSessionId,
                consoleLastInputTime);
        }
        finally
        {
            if (sessions != IntPtr.Zero)
            {
                WTSFreeMemory(sessions);
            }
        }
    }

    public static DateTime? GetLastInputTime(int sessionId)
    {
        return GetLastInputTime(
            QueryExtendedInfo(sessionId));
    }

    private static DateTime? GetLastInputTime(
        WtsInfoExLevel1? extended)
    {
        if (extended is not { LastInputTime: > 0 })
        {
            return null;
        }

        try
        {
            return DateTime.FromFileTimeUtc(
                extended.Value.LastInputTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static WtsInfoExLevel1? QueryExtendedInfo(int sessionId)
    {
        IntPtr buffer = IntPtr.Zero;

        try
        {
            if (!WTSQuerySessionInformation(
                    IntPtr.Zero,
                    sessionId,
                    WtsSessionInfoEx,
                    out buffer,
                    out var bytesReturned) ||
                buffer == IntPtr.Zero ||
                bytesReturned < Marshal.SizeOf<WtsInfoEx>())
            {
                return null;
            }

            var info = Marshal.PtrToStructure<WtsInfoEx>(buffer);

            return info.Level == 1
                ? info.Data.Level1
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    private static string QueryString(
        int sessionId,
        int infoClass)
    {
        IntPtr buffer = IntPtr.Zero;

        try
        {
            if (!WTSQuerySessionInformation(
                    IntPtr.Zero,
                    sessionId,
                    infoClass,
                    out buffer,
                    out var bytesReturned) ||
                buffer == IntPtr.Zero ||
                bytesReturned <= 1)
            {
                return string.Empty;
            }

            return Marshal.PtrToStringUni(buffer) ??
                   string.Empty;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                WTSFreeMemory(buffer);
            }
        }
    }
}
