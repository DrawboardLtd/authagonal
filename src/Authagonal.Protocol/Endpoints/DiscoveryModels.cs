using System.Text.Json.Serialization;

namespace Authagonal.Protocol.Endpoints;

/// <summary>
/// OIDC discovery document shared by Authagonal.Protocol and Authagonal.Server.
/// Optional members are omitted when null so each host advertises only the
/// endpoints it actually maps.
/// </summary>
public sealed class DiscoveryResponse
{
    [JsonPropertyName("issuer")] public string Issuer { get; set; } = "";
    [JsonPropertyName("authorization_endpoint")] public string AuthorizationEndpoint { get; set; } = "";
    [JsonPropertyName("token_endpoint")] public string TokenEndpoint { get; set; } = "";
    [JsonPropertyName("userinfo_endpoint")] public string UserinfoEndpoint { get; set; } = "";
    [JsonPropertyName("jwks_uri")] public string JwksUri { get; set; } = "";
    [JsonPropertyName("revocation_endpoint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RevocationEndpoint { get; set; }
    [JsonPropertyName("introspection_endpoint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? IntrospectionEndpoint { get; set; }
    [JsonPropertyName("end_session_endpoint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? EndSessionEndpoint { get; set; }
    [JsonPropertyName("device_authorization_endpoint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DeviceAuthorizationEndpoint { get; set; }
    [JsonPropertyName("registration_endpoint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RegistrationEndpoint { get; set; }
    [JsonPropertyName("pushed_authorization_request_endpoint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PushedAuthorizationRequestEndpoint { get; set; }
    [JsonPropertyName("scopes_supported")] public string[] ScopesSupported { get; set; } = [];
    [JsonPropertyName("response_types_supported")] public string[] ResponseTypesSupported { get; set; } = [];
    [JsonPropertyName("grant_types_supported")] public string[] GrantTypesSupported { get; set; } = [];
    [JsonPropertyName("subject_types_supported")] public string[] SubjectTypesSupported { get; set; } = [];
    [JsonPropertyName("id_token_signing_alg_values_supported")] public string[] IdTokenSigningAlgValuesSupported { get; set; } = [];
    [JsonPropertyName("token_endpoint_auth_methods_supported")] public string[] TokenEndpointAuthMethodsSupported { get; set; } = [];
    [JsonPropertyName("code_challenge_methods_supported")] public string[] CodeChallengeMethodsSupported { get; set; } = [];
    [JsonPropertyName("backchannel_logout_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? BackchannelLogoutSupported { get; set; }
    [JsonPropertyName("backchannel_logout_session_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? BackchannelLogoutSessionSupported { get; set; }
    [JsonPropertyName("frontchannel_logout_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? FrontchannelLogoutSupported { get; set; }
    [JsonPropertyName("frontchannel_logout_session_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? FrontchannelLogoutSessionSupported { get; set; }
    [JsonPropertyName("require_pushed_authorization_requests"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? RequirePushedAuthorizationRequests { get; set; }
    [JsonPropertyName("claims_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? ClaimsSupported { get; set; }
}

public sealed class JwksDocument
{
    [JsonPropertyName("keys")] public List<JwkKey> Keys { get; set; } = [];
}

public sealed class JwkKey
{
    [JsonPropertyName("kty")] public string Kty { get; set; } = "";
    [JsonPropertyName("use")] public string Use { get; set; } = "";
    [JsonPropertyName("kid")] public string Kid { get; set; } = "";
    [JsonPropertyName("alg")] public string Alg { get; set; } = "";

    // EC fields (ES256/384/521)
    [JsonPropertyName("crv"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Crv { get; set; }
    [JsonPropertyName("x"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? X { get; set; }
    [JsonPropertyName("y"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Y { get; set; }

    // RSA fields (RS256, retained for legacy / interop)
    [JsonPropertyName("n"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? N { get; set; }
    [JsonPropertyName("e"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? E { get; set; }
}

internal sealed class OAuthErrorResponse
{
    [JsonPropertyName("error")] public required string Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}
