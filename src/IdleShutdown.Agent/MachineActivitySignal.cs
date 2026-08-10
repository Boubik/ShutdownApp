using System.Globalization;

namespace IdleShutdown.AgentApp;

internal static class MachineActivitySignal
{
    private static string SignalPath =>
        Path.Combine(AppConfig.BaseDirectory, "activity.signal");

    public static long ReadVersion()
    {
        try
        {
            var value = File.ReadAllText(SignalPath);

            return long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var version)
                    ? version
                    : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static TimeSpan? GetIdleTime()
    {
        var version = ReadVersion();

        if (version <= 0)
        {
            return null;
        }

        try
        {
            var lastActivityUtc = new DateTime(
                version,
                DateTimeKind.Utc);
            var idle = DateTime.UtcNow - lastActivityUtc;

            return idle < TimeSpan.Zero
                ? TimeSpan.Zero
                : idle;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
