using System.Text.Json;
using System.Text.Json.Serialization;

namespace Authagonal.Server.Endpoints;

// --- Common reusable responses ---

public sealed class SuccessResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; } = true;
}

/// <summary>Response for <c>POST /api/auth/logout</c>.</summary>
public sealed class LogoutResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; } = true;

    /// <summary>
    /// Front-channel logout URIs the caller should load (hidden iframes) to complete sign-out at the relying
    /// parties that registered one.
    /// </summary>
    /// <remarks>
    /// Returned rather than acted on because a front-channel logout is, by definition, performed by the user's
    /// browser — a JSON endpoint has no page to render them into. The back-channel notifications and the
    /// session-bound grant revocation are done server-side before this responds; this list is the part only the
    /// caller can finish.
    /// </remarks>
    [JsonPropertyName("frontchannel_logout_uris")]
    public IReadOnlyList<string> FrontChannelLogoutUris { get; set; } = [];
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

/// <summary>One application link for the hosted account pages' "back to app" button / launcher:
/// an enabled client with a usable home URI. <c>homeUri</c> is the navigation target
/// (initiate-login URI when set, else the client URI).</summary>
public sealed class AppLinkResponse
{
    [JsonPropertyName("clientId")] public string ClientId { get; set; } = "";
    [JsonPropertyName("clientName")] public string ClientName { get; set; } = "";
    [JsonPropertyName("homeUri")] public string HomeUri { get; set; } = "";
    [JsonPropertyName("logoUri")] public string? LogoUri { get; set; }
    [JsonPropertyName("isDefault")] public bool IsDefault { get; set; }
}

/// <summary>Programmatic (POST) email-confirmation result. <c>appLink</c> is the resolved
/// "continue to app" target (flow client, else tenant default), or null for the plain UX.</summary>
public sealed class ConfirmEmailResponse
{
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("appLink")] public AppLinkResponse? AppLink { get; set; }
}

/// <summary>Password-reset completion result. <c>appLink</c> as on <see cref="ConfirmEmailResponse"/>.</summary>
public sealed class ResetPasswordResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; } = true;
    [JsonPropertyName("appLink")] public AppLinkResponse? AppLink { get; set; }
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
    /// <summary>Connection protocol: "oidc" or "saml". Lets the UI vary affordances if needed.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    /// <summary>Optional branding icon URL for the "Continue with {name}" button.</summary>
    [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
}

public sealed class SsoProviderListResponse
{
    [JsonPropertyName("providers")] public IEnumerable<SsoProviderInfo> Providers { get; set; } = [];

    /// <summary>Cloudflare Turnstile site key when configured; null = Turnstile disabled (UI renders no widget).</summary>
    [JsonPropertyName("turnstileSiteKey")] public string? TurnstileSiteKey { get; set; }
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

    /// <summary>Whether MFA is offered for this tenant at all — false when every client's MfaPolicy is
    /// Disabled, so the self-service setup UI can hide itself. Distinct from <see cref="Enabled"/>, which
    /// means the user has already set MFA up.</summary>
    [JsonPropertyName("offered")] public bool Offered { get; set; }

    /// <summary>
    /// Whether the caller is in a FORCED enrolment — it reached these endpoints with an enrolment token
    /// rather than a session, so it has no session until it enrols.
    /// </summary>
    /// <remarks>
    /// The setup page needs to know this to decide whether to show a way out and where to send the user
    /// afterwards. It used to infer it from a <c>setupToken</c> query parameter, which is why the token was
    /// in the URL at all. The server already knows, so it says.
    /// </remarks>
    [JsonPropertyName("forced")] public bool Forced { get; set; }
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
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("clientUri")] public string? ClientUri { get; set; }
    [JsonPropertyName("logoUri")] public string? LogoUri { get; set; }

    /// <summary>The requested scope names, in the order the client asked for them.</summary>
    [JsonPropertyName("scopes")] public string[] Scopes { get; set; } = [];

    /// <summary>
    /// The same scopes with their registered presentation: display name, description, and whether the
    /// user may decline them.
    /// </summary>
    /// <remarks>
    /// Parallel to <see cref="Scopes"/> rather than replacing it, so a login app built against an
    /// earlier version keeps working. Without this the consent screen only ever saw raw scope names and
    /// had to invent wording — which produced "View your search" for
    /// <c>projects-api.search.read</c> while the registry held "Search drawings and documents" all
    /// along. Scope wording belongs to whoever registered the scope; the login app renders it.
    /// </remarks>
    [JsonPropertyName("scopeDetails")] public ConsentScopeInfo[] ScopeDetails { get; set; } = [];
}

