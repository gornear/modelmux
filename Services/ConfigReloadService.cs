using System.Text.Json;
using modelmux.Models;
using Microsoft.Extensions.Logging;

namespace modelmux.Services;

public class ConfigReloadService : IDisposable
{
    /// <summary>
    /// How long to wait after the last filesystem event before reading the file.
    /// Editors write config in chunks (vi: 4096 bytes, then the remainder), so reading
    /// on the first event observes a truncated file. Waiting until the writes settle
    /// avoids parsing a half-written config.
    /// </summary>
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Per-attempt wait when the file exists but does not parse yet (e.g. the writer
    /// flushed page 1 but not page 2). Increases linearly; total budget is roughly
    /// Sum(RetryDelays) = 1.5s.
    /// </summary>
    private static readonly int[] RetryDelaysMs = [100, 200, 400, 800];

    private readonly ILogger<ConfigReloadService> _logger;
    private readonly string _configPath;
    private FileSystemWatcher? _watcher;
    private readonly Timer? _debounceTimer;
    private RouterConfig _config = new();
    private readonly object _lock = new();
    private readonly object _reloadLock = new();
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

        // A single-shot timer that gets pushed back by every filesystem event, so the
        // reload only runs once the writer has gone quiet.
        _debounceTimer = new Timer(_ => ReloadAfterChange(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        StartWatching();
    }

    /// <summary>
    /// Load the config file once. Errors are logged; the previous config is kept.
    /// </summary>
    private void LoadConfig()
    {
        LoadConfig(reload: false);
    }

    /// <summary>
    /// Read, parse, and apply the config file.
    ///
    /// The read is not atomic on any OS — an editor or deploy script may be mid-write
    /// when we get notified. So a parse failure is treated as "file not complete yet"
    /// and retried a few times before giving up. Only the final failure is logged as an
    /// error: intermediate truncation is expected, not a config mistake.
    /// </summary>
    /// <param name="reload">True when triggered by a file change (affects log wording).</param>
    private void LoadConfig(bool reload)
    {
        lock (_reloadLock)
        {
            JsonException? lastJsonError = null;

            for (var attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
            {
                try
                {
                    if (!File.Exists(_configPath))
                    {
                        _logger.LogWarning("Config file not found at {Path}, using empty config", _configPath);
                        ApplyConfig(new RouterConfig { Models = new Dictionary<string, ModelEntry>() });
                        return;
                    }

                    var json = File.ReadAllText(_configPath);

                    // Parse before binding so a truncated file is caught as a plain
                    // JsonException and can be retried without touching live state.
                    using (JsonDocument.Parse(json)) { }

                    var providers = JsonSerializer.Deserialize(
                        json,
                        ConfigModelsJsonContext.Default.DictionaryStringListEndpointGroup);

                    if (providers != null && providers.Count > 0)
                    {
                        var config = RouterConfig.Flatten(providers, _logger);
                        ApplyConfig(config);
                        _logger.LogInformation(
                            reload ? "Config reloaded successfully with {Count} models from {Path}"
                                   : "Config loaded successfully with {Count} models from {Path}",
                            config.Models?.Count ?? 0, _configPath);
                    }
                    else
                    {
                        _logger.LogWarning("Config deserialized as null or empty from {Path}", _configPath);
                        ApplyConfig(new RouterConfig { Models = new Dictionary<string, ModelEntry>() });
                    }

                    return;
                }
                catch (JsonException ex)
                {
                    // Likely a partially written file. Wait and re-read, unless this is
                    // the last attempt.
                    lastJsonError = ex;
                    if (attempt < RetryDelaysMs.Length)
                    {
                        _logger.LogDebug(
                            "Config at {Path} is not complete (attempt {Attempt}): {Reason}. Retrying in {Delay}ms.",
                            _configPath, attempt + 1, ex.Message, RetryDelaysMs[attempt]);
                        Thread.Sleep(RetryDelaysMs[attempt]);
                    }
                }
                catch (IOException ex)
                {
                    // Sharing violation while the writer holds the file open — retry too.
                    lastJsonError = null;
                    if (attempt < RetryDelaysMs.Length)
                    {
                        _logger.LogDebug(
                            "Config at {Path} is locked (attempt {Attempt}): {Reason}. Retrying in {Delay}ms.",
                            _configPath, attempt + 1, ex.Message, RetryDelaysMs[attempt]);
                        Thread.Sleep(RetryDelaysMs[attempt]);
                        continue;
                    }

                    _logger.LogError(ex,
                        "Failed to load config from {Path} after {Attempts} attempts; keeping the previous configuration.",
                        _configPath, attempt + 1);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to load config from {Path}; keeping the previous configuration.", _configPath);
                    return;
                }
            }

            _logger.LogError(lastJsonError,
                "Failed to load config from {Path} after {Attempts} attempts; the file does not parse as JSON. " +
                "Keeping the previous configuration.",
                _configPath, RetryDelaysMs.Length + 1);
        }
    }

    private void ApplyConfig(RouterConfig config)
    {
        lock (_lock)
        {
            _config = config;
        }
    }

    private void StartWatching()
    {
        var directory = Path.GetDirectoryName(_configPath) ?? ".";
        var filename = Path.GetFileName(_configPath);

        _watcher = new FileSystemWatcher(directory, filename)
        {
            // LastWrite/CreationTime cover in-place edits; FileName covers atomic
            // saves ("write temp file, rename over target", e.g. sed -i, vim with
            // writebackup off), which emit Renamed/Moved rather than Changed.
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        _watcher.Changed += (_, _) => ScheduleReload();
        _watcher.Created += (_, _) => ScheduleReload();
        _watcher.Renamed += (_, _) => ScheduleReload();

        _logger.LogInformation("Watching config file at {Path}", _configPath);
    }

    /// <summary>
    /// Push the debounce timer back. Every event restarts the window, so a chunked
    /// write results in exactly one reload after the writer finishes.
    /// </summary>
    private void ScheduleReload()
    {
        if (_disposed) return;

        try
        {
            _debounceTimer?.Change(ReloadDebounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Service is shutting down.
        }
    }

    private void ReloadAfterChange()
    {
        if (_disposed) return;

        // A rename may have produced a brief window where the path does not exist.
        // Give it one short chance to reappear before treating it as "no config".
        if (!File.Exists(_configPath))
        {
            Thread.Sleep(100);
            if (!File.Exists(_configPath))
            {
                _logger.LogDebug("Config file {Path} disappeared; keeping the previous configuration.", _configPath);
                return;
            }
        }

        _logger.LogInformation("Config file changed, reloading...");
        LoadConfig(reload: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }
}