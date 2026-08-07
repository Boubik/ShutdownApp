namespace IdleShutdown.AgentApp;

internal static class IdleThreshold
{
    public static bool IsReached(
        TimeSpan idle,
        int idleMinutes)
    {
        return idle >= TimeSpan.FromMinutes(
            Math.Max(1, idleMinutes));
    }
}
