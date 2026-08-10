namespace IdleShutdown.ServiceApp;

internal static class Log
{
    private const long MaxLogBytes = 2L * 1024 * 1024;
    private const int ArchiveCount = 2;
    private static readonly object Sync = new();
    private static DateTime _nextMaintenanceUtc;

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.BaseDirectory);
            lock (Sync)
            {
                RotateIfNeeded();
                File.AppendAllText(AppConfig.LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SERVICE] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never terminate the service.
        }
    }

    public static void Maintain()
    {
        if (DateTime.UtcNow < _nextMaintenanceUtc)
        {
            return;
        }

        try
        {
            lock (Sync)
            {
                _nextMaintenanceUtc = DateTime.UtcNow.AddMinutes(1);
                RotateIfNeeded();
            }
        }
        catch
        {
            // A user agent may be appending at the same moment. Retry later.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(AppConfig.LogPath))
        {
            return;
        }

        using var source = new FileStream(
            AppConfig.LogPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        if (source.Length < MaxLogBytes)
        {
            return;
        }

        for (var archive = ArchiveCount; archive >= 1; archive--)
        {
            var targetPath = $"{AppConfig.LogPath}.{archive}";

            if (archive == ArchiveCount && File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            if (archive > 1)
            {
                var previousPath = $"{AppConfig.LogPath}.{archive - 1}";

                if (File.Exists(previousPath))
                {
                    File.Move(previousPath, targetPath);
                }
            }
        }

        source.Position = 0;

        using (var archiveStream = new FileStream(
                   $"{AppConfig.LogPath}.1",
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.Read))
        {
            source.CopyTo(archiveStream);
        }

        source.SetLength(0);
    }
}
