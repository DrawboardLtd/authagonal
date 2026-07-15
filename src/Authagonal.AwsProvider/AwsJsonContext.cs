using System.Text.Json.Serialization;
using Authagonal.Core.Models;

namespace Authagonal.AwsProvider;

/// <summary>
/// Source-generated JSON context for the model documents the AWS stores persist as a single DynamoDB
/// attribute. Trim-safe (the package is <c>IsTrimmable</c>); the same context is used for both
/// serialize and deserialize so the round-trip is stable.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OAuthClient))]
[JsonSerializable(typeof(PersistedGrant))]
[JsonSerializable(typeof(AuthUser))]
[JsonSerializable(typeof(Role))]
[JsonSerializable(typeof(Scope))]
[JsonSerializable(typeof(ProvisioningAppConfig))]
[JsonSerializable(typeof(UserProvision))]
[JsonSerializable(typeof(OidcProviderConfig))]
[JsonSerializable(typeof(SamlProviderConfig))]
[JsonSerializable(typeof(SsoDomain))]
[JsonSerializable(typeof(ScimToken))]
[JsonSerializable(typeof(ScimGroup))]
[JsonSerializable(typeof(ScimGroupRoleMapping))]
[JsonSerializable(typeof(MfaCredential))]
[JsonSerializable(typeof(MfaChallenge))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class AwsJsonContext : JsonSerializerContext;
