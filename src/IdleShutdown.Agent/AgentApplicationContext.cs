using System.IO.Pipes;

namespace IdleShutdown.AgentApp;

internal sealed class AgentApplicationContext : ApplicationContext
{
    private const string PipeName = "IdleShutdown";
    private const int LockedInputPollMilliseconds = 250;
    private const int SessionStateHeartbeatSeconds = 10;
    private const int SessionStateRetrySeconds = 2;

    private readonly System.Windows.Forms.Timer _timer;
    private readonly int _sessionId;

    private bool _warningVisible;
    private bool _waitingForInputAfterExpiredWarning;
    private uint? _inputTickAtExpiredWarning;
    private bool _presentationDeferralLogged;
    private bool _wasWorkstationLocked;
    private uint? _lastObservedInputTick;
    private DateTime? _lastLockDiagnosticUtc;
    private int _lockInputChanges;
    private int _lockPipeSuccesses;
    private string? _lastLockPipeError;
    private bool? _lastReportedLockedState;
    private DateTime? _lastSessionStateReportUtc;
    private DateTime? _lastSessionStateAttemptUtc;

    public AgentApplicationContext()
    {
        _sessionId = System.Diagnostics.Process
            .GetCurrentProcess()
            .SessionId;

        var config = AppConfig.Load();

        _timer = new System.Windows.Forms.Timer
        {
            Interval =
                Math.Max(
                    1,
                    config.CheckIntervalSeconds) * 1000
        };

        _timer.Tick += TimerOnTick;
        _timer.Start();

        if (NativeMethods.TryGetLastInputTick(out var lastInputTick))
        {
            _lastObservedInputTick = lastInputTick;
        }

        Log.Write(
            $"Agent started as " +
            $"{Environment.UserDomainName}\\" +
            $"{Environment.UserName} in session " +
            $"{_sessionId}.");
    }

