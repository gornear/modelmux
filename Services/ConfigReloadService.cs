using System.Text.Json;
using modelmux.Models;
using Microsoft.Extensions.Logging;

namespace modelmux.Services;

public class ConfigReloadService : IDisposable
{
    private readonly ILogger<ConfigReloadService> _logger;
    private readonly string _configPath;
    private FileSystemWatcher? _watcher;
    private RouterConfig _config = new();
    private readonly object _lock = new();
    private bool _disposed;

    public RouterConfig Config
    {
        get
        {
            lock (_lock)
            {
                // Return a shallow copy of the models dictionary for thread safety
                var clone = new RouterConfig();
                if (_config.Models != null)
                {
                    clone.Models = new Dictionary<string, ModelEntry>(_config.Models);
                }
                return clone;
            }
        }
    }

    public ConfigReloadService(ILogger<ConfigReloadService> logger)
    {
        _logger = logger;
        
        _configPath = Path.Combine(Directory.GetCurrentDirectory(), "config.json");
        LoadConfig();
        StartWatching();
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                _logger.LogWarning("Config file not found at {Path}, using empty config", _configPath);
                lock (_lock)
                {
                    _config = new RouterConfig { Models = new Dictionary<string, ModelEntry>() };
                }
                return;
            }

            var json = File.ReadAllText(_configPath);
            var providers = JsonSerializer.Deserialize(json, ConfigModelsJsonContext.Default.DictionaryStringListEndpointGroup);

            if (providers != null && providers.Count > 0)
            {
                var config = RouterConfig.Flatten(providers);
                lock (_lock)
                {
                    _config = config;
                }
                _logger.LogInformation("Config loaded successfully with {Count} models from {Path}",
                    config.Models?.Count ?? 0, _configPath);
            }
            else
            {
                _logger.LogWarning("Config deserialized as null or empty from {Path}", _configPath);
                lock (_lock)
                {
                    _config = new RouterConfig { Models = new Dictionary<string, ModelEntry>() };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load config from {Path}", _configPath);
        }
    }

    private void StartWatching()
    {
        var directory = Path.GetDirectoryName(_configPath) ?? ".";
        var filename = Path.GetFileName(_configPath);

        _watcher = new FileSystemWatcher(directory, filename)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        // Debounce: file change events can fire multiple times
        DateTime lastRead = DateTime.MinValue;
        _watcher.Changed += (sender, e) =>
        {
            var now = DateTime.UtcNow;
            if ((now - lastRead).TotalMilliseconds < 500)
                return;
            lastRead = now;

            _logger.LogInformation("Config file changed, reloading...");
            LoadConfig();
        };

        _watcher.Created += (sender, e) =>
        {
            _logger.LogInformation("Config file created, loading...");
            LoadConfig();
        };

        _logger.LogInformation("Watching config file at {Path}", _configPath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
    }
}