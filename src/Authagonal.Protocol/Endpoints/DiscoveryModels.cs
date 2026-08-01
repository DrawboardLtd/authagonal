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

    /// <summary>
    /// OIDC Discovery §3 — the JWS algorithms accepted on a <c>private_key_jwt</c> client assertion.
    /// </summary>
    /// <remarks>
    /// Omitting it while advertising <c>private_key_jwt</c> left a client with no way to learn which
    /// algorithms it may sign with, so agreement was by trial and error against an endpoint whose only
    /// failure signal is <c>invalid_client</c>. The list mirrors <c>ValidAlgorithms</c> in
    /// ClientAuthentication — RFC 7518 asymmetric algorithms only, so a symmetric key in a client's own
    /// published JWKS can never turn client authentication into an HMAC over a public value.
    /// </remarks>
    [JsonPropertyName("token_endpoint_auth_signing_alg_values_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? TokenEndpointAuthSigningAlgValuesSupported { get; set; }
    [JsonPropertyName("code_challenge_methods_supported")] public string[] CodeChallengeMethodsSupported { get; set; } = [];
    /// <summary>RFC 9207 — we name ourselves in the authorization response so a client talking to
    /// several authorization servers can tell which one answered (the mix-up defence).</summary>
    [JsonPropertyName("authorization_response_iss_parameter_supported")] public bool AuthorizationResponseIssParameterSupported { get; set; }
    [JsonPropertyName("backchannel_logout_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? BackchannelLogoutSupported { get; set; }
    [JsonPropertyName("backchannel_logout_session_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? BackchannelLogoutSessionSupported { get; set; }
    [JsonPropertyName("frontchannel_logout_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? FrontchannelLogoutSupported { get; set; }
    [JsonPropertyName("frontchannel_logout_session_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? FrontchannelLogoutSessionSupported { get; set; }
    [JsonPropertyName("require_pushed_authorization_requests"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? RequirePushedAuthorizationRequests { get; set; }
    /// <summary>
    /// OIDC Discovery §3 — RFC 9101 request objects. Both default to a value that over-states this
    /// server when omitted: <c>request_uri_parameter_supported</c> defaults to <b>true</b>, and JAR
    /// by reference is not implemented (the only <c>request_uri</c> values accepted are the opaque
    /// URNs this server's own PAR endpoint issued). Stated explicitly so the document says what is
    /// true rather than inheriting a default that is not.
    /// </summary>
    [JsonPropertyName("request_parameter_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? RequestParameterSupported { get; set; }

    /// <inheritdoc cref="RequestParameterSupported"/>
    [JsonPropertyName("request_uri_parameter_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? RequestUriParameterSupported { get; set; }

    /// <summary>
    /// OIDC Discovery §3 — omitting this claims <c>["query", "fragment"]</c> by default, but
    /// <c>response_mode</c> is never read and the authorization code is always written into the
    /// query string.
    /// </summary>
    [JsonPropertyName("response_modes_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? ResponseModesSupported { get; set; }

    [JsonPropertyName("claims_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? ClaimsSupported { get; set; }
    [JsonPropertyName("authorization_details_types_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? AuthorizationDetailsTypesSupported { get; set; }
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

/// <summary>The <c>authorization_pending</c> body a delegated exchange returns while parked
/// on an approval — the error shape plus the handle and poll interval the agent needs.</summary>
internal sealed class ApprovalPendingResponse
{
    [JsonPropertyName("error")] public string Error { get; set; } = "authorization_pending";
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    [JsonPropertyName("approval_id")] public required string ApprovalId { get; set; }
    [JsonPropertyName("interval")] public int Interval { get; set; }
}
