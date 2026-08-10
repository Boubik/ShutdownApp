using System.Text.Json;

namespace IdleShutdown.AgentApp;

internal sealed class AppConfig
{
    private static readonly string[] RequiredProperties =
    [
        nameof(IdleMinutes),
        nameof(WarningSeconds),
        nameof(CheckIntervalSeconds),
        nameof(PauseWhenFullscreen),
        nameof(DryRun)
    ];

    public int IdleMinutes { get; set; } = 90;
    public int WarningSeconds { get; set; } = 300;
    public int CheckIntervalSeconds { get; set; } = 5;
    public bool PauseWhenFullscreen { get; set; } = true;
    public bool DryRun { get; set; } = false;
    public bool IsValid { get; private set; } = true;

    public static string BaseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "IdleShutdown");
    public static string ConfigPath => Path.Combine(BaseDirectory, "config.json");
    public static string LogPath => Path.Combine(BaseDirectory, "IdleShutdown.log");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return Invalid();
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var json = File.ReadAllText(ConfigPath);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid();
            }

            var presentProperties = document.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (RequiredProperties.Any(
                    property => !presentProperties.Contains(property)))
            {
                return Invalid();
            }

            var config = JsonSerializer.Deserialize<AppConfig>(
                json,
                options);

            if (
                config is null ||
                config.IdleMinutes < 1 ||
                config.WarningSeconds < 1 ||
                config.CheckIntervalSeconds < 1)
            {
                return Invalid();
            }

            return config;
        }
        catch
        {
            return Invalid();
        }
    }

    private static AppConfig Invalid() =>
        new()
        {
            IsValid = false
        };
}
