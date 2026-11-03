namespace modelmux.Models;

/// <summary>
/// One failed upstream attempt, captured so the final error response can tell the
/// caller what actually went wrong instead of a generic "all routes failed".
/// </summary>
public sealed record AttemptFailure
{
    /// <summary>Public-facing model name that was attempted.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Upstream HTTP status code. Null when the upstream was unreachable.</summary>
    public int? Status { get; init; }

    /// <summary>Upstream error.message, or a truncated raw body when not extractable.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Upstream error.code, normalized to a string.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Truncated raw body when no structured error object was found.</summary>
    public string? Raw { get; init; }
}
