using System.Text.Json;

namespace IdleShutdown.ServiceApp;

internal sealed class AppConfig
{
    public int IdleMinutes { get; set; } = 60;
    public int WarningSeconds { get; set; } = 300;
    public int LockedMinutes { get; set; } = 60;
    public int NoUserMinutes { get; set; } = 60;
    public int CheckIntervalSeconds { get; set; } = 5;
    public bool PauseWhenFullscreen { get; set; } = true;
    public bool DryRun { get; set; } = false;


    public static string BaseDirectory =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "IdleShutdown");

    public static string LogPath =>
        Path.Combine(
            BaseDirectory,
            "IdleShutdown.log");
    public static string ConfigPath =>
        Path.Combine(BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return new AppConfig();
            }

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (config is null)
            {
                return new AppConfig();
            }

            config.IdleMinutes = Math.Max(1, config.IdleMinutes);
            config.WarningSeconds = Math.Max(1, config.WarningSeconds);
            config.LockedMinutes = Math.Max(1, config.LockedMinutes);
            config.NoUserMinutes = Math.Max(1, config.NoUserMinutes);
            config.CheckIntervalSeconds =
                Math.Max(1, config.CheckIntervalSeconds);

            return config;
        }
        catch (Exception ex)
        {
            Log.Write(
                $"Configuration load failed: " +
                $"{ex.GetType().Name}: {ex.Message}");

            return new AppConfig();
        }
    }
}
