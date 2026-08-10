using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace IdleShutdown.ServiceApp;

internal sealed class ConsoleInputMonitorLauncher
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint MaximumAllowed = 0x02000000;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

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

        var primaryToken = DuplicateConsoleSessionSystemToken(sessionId);
        var processInfo = new ProcessInformation();

        try
        {
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
        }
    }

    private static IntPtr DuplicateConsoleSessionSystemToken(
        int sessionId)
    {
        var matchingWinlogonFound = false;
        Exception? lastError = null;

        foreach (var candidate in Process.GetProcessesByName("winlogon"))
        {
            using (candidate)
            {
                if (
                    !ProcessIdToSessionId(
                        unchecked((uint)candidate.Id),
                        out var candidateSessionId) ||
                    candidateSessionId != unchecked((uint)sessionId))
                {
                    continue;
                }

                matchingWinlogonFound = true;
                IntPtr processHandle = IntPtr.Zero;
                IntPtr sourceToken = IntPtr.Zero;

                try
                {
                    processHandle = OpenProcess(
                        ProcessQueryLimitedInformation,
                        false,
                        unchecked((uint)candidate.Id));

                    if (processHandle == IntPtr.Zero)
                    {
                        throw LastWin32("OpenProcess(winlogon)");
                    }

                    if (!OpenProcessToken(
                            processHandle,
                            TokenDuplicate | TokenQuery,
                            out sourceToken))
                    {
                        throw LastWin32("OpenProcessToken(winlogon)");
                    }

                    if (!DuplicateTokenEx(
                            sourceToken,
                            MaximumAllowed,
                            IntPtr.Zero,
                            SecurityImpersonation,
                            TokenPrimary,
                            out var primaryToken))
                    {
                        throw LastWin32("DuplicateTokenEx(winlogon)");
                    }

                    return primaryToken;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
                finally
                {
                    CloseIfValid(sourceToken);
                    CloseIfValid(processHandle);
                }
            }
        }

        if (!matchingWinlogonFound)
        {
            throw new InvalidOperationException(
                $"No winlogon process was found in console session " +
                $"{sessionId}.");
        }

        throw new InvalidOperationException(
            $"The winlogon token in console session {sessionId} " +
            $"could not be duplicated: " +
            $"{lastError?.Message ?? "unknown error"}",
            lastError);
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
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(
        uint processId,
        out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
