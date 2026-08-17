using System.Text;
using System.Text.Json;
using modelmux.Models;
using modelmux.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace modelmux.Endpoints;

public static class ProxyEndpoint
{
    /// <summary>
    /// Catch-all proxy endpoint for /v1/{**path}
    /// Extracts model from the request body, resolves the route, and proxies to upstream.
    /// </summary>
    public static async Task HandleProxyAsync(
        HttpContext context,
        RouterService router,
        ProxyService proxy,
        ILogger logger)
    {
        // path = "chat/completions" (the **path captured from /v1/{**path})
        // We reconstruct the full OpenAI path: /v1/chat/completions
        var capturedPath = context.Request.RouteValues["path"]?.ToString() ?? string.Empty;
        var path = "/v1/" + capturedPath.TrimStart('/');

        // Extract model from request body
        string? model = null;
        string bodyText = string.Empty;
        if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
            context.Request.ContentLength > 0)
        {
            // We need to read the body to extract the model, then rewind for forwarding
            context.Request.EnableBuffering();

            // Read the ENTIRE body (a single BodyReader.ReadAsync() is not guaranteed to
            // return the full body for large payloads — e.g. multi-turn history with
            // inlined base64 images can exceed the pipe buffer and arrive truncated,
            // which would break capability detection and model extraction).
            using var bodyMs = new MemoryStream();
            await context.Request.Body.CopyToAsync(bodyMs);
            bodyMs.Position = 0;

            if (bodyMs.Length > 0)
            {
                bodyText = Encoding.UTF8.GetString(bodyMs.ToArray());
                context.Request.Body.Position = 0; // Rewind for forwarding

                // Quick manual extraction of "model" field to avoid full deserialization
                model = ExtractJsonField(bodyText, "model");
            }
            else
            {
                context.Request.Body.Position = 0;
            }
        }
        else
        {
            // GET /models might not have a body, but we still need model
            // Try query string
            model = context.Request.Query["model"].FirstOrDefault();
        }

