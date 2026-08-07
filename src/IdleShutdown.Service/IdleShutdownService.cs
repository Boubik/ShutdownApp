using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;

namespace IdleShutdown.ServiceApp;

internal sealed class IdleShutdownService : ServiceBase
{
    private const string PipeName = "IdleShutdown";

    private readonly object _sync = new();
    private readonly Dictionary<int, DateTime> _lockedSince = new();
    private readonly Dictionary<int, DateTime?> _lockedSessionLastInput = new();
    private readonly NoUserActivityTracker _noUserTracker = new();

    private CancellationTokenSource? _cts;
    private Task? _pipeTask;
    private System.Threading.Timer? _timer;
    private int _machineStateCheckRunning;

    public IdleShutdownService()
    {
        ServiceName = "IdleShutdown";
        CanHandleSessionChangeEvent = true;
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

    protected override void OnSessionChange(
        SessionChangeDescription changeDescription)
    {
        base.OnSessionChange(changeDescription);

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

                    Log.Write(
                        $"Session {changeDescription.SessionId} locked; " +
                        "lock timer started.");
                    break;

                case SessionChangeReason.SessionUnlock:
                case SessionChangeReason.SessionLogon:
                    _lockedSince.Remove(
                        changeDescription.SessionId);

                    _lockedSessionLastInput.Remove(
                        changeDescription.SessionId);

                    _noUserTracker.ObserveLoggedOnUser();

                    Log.Write(
                        $"Session {changeDescription.SessionId}: " +
                        $"{changeDescription.Reason}; timers reset.");
                    break;

                case SessionChangeReason.SessionLogoff:
                    _lockedSince.Remove(
                        changeDescription.SessionId);

                    _lockedSessionLastInput.Remove(
                        changeDescription.SessionId);

                    Log.Write(
                        $"Session {changeDescription.SessionId}: " +
                        $"{changeDescription.Reason}; lock timer cleared.");
                    break;
            }
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

        RequestShutdown(
            "locked-session timeout",
            config);

        lock (_sync)
        {
            _lockedSince.Remove(
                sessionToShutdown.Value);

            _lockedSessionLastInput.Remove(
                sessionToShutdown.Value);
        }
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

        RequestShutdown(
            "no logged-on user timeout",
            config);
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

                    RequestShutdown(
                        "interactive idle timeout",
                        config);
                }
                else if (TryHandleLockedInputCommand(command))
                {
                    // Input movement is intentionally not logged. It may be
                    // reported many times while the lock screen is active.
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

        lock (_sync)
        {
            if (_lockedSince.ContainsKey(sessionId))
            {
                _lockedSince[sessionId] = DateTime.UtcNow;
            }
        }

        return true;
    }

    private static void RequestShutdown(
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
}
