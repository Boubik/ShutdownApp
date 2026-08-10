using System.Globalization;

namespace IdleShutdown.AgentApp;

internal static class MachineWarningSignal
{
    private static string SignalPath =>
        Path.Combine(AppConfig.BaseDirectory, "warning.signal");

    public static DateTime? GetActiveDeadlineUtc()
    {
        try
        {
            if (!long.TryParse(
                    File.ReadAllText(SignalPath),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var ticks))
            {
                return null;
            }

            var deadlineUtc = new DateTime(ticks, DateTimeKind.Utc);

            return deadlineUtc > DateTime.UtcNow
                ? deadlineUtc
                : null;
        }
        catch
        {
            return null;
        }
    }
}