        if (string.IsNullOrEmpty(model))
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":{\"message\":\"Model parameter is required\",\"type\":\"bad_request\"}}");
            return;
        }

        // Detect required capabilities (image/audio) from path and request body.
        // Only image/audio participate in filtering; text is always supported.
        var requiredCapabilities = DetectRequiredCapabilities(path, bodyText);

        // Resolve route chain (filtered by capability + health)
        var routes = router.ResolveRoute(model, requiredCapabilities);
        if (routes.Count == 0)
        {
            context.Response.StatusCode = 502;
            context.Response.ContentType = "application/json";

            if (requiredCapabilities != null && requiredCapabilities.Count > 0)
            {
                var capsList = string.Join(",", requiredCapabilities);
                await context.Response.WriteAsync(
                    $"{{\"error\":{{\"message\":\"No healthy model supporting '{capsList}' found for model '{model}'\",\"type\":\"capability_unavailable\"}}}}");
            }
            else
            {
                await context.Response.WriteAsync(
                    $"{{\"error\":{{\"message\":\"No healthy routes found for model '{model}'\",\"type\":\"no_route\"}}}}");
            }
            return;
        }

        // Rewind body stream so each retry attempt reads from start
        if (context.Request.Body.CanSeek)
            context.Request.Body.Position = 0;

        // Try each route in the chain
        var ct = context.RequestAborted;
        for (int i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            // Compute the final upstream URL (deduplicates any /v1 prefix overlap) so the
            // log matches what is actually requested.
            var upstreamUrl = ProxyService.BuildUpstreamUrl(route.BaseUrl, path);
            logger.LogInformation(
                "Routing request: model={ClientModel} -> target={TargetModel} @ {UpstreamUrl}",
                model, route.UpstreamModelName, upstreamUrl);

            var upstreamResponse = await proxy.ForwardAsync(
                route.BaseUrl,
                route.ApiKey,
                path,
                route.UpstreamModelName,
                context.Request,
                route.DefaultParams,
                ct);

            if (upstreamResponse != null)
            {
                // Success — stream the response back
                await proxy.StreamResponseAsync(upstreamResponse, context.Response, ct);
                logger.LogInformation(
                    "Completed: model={ClientModel} via {TargetModel} (status={Status})",
                    model, route.ModelName, (int)upstreamResponse.StatusCode);
                return;
            }

            // Failed — try next fallback
            logger.LogWarning(
                "Route {Index}/{Total} failed: {TargetModel}, trying next fallback...",
                i + 1, routes.Count, route.ModelName);
        }

        // All routes failed
        context.Response.StatusCode = 502;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            $"{{\"error\":{{\"message\":\"All routes exhausted for model '{model}'\",\"type\":\"all_routes_failed\"}}}}");
    }

    /// <summary>
    /// Health check endpoint
    /// </summary>
    public static async Task HandleHealthAsync(HttpContext context)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"status\":\"ok\"}");
    }

    /// <summary>
    /// List models endpoint — returns all models from config.json
    /// </summary>
    public static async Task HandleModelsAsync(
        HttpContext context,
        ConfigReloadService configReload)
    {
        var config = configReload.Config;
        var modelList = new ListModelsResponse();

        if (config.Models != null)
        {
            foreach (var kvp in config.Models)
            {
                modelList.Data.Add(new ModelInfo
                {
                    Id = kvp.Key,
                    Object = "model",
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    OwnedBy = "modelmux"
                });
            }
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(modelList, ListModelsJsonContext.Default.ListModelsResponse);
        await context.Response.WriteAsync(json);
    }

    /// <summary>
    /// Detect the required model capabilities (image/audio) from the request path
    /// and body content. Returns an empty set when only text is required.
    /// Only the LAST message in the messages array is inspected for image/audio;
    /// historical messages may still reference image_url/audio_url but their content
    /// has already been digested by the model in previous turns, so they do not
    /// require the model to be multimodal.
    /// Matching is string-based (consistent with the rest of this AOT-first proxy).
    /// </summary>
    private static HashSet<string>? DetectRequiredCapabilities(string path, string bodyText)
    {
        var caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Path-based detection (the request's own target path, no historical semantics)
        if (path.Contains("/audio/", StringComparison.OrdinalIgnoreCase))
            caps.Add("AUDIO");
        if (path.Contains("/images/", StringComparison.OrdinalIgnoreCase))
            caps.Add("IMAGE");

        // Body-based detection: only the LAST message's content matters.
        var lastMessageContent = ExtractLastMessageContent(bodyText);
        if (!string.IsNullOrEmpty(lastMessageContent))
        {
            if (lastMessageContent.Contains("image_url", StringComparison.OrdinalIgnoreCase) ||
                lastMessageContent.Contains("input_image", StringComparison.OrdinalIgnoreCase))
                caps.Add("IMAGE");

            if (lastMessageContent.Contains("audio_url", StringComparison.OrdinalIgnoreCase) ||
                lastMessageContent.Contains("input_audio", StringComparison.OrdinalIgnoreCase))
                caps.Add("AUDIO");
        }

        // Return null when no extra capability is required, so RouterService
        // skips the capability filter entirely (zero behavior change for text-only).
        return caps.Count > 0 ? caps : null;
    }

    /// <summary>
    /// Extract the content of the LAST message object inside the "messages" array.
    /// Returns the raw substring from the last "content" key to the end of that
    /// message object (best-effort; empty string when not found).
    /// AOT-safe: pure string/bracket scanning, no reflection or full deserialization.
    /// </summary>
    private static string ExtractLastMessageContent(string bodyText)
    {
        if (string.IsNullOrEmpty(bodyText))
            return string.Empty;

        // Locate the "messages" array's opening bracket
        var msgsIdx = bodyText.IndexOf("\"messages\"", StringComparison.Ordinal);
        if (msgsIdx < 0)
            return string.Empty;

        var bracketIdx = bodyText.IndexOf('[', msgsIdx);
        if (bracketIdx < 0)
            return string.Empty;

        // Find the last top-level '{' object inside the messages array.
        // We scan from the opening '[' and track nesting depth, remembering the
        // start index of the most recent object at depth 1 (i.e. a direct element).
        int depth = 0;
        int lastTopLevelObjectStart = -1;
        bool inString = false;
        bool escaped = false;

        for (int i = bracketIdx; i < bodyText.Length; i++)
        {
            char c = bodyText[i];

            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') { inString = false; continue; }
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '[') { depth++; continue; }
            if (c == ']')
            {
                depth--;
                if (depth <= 0)
                    break; // reached end of messages array
                continue;
            }
            if (c == '{')
            {
                if (depth == 1)
                    lastTopLevelObjectStart = i;
                depth++;
                continue;
            }
            if (c == '}')
            {
                depth--;
                continue;
            }
        }

        if (lastTopLevelObjectStart < 0)
            return string.Empty;

        return bodyText[lastTopLevelObjectStart..];
    }

    /// <summary>
    /// Extract a simple string field value from a JSON string.
    /// Handles: "model": "gpt-4o" and "model":"gpt-4o"
    /// </summary>
    private static string? ExtractJsonField(string json, string fieldName)
    {
        var search = $"\"{fieldName}\"";
        var idx = json.IndexOf(search, StringComparison.Ordinal);
        if (idx < 0) return null;

        idx += search.Length;
        // Skip whitespace and colon
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':'))
        {
            idx++;
            // Also skip \r\n\t
            if (idx < json.Length && (json[idx] == '\r' || json[idx] == '\n' || json[idx] == '\t'))
                idx++;
        }

        if (idx >= json.Length) return null;

        // Expect opening quote
        if (json[idx] != '"') return null;
        idx++;

        // Read until closing unescaped quote
        var end = idx;
        while (end < json.Length)
        {
            if (json[end] == '\\')
            {
                end += 2; // skip escaped char
                continue;
            }
            if (json[end] == '"')
                break;
            end++;
        }

        if (end > idx)
            return json[idx..end];

        return null;
    }
}