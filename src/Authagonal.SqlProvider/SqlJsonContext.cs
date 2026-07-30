using System.Text.Json.Serialization;
using Authagonal.Core.Models;

namespace Authagonal.SqlProvider;

/// <summary>
/// Source-generated JSON context for the model documents the SQL stores persist in the <c>data</c>
/// column, plus the attribute bag. Trim-safe (the package is <c>IsTrimmable</c>), and deliberately
/// configured identically to <c>AwsJsonContext</c> — same camelCase policy, same types — so a
/// document written by one backend deserializes on another and backup/restore can move a deployment
/// between Azure Tables, DynamoDB and SQL without a translation step.
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
internal partial class SqlJsonContext : JsonSerializerContext;
