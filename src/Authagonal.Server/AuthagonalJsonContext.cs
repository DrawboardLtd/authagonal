using System.Text.Json.Serialization;
using Authagonal.Core.Models;
using Authagonal.Server.Endpoints;
using Authagonal.Server.Endpoints.Scim;
using Authagonal.Server.Middleware;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Cluster;

namespace Authagonal.Server;

[JsonSerializable(typeof(AuthorizationCode))]
[JsonSerializable(typeof(TokenService.RefreshTokenData))]
[JsonSerializable(typeof(DeviceCodeData))]
[JsonSerializable(typeof(AuthorizeEndpoint.ConsentData))]
[JsonSerializable(typeof(WebAuthnCredentialData))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(VaultResponse<SignResponse>))]
[JsonSerializable(typeof(VaultResponse<VerifyResponse>))]
[JsonSerializable(typeof(VaultResponse<TransitKeyInfo>))]
[JsonSerializable(typeof(VaultSignRequest))]
[JsonSerializable(typeof(VaultVerifyRequest))]
[JsonSerializable(typeof(VaultCreateKeyRequest))]
[JsonSerializable(typeof(VaultKeyConfigRequest))]
[JsonSerializable(typeof(ErrorResponse))]
// Discovery
[JsonSerializable(typeof(DiscoveryResponse))]
[JsonSerializable(typeof(JwksDocument))]
// Common response DTOs
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(ApiErrorDetail))]
[JsonSerializable(typeof(OAuthErrorResponse))]
[JsonSerializable(typeof(SsoRedirectError))]
[JsonSerializable(typeof(LockedOutError))]
[JsonSerializable(typeof(RegistrationSuccess))]
[JsonSerializable(typeof(ScimError))]
// Endpoint response DTOs (trim-safe replacements for anonymous types)
[JsonSerializable(typeof(SuccessResponse))]
[JsonSerializable(typeof(SuccessMessageResponse))]
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(RedirectResponse))]
[JsonSerializable(typeof(ErrorInfoResponse))]
[JsonSerializable(typeof(LoginSuccessResponse))]
[JsonSerializable(typeof(MfaRequiredResponse))]
[JsonSerializable(typeof(MfaSetupRequiredResponse))]
[JsonSerializable(typeof(SessionResponse))]
[JsonSerializable(typeof(UserIdentityResponse))]
[JsonSerializable(typeof(SsoCheckResponse))]
[JsonSerializable(typeof(SsoProviderListResponse))]
[JsonSerializable(typeof(PasswordPolicyResponse))]
[JsonSerializable(typeof(MfaStatusResponse))]
[JsonSerializable(typeof(TotpSetupResponse))]
[JsonSerializable(typeof(WebAuthnSetupResponse))]
[JsonSerializable(typeof(WebAuthnConfirmResponse))]
[JsonSerializable(typeof(RecoveryCodesResponse))]
[JsonSerializable(typeof(DeviceAuthorizationResponse))]
[JsonSerializable(typeof(DeviceApprovedResponse))]
[JsonSerializable(typeof(IntrospectionInactiveResponse))]
[JsonSerializable(typeof(ConsentInfoResponse))]
[JsonSerializable(typeof(BackChannelLogoutResult))]
[JsonSerializable(typeof(UserDetailResponse))]
[JsonSerializable(typeof(UserUpdateResponse))]
[JsonSerializable(typeof(RoleListResponse))]
[JsonSerializable(typeof(UserRolesResponse))]
[JsonSerializable(typeof(ScimTokenCreatedResponse))]
[JsonSerializable(typeof(ScimTokenListResponse))]
// Cluster gossip
[JsonSerializable(typeof(GossipMessage))]
[JsonSerializable(typeof(GossipResponse))]
// Email
[JsonSerializable(typeof(ResendEmailRequest))]
// Auth request DTOs
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(ConfirmEmailRequest))]
[JsonSerializable(typeof(ForgotPasswordRequest))]
[JsonSerializable(typeof(ResetPasswordRequest))]
[JsonSerializable(typeof(MfaVerifyRequest))]
[JsonSerializable(typeof(TotpConfirmRequest))]
[JsonSerializable(typeof(WebAuthnConfirmRequest))]
internal partial class AuthagonalJsonContext : JsonSerializerContext
{
}
