using System.Text.Json;

namespace IdleShutdown.ServiceApp;

internal sealed class AppConfig
{
    private static readonly string[] RequiredProperties =
    [
        nameof(IdleMinutes),
        nameof(WarningSeconds),
        nameof(LockedMinutes),
        nameof(NoUserMinutes),
        nameof(CheckIntervalSeconds),
        nameof(PauseWhenFullscreen),
        nameof(DryRun)
    ];

    public int IdleMinutes { get; set; } = 90;
    public int WarningSeconds { get; set; } = 300;
    public int LockedMinutes { get; set; } = 90;
    public int NoUserMinutes { get; set; } = 90;
    public int CheckIntervalSeconds { get; set; } = 5;
    public bool PauseWhenFullscreen { get; set; } = true;
    public bool DryRun { get; set; } = false;
    public bool IsValid { get; private set; } = true;
    public string? LoadError { get; private set; }


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
                return Invalid("config.json does not exist");
            }

            var json = File.ReadAllText(ConfigPath);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid("config.json must contain a JSON object");
            }

            var presentProperties = document.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missingProperties = RequiredProperties
                .Where(property => !presentProperties.Contains(property))
                .ToArray();

            if (missingProperties.Length > 0)
            {
                return Invalid(
                    $"config.json is missing: " +
                    string.Join(", ", missingProperties));
            }

            var config = JsonSerializer.Deserialize<AppConfig>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (config is null)
            {
                return Invalid("config.json could not be deserialized");
            }

            if (
                config.IdleMinutes < 1 ||
                config.WarningSeconds < 1 ||
                config.LockedMinutes < 1 ||
                config.NoUserMinutes < 1 ||
                config.CheckIntervalSeconds < 1)
            {
                return Invalid(
                    "all timeout and interval values must be at least 1");
            }

            return config;
        }
        catch (Exception ex)
        {
            return Invalid(
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static AppConfig Invalid(string error) =>
        new()
        {
            IsValid = false,
            LoadError = error
        };
}
