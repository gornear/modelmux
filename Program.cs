using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using modelmux.Endpoints;
using modelmux.Middleware;
using modelmux.Models;
using modelmux.Services;
using Microsoft.AspNetCore.Http;

namespace modelmux;

public class Program
{
    public static int Main(string[] args)
    {
        // ---- Manual pre-check for help/version/bare invocation ----
        // Handled manually so we can print the full AI-agent deployment guide.
        if (args.Length == 0 ||
            Array.IndexOf(args, "-h") >= 0 ||
            Array.IndexOf(args, "--help") >= 0 ||
            Array.IndexOf(args, "-?") >= 0)
        {
            PrintHelp();
            return HelpRequestedExitCode;
        }

        if (Array.IndexOf(args, "--version") >= 0)
        {
            Console.WriteLine(GetVersion());
            return 0;
        }

        // ---- Command line definition ----
        var rootCommand = new RootCommand(
            "modelmux - Capability-aware LLM model multiplexer (Native AOT single binary).");

        var serveCommand = new Command("serve", "Start the gateway (auto-generates appsettings.json if missing).");
        var generateConfigCommand = new Command(
            "generateconfig",
            "Generate a redacted config.json.example in the current directory.");

        rootCommand.Add(serveCommand);
        rootCommand.Add(generateConfigCommand);

        serveCommand.SetAction(_ =>
        {
            RunServeAsync().GetAwaiter().GetResult();
        });

        generateConfigCommand.SetAction(_ =>
        {
            RunGenerateConfig();
        });

        return rootCommand.Parse(args).Invoke();
    }

    private const int HelpRequestedExitCode = 0;

    private static string GetVersion()
    {
        var attr = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (attr != null && !string.IsNullOrWhiteSpace(attr.InformationalVersion))
        {
            // Strip any git commit suffix (e.g. "1.0.0+eb43383..." -> "1.0.0").
            var v = attr.InformationalVersion;
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
        return typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private static void PrintHelp()
    {
        Console.WriteLine(HelpText);
    }

    private static void RunGenerateConfig()
    {
        var generator = CreateGenerator();
        var directory = Directory.GetCurrentDirectory();

        generator.GenerateConfigExample(directory, out var path, out var fromExisting);

        if (fromExisting)
        {
            Console.WriteLine($"Generated {ConfigGeneratorService.ConfigExampleFileName} from existing {ConfigGeneratorService.ConfigFileName} (apiKey redacted).");
        }
        else
        {
            Console.WriteLine($"Generated {ConfigGeneratorService.ConfigExampleFileName} from built-in template.");
        }
        Console.WriteLine($"  -> {path}");
        Console.WriteLine();
        Console.WriteLine("Next steps:");
        Console.WriteLine($"  1. Edit {ConfigGeneratorService.ConfigExampleFileName} to set your providers, baseUrl, apiKey and models.");
        Console.WriteLine($"  2. Copy it to {ConfigGeneratorService.ConfigFileName}:");
        Console.WriteLine($"       cp {ConfigGeneratorService.ConfigExampleFileName} {ConfigGeneratorService.ConfigFileName}");
        Console.WriteLine("  3. Start the gateway:");
        Console.WriteLine("       modelmux serve");
    }

    private static ConfigGeneratorService CreateGenerator()
    {
        return new ConfigGeneratorService(
            ReadEmbeddedResource("modelmux.Templates.appsettings.template.json"),
            ReadEmbeddedResource("modelmux.Templates.config.example.template.json"));
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        var assembly = typeof(Program).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.");
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task RunServeAsync()
    {
        // Config files are resolved relative to the current working directory,
        // matching the "single-file, download and run" deployment model.
        var workingDir = Directory.GetCurrentDirectory();

        // Auto-generate appsettings.json if missing (it is required).
        var generator = CreateGenerator();
        var appSettingsPath = Path.Combine(workingDir, "appsettings.json");

        var created = generator.EnsureAppSettings(workingDir, out var resolvedPath);
        if (created)
        {
            Console.WriteLine($"appsettings.json not found, auto-generated at: {resolvedPath}");
            Console.WriteLine($"  -> IMPORTANT: edit {ConfigGeneratorService.AppSettingsFileName} and set \"ApiKey\" before exposing the gateway.");
            Console.WriteLine();
        }

        // Load appsettings.json with AOT-compatible serializer.
        var appSettings = LoadAppSettings(appSettingsPath);

        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());

        // Configure Kestrel limits
        var limits = appSettings.Kestrel?.Limits;
        if (limits != null)
        {
            builder.WebHost.ConfigureKestrel(options =>
            {
                if (limits.MaxConcurrentConnections > 0)
                    options.Limits.MaxConcurrentConnections = limits.MaxConcurrentConnections;
                if (limits.MaxConcurrentUpgradedConnections > 0)
                    options.Limits.MaxConcurrentUpgradedConnections = limits.MaxConcurrentUpgradedConnections;
            });
        }

        // Register services
        builder.Services.AddSingleton(appSettings);
        builder.Services.AddSingleton(appSettings.HealthCheck ?? new HealthCheckSettings());
        builder.Services.AddSingleton<ConfigReloadService>();
        builder.Services.AddSingleton<HealthCheckService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthCheckService>());
        builder.Services.AddSingleton<RouterService>(sp =>
            new RouterService(
                sp.GetRequiredService<ILogger<RouterService>>(),
                sp.GetRequiredService<ConfigReloadService>(),
                sp.GetRequiredService<HealthCheckService>(),
                appSettings.MaxRetryAttempts));
        builder.Services.AddSingleton<ProxyService>(sp =>
            new ProxyService(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<ProxyService>>(),
                sp.GetRequiredService<HealthCheckService>(),
                appSettings.RequestTimeoutSeconds,
                appSettings.DebugPrompt));
        // Configure IHttpClientFactory for TCP connection pooling
        builder.Services.AddHttpClient("Proxy", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(appSettings.RequestTimeoutSeconds);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = limits?.MaxConcurrentConnections ?? 200,
            EnableMultipleHttp2Connections = true
        });

        builder.Services.AddHttpClient("HealthCheck", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(appSettings.HealthCheck?.TimeoutSeconds ?? 3);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 10
        });

