using System.Text.Json.Serialization;

namespace Authagonal.Server.Endpoints;

// --- Common reusable responses ---

public sealed class SuccessResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; } = true;
}

public sealed class SuccessMessageResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; } = true;
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public sealed class MessageResponse
{
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public sealed class RedirectResponse
{
    [JsonPropertyName("redirect")] public string Redirect { get; set; } = "";
}

public sealed class ErrorInfoResponse
{
    [JsonPropertyName("error")] public string Error { get; set; } = "";

    [JsonPropertyName("error_description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorDescription { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

// --- Auth / Login ---

public sealed class LoginSuccessResponse
{
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("mfaAvailable")] public bool MfaAvailable { get; set; }

    [JsonPropertyName("clientId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientId { get; set; }
}

public sealed class MfaRequiredResponse
{
    [JsonPropertyName("mfaRequired")] public bool MfaRequired { get; set; } = true;
    [JsonPropertyName("challengeId")] public string ChallengeId { get; set; } = "";
    [JsonPropertyName("methods")] public List<string> Methods { get; set; } = [];

    [JsonPropertyName("webAuthn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? WebAuthn { get; set; }
}

public sealed class MfaSetupRequiredResponse
{
    [JsonPropertyName("mfaSetupRequired")] public bool MfaSetupRequired { get; set; } = true;
    [JsonPropertyName("setupToken")] public string SetupToken { get; set; } = "";
}

public sealed class SessionResponse
{
    [JsonPropertyName("authenticated")] public bool Authenticated { get; set; } = true;
    [JsonPropertyName("userId")] public string? UserId { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class UserIdentityResponse
{
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

// --- SSO ---

public sealed class SsoCheckResponse
{
    [JsonPropertyName("ssoRequired")] public bool SsoRequired { get; set; }

    [JsonPropertyName("providerType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderType { get; set; }

    [JsonPropertyName("connectionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectionId { get; set; }

    [JsonPropertyName("redirectUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RedirectUrl { get; set; }
}

public sealed class SsoProviderInfo
{
    [JsonPropertyName("connectionId")] public string ConnectionId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("loginUrl")] public string LoginUrl { get; set; } = "";
}

public sealed class SsoProviderListResponse
{
    [JsonPropertyName("providers")] public IEnumerable<SsoProviderInfo> Providers { get; set; } = [];
}

// --- Password Policy ---

public sealed class PasswordPolicyRule
{
    [JsonPropertyName("rule")] public string Rule { get; set; } = "";

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Value { get; set; }

    [JsonPropertyName("label")] public string Label { get; set; } = "";
}

public sealed class PasswordPolicyResponse
{
    [JsonPropertyName("rules")] public List<PasswordPolicyRule> Rules { get; set; } = [];
}

// --- MFA Setup ---

public sealed class MfaStatusResponse
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("methods")] public List<MfaMethodInfo> Methods { get; set; } = [];
}

public sealed class MfaMethodInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("lastUsedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastUsedAt { get; set; }

    [JsonPropertyName("isConsumed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsConsumed { get; set; }
}

public sealed class TotpSetupResponse
{
    [JsonPropertyName("setupToken")] public string SetupToken { get; set; } = "";
    [JsonPropertyName("qrCodeDataUri")] public string QrCodeDataUri { get; set; } = "";
    [JsonPropertyName("manualKey")] public string ManualKey { get; set; } = "";
}

public sealed class WebAuthnSetupResponse
{
    [JsonPropertyName("setupToken")] public string SetupToken { get; set; } = "";
    [JsonPropertyName("options")] public object Options { get; set; } = null!;
}

public sealed class WebAuthnConfirmResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; } = true;
    [JsonPropertyName("credentialId")] public string CredentialId { get; set; } = "";
}

public sealed class RecoveryCodesResponse
{
    [JsonPropertyName("codes")] public List<string> Codes { get; set; } = [];
}

// --- Device Authorization ---

public sealed class DeviceAuthorizationResponse
{
    [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
    [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
    [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "";
    [JsonPropertyName("verification_uri_complete")] public string VerificationUriComplete { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
}

public sealed class DeviceApprovedResponse
{
    [JsonPropertyName("approved")] public bool Approved { get; set; } = true;
}

// --- Introspection ---

public sealed class IntrospectionInactiveResponse
{
    [JsonPropertyName("active")] public bool Active { get; set; }
}

// --- Consent ---

public sealed class ConsentInfoResponse
{
    [JsonPropertyName("clientId")] public string ClientId { get; set; } = "";
    [JsonPropertyName("clientName")] public string ClientName { get; set; } = "";
    [JsonPropertyName("scopes")] public string[] Scopes { get; set; } = [];
}

// --- BackChannel Logout ---

public sealed class BackChannelLogoutResult
{
    [JsonPropertyName("notified")] public int Notified { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
    [JsonPropertyName("grantsRevoked")] public int GrantsRevoked { get; set; }
}

// --- Admin: User ---

public sealed class ExternalLoginDto
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("providerKey")] public string ProviderKey { get; set; } = "";
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
}

public sealed class UserDetailResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("emailConfirmed")] public bool EmailConfirmed { get; set; }
    [JsonPropertyName("firstName")] public string? FirstName { get; set; }
    [JsonPropertyName("lastName")] public string? LastName { get; set; }
    [JsonPropertyName("companyName")] public string? CompanyName { get; set; }
    [JsonPropertyName("phone")] public string? Phone { get; set; }
    [JsonPropertyName("organizationId")] public string? OrganizationId { get; set; }
    [JsonPropertyName("lockoutEnabled")] public bool LockoutEnabled { get; set; }

    [JsonPropertyName("lockoutEnd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LockoutEnd { get; set; }

    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("externalLogins")] public IEnumerable<ExternalLoginDto> ExternalLogins { get; set; } = [];
}

public sealed class UserUpdateResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("emailConfirmed")] public bool EmailConfirmed { get; set; }
    [JsonPropertyName("firstName")] public string? FirstName { get; set; }
    [JsonPropertyName("lastName")] public string? LastName { get; set; }
    [JsonPropertyName("companyName")] public string? CompanyName { get; set; }
    [JsonPropertyName("phone")] public string? Phone { get; set; }
    [JsonPropertyName("organizationId")] public string? OrganizationId { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
}

// --- Admin: Roles ---

public sealed class RoleListResponse
{
    [JsonPropertyName("roles")] public IEnumerable<Authagonal.Core.Models.Role> Roles { get; set; } = [];
}

public sealed class UserRolesResponse
{
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = [];
}

// --- Admin: Scopes ---

public sealed class ScopeListResponse
{
    [JsonPropertyName("scopes")] public IEnumerable<Authagonal.Core.Models.Scope> Scopes { get; set; } = [];
}

// --- Dynamic Client Registration (RFC 7591) ---

public sealed class ClientRegistrationRequest
{
    [JsonPropertyName("client_name")] public string? ClientName { get; set; }
    [JsonPropertyName("redirect_uris")] public List<string>? RedirectUris { get; set; }
    [JsonPropertyName("post_logout_redirect_uris")] public List<string>? PostLogoutRedirectUris { get; set; }
    [JsonPropertyName("grant_types")] public List<string>? GrantTypes { get; set; }
    [JsonPropertyName("response_types")] public List<string>? ResponseTypes { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("token_endpoint_auth_method")] public string? TokenEndpointAuthMethod { get; set; }
    [JsonPropertyName("application_type")] public string? ApplicationType { get; set; }
    [JsonPropertyName("contacts")] public List<string>? Contacts { get; set; }
    [JsonPropertyName("backchannel_logout_uri")] public string? BackchannelLogoutUri { get; set; }
    [JsonPropertyName("frontchannel_logout_uri")] public string? FrontchannelLogoutUri { get; set; }
    [JsonPropertyName("frontchannel_logout_session_required")] public bool? FrontchannelLogoutSessionRequired { get; set; }
    [JsonPropertyName("audiences")] public List<string>? Audiences { get; set; }
    [JsonPropertyName("allowed_cors_origins")] public List<string>? AllowedCorsOrigins { get; set; }
}

public sealed class ClientRegistrationResponse
{
    [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";

    [JsonPropertyName("client_secret")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientSecret { get; set; }

    [JsonPropertyName("client_id_issued_at")] public long ClientIdIssuedAt { get; set; }
    [JsonPropertyName("client_secret_expires_at")] public long ClientSecretExpiresAt { get; set; }
    [JsonPropertyName("client_name")] public string ClientName { get; set; } = "";
    [JsonPropertyName("redirect_uris")] public List<string> RedirectUris { get; set; } = [];
    [JsonPropertyName("post_logout_redirect_uris")] public List<string> PostLogoutRedirectUris { get; set; } = [];
    [JsonPropertyName("grant_types")] public List<string> GrantTypes { get; set; } = [];
    [JsonPropertyName("response_types")] public List<string> ResponseTypes { get; set; } = [];
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    [JsonPropertyName("token_endpoint_auth_method")] public string TokenEndpointAuthMethod { get; set; } = "";
}

// --- Admin: SCIM Tokens ---

public sealed class ScimTokenCreatedResponse
{
    [JsonPropertyName("tokenId")] public string TokenId { get; set; } = "";
    [JsonPropertyName("clientId")] public string ClientId { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ScimTokenInfo
{
    [JsonPropertyName("tokenId")] public string TokenId { get; set; } = "";
    [JsonPropertyName("clientId")] public string ClientId { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; set; }
    [JsonPropertyName("isRevoked")] public bool IsRevoked { get; set; }
}

public sealed class ScimTokenListResponse
{
    [JsonPropertyName("tokens")] public IEnumerable<ScimTokenInfo> Tokens { get; set; } = [];
}
