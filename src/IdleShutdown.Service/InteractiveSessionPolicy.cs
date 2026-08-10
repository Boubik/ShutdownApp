namespace IdleShutdown.ServiceApp;

internal sealed record AgentSessionState(
    bool IsLocked,
    DateTime ReportedAtUtc,
    DateTime? LastInputUtc);

internal static class InteractiveSessionPolicy
{
    public static bool HasActiveSession(
        IEnumerable<AgentSessionState> states,
        DateTime now,
        TimeSpan maximumHeartbeatAge,
        TimeSpan idleThreshold)
    {
        return states.Any(state =>
            !state.IsLocked &&
            now - state.ReportedAtUtc <= maximumHeartbeatAge &&
            state.LastInputUtc.HasValue &&
            now - state.LastInputUtc.Value < idleThreshold);
    }
}
