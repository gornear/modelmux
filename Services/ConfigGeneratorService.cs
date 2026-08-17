using System.Text.Json;
using modelmux.Models;

namespace modelmux.Services;

/// <summary>
/// Generates example/redacted config files for first-time deployment.
/// Supports the "generateconfig" command and auto-generation of appsettings.json.
/// All methods are AOT-safe: they use the source-generated JSON contexts declared in
/// AppSettingsJsonContext and ConfigModelsJsonContext rather than reflection-based parsing.
/// </summary>
public class ConfigGeneratorService
{
    public const string AppSettingsFileName = "appsettings.json";
    public const string ConfigExampleFileName = "config.json.example";
    public const string ConfigFileName = "config.json";
    public const string DefaultApiKey = "sk-modelmux-local-key-change-me";
    public const string RedactedApiKey = "your-api-key";

    private readonly string _appSettingsTemplate;
    private readonly string _configExampleTemplate;

    public ConfigGeneratorService(string appSettingsTemplate, string configExampleTemplate)
    {
        _appSettingsTemplate = appSettingsTemplate;
        _configExampleTemplate = configExampleTemplate;
    }

    /// <summary>
    /// Ensure appsettings.json exists in the target directory. If missing, write a
    /// redacted default template. Returns true if the file was created, false if it
    /// already existed.
    /// </summary>
    public bool EnsureAppSettings(string directory, out string path)
    {
        path = Path.Combine(directory, AppSettingsFileName);
        if (File.Exists(path))
        {
            return false;
        }

        File.WriteAllText(path, _appSettingsTemplate);
        return true;
    }

    /// <summary>
    /// Generate (and overwrite) config.json.example in the target directory.
    /// If an existing config.json is present in the same directory, its structure is
    /// cloned with sensitive fields (apiKey) redacted; otherwise the built-in template
    /// is used.
    /// </summary>
    public void GenerateConfigExample(string directory, out string path, out bool fromExisting)
    {
        path = Path.Combine(directory, ConfigExampleFileName);

        var existingConfig = Path.Combine(directory, ConfigFileName);
        if (File.Exists(existingConfig))
        {
            var redacted = RedactConfig(existingConfig);
            if (redacted != null)
            {
                File.WriteAllText(path, redacted);
                fromExisting = true;
                return;
            }
        }

        File.WriteAllText(path, _configExampleTemplate);
        fromExisting = false;
    }

    /// <summary>
    /// Read an existing config.json and redact all apiKey fields, preserving every
    /// other field and the overall structure. Returns null if the file cannot be
    /// parsed as the provider-grouped structure.
    /// </summary>
    public static string? RedactConfig(string configPath)
    {
        try
        {
            var json = File.ReadAllText(configPath);
            var providers = JsonSerializer.Deserialize(
                json,
                ConfigModelsJsonContext.Default.DictionaryStringListEndpointGroup);

            if (providers == null || providers.Count == 0)
            {
                return null;
            }

            foreach (var groupList in providers.Values)
            {
                foreach (var group in groupList)
                {
                    group.ApiKey = RedactedApiKey;
                }
            }

            return JsonSerializer.Serialize(
                providers,
                ConfigModelsJsonContext.Default.DictionaryStringListEndpointGroup);
        }
        catch
        {
            return null;
        }
    }
}
