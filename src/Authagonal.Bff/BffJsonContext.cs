using System.Text.Json.Serialization;

namespace Authagonal.Bff;

/// <summary>Short-lived pre-authentication state, carried in the protected correlation cookie across the
/// login → callback navigation. <see cref="TenantKey"/> pins the tenant the login was started for, so the
/// callback exchanges the code against the same client (null in single-tenant mode).</summary>
internal sealed record CorrelationState(string State, string CodeVerifier, string Nonce, string ReturnUrl, string? TenantKey);

/// <summary>The <c>/bff/user</c> response shape.</summary>
internal sealed class UserResponse
{
    public bool IsAuthenticated { get; set; }
    public DateTimeOffset? SessionExpiresAt { get; set; }
    public Dictionary<string, string>? Claims { get; set; }
}

// Source-generated (trim-safe) JSON for everything the BFF serializes itself.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BffSession))]
[JsonSerializable(typeof(CorrelationState))]
[JsonSerializable(typeof(UserResponse))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class BffJsonContext : JsonSerializerContext;
