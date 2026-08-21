using System.Text.Json;
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
    /// <summary>
    /// Unified client-facing API key(s). Accepts a single string or an array of
    /// strings in appsettings.json; any listed key is accepted (OR semantics).
    /// An empty/missing value means passthrough mode (no auth).
    /// </summary>
    [JsonConverter(typeof(ApiKeyListConverter))]
    public ApiKeyList? ApiKey { get; set; }
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

/// <summary>
/// Holds one or more accepted client-facing API keys. Backed by a
/// case-sensitive hash set for O(1) lookup in the auth middleware.
/// </summary>
public class ApiKeyList
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public bool IsEmpty => _keys.Count == 0;

    public bool Contains(string key) => _keys.Contains(key);

    internal void Add(string key) => _keys.Add(key);

    internal IEnumerable<string> All() => _keys;
}

/// <summary>
/// AOT-safe JSON converter that accepts either a single string or an array of
/// strings for the ApiKey property, normalizing both into an <see cref="ApiKeyList"/>.
/// Serialization writes a single string when exactly one key is present, and an
/// array otherwise (compact round-trip).
/// </summary>
public class ApiKeyListConverter : JsonConverter<ApiKeyList>
{
    public override ApiKeyList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = new ApiKeyList();

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }
                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException("ApiKey array entries must be strings");
                }
                var s = reader.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    list.Add(s);
                }
            }
        }
        else if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (!string.IsNullOrEmpty(s))
            {
                list.Add(s);
            }
        }
        else if (reader.TokenType is JsonTokenType.Null or JsonTokenType.Number)
        {
            // null / number: treat as no key (passthrough mode)
        }
        else
        {
            throw new JsonException("ApiKey must be a string or an array of strings");
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, ApiKeyList value, JsonSerializerOptions options)
    {
        var keys = value.All().ToList();
        if (keys.Count == 1)
        {
            writer.WriteStringValue(keys[0]);
        }
        else
        {
            writer.WriteStartArray();
            foreach (var key in keys)
            {
                writer.WriteStringValue(key);
            }
            writer.WriteEndArray();
        }
    }
}