/// <summary>How one requested scope should be presented on the consent screen.</summary>
public sealed class ConsentScopeInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>The registered display name, or null when the scope is not registered.</summary>
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }

    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>Registered as consequential, so the screen may draw attention to it.</summary>
    [JsonPropertyName("emphasize")] public bool Emphasize { get; set; }

    /// <summary>Registered as not declinable: the screen shows it ticked and locked.</summary>
    [JsonPropertyName("required")] public bool Required { get; set; }

    /// <summary>The heading to file this scope under, or null to show it on its own.</summary>
    [JsonPropertyName("group")] public string? Group { get; set; }
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
    [JsonPropertyName("locale")] public string? Locale { get; set; }
    [JsonPropertyName("organizationId")] public string? OrganizationId { get; set; }
    [JsonPropertyName("lockoutEnabled")] public bool LockoutEnabled { get; set; }

    [JsonPropertyName("lockoutEnd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LockoutEnd { get; set; }

    /// <summary>How many failed sign-ins have accrued toward a lockout. A support console showing a
    /// lockout without this cannot tell "one fat-fingered attempt" from "someone is guessing".</summary>
    [JsonPropertyName("accessFailedCount")] public int AccessFailedCount { get; set; }

    /// <summary>
    /// Whether the account has a local password at all, as opposed to being reachable only through an
    /// external provider.
    /// </summary>
    /// <remarks>
    /// The hash itself is never returned — only whether one exists. It is the difference between "they
    /// have forgotten their password" and "they have never had one, they sign in with SSO", which are
    /// opposite pieces of advice to give someone who cannot get in.
    /// </remarks>
    [JsonPropertyName("hasPassword")] public bool HasPassword { get; set; }

    /// <summary>Whether the account is enabled. A disabled one cannot obtain a token at all.</summary>
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }

    /// <summary>Every role the user holds.</summary>
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = [];

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
    [JsonPropertyName("locale")] public string? Locale { get; set; }
    [JsonPropertyName("organizationId")] public string? OrganizationId { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Self-service profile (the authenticated user's own editable fields).</summary>
public sealed class ProfileResponse
{
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("emailConfirmed")] public bool EmailConfirmed { get; set; }
    [JsonPropertyName("firstName")] public string? FirstName { get; set; }
    [JsonPropertyName("lastName")] public string? LastName { get; set; }
    [JsonPropertyName("companyName")] public string? CompanyName { get; set; }
    [JsonPropertyName("phone")] public string? Phone { get; set; }
    [JsonPropertyName("locale")] public string? Locale { get; set; }
}

/// <summary>One of the caller's own active server-side sessions.</summary>
public sealed class ActiveSessionView
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("current")] public bool Current { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("lastSeenAt")] public DateTimeOffset LastSeenAt { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("ip")] public string Ip { get; set; } = "";
    [JsonPropertyName("userAgent")] public string UserAgent { get; set; } = "";
}

/// <summary>The caller's active server-side sessions (empty when session tracking isn't enabled).</summary>
public sealed class ActiveSessionsResponse
{
    [JsonPropertyName("sessions")] public IReadOnlyList<ActiveSessionView> Sessions { get; set; } = [];
}

/// <summary>Result of a bulk session revocation.</summary>
public sealed class RevokeSessionsResponse
{
    [JsonPropertyName("revoked")] public int Revoked { get; set; }
}

// --- Admin: User directory ---

public sealed class UserSummary
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("emailConfirmed")] public bool EmailConfirmed { get; set; }
    [JsonPropertyName("firstName")] public string? FirstName { get; set; }
    [JsonPropertyName("lastName")] public string? LastName { get; set; }
    [JsonPropertyName("organizationId")] public string? OrganizationId { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("lockoutEnd")] public DateTimeOffset? LockoutEnd { get; set; }
    [JsonPropertyName("mfaEnabled")] public bool MfaEnabled { get; set; }
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = [];
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("lastLoginAt")] public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class UserSearchResponse
{
    [JsonPropertyName("users")] public List<UserSummary> Users { get; set; } = [];
}

public sealed class UserListResponse
{
    [JsonPropertyName("users")] public List<UserSummary> Users { get; set; } = [];

    /// <summary>Pass back as <c>continuationToken</c> for the next page; null when there are none.</summary>
    [JsonPropertyName("continuationToken")] public string? ContinuationToken { get; set; }
}

public sealed class UserExistsResponse
{
    /// <summary>The subset of the requested ids that exist.</summary>
    [JsonPropertyName("userIds")] public List<string> UserIds { get; set; } = [];

    /// <summary>True when the request exceeded the per-call cap and was trimmed.</summary>
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
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

public sealed class RoleMembersResponse
{
    [JsonPropertyName("roleName")] public string RoleName { get; set; } = "";
    [JsonPropertyName("members")] public List<RoleMemberResponse> Members { get; set; } = [];
}

public sealed class RoleMemberResponse
{
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("firstName")] public string? FirstName { get; set; }
    [JsonPropertyName("lastName")] public string? LastName { get; set; }

    /// <summary>Every role this person holds, not just the one queried — a console listing one role
    /// almost always wants to show what else its members have.</summary>
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

    /// <summary>RFC 7591 §2 — inline JWKS, required (or <see cref="JwksUri"/>) for
    /// <c>private_key_jwt</c>. Without one the method binds no key and cannot authenticate anything.</summary>
    [JsonPropertyName("jwks")] public JsonElement? Jwks { get; set; }

    /// <summary>RFC 7591 §2 — JWKS by reference. Fetched by this server during client authentication,
    /// so it is validated against the outbound SSRF guard at registration time.</summary>
    [JsonPropertyName("jwks_uri")] public string? JwksUri { get; set; }

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

    /// <summary>Echoed so the caller can see the bound that was actually recorded. Empty = unrestricted.</summary>
    [JsonPropertyName("allowedEmailDomains")] public List<string> AllowedEmailDomains { get; set; } = [];
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
