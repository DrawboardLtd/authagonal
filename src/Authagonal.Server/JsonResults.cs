using System.Text.Json.Serialization;

namespace Authagonal.Server;

/// <summary>
/// Trim-safe replacements for Results.Json with anonymous types.
/// Uses source-generated JSON serialization via AuthagonalJsonContext.
/// </summary>
internal static class JsonResults
{
    public static IResult Error(string error, int statusCode = 400)
        => TypedResults.Json(new ApiError { Error = error },
            AuthagonalJsonContext.Default.ApiError, statusCode: statusCode);

    public static IResult Error(string error, string message, int statusCode = 400)
        => TypedResults.Json(new ApiErrorDetail { Error = error, Message = message },
            AuthagonalJsonContext.Default.ApiErrorDetail, statusCode: statusCode);

    public static IResult OAuthError(string error, string description, int statusCode = 400)
        => TypedResults.Json(new OAuthErrorResponse { Error = error, ErrorDescription = description },
            AuthagonalJsonContext.Default.OAuthErrorResponse, statusCode: statusCode);
}

// ── Common response DTOs ────────────────────────────────────────────

internal sealed class ApiError
{
    [JsonPropertyName("error")]
    public required string Error { get; set; }
}

internal sealed class ApiErrorDetail
{
    [JsonPropertyName("error")]
    public required string Error { get; set; }
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>OAuth 2.0 standard error response format.</summary>
internal sealed class OAuthErrorResponse
{
    [JsonPropertyName("error")]
    public required string Error { get; set; }
    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

internal sealed class SsoRedirectError
{
    [JsonPropertyName("error")]
    public required string Error { get; set; }
    [JsonPropertyName("redirectUrl")]
    public required string RedirectUrl { get; set; }
}

internal sealed class LockedOutError
{
    [JsonPropertyName("error")]
    public required string Error { get; set; }
    [JsonPropertyName("retryAfter")]
    public int RetryAfter { get; set; }
}

internal sealed class RegistrationSuccess
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    [JsonPropertyName("userId")]
    public required string UserId { get; set; }
}

internal sealed class ResendEmailRequest
{
    [JsonPropertyName("from")]
    public required string From { get; set; }
    [JsonPropertyName("to")]
    public required string[] To { get; set; }
    [JsonPropertyName("subject")]
    public required string Subject { get; set; }
    [JsonPropertyName("html")]
    public required string Html { get; set; }
}
