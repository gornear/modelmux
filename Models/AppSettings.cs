using System.Text.Json.Serialization;

namespace modelmux.Models;

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
public partial class AppSettingsJsonContext : JsonSerializerContext { }

public class AppSettings
{
    public KestrelSettings? Kestrel { get; set; }
    public string? ApiKey { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 120;
    public int MaxRetryAttempts { get; set; } = 3;
    public HealthCheckSettings? HealthCheck { get; set; }
    /// <summary>
    /// When true, log message content at Debug level. Default false (privacy-safe).
    /// </summary>
    public bool DebugPrompt { get; set; } = false;
}

public class KestrelSettings
{
    public EndpointsSettings? Endpoints { get; set; }
    public LimitsSettings? Limits { get; set; }
}

public class EndpointsSettings
{
    public EndpointConfig? Http { get; set; }
    public EndpointConfig? Https { get; set; }
}

public class EndpointConfig
{
    public string? Url { get; set; }
}

public class LimitsSettings
{
    public int MaxConcurrentConnections { get; set; } = 100;
    public int MaxConcurrentUpgradedConnections { get; set; } = 100;
    public string? KeepAliveTimeout { get; set; }
}

public class HealthCheckSettings
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 3;
    public int UnhealthyCooldownSeconds { get; set; } = 30;
}

