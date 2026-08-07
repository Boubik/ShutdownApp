namespace IdleShutdown.ServiceApp;

internal static class Log
{
    private static readonly object Sync = new();

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.BaseDirectory);
            lock (Sync)
            {
                File.AppendAllText(AppConfig.LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SERVICE] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never terminate the service.
        }
    }
}
