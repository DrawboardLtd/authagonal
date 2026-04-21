namespace Authagonal.Protocol.Models;

/// <summary>
/// Persisted pushed-authorization request (RFC 9126). The full authorize request parameters
/// are stashed server-side under an opaque request_uri so the subsequent browser-facing
/// /authorize call only needs to carry client_id + request_uri.
/// </summary>
public sealed class PushedAuthorizationRequest
{
    public required string RequestUri { get; set; }
    public required string ClientId { get; set; }
    public required Dictionary<string, string[]> Parameters { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class PushedAuthorizationResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("request_uri")] public required string RequestUri { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("expires_in")] public required int ExpiresIn { get; set; }
}
