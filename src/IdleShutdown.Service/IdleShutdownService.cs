using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;

namespace IdleShutdown.ServiceApp;

internal sealed class IdleShutdownService : ServiceBase
{
    private const string PipeName = "IdleShutdown";
    private const int MachineStateShutdownGraceSeconds = 60;

    private readonly object _sync = new();
    private readonly Dictionary<int, DateTime> _lockedSince = new();
    private readonly Dictionary<int, DateTime?> _lockedSessionLastInput = new();
    private readonly Dictionary<int, DateTime> _lockedDiagnosticLoggedAt = new();
    private readonly Dictionary<int, int> _lockedPipeResetCount = new();
    private readonly NoUserActivityTracker _noUserTracker = new();
    private readonly ConsoleInputMonitorLauncher _inputMonitor = new();

    private CancellationTokenSource? _cts;
    private Task? _pipeTask;
    private System.Threading.Timer? _timer;
    private DateTime? _noUserDiagnosticLoggedAt;
    private int _noUserHelperResetCount;
    private PendingShutdown? _pendingShutdown;
    private bool _systemShutdownInProgress;
    private int _machineStateCheckRunning;

    public IdleShutdownService()
    {
        ServiceName = "IdleShutdown";
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
        CanStop = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        _cts = new CancellationTokenSource();

        _pipeTask = Task.Run(
            () => PipeLoopAsync(_cts.Token));

        var config = AppConfig.Load();
        var checkInterval = TimeSpan.FromSeconds(
            config.CheckIntervalSeconds);

        _timer = new System.Threading.Timer(
            CheckMachineState,
            null,
            TimeSpan.Zero,
            checkInterval);

        Log.Write(
            $"Service started; state check interval is " +
            $"{config.CheckIntervalSeconds} second(s).");
    }

    protected override void OnStop()
    {
        if (!_systemShutdownInProgress)
        {
            CancelPendingShutdown("service is stopping");
        }

        _inputMonitor.Stop();

        _cts?.Cancel();
        _timer?.Dispose();

        try
        {
            _pipeTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Service is stopping.
        }

        Log.Write("Service stopped.");
    }

    protected override void OnShutdown()
    {
        _systemShutdownInProgress = true;
        Log.Write("System shutdown notification received.");
        base.OnShutdown();
    }

    protected override void OnSessionChange(
        SessionChangeDescription changeDescription)
    {
        base.OnSessionChange(changeDescription);
        var cancelPendingShutdown = false;

        lock (_sync)
        {
            switch (changeDescription.Reason)
            {
                case SessionChangeReason.SessionLock:
                case SessionChangeReason.RemoteDisconnect:
                    _lockedSince[changeDescription.SessionId] =
                        DateTime.UtcNow;

                    _lockedSessionLastInput[changeDescription.SessionId] =
                        SessionManager.GetLastInputTime(changeDescription.SessionId);

                    _lockedDiagnosticLoggedAt.Remove(changeDescription.SessionId);
                    _lockedPipeResetCount[changeDescription.SessionId] = 0;

                    Log.Write(
                        $"Session {changeDescription.SessionId} locked; " +
                        "lock timer started.");
                    break;

                case SessionChangeReason.SessionUnlock:
                case SessionChangeReason.SessionLogon:
                    cancelPendingShutdown = true;

                    _lockedSince.Remove(
                        changeDescription.SessionId);

                    _lockedSessionLastInput.Remove(
                        changeDescription.SessionId);

                    _lockedDiagnosticLoggedAt.Remove(
                        changeDescription.SessionId);

                    _lockedPipeResetCount.Remove(
                        changeDescription.SessionId);

                    _noUserTracker.ObserveLoggedOnUser();
                    _noUserDiagnosticLoggedAt = null;
                    _noUserHelperResetCount = 0;

                    Log.Write(
                        $"Session {changeDescription.SessionId}: " +
                        $"{changeDescription.Reason}; timers reset.");
                    break;

                case SessionChangeReason.SessionLogoff:
                    _lockedSince.Remove(
                        changeDescription.SessionId);

                    _lockedSessionLastInput.Remove(
                        changeDescription.SessionId);

                    _lockedDiagnosticLoggedAt.Remove(
                        changeDescription.SessionId);

                    _lockedPipeResetCount.Remove(
                        changeDescription.SessionId);

                    Log.Write(
                        $"Session {changeDescription.SessionId}: " +
                        $"{changeDescription.Reason}; lock timer cleared.");
                    break;
            }
        }

        if (cancelPendingShutdown)
        {
            CancelPendingShutdown(
                $"session {changeDescription.SessionId} " +
                $"reported {changeDescription.Reason}");
        }
    }