    private void TimerOnTick(
        object? sender,
        EventArgs e)
    {
        if (_warningVisible)
        {
            return;
        }

        var config =
            AppConfig.Load();

        if (!config.IsValid)
        {
            _timer.Interval = 5000;
            return;
        }

        var isWorkstationLocked = NativeMethods.IsWorkstationLocked();
        ReportSessionState(isWorkstationLocked);

        if (isWorkstationLocked)
        {
            MonitorLockedSessionInput(config.DryRun);
            return;
        }

        if (_wasWorkstationLocked)
        {
            _wasWorkstationLocked = false;
            _lastLockDiagnosticUtc = null;
            _lockInputChanges = 0;
            _lockPipeSuccesses = 0;
            _lastLockPipeError = null;

            Log.Write(
                "Workstation unlocked; lock-screen input " +
                "monitoring stopped.");
        }

        _timer.Interval = Math.Max(
            1,
            config.CheckIntervalSeconds) * 1000;

        if (NativeMethods.TryGetLastInputTick(out var observedInputTick))
        {
            var inputChanged =
                _lastObservedInputTick.HasValue &&
                observedInputTick != _lastObservedInputTick.Value;

            _lastObservedInputTick = observedInputTick;

            if (inputChanged)
            {
                SendQuietCommand(
                    $"SESSION_INPUT:{_sessionId}",
                    out _);
            }

            if (_waitingForInputAfterExpiredWarning)
            {
                if (
                    _inputTickAtExpiredWarning.HasValue &&
                    observedInputTick == _inputTickAtExpiredWarning.Value)
                {
                    return;
                }

                _waitingForInputAfterExpiredWarning = false;
                _inputTickAtExpiredWarning = null;
            }
        }
        else if (_waitingForInputAfterExpiredWarning)
        {
            return;
        }

        var idle = NativeMethods.GetIdleTime();
        var machineIdle = MachineActivitySignal.GetIdleTime();

        if (machineIdle.HasValue && machineIdle.Value < idle)
        {
            idle = machineIdle.Value;
        }

        var sharedWarningDeadlineUtc =
            MachineWarningSignal.GetActiveDeadlineUtc();
        var joiningSharedWarning = sharedWarningDeadlineUtc.HasValue;

        if (!WarningDisplayPolicy.ShouldShow(
                idle,
                config.IdleMinutes,
                sharedWarningDeadlineUtc,
                DateTime.UtcNow))
        {
            _presentationDeferralLogged = false;
            return;
        }

        if (
            config.PauseWhenFullscreen &&
            PowerActivity.ShouldPauseForPresentation())
        {
            if (joiningSharedWarning)
            {
                SendQuietCommand(
                    $"SESSION_INPUT:{_sessionId}",
                    out _);
            }

            if (!_presentationDeferralLogged)
            {
                Log.Write(
                    "Idle timeout reached, but shutdown was " +
                    "deferred by an active power request or " +
                    "a fullscreen foreground window.");

                _presentationDeferralLogged = true;
            }

            return;
        }

        _warningVisible = true;
        _timer.Stop();

        try
        {
            var inputAtStart =
                NativeMethods.GetIdleTime();

            if (!sharedWarningDeadlineUtc.HasValue)
            {
                sharedWarningDeadlineUtc = SendWarningActive(
                    config.WarningSeconds);
            }

            var visibleWarningSeconds =
                WarningDisplayPolicy.GetVisibleSeconds(
                    config.WarningSeconds,
                    sharedWarningDeadlineUtc,
                    DateTime.UtcNow);

            Log.Write(
                $"Idle timeout reached after " +
                $"{inputAtStart.TotalSeconds:F0} second(s); " +
                $"showing a {visibleWarningSeconds}-second " +
                (joiningSharedWarning
                    ? "shared warning."
                    : "warning."));

            using var dialog =
                new WarningForm(
                    visibleWarningSeconds,
                    inputAtStart);

            var result =
                dialog.ShowDialog();

            if (result == DialogResult.OK)
            {
                if (NativeMethods.TryGetLastInputTick(out var inputTick))
                {
                    _waitingForInputAfterExpiredWarning = true;
                    _inputTickAtExpiredWarning = inputTick;
                }

                Log.Write(
                    "Warning expired without user activity; " +
                    "requesting shutdown.");

                if (!SendShutdownRequest())
                {
                    _waitingForInputAfterExpiredWarning = false;
                    _inputTickAtExpiredWarning = null;
                }
            }
            else
            {
                SendQuietCommand(
                    $"SESSION_INPUT:{_sessionId}",
                    out _);

                Log.Write(
                    "Shutdown warning cancelled by user " +
                    "activity or button.");
            }
        }
        finally
        {
            _warningVisible = false;
            _timer.Start();
        }
    }

    private void MonitorLockedSessionInput(bool diagnosticLogging)
    {
        _timer.Interval = LockedInputPollMilliseconds;

        if (!_wasWorkstationLocked)
        {
            _wasWorkstationLocked = true;
            Log.Write(
                "Workstation locked; session-specific lock-screen " +
                "input monitoring started.");
        }

        if (!NativeMethods.TryGetLastInputTick(out var currentInputTick))
        {
            return;
        }

        if (
            _lastObservedInputTick.HasValue &&
            currentInputTick != _lastObservedInputTick.Value)
        {
            _lockInputChanges++;

            if (SendQuietCommand(
                    $"LOCK_INPUT:{_sessionId}",
                    out var error))
            {
                _lockPipeSuccesses++;
            }
            else
            {
                _lastLockPipeError = error;
            }
        }

        _lastObservedInputTick = currentInputTick;

        if (!diagnosticLogging)
        {
            return;
        }

        var now = DateTime.UtcNow;

        if (
            _lastLockDiagnosticUtc.HasValue &&
            now - _lastLockDiagnosticUtc.Value < TimeSpan.FromSeconds(10))
        {
            return;
        }

        Log.Write(
            $"DRY RUN lock diagnostic: agent heartbeat; " +
            $"inputTick={currentInputTick}; " +
            $"inputChanges={_lockInputChanges}; " +
            $"pipeSuccesses={_lockPipeSuccesses}; " +
            $"lastPipeError={_lastLockPipeError ?? "none"}.");

        _lastLockDiagnosticUtc = now;
        _lockInputChanges = 0;
        _lockPipeSuccesses = 0;
        _lastLockPipeError = null;
    }

