namespace IdleShutdown.ServiceApp;

internal static class WarningCoordinationPolicy
{
    public static DateTime? GetLatestActiveDeadline(
        IEnumerable<DateTime> deadlines,
        DateTime now)
    {
        var activeDeadlines = deadlines
            .Where(deadline => deadline > now)
            .ToArray();

        return activeDeadlines.Length == 0
            ? null
            : activeDeadlines.Max();
    }
}
