namespace IdleShutdown.AgentApp;

internal static class WarningDisplayPolicy
{
    public static bool ShouldShow(
        TimeSpan idle,
        int idleMinutes,
        DateTime? sharedDeadlineUtc,
        DateTime nowUtc)
    {
        return
            sharedDeadlineUtc > nowUtc ||
            IdleThreshold.IsReached(idle, idleMinutes);
    }

    public static int GetVisibleSeconds(
        int configuredSeconds,
        DateTime? sharedDeadlineUtc,
        DateTime nowUtc)
    {
        return sharedDeadlineUtc.HasValue
            ? Math.Max(
                1,
                (int)Math.Ceiling(
                    (sharedDeadlineUtc.Value - nowUtc).TotalSeconds))
            : Math.Max(1, configuredSeconds);
    }
}
