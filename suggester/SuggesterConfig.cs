using Microsoft.Extensions.Configuration;

namespace suggester;

/// <summary>
/// Static configuration class that loads settings from appsettings.json.
/// Access config values via SuggesterConfig.Settings property.
/// </summary>
public static class SuggesterConfig
{
    private static SuggesterSettings? _settings;
    private static readonly object _lock = new();

    public static SuggesterSettings Settings
    {
        get
        {
            if (_settings == null)
            {
                lock (_lock)
                {
                    _settings ??= LoadSettings();
                }
            }
            return _settings;
        }
    }

    private static SuggesterSettings LoadSettings()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var dataAppsettings = Path.Combine(dataDir, "appsettings.json");

        // If no appsettings.json exists in the data directory, seed one from the example
        if (!File.Exists(dataAppsettings))
        {
            Directory.CreateDirectory(dataDir);
            var exampleFile = Path.Combine(AppContext.BaseDirectory, "appsettings_example.json");
            if (File.Exists(exampleFile))
            {
                File.Copy(exampleFile, dataAppsettings);
                Console.WriteLine($"Created {dataAppsettings} from appsettings_example.json — edit it with your settings.");
            }
            else
            {
                // If the example file is missing, create an empty config to avoid errors
                File.WriteAllText(dataAppsettings, "{}");
                Console.WriteLine($"Created empty {dataAppsettings} — edit it with your settings.");
            }
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
            .AddJsonFile(dataAppsettings, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var settings = new SuggesterSettings();
        configuration.GetSection("Suggester").Bind(settings);
        return settings;
    }
}

public class SuggesterSettings
{
    public string EmbyApiBaseUrl { get; set; } = "http://localhost:8096/emby";
    public string EmbyApiKey { get; set; } = "";
    public string OllamaEndpoint { get; set; } = "http://localhost:11434/v1";
    public string EmbeddingModel { get; set; } = "qwen3-embedding:0.6b";
    public string DatabasePath { get; set; } = "data/docs.db";
    public string SessionIdHeader { get; set; } = "";
}
