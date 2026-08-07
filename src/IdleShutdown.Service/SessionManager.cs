using System.Runtime.InteropServices;

namespace IdleShutdown.ServiceApp;

internal sealed record SessionSnapshot(
    int SessionId,
    bool IsLocked,
    DateTime? LastInputTime);

internal static class SessionManager
{
    private const int WtsUserName = 5;
    private const int WtsSessionInfoEx = 25;
    private const int WtsDisconnected = 4;
    private const int WtsSessionStateLock = 0;

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

    public static IReadOnlyList<SessionSnapshot> GetInteractiveSessions()
    {
        IntPtr sessions = IntPtr.Zero;

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

            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<WtsSessionInfo>(current);
                current = IntPtr.Add(current, itemSize);

                var extended = QueryExtendedInfo(item.SessionId);
                var userName = extended?.UserName ??
                               QueryString(item.SessionId, WtsUserName);

                if (string.IsNullOrWhiteSpace(userName))
                {
                    continue;
                }

                var isLocked =
                    item.State == WtsDisconnected ||
                    extended?.SessionState == WtsDisconnected ||
                    extended?.SessionFlags == WtsSessionStateLock;

                DateTime? lastInputTime = null;

                if (extended is { LastInputTime: > 0 })
                {
                    try
                    {
                        lastInputTime = DateTime.FromFileTimeUtc(
                            extended.Value.LastInputTime);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // Treat an invalid WTS timestamp as unavailable.
                    }
                }

                result.Add(
                    new SessionSnapshot(
                        item.SessionId,
                        isLocked,
                        lastInputTime));
            }

            return result;
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
        var extended = QueryExtendedInfo(sessionId);

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