    private void CheckMachineState(object? state)
    {
        if (Interlocked.Exchange(ref _machineStateCheckRunning, 1) != 0)
        {
            return;
        }

        try
        {
            var config = AppConfig.Load();
            var machineState = SessionManager.GetMachineSessionState();

            if (machineState.ConsoleSessionId.HasValue)
            {
                _inputMonitor.EnsureRunning(
                    machineState.ConsoleSessionId.Value);
            }
            else
            {
                _inputMonitor.Stop();
            }

            CancelPendingShutdownIfUserBecameActive(machineState);

            CheckLockedSessions(
                config,
                machineState.LoggedOnSessions);

            CheckNoUserState(
                config,
                machineState);
        }
        catch (Exception ex)
        {
            Log.Write(
                $"Machine-state check failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _machineStateCheckRunning, 0);
        }
    }

    private void CheckLockedSessions(
        AppConfig config,
        IReadOnlyList<SessionSnapshot> sessions)
    {
        int? sessionToShutdown = null;
        var lockedSessions = sessions
            .Where(session => session.IsLocked)
            .ToDictionary(session => session.SessionId);

        lock (_sync)
        {
            foreach (var trackedSessionId in _lockedSince.Keys.ToArray())
            {
                if (lockedSessions.ContainsKey(trackedSessionId))
                {
                    continue;
                }

                _lockedSince.Remove(trackedSessionId);
                _lockedSessionLastInput.Remove(trackedSessionId);
                _lockedDiagnosticLoggedAt.Remove(trackedSessionId);
                _lockedPipeResetCount.Remove(trackedSessionId);

                Log.Write(
                    $"Session {trackedSessionId} is no longer locked; " +
                    "lock timer cleared.");
            }

            foreach (var session in lockedSessions.Values)
            {
                if (!_lockedSince.ContainsKey(session.SessionId))
                {
                    _lockedSince[session.SessionId] = DateTime.UtcNow;
                    _lockedSessionLastInput[session.SessionId] =
                        session.LastInputTime;

                    _lockedPipeResetCount[session.SessionId] = 0;

                    Log.Write(
                        $"Locked session {session.SessionId} detected; " +
                        "lock timer started.");
                }

                var currentLastInput = session.LastInputTime;

                _lockedSessionLastInput.TryGetValue(
                    session.SessionId,
                    out var previousLastInput);

                if (SessionInputActivity.HasChanged(
                        previousLastInput,
                        currentLastInput))
                {
                    _lockedSessionLastInput[session.SessionId] =
                        currentLastInput;

                    _lockedSince[session.SessionId] =
                        DateTime.UtcNow;

                    continue;
                }

                WriteLockedDiagnosticIfDue(
                    config,
                    session.SessionId,
                    currentLastInput);

                if (
                    DateTime.UtcNow - _lockedSince[session.SessionId] >=
                    TimeSpan.FromMinutes(config.LockedMinutes))
                {
                    // Final race-condition protection: re-read input just
                    // before choosing this session for shutdown.
                    var finalLastInput =
                        SessionManager.GetLastInputTime(session.SessionId);

                    if (
                        SessionInputActivity.HasChanged(
                            currentLastInput,
                            finalLastInput) ||
                        (
                            finalLastInput.HasValue &&
                            DateTime.UtcNow - finalLastInput.Value <
                            TimeSpan.FromSeconds(
                                Math.Max(
                                    10,
                                    config.CheckIntervalSeconds * 2))
                        ))
                    {
                        _lockedSessionLastInput[session.SessionId] =
                            finalLastInput;

                        _lockedSince[session.SessionId] =
                            DateTime.UtcNow;

                        continue;
                    }

                    sessionToShutdown = session.SessionId;
                    break;
                }
            }
        }

        if (!sessionToShutdown.HasValue)
        {
            return;
        }

        Log.Write(
            $"Locked timeout reached for session " +
            $"{sessionToShutdown.Value}.");

        ScheduleMachineStateShutdown(
            "locked-session timeout",
            config,
            PendingShutdownKind.LockedSession,
            sessionToShutdown.Value);

        lock (_sync)
        {
            _lockedSince.Remove(
                sessionToShutdown.Value);

            _lockedSessionLastInput.Remove(
                sessionToShutdown.Value);

            _lockedDiagnosticLoggedAt.Remove(
                sessionToShutdown.Value);

            _lockedPipeResetCount.Remove(
                sessionToShutdown.Value);
        }
    }

    private void WriteLockedDiagnosticIfDue(
        AppConfig config,
        int sessionId,
        DateTime? wtsLastInput)
    {
        if (!config.DryRun)
        {
            return;
        }

        var now = DateTime.UtcNow;

        if (
            _lockedDiagnosticLoggedAt.TryGetValue(sessionId, out var lastLog) &&
            now - lastLog < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lockedPipeResetCount.TryGetValue(
            sessionId,
            out var pipeResets);

        var timerSeconds = _lockedSince.TryGetValue(
            sessionId,
            out var lockedSince)
                ? Math.Max(0, (now - lockedSince).TotalSeconds)
                : 0;

        Log.Write(
            $"DRY RUN lock diagnostic: service heartbeat; " +
            $"session={sessionId}; " +
            $"wtsLastInput={wtsLastInput?.ToString("O") ?? "unavailable"}; " +
            $"pipeResets={pipeResets}; " +
            $"timerSeconds={timerSeconds:F0}.");

        _lockedDiagnosticLoggedAt[sessionId] = now;
        _lockedPipeResetCount[sessionId] = 0;
    }
    private void CheckNoUserState(
        AppConfig config,
        MachineSessionState machineState)
    {
        var now = DateTime.UtcNow;
        var timeout = TimeSpan.FromMinutes(
            config.NoUserMinutes);
        NoUserObservation observation;

        lock (_sync)
        {
            if (machineState.LoggedOnSessions.Count > 0)
            {
                if (_noUserTracker.IsMonitoring)
                {
                    Log.Write(
                        "A logged-on user was detected; " +
                        "no-user timer cleared.");
                }

                _noUserTracker.ObserveLoggedOnUser();
                _noUserDiagnosticLoggedAt = null;
                _noUserHelperResetCount = 0;
                return;
            }

            observation = _noUserTracker.ObserveNoUser(
                now,
                timeout,
                machineState.ConsoleSessionId,
                machineState.ConsoleLastInputTime);

            if (observation == NoUserObservation.Started)
            {
                Log.Write(
                    $"No logged-on user detected; monitoring " +
                    $"sign-in-screen input and shutting down after " +
                    $"{config.NoUserMinutes} minute(s).");
            }

            WriteNoUserDiagnosticIfDue(
                config,
                now,
                machineState,
                observation);

            if (observation != NoUserObservation.TimeoutReached)
            {
                return;
            }
        }

        // Re-read the complete state immediately before shutdown. This covers
        // a user starting to sign in or pressing a key between periodic checks.
        var finalState = SessionManager.GetMachineSessionState();
        var finalNow = DateTime.UtcNow;

        lock (_sync)
        {
            if (finalState.LoggedOnSessions.Count > 0)
            {
                _noUserTracker.ObserveLoggedOnUser();
                _noUserDiagnosticLoggedAt = null;
                _noUserHelperResetCount = 0;

                Log.Write(
                    "No-user shutdown cancelled because a logged-on " +
                    "user was detected during the final check.");

                return;
            }

            observation = _noUserTracker.ObserveNoUser(
                finalNow,
                timeout,
                finalState.ConsoleSessionId,
                finalState.ConsoleLastInputTime);

            if (observation != NoUserObservation.TimeoutReached)
            {
                return;
            }

            // Prevent rapid repeated DryRun actions.
            _noUserTracker.ResetAfterAction(finalNow);
        }

        Log.Write("No-user timeout reached after final input check.");

        ScheduleMachineStateShutdown(
            "no logged-on user timeout",
            config,
            PendingShutdownKind.NoUser,
            null);
    }

    private void WriteNoUserDiagnosticIfDue(
        AppConfig config,
        DateTime now,
        MachineSessionState machineState,
        NoUserObservation observation)
    {
        if (!config.DryRun)
        {
            return;
        }

        if (
            observation != NoUserObservation.InputDetected &&
            _noUserDiagnosticLoggedAt.HasValue &&
            now - _noUserDiagnosticLoggedAt.Value < TimeSpan.FromSeconds(10))
        {
            return;
        }

        Log.Write(
            $"DRY RUN no-user diagnostic: service heartbeat; " +
            $"consoleSession={machineState.ConsoleSessionId?.ToString() ?? "unavailable"}; " +
            $"wtsLastInput={machineState.ConsoleLastInputTime?.ToString("O") ?? "unavailable"}; " +
            $"helperResets={_noUserHelperResetCount}; " +
            $"observation={observation}; " +
            $"timerSeconds={_noUserTracker.GetElapsedSeconds(now):F0}.");

        _noUserDiagnosticLoggedAt = now;
        _noUserHelperResetCount = 0;
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(
                    WellKnownSidType.LocalSystemSid,
                    null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(
                    WellKnownSidType.BuiltinAdministratorsSid,
                    null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(
                    WellKnownSidType.AuthenticatedUserSid,
                    null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(
                    WellKnownSidType.BuiltinUsersSid,
                    null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

        return security;
    }

    private async Task PipeLoopAsync(
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe =
                    NamedPipeServerStreamAcl.Create(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        4096,
                        4096,
                        CreatePipeSecurity());

                await pipe.WaitForConnectionAsync(token);

                using var reader = new StreamReader(pipe);
                var command = await reader.ReadLineAsync(token);

                if (
                    string.Equals(
                        command,
                        "SHUTDOWN_IDLE",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var config = AppConfig.Load();

                    Log.Write(
                        "Shutdown requested by interactive agent " +
                        "after idle warning.");

                    RequestImmediateShutdown(
                        "interactive idle timeout",
                        config);
                }
                else if (TryHandleLockedInputCommand(command))
                {
                    // Input movement is intentionally not logged. It may be
                    // reported many times while the lock screen is active.
                }
                else if (TryHandleMachineInputCommand(command))
                {
                    // The console-session helper reports only changes, and
                    // physical input is intentionally not written to the log.
                }
                else
                {
                    Log.Write(
                        "Unknown or empty named pipe command ignored.");
                }
            }
            catch (OperationCanceledException)
                when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Write(
                    $"Pipe listener error: " +
                    $"{ex.GetType().Name}: {ex.Message}");

                try
                {
                    await Task.Delay(1000, token);
                }
                catch
                {
                    // Service is stopping.
                }
            }
        }
    }

    private bool TryHandleLockedInputCommand(string? command)
    {
        const string prefix = "LOCK_INPUT:";

        if (
            command is null ||
            !command.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(
                command[prefix.Length..],
                out var sessionId) ||
            sessionId < 0)
        {
            return false;
        }

        HandleSessionInput(sessionId);
        return true;
    }

    private bool TryHandleMachineInputCommand(string? command)
    {
        const string prefix = "MACHINE_INPUT:";

        if (
            command is null ||
            !command.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(
                command[prefix.Length..],
                out var sessionId) ||
            sessionId < 0)
        {
            return false;
        }

        HandleSessionInput(sessionId);
        return true;
    }

    private void HandleSessionInput(int sessionId)
    {
        var cancelPending = false;

        lock (_sync)
        {
            if (_lockedSince.ContainsKey(sessionId))
            {
                _lockedSince[sessionId] = DateTime.UtcNow;

                _lockedPipeResetCount.TryGetValue(
                    sessionId,
                    out var resetCount);

                _lockedPipeResetCount[sessionId] = resetCount + 1;
            }

            if (_noUserTracker.ObserveExternalInput(
                    DateTime.UtcNow,
                    sessionId))
            {
                _noUserHelperResetCount++;
            }

            cancelPending =
                _pendingShutdown is
                {
                    Kind: PendingShutdownKind.NoUser
                } ||
                _pendingShutdown is
                {
                    Kind: PendingShutdownKind.LockedSession,
                    SessionId: var pendingSessionId
                } &&
                pendingSessionId == sessionId;
        }

        if (cancelPending)
        {
            CancelPendingShutdown(
                $"physical input was detected in console session {sessionId}");
        }
    }

    private void ScheduleMachineStateShutdown(
        string reason,
        AppConfig config,
        PendingShutdownKind kind,
        int? sessionId)
    {
        if (config.DryRun)
        {
            Log.Write(
                $"DRY RUN: {MachineStateShutdownGraceSeconds}-second " +
                $"shutdown grace period skipped ({reason}).");

            return;
        }

        lock (_sync)
        {
            if (_pendingShutdown is not null)
            {
                return;
            }

            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = Path.Combine(
                            Environment.SystemDirectory,
                            "shutdown.exe"),

                        Arguments =
                            $"/s /t {MachineStateShutdownGraceSeconds} " +
                            $"/d p:0:0 " +
                            $"/c \"Automatic shutdown: {reason}. " +
                            $"Sign in or unlock this computer to cancel.\"",

                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                _pendingShutdown = new PendingShutdown(
                    kind,
                    sessionId,
                    reason);

                Log.Write(
                    $"Shutdown scheduled in " +
                    $"{MachineStateShutdownGraceSeconds} second(s) " +
                    $"({reason}); sign-in or unlock will cancel it.");
            }
            catch (Exception ex)
            {
                Log.Write(
                    $"Shutdown scheduling failed: " +
                    $"{ex.GetType().Name}: {ex.Message}");

                return;
            }
        }

        // Close the small race between the final pre-shutdown state read and
        // scheduling the grace period. Later changes are covered by session
        // notifications and the periodic state check.
        try
        {
            CancelPendingShutdownIfUserBecameActive(
                SessionManager.GetMachineSessionState());
        }
        catch (Exception ex)
        {
            Log.Write(
                $"Post-schedule state check failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CancelPendingShutdownIfUserBecameActive(
        MachineSessionState machineState)
    {
        PendingShutdown? pending;

        lock (_sync)
        {
            pending = _pendingShutdown;
        }

        if (pending is null)
        {
            return;
        }

        var shouldCancel = pending.Kind switch
        {
            PendingShutdownKind.NoUser =>
                machineState.LoggedOnSessions.Count > 0,

            PendingShutdownKind.LockedSession =>
                machineState.LoggedOnSessions.Any(
                    session =>
                        session.SessionId == pending.SessionId &&
                        !session.IsLocked),

            _ => false
        };

        if (shouldCancel)
        {
            CancelPendingShutdown(
                "a user signed in or unlocked the computer");
        }
    }

    private void CancelPendingShutdown(string cancellationReason)
    {
        lock (_sync)
        {
            if (_pendingShutdown is null)
            {
                return;
            }

            try
            {
                using var process = Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = Path.Combine(
                            Environment.SystemDirectory,
                            "shutdown.exe"),

                        Arguments = "/a",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                if (process is null)
                {
                    throw new InvalidOperationException(
                        "shutdown.exe did not start.");
                }

                if (!process.WaitForExit(5000))
                {
                    throw new System.TimeoutException(
                        "shutdown.exe /a did not exit within 5 seconds.");
                }

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"shutdown.exe /a exited with code " +
                        $"{process.ExitCode}.");
                }

                Log.Write(
                    $"Pending shutdown cancelled because " +
                    $"{cancellationReason} ({_pendingShutdown.Reason}).");

                _pendingShutdown = null;
            }
            catch (Exception ex)
            {
                Log.Write(
                    $"Pending shutdown cancellation failed: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void RequestImmediateShutdown(
        string reason,
        AppConfig config)
    {
        if (config.DryRun)
        {
            Log.Write(
                $"DRY RUN: shutdown skipped ({reason}).");
            return;
        }

        try
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.SystemDirectory,
                        "shutdown.exe"),

                    Arguments =
                        $"/s /t 0 /d p:0:0 " +
                        $"/c \"Automatic shutdown: {reason}\"",

                    UseShellExecute = false,
                    CreateNoWindow = true
                });

            Log.Write(
                $"Shutdown command executed ({reason}).");
        }
        catch (Exception ex)
        {
            Log.Write(
                $"Shutdown failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed record PendingShutdown(
        PendingShutdownKind Kind,
        int? SessionId,
        string Reason);

    private enum PendingShutdownKind
    {
        LockedSession,
        NoUser
    }
}
