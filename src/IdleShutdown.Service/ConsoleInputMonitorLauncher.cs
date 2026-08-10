using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace IdleShutdown.ServiceApp;

internal sealed class ConsoleInputMonitorLauncher
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionId = 12;

    private readonly object _sync = new();
    private Process? _process;
    private int? _sessionId;
    private DateTime? _lastFailureLoggedUtc;

    public void EnsureRunning(int sessionId)
    {
        lock (_sync)
        {
            if (
                _process is not null &&
                !_process.HasExited &&
                _sessionId == sessionId)
            {
                return;
            }

            StopCore();

            try
            {
                _process = StartInSession(sessionId);
                _sessionId = sessionId;
                _lastFailureLoggedUtc = null;

                Log.Write(
                    $"Console input monitor started in session {sessionId}.");
            }
            catch (Exception ex)
            {
                var now = DateTime.UtcNow;

                if (
                    !_lastFailureLoggedUtc.HasValue ||
                    now - _lastFailureLoggedUtc.Value >= TimeSpan.FromMinutes(1))
                {
                    Log.Write(
                        $"Console input monitor could not start in session " +
                        $"{sessionId}: {ex.GetType().Name}: {ex.Message}");

                    _lastFailureLoggedUtc = now;
                }
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopCore();
        }
    }

    private void StopCore()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(3000);
            }
        }
        catch
        {
            // The process also exits when Windows tears down its session.
        }
        finally
        {
            _process.Dispose();
            _process = null;
            _sessionId = null;
        }
    }

    private static Process StartInSession(int sessionId)
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "IdleShutdown.Agent.exe");

        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "The console input monitor executable is missing.",
                executable);
        }

        EnablePrivilege("SeTcbPrivilege");

        IntPtr sourceToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        var processInfo = new ProcessInformation();

        try
        {
            if (!OpenProcessToken(
                    Process.GetCurrentProcess().Handle,
                    TokenAssignPrimary |
                    TokenDuplicate |
                    TokenQuery |
                    TokenAdjustSessionId,
                    out sourceToken))
            {
                throw LastWin32("OpenProcessToken");
            }

            if (!DuplicateTokenEx(
                    sourceToken,
                    TokenAssignPrimary |
                    TokenDuplicate |
                    TokenQuery |
                    TokenAdjustSessionId,
                    IntPtr.Zero,
                    SecurityImpersonation,
                    TokenPrimary,
                    out primaryToken))
            {
                throw LastWin32("DuplicateTokenEx");
            }

            var targetSession = unchecked((uint)sessionId);

            if (!SetTokenInformation(
                    primaryToken,
                    TokenSessionId,
                    ref targetSession,
                    sizeof(uint)))
            {
                throw LastWin32("SetTokenInformation(TokenSessionId)");
            }

            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>()
            };

            var commandLine = new StringBuilder(
                $"\"{executable}\" --machine-input-monitor {sessionId}");

            if (!CreateProcessAsUser(
                    primaryToken,
                    executable,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    0,
                    IntPtr.Zero,
                    Path.GetDirectoryName(executable),
                    ref startupInfo,
                    out processInfo))
            {
                throw LastWin32("CreateProcessAsUser");
            }

            return Process.GetProcessById(
                unchecked((int)processInfo.ProcessId));
        }
        finally
        {
            CloseIfValid(processInfo.Thread);
            CloseIfValid(processInfo.Process);
            CloseIfValid(primaryToken);
            CloseIfValid(sourceToken);
        }
    }

    private static void EnablePrivilege(string privilegeName)
    {
        IntPtr token = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(
                    Process.GetCurrentProcess().Handle,
                    TokenQuery | TokenAdjustPrivileges,
                    out token))
            {
                throw LastWin32("OpenProcessToken");
            }

            if (!LookupPrivilegeValue(
                    null,
                    privilegeName,
                    out var luid))
            {
                throw LastWin32("LookupPrivilegeValue");
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes
                {
                    Luid = luid,
                    Attributes = SePrivilegeEnabled
                }
            };

            if (!AdjustTokenPrivileges(
                    token,
                    false,
                    ref privileges,
                    0,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw LastWin32("AdjustTokenPrivileges");
            }

            var error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new Win32Exception(error, "AdjustTokenPrivileges failed.");
            }
        }
        finally
        {
            CloseIfValid(token);
        }
    }

    private static Win32Exception LastWin32(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation} failed ({error}).");
    }

    private static void CloseIfValid(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        ref uint tokenInformation,
        int tokenInformationLength);

    [DllImport(
        "advapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool LookupPrivilegeValue(
        string? systemName,
        string name,
        out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport(
        "advapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