        var app = builder.Build();

        // Use ApiKey middleware
        app.UseMiddleware<ApiKeyMiddleware>();

        // Health check endpoint (no auth by middleware)
        app.MapGet("/health", ProxyEndpoint.HandleHealthAsync);

        // List models endpoint (OpenAI-compatible)
        app.MapGet("/v1/models", async (HttpContext context) =>
        {
            var configReload = context.RequestServices.GetRequiredService<ConfigReloadService>();
            await ProxyEndpoint.HandleModelsAsync(context, configReload);
        });

        // Catch-all proxy for all /v1/* endpoints (OpenAI-compatible)
        app.Map("/v1/{**path}", async (HttpContext context) =>
        {
            var router = context.RequestServices.GetRequiredService<RouterService>();
            var proxy = context.RequestServices.GetRequiredService<ProxyService>();
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ProxyEndpoint");
            await ProxyEndpoint.HandleProxyAsync(context, router, proxy, logger);
        });

        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        var listenUrl = appSettings.Kestrel?.Endpoints?.Http?.Url ?? "http://0.0.0.0:5000";
        logger.LogInformation("ModelMux starting on {Url}", listenUrl);
        logger.LogInformation("Health check: {Enabled}, Interval: {Interval}s",
            appSettings.HealthCheck?.Enabled ?? true,
            appSettings.HealthCheck?.IntervalSeconds ?? 30);

        // Warn if config.json is missing.
        if (!File.Exists(Path.Combine(workingDir, "config.json")))
        {
            logger.LogWarning(
                "config.json not found. Run 'modelmux generateconfig' to create config.json.example, then copy it to config.json.");
        }

        await app.RunAsync();
    }

    private static AppSettings LoadAppSettings(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                return settings ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to load {path}: {ex.Message}");
        }

        return new AppSettings();
    }

    private static string HelpText => $"""
modelmux - Capability-aware LLM model multiplexer (Native AOT single binary)
Version: {GetVersion()}

Usage:
  modelmux serve              Start the gateway (auto-generates appsettings.json if missing)
  modelmux generateconfig     Generate a redacted config.json.example in the current directory
  modelmux --help | -h        Show this help
  modelmux --version          Show version

Deployment guide (for AI agents / automation):
  1. Generate the example router config:
       modelmux generateconfig
     This creates config.json.example in the current directory.

  2. Create your real config from the example:
       cp config.json.example config.json

  3. Edit config.json to set your upstream providers, baseUrl, apiKey and models.
     Field reference:
       "<provider>"            Provider namespace (e.g. "local", "deepseek", "openai")
       "[].baseUrl"            Upstream API base URL (e.g. https://api.deepseek.com)
       "[].apiKey"             Upstream API key shared by all models in the group
       "[].models"             Array of models under this endpoint group
       "models[].modelid"      Upstream model name sent to the provider API
       "models[].alias"        Optional public alias; string OR array of strings (exposed as provider/alias)
       "models[].type"         Optional capability list (["text"], ["image"], ["audio"])
       "models[].defaultParams" Optional default params injected when client omits them
       "models[].fallback"     Optional fallback models as "provider/modelid"

  4. Start the gateway:
       modelmux serve
     appsettings.json is auto-generated on first start. Edit it to set:
       "ApiKey"                          The unified client-facing API key (REQUIRED - clients send this)
       "Kestrel.Endpoints.Http.Url"      Listen address (default http://0.0.0.0:5000)

  5. Endpoints:
       GET  /health                 Health check (no auth)
       GET  /v1/models              List models (Bearer <ApiKey>)
       POST /v1/chat/completions    OpenAI-compatible proxy (Bearer <ApiKey>)

  Client usage example:
       Authorization: Bearer <ApiKey from appsettings.json>
       Body model: "provider/modelid"  (or "provider/alias")
""";
}
