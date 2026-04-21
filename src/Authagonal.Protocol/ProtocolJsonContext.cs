using System.Text.Json.Serialization;
using Authagonal.Core.Models;
using Authagonal.Protocol.Endpoints;
using Authagonal.Protocol.Models;

namespace Authagonal.Protocol;

[JsonSerializable(typeof(ProtocolAuthorizationCode))]
[JsonSerializable(typeof(RefreshTokenData))]
[JsonSerializable(typeof(OidcSubject))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(DiscoveryResponse))]
[JsonSerializable(typeof(JwksDocument))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(OAuthErrorResponse))]
[JsonSerializable(typeof(PushedAuthorizationRequest))]
[JsonSerializable(typeof(PushedAuthorizationResponse))]
internal partial class ProtocolJsonContext : JsonSerializerContext
{
}
