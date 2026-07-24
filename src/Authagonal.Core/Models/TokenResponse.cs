using System.Text.Json.Serialization;

namespace Authagonal.Core.Models;

public sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("refresh_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("id_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdToken { get; set; }

    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; set; }

    /// <summary>RFC 8693 §2.2.1 — set only on token-exchange responses.</summary>
    [JsonPropertyName("issued_token_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IssuedTokenType { get; set; }

    /// <summary>RFC 9396 §7 — the authorization details actually granted, echoed on
    /// responses whose access token carries an <c>authorization_details</c> claim.</summary>
    [JsonPropertyName("authorization_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.JsonElement? AuthorizationDetails { get; set; }
}
