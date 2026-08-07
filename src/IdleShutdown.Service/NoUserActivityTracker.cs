namespace IdleShutdown.ServiceApp;

internal sealed class NoUserActivityTracker
{
    private DateTime? _inactiveSince;
    private DateTime? _lastInputTime;
    private int? _consoleSessionId;

    public bool IsMonitoring => _inactiveSince.HasValue;

    public void ObserveLoggedOnUser()
    {
        _inactiveSince = null;
        _lastInputTime = null;
        _consoleSessionId = null;
    }

    public NoUserObservation ObserveNoUser(
        DateTime now,
        TimeSpan timeout,
        int? consoleSessionId,
        DateTime? lastInputTime)
    {
        if (!_inactiveSince.HasValue)
        {
            StartMonitoring(
                now,
                consoleSessionId,
                lastInputTime);

            return NoUserObservation.Started;
        }

        if (_consoleSessionId != consoleSessionId)
        {
            StartMonitoring(
                now,
                consoleSessionId,
                lastInputTime);

            return NoUserObservation.ConsoleSessionChanged;
        }

        if (SessionInputActivity.HasChanged(
                _lastInputTime,
                lastInputTime))
        {
            _inactiveSince = now;
            _lastInputTime = lastInputTime;

            return NoUserObservation.InputDetected;
        }

        return now - _inactiveSince.Value >= timeout
            ? NoUserObservation.TimeoutReached
            : NoUserObservation.Waiting;
    }

    public void ResetAfterAction(DateTime now)
    {
        if (_inactiveSince.HasValue)
        {
            _inactiveSince = now;
        }
    }

    private void StartMonitoring(
        DateTime now,
        int? consoleSessionId,
        DateTime? lastInputTime)
    {
        _inactiveSince = now;
        _consoleSessionId = consoleSessionId;
        _lastInputTime = lastInputTime;
    }
}

internal enum NoUserObservation
{
    Started,
    Waiting,
    InputDetected,
    ConsoleSessionChanged,
    TimeoutReached
}
