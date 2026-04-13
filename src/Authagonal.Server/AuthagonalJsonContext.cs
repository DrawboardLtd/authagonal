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
// Common response DTOs
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(ApiErrorDetail))]
[JsonSerializable(typeof(OAuthErrorResponse))]
[JsonSerializable(typeof(SsoRedirectError))]
[JsonSerializable(typeof(LockedOutError))]
[JsonSerializable(typeof(RegistrationSuccess))]
[JsonSerializable(typeof(ScimError))]
// Cluster gossip
[JsonSerializable(typeof(GossipMessage))]
[JsonSerializable(typeof(GossipResponse))]
// Email
[JsonSerializable(typeof(ResendEmailRequest))]
internal partial class AuthagonalJsonContext : JsonSerializerContext
{
}
