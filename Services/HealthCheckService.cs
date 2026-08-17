using System.Collections.Concurrent;
using modelmux.Models;
using Microsoft.Extensions.Logging;

namespace modelmux.Services;

public class HealthCheckService : BackgroundService
{
    private readonly ILogger<HealthCheckService> _logger;
    private readonly ConfigReloadService _configReload;
    private readonly HealthCheckSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;

    private readonly ConcurrentDictionary<string, HealthState> _healthStates = new();

    public HealthCheckService(
        ILogger<HealthCheckService> logger,
        ConfigReloadService configReload,
        HealthCheckSettings settings,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _configReload = configReload;
        _settings = settings;
        _httpClientFactory = httpClientFactory;
    }

    public bool IsHealthy(string baseUrl)
    {
        if (!_settings.Enabled)
            return true;

        if (_healthStates.TryGetValue(baseUrl, out var state))
        {
            if (!state.IsHealthy)
            {
                // If cooldown has expired, allow one retry attempt
                // but do NOT auto-recover — let the actual request success/failure decide
                if (DateTime.UtcNow - state.LastUnhealthyTime > TimeSpan.FromSeconds(_settings.UnhealthyCooldownSeconds))
                {
                    _logger.LogInformation("Cooldown expired for {BaseUrl}, allowing retry attempt", baseUrl);
                    return true;
                }
                return false;
            }
            return true;
        }

        // Unknown endpoints are considered healthy until proven otherwise
        return true;
    }

    public void MarkUnhealthy(string baseUrl)
    {
        if (!_settings.Enabled)
            return;

        _healthStates.AddOrUpdate(
            baseUrl,
            _ => new HealthState { IsHealthy = false, LastUnhealthyTime = DateTime.UtcNow },
            (_, existing) =>
            {
                existing.IsHealthy = false;
                existing.LastUnhealthyTime = DateTime.UtcNow;
                return existing;
            });

        _logger.LogWarning("Marked {BaseUrl} as unhealthy", baseUrl);
    }

    public void MarkHealthy(string baseUrl)
    {
        if (!_settings.Enabled)
            return;

        _healthStates.AddOrUpdate(
            baseUrl,
            _ => new HealthState { IsHealthy = true, LastUnhealthyTime = DateTime.MinValue },
            (_, existing) =>
            {
                existing.IsHealthy = true;
                return existing;
            });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Health check service is disabled");
            return;
        }

        _logger.LogInformation("Health check service started (interval: {Interval}s, timeout: {Timeout}s)",
            _settings.IntervalSeconds, _settings.TimeoutSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.IntervalSeconds), stoppingToken);
                await CheckAllEndpointsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check cycle");
            }
        }
    }

    private async Task CheckAllEndpointsAsync(CancellationToken ct)
    {
        var config = _configReload.Config;
        if (config.Models == null || config.Models.Count == 0)
            return;

        // Deduplicate by (baseUrl, apiKey) so each unique upstream is checked once
        var seen = new HashSet<string>();
        var checkTargets = new List<(string BaseUrl, string? ApiKey)>();
        foreach (var entry in config.Models.Values)
        {
            if (string.IsNullOrEmpty(entry.BaseUrl)) continue;
            var key = entry.BaseUrl + "|" + (entry.ApiKey ?? "");
            if (seen.Add(key))
                checkTargets.Add((entry.BaseUrl, entry.ApiKey));
        }

        var client = _httpClientFactory.CreateClient("HealthCheck");
        client.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);

        foreach (var (baseUrl, apiKey) in checkTargets)
        {
            try
            {
                var url = ProxyService.BuildUpstreamUrl(baseUrl, "/v1/models");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                // Attach API key if available so auth-protected /v1/models returns 200 instead of 401
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, ct);

                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

                // 2xx: fully healthy; 401/403: alive but auth issue — still reachable
                if (response.IsSuccessStatusCode ||
                    response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    MarkHealthy(baseUrl);
                    _logger.LogDebug("Health check OK for {BaseUrl} (status: {Status})", baseUrl, (int)response.StatusCode);
                }
                else
                {
                    MarkUnhealthy(baseUrl);
                    _logger.LogWarning("Health check failed for {BaseUrl} (status: {Status})", baseUrl, (int)response.StatusCode);
                }
            }
            catch (TaskCanceledException)
            {
                MarkUnhealthy(baseUrl);
                _logger.LogWarning("Health check timeout for {BaseUrl}", baseUrl);
            }
            catch (HttpRequestException ex)
            {
                MarkUnhealthy(baseUrl);
                _logger.LogWarning("Health check connection error for {BaseUrl}: {Message}", baseUrl, ex.Message);
            }
            catch (Exception ex)
            {
                MarkUnhealthy(baseUrl);
                _logger.LogWarning("Health check error for {BaseUrl}: {Message}", baseUrl, ex.Message);
            }
        }
    }

    private class HealthState
    {
        public bool IsHealthy { get; set; } = true;
        public DateTime LastUnhealthyTime { get; set; } = DateTime.MinValue;
    }
}