using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

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
    public static RouterConfig Flatten(
        Dictionary<string, List<EndpointGroup>> providers,
        ILogger? logger = null)
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
                    // Public-facing keys. When any alias is set, only the alias(es) are
                    // exposed; otherwise the modelid itself is the public name.
                    var aliases = pm.GetAliases();
                    var publicNames = aliases ?? new List<string> { pm.ModelId };

                    foreach (var publicName in publicNames)
                    {
                        var key = $"{provider}/{publicName}";

                        // Conflict tolerance: if another model already registered this
                        // public name within the same provider, keep the FIRST registration
                        // and skip the duplicate (with a warning).
                        if (config.Models.ContainsKey(key))
                        {
                            logger?.LogWarning(
                                "Duplicate public model name '{Key}' under provider '{Provider}'; " +
                                "keeping the first registration and skipping model '{ModelId}' after alias '{Alias}'.",
                                key, provider, pm.ModelId, string.Join(",", publicNames));
                            continue;
                        }

                        config.Models[key] = new ModelEntry
                        {
                            BaseUrl = group.BaseUrl,
                            ApiKey = group.ApiKey,
                            // Upstream model name: always use the real modelid, never the alias
                            UpstreamModelName = pm.ModelId,
                            DefaultParams = pm.DefaultParams,
                            Headers = group.Headers,
                            Fallback = pm.Fallback,
                            Capabilities = NormalizeCapabilities(pm.Type)
                        };
                    }
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
    /// <summary>
    /// Optional custom HTTP headers to inject into every upstream request
    /// for this provider. Values here are overridden by client-supplied
    /// headers of the same name.
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }
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
    /// Optional public-facing alias(es). When set, the model is exposed as "provider/alias"
    /// to clients and in /v1/models, but upstream requests still use ModelId.
    /// Useful for exposing the same physical model with different defaultParams
    /// (e.g. thinking vs non-thinking mode).
    ///
    /// Accepts a single string ("x") OR an array of strings (["x","y"]). When multiple
    /// aliases are given, every alias registers a distinct public key pointing to the
    /// same upstream model. When any alias is set, ModelId itself is NOT exposed except
    /// through an explicit alias name.
    ///
    /// Stored as JsonElement because System.Text.Json's source generator cannot bind both
    /// a scalar string and an array to a single strongly-typed property; normalization to
    /// a List&lt;string&gt; happens in Flatten() via <see cref="GetAliases"/>.
    /// </summary>
    public JsonElement? Alias { get; set; }

    /// <summary>
    /// Normalize the (possibly polymorphic) Alias into a List&lt;string&gt;, or null when
    /// no alias is configured. A scalar string becomes a single-element list; an array
    /// yields one entry per non-empty string element; any other value shape (object,
    /// number, etc.) is treated as "no alias" for resilience.
    /// </summary>
    public List<string>? GetAliases()
    {
        if (Alias == null)
            return null;

        var elem = Alias.Value;
        var result = new List<string>();

        switch (elem.ValueKind)
        {
            case JsonValueKind.String:
                var s = elem.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    result.Add(s.Trim());
                break;

            case JsonValueKind.Array:
                foreach (var item in elem.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var t = item.GetString();
                        if (!string.IsNullOrWhiteSpace(t))
                            result.Add(t.Trim());
                    }
                }
                break;

            default:
                // Object / number / boolean / null → treat as unset.
                return null;
        }

        return result.Count > 0 ? result : null;
    }

    public Dictionary<string, JsonElement>? DefaultParams { get; set; }
    /// <summary>
    /// Custom HTTP headers inherited from the parent EndpointGroup.
    /// Injected into every upstream request for this model.
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }
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
    /// <summary>
    /// Custom HTTP headers inherited from the parent EndpointGroup.
    /// Injected into every upstream request for this model.
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }
    public List<string>? Fallback { get; set; }
    /// <summary>
    /// Normalized capability set (uppercase, e.g. {"TEXT","IMAGE"}).
    /// Always contains at least "TEXT" (models without a Type default to text-only).
    /// </summary>
    public HashSet<string> Capabilities { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "TEXT" };
}
