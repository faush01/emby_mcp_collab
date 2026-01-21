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
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
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
    public string DatabasePath { get; set; } = "docs.db";
}
