namespace IdleShutdown.AgentApp;

internal static class WarningDisplayPolicy
{
    // With maintenance enabled, zero means the grace period ended—not that
    // Windows Update / Chocolatey is complete or the shutdown is imminent.
    public static bool ShouldRemainVisibleAtZero(bool maintenanceEnabled) =>
        maintenanceEnabled;

    public static bool HasVerifiedLocalInput(
        uint? inputTickAtStart,
        uint? currentInputTick)
    {
        return
            inputTickAtStart.HasValue &&
            currentInputTick.HasValue &&
            inputTickAtStart.Value != currentInputTick.Value;
    }

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
