using System.Text.Json.Serialization;

namespace Authagonal.Server.Endpoints;

public sealed class OidcDiscoveryDocument
{
    [JsonPropertyName("issuer")] public string Issuer { get; set; } = "";
    [JsonPropertyName("authorization_endpoint")] public string AuthorizationEndpoint { get; set; } = "";
    [JsonPropertyName("token_endpoint")] public string TokenEndpoint { get; set; } = "";
    [JsonPropertyName("userinfo_endpoint")] public string UserinfoEndpoint { get; set; } = "";
    [JsonPropertyName("jwks_uri")] public string JwksUri { get; set; } = "";
    [JsonPropertyName("revocation_endpoint")] public string RevocationEndpoint { get; set; } = "";
    [JsonPropertyName("introspection_endpoint")] public string IntrospectionEndpoint { get; set; } = "";
    [JsonPropertyName("end_session_endpoint")] public string EndSessionEndpoint { get; set; } = "";
    [JsonPropertyName("device_authorization_endpoint")] public string DeviceAuthorizationEndpoint { get; set; } = "";
    [JsonPropertyName("scopes_supported")] public string[] ScopesSupported { get; set; } = [];
    [JsonPropertyName("response_types_supported")] public string[] ResponseTypesSupported { get; set; } = [];
    [JsonPropertyName("grant_types_supported")] public string[] GrantTypesSupported { get; set; } = [];
    [JsonPropertyName("subject_types_supported")] public string[] SubjectTypesSupported { get; set; } = [];
    [JsonPropertyName("id_token_signing_alg_values_supported")] public string[] IdTokenSigningAlgValuesSupported { get; set; } = [];
    [JsonPropertyName("token_endpoint_auth_methods_supported")] public string[] TokenEndpointAuthMethodsSupported { get; set; } = [];
    [JsonPropertyName("code_challenge_methods_supported")] public string[] CodeChallengeMethodsSupported { get; set; } = [];
    [JsonPropertyName("backchannel_logout_supported")] public bool BackchannelLogoutSupported { get; set; }
    [JsonPropertyName("backchannel_logout_session_supported")] public bool BackchannelLogoutSessionSupported { get; set; }
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
    [JsonPropertyName("n")] public string N { get; set; } = "";
    [JsonPropertyName("e")] public string E { get; set; } = "";
}
