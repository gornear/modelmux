using modelmux.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace modelmux.Middleware;

public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyMiddleware> _logger;
    private readonly ApiKeyList? _apiKeys;

    public ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger, AppSettings appSettings)
    {
        _next = next;
        _logger = logger;
        _apiKeys = appSettings.ApiKey;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip auth for /health
        if (context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // If no API key configured, allow all requests (passthrough mode)
        if (_apiKeys is null or { IsEmpty: true })
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader))
        {
            _logger.LogWarning("Missing Authorization header from {IP}", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":{\"message\":\"Missing Authorization header. Use: Bearer <api-key>\",\"type\":\"unauthorized\"}}");
            return;
        }

        // Expect: "Bearer sk-xxx"
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Invalid Authorization scheme from {IP}", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":{\"message\":\"Invalid Authorization scheme. Use: Bearer <api-key>\",\"type\":\"unauthorized\"}}");
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        if (!_apiKeys.Contains(token))
        {
            _logger.LogWarning("Invalid API key from {IP}", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":{\"message\":\"Invalid API key\",\"type\":\"unauthorized\"}}");
            return;
        }

        await _next(context);
    }
}