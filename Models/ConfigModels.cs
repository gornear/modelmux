using System.Text.Json;
using System.Text.Json.Serialization;

namespace modelmux.Models;

// --- Source Generator registrations ---
[JsonSerializable(typeof(RouterConfig))]
[JsonSerializable(typeof(EndpointGroup))]
[JsonSerializable(typeof(ProviderModel))]
[JsonSerializable(typeof(Dictionary<string, List<EndpointGroup>>))]
[JsonSerializable(typeof(List<EndpointGroup>))]
[JsonSerializable(typeof(List<ProviderModel>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
public partial class ConfigModelsJsonContext : JsonSerializerContext { }

// --- Config types ---

/// <summary>
/// Root config object. JSON shape:
/// {
///   "local": [ { "baseUrl": "...", "apiKey": "...", "models": [...] } ],
///   "deepseek": [ { ... } ]
/// }
/// After deserialization, Flatten() builds the runtime Models dictionary.
/// </summary>
public class RouterConfig
{
    /// <summary>Flattened runtime model lookup (Provider/modelid → ModelEntry). Built by Flatten().</summary>
    public Dictionary<string, ModelEntry>? Models { get; set; }

    /// <summary>
    /// Flatten a providers dictionary into the Models dictionary.
    /// Each model gets keyed as "Provider/modelid".
    /// baseUrl and apiKey are inherited from EndpointGroup; fallback values are
    /// already in "Provider/modelid" format.
    /// </summary>
    public static RouterConfig Flatten(Dictionary<string, List<EndpointGroup>> providers)
    {
        var config = new RouterConfig
        {
            Models = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var (provider, groups) in providers)
        {
            foreach (var group in groups)
            {
                if (group.Models == null) continue;

                foreach (var pm in group.Models)
                {
                    // Public-facing key: alias takes priority over modelid
                    var publicName = pm.Alias ?? pm.ModelId;
                    var key = $"{provider}/{publicName}";
                    config.Models[key] = new ModelEntry
                    {
                        BaseUrl = group.BaseUrl,
                        ApiKey = group.ApiKey,
                        // Upstream model name: always use the real modelid, never the alias
                        UpstreamModelName = pm.ModelId,
                        DefaultParams = pm.DefaultParams,
                        Fallback = pm.Fallback,
                        Capabilities = NormalizeCapabilities(pm.Type)
                    };
                }
            }
        }

        return config;
    }

    /// <summary>
    /// Normalize a provider model's Type list into a capability HashSet.
    /// Empty/null → ["text"]. Always includes "TEXT". Values are uppercased.
    /// </summary>
    private static HashSet<string> NormalizeCapabilities(List<string>? type)
    {
        var caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TEXT" };
        if (type != null)
        {
            foreach (var t in type)
            {
                if (!string.IsNullOrWhiteSpace(t))
                    caps.Add(t.Trim().ToUpperInvariant());
            }
        }
        return caps;
    }
}

/// <summary>
/// One endpoint configuration group under a provider.
/// A provider may have multiple groups (e.g. different regions / deployments).
/// </summary>
public class EndpointGroup
{
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public List<ProviderModel>? Models { get; set; }
}

/// <summary>
/// A single model entry within an endpoint group.
/// Inherits baseUrl/apiKey from its parent EndpointGroup.
/// </summary>
public class ProviderModel
{
    public string ModelId { get; set; } = string.Empty;
    /// <summary>
    /// Optional public-facing alias. When set, the model is exposed as "provider/alias"
    /// to clients and in /v1/models, but upstream requests still use ModelId.
    /// Useful for exposing the same physical model with different defaultParams
    /// (e.g. thinking vs non-thinking mode).
    /// </summary>
    public string? Alias { get; set; }
    public Dictionary<string, JsonElement>? DefaultParams { get; set; }
    public List<string>? Fallback { get; set; }
    /// <summary>
    /// The capability set this model supports (e.g. ["text"], ["text","image"], ["text","image","audio"]).
    /// Defaults to ["text"] when omitted. Used for capability-based routing:
    /// a request requiring "image" or "audio" is only routed to a model whose
    /// Type contains that capability.
    /// </summary>
    public List<string>? Type { get; set; }
}

/// <summary>
/// Flattened runtime model entry (unchanged from previous version).
/// </summary>
public class ModelEntry
{
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>
    /// The model name sent to the upstream API. Defaults to the public-facing key
    /// if not set. Used when alias differs from the actual upstream model name.
    /// </summary>
    public string? UpstreamModelName { get; set; }
    /// <summary>
    /// Default parameters to inject into requests if not provided by the client.
    /// Supports any JSON value type (number, string, bool, object, array) via JsonElement.
    /// </summary>
    public Dictionary<string, JsonElement>? DefaultParams { get; set; }
    public List<string>? Fallback { get; set; }
    /// <summary>
    /// Normalized capability set (uppercase, e.g. {"TEXT","IMAGE"}).
    /// Always contains at least "TEXT" (models without a Type default to text-only).
    /// </summary>
    public HashSet<string> Capabilities { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "TEXT" };
}