    private void ReportSessionState(bool isLocked)
    {
        var now = DateTime.UtcNow;
        var stateChanged = _lastReportedLockedState != isLocked;

        if (
            !stateChanged &&
            _lastSessionStateReportUtc.HasValue &&
            now - _lastSessionStateReportUtc.Value <
            TimeSpan.FromSeconds(SessionStateHeartbeatSeconds))
        {
            return;
        }

        if (
            _lastSessionStateAttemptUtc.HasValue &&
            now - _lastSessionStateAttemptUtc.Value <
            TimeSpan.FromSeconds(SessionStateRetrySeconds))
        {
            return;
        }

        _lastSessionStateAttemptUtc = now;

        var idleSeconds = Math.Max(
            0,
            NativeMethods.GetIdleTime().TotalSeconds);

        if (!SendQuietCommand(
                $"SESSION_STATE:{_sessionId}:" +
                (isLocked ? "LOCKED" : "UNLOCKED") +
                $":{idleSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}",
                out _))
        {
            return;
        }

        _lastReportedLockedState = isLocked;
        _lastSessionStateReportUtc = now;
    }

    private static bool SendQuietCommand(
        string command,
        out string? error)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            pipe.Connect(750);

            using var writer = new StreamWriter(pipe)
            {
                AutoFlush = true
            };

            writer.WriteLine(command);

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            // State and input reporting are best-effort. WTS remains the
            // fallback and failures must not display UI to the user.
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool SendShutdownRequest()
    {
        Exception? lastException = null;

        // A short retry covers the moment when the service is closing the
        // previous connection and creating the next pipe instance.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Log.Write(
                    $"Connecting to named pipe '{PipeName}', " +
                    $"attempt {attempt}/3.");

                using var pipe =
                    new NamedPipeClientStream(
                        ".",
                        PipeName,
                        PipeDirection.Out,
                        PipeOptions.Asynchronous);

                pipe.Connect(5000);

                using var writer =
                    new StreamWriter(pipe)
                    {
                        AutoFlush = true
                    };

                writer.WriteLine(
                    "SHUTDOWN_IDLE");

                Log.Write(
                    "Shutdown request was successfully sent " +
                    "to the Windows service.");

                return true;
            }
            catch (Exception ex)
            {
                lastException = ex;

                Log.Write(
                    $"Named pipe attempt {attempt}/3 failed: " +
                    $"{ex.GetType().Name}; " +
                    $"HResult=0x{ex.HResult:X8}; " +
                    $"{ex.Message}");

                if (attempt < 3)
                {
                    Thread.Sleep(750);
                }
            }
        }

        var errorText =
            lastException is null
                ? "Unknown communication error."
                : $"{lastException.GetType().Name}: " +
                  $"{lastException.Message}";

        Log.Write(
            $"Unable to contact service after 3 attempts: " +
            $"{errorText}");

        var ui = LocalizedText.Current;

        MessageBox.Show(
            $"{ui.ServiceContactFailed}\r\n\r\n" +
            $"{ui.ComputerWillNotShutdown}\r\n\r\n" +
            $"{ui.TechnicalDetail}: {errorText}",
            ui.WindowTitle,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);

        return false;
    }

    private DateTime? SendWarningActive(int warningSeconds)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var deadlineUtc = DateTime.UtcNow.AddSeconds(
                Math.Max(1, warningSeconds));
            var command =
                $"WARNING_ACTIVE:{_sessionId}:{deadlineUtc.Ticks}";

            if (SendQuietCommand(command, out _))
            {
                return deadlineUtc;
            }

            if (attempt < 3)
            {
                Thread.Sleep(100);
            }
        }

        Log.Write(
            "Warning coordination heartbeat could not be sent; " +
            "the service will still apply its local safety checks.");
        return null;
    }
}
