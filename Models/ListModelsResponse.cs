using System.Text.Json.Serialization;

namespace modelmux.Models;

[JsonSerializable(typeof(ListModelsResponse))]
[JsonSerializable(typeof(ModelInfo))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase
)]
public partial class ListModelsJsonContext : JsonSerializerContext { }

public class ListModelsResponse
{
    public string Object { get; set; } = "list";
    public List<ModelInfo> Data { get; set; } = new();
}

public class ModelInfo
{
    public string Id { get; set; } = string.Empty;
    public string Object { get; set; } = "model";
    public long Created { get; set; }
    public string OwnedBy { get; set; } = string.Empty;
}