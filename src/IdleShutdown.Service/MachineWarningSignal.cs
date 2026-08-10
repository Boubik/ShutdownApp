using System.Globalization;

namespace IdleShutdown.ServiceApp;

internal static class MachineWarningSignal
{
    private static readonly object Sync = new();

    private static string SignalPath =>
        Path.Combine(AppConfig.BaseDirectory, "warning.signal");

    public static void SetDeadline(DateTime deadlineUtc)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppConfig.BaseDirectory);
                File.WriteAllText(
                    SignalPath,
                    deadlineUtc.Ticks.ToString(
                        CultureInfo.InvariantCulture));
            }
        }
        catch
        {
            // Agents still retain their local warning fallback.
        }
    }

    public static void Clear()
    {
        try
        {
            lock (Sync)
            {
                File.Delete(SignalPath);
            }
        }
        catch
        {
            // An expired signal is ignored by agents even if deletion fails.
        }
    }
}
