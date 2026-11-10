namespace IdleShutdown.AgentApp;

internal static class Log
{
#if PRERELEASE_VERBOSE
    public const bool DebugEnabled = true;
#else
    public const bool DebugEnabled = false;
#endif

    public static void Debug(string message)
    {
        if (DebugEnabled)
            Write("DEBUG: " + message);
    }

    private static readonly object Sync = new();

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.BaseDirectory);
            lock (Sync)
            {
                File.AppendAllText(AppConfig.LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [AGENT:{Environment.UserName}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
