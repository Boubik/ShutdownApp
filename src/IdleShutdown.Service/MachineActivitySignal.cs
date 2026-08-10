using System.Globalization;

namespace IdleShutdown.ServiceApp;

internal static class MachineActivitySignal
{
    private static readonly object Sync = new();

    private static string SignalPath =>
        Path.Combine(AppConfig.BaseDirectory, "activity.signal");

    public static void Pulse()
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppConfig.BaseDirectory);
                File.WriteAllText(
                    SignalPath,
                    DateTime.UtcNow.Ticks.ToString(
                        CultureInfo.InvariantCulture));
            }
        }
        catch
        {
            // Cross-session popup cancellation is best-effort. The service's
            // own timers and pending-shutdown cancellation remain authoritative.
        }
    }
}
