using System.Text.Json;
using modelmux.Models;
using Microsoft.Extensions.Logging;

namespace modelmux.Services;

public class RouterService
{
    private readonly ILogger<RouterService> _logger;
    private readonly ConfigReloadService _configReload;
    private readonly HealthCheckService _healthCheck;
    private readonly int _maxRetryAttempts;

    public RouterService(
        ILogger<RouterService> logger,
        ConfigReloadService configReload,
        HealthCheckService healthCheck,
        int maxRetryAttempts = 3)
    {
        _logger = logger;
        _configReload = configReload;
        _healthCheck = healthCheck;
        _maxRetryAttempts = maxRetryAttempts;
    }

    /// <summary>
    /// Resolve the routing chain for a given model name.
    /// Returns a list of (baseUrl, apiKey) tuples in order: primary model first, then fallbacks.
    /// Unhealthy endpoints and models without baseUrl/apiKey are skipped.
    /// When <paramref name="requiredCapabilities"/> is non-null and non-empty, only models
    /// whose capability set contains every required capability are eligible.
    /// </summary>
    public List<RouteTarget> ResolveRoute(string modelName, HashSet<string>? requiredCapabilities = null)
    {
        var config = _configReload.Config;
        var results = new List<RouteTarget>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (config.Models == null || config.Models.Count == 0)
        {
            _logger.LogWarning("No models configured, cannot route model: {Model}", modelName);
            return results;
        }

        // Try primary model + fallback chain
        CollectRouteChain(modelName, config.Models, results, visited, requiredCapabilities);

        if (results.Count == 0)
        {
            var caps = requiredCapabilities != null && requiredCapabilities.Count > 0
                ? $" (required capabilities: {string.Join(",", requiredCapabilities)})"
                : "";
            _logger.LogWarning("No healthy routes found for model: {Model}{Caps}", modelName, caps);
        }

        return results;
    }

    private void CollectRouteChain(
        string modelName,
        Dictionary<string, ModelEntry> models,
        List<RouteTarget> results,
        HashSet<string> visited,
        HashSet<string>? requiredCapabilities)
    {
        if (!visited.Add(modelName))
            return; // Prevent circular fallback references

        if (!models.TryGetValue(modelName, out var entry))
        {
            _logger.LogWarning("Model '{Model}' not found in config", modelName);
            return;
        }

        // Capability filter: if the request requires image/audio, skip models
        // that do not support every required capability.
        if (requiredCapabilities != null && requiredCapabilities.Count > 0)
        {
            if (!SupportsAll(entry.Capabilities, requiredCapabilities))
            {
                _logger.LogDebug(
                    "Skipping model {Model}: lacks required capability (has [{Has}], needs [{Need}])",
                    modelName,
                    string.Join(",", entry.Capabilities),
                    string.Join(",", requiredCapabilities));
                // Still traverse its fallbacks so a capable model deeper in the chain can be found.
                TraverseFallbacks(entry, models, results, visited, requiredCapabilities);
                return;
            }
        }

        if (!string.IsNullOrEmpty(entry.BaseUrl) && !string.IsNullOrEmpty(entry.ApiKey))
        {
            if (_healthCheck.IsHealthy(entry.BaseUrl))
            {
                results.Add(new RouteTarget
                {
                    ModelName = modelName,
                    // Use UpstreamModelName if set (for alias), otherwise fall back to parsed model name
                    UpstreamModelName = entry.UpstreamModelName ?? modelName,
                    BaseUrl = entry.BaseUrl,
                    ApiKey = entry.ApiKey,
                    DefaultParams = entry.DefaultParams
                });
                _logger.LogDebug("Route added: {Model} @ {BaseUrl}", modelName, entry.BaseUrl);
            }
            else
            {
                _logger.LogWarning("Skipping unhealthy route: {Model} @ {BaseUrl}", modelName, entry.BaseUrl);
            }
        }

        // Process fallback chain
        TraverseFallbacks(entry, models, results, visited, requiredCapabilities);
    }

    private void TraverseFallbacks(
        ModelEntry entry,
        Dictionary<string, ModelEntry> models,
        List<RouteTarget> results,
        HashSet<string> visited,
        HashSet<string>? requiredCapabilities)
    {
        if (entry.Fallback == null)
            return;

        foreach (var fallbackModel in entry.Fallback)
        {
            CollectRouteChain(fallbackModel, models, results, visited, requiredCapabilities);
        }
    }

    /// <summary>
    /// True when modelCapabilities contains every required capability.
    /// </summary>
    private static bool SupportsAll(HashSet<string> modelCapabilities, HashSet<string> requiredCapabilities)
    {
        foreach (var required in requiredCapabilities)
        {
            if (!modelCapabilities.Contains(required))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Try routing a request through the resolved chain until one succeeds (or all fail).
    /// Returns the upstream HttpResponseMessage on success.
    /// </summary>
    public record RouteTarget
    {
        /// <summary>Public-facing model name (as resolved from client request).</summary>
        public string ModelName { get; init; } = string.Empty;
        /// <summary>
        /// Actual model name to send to the upstream API.
        /// Differs from ModelName when an alias is used.
        /// </summary>
        public string UpstreamModelName { get; init; } = string.Empty;
        public string BaseUrl { get; init; } = string.Empty;
        public string ApiKey { get; init; } = string.Empty;
        public Dictionary<string, JsonElement>? DefaultParams { get; init; }
    }
}