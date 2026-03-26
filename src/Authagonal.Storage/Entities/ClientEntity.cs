using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.Storage.Entities;

public sealed class ClientEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string ConfigRowKey = "config";

    public required string ClientName { get; set; }
    public required string ClientSecretHashesJson { get; set; }
    public required string AllowedGrantTypesJson { get; set; }
    public required string RedirectUrisJson { get; set; }
    public required string PostLogoutRedirectUrisJson { get; set; }
    public required string AllowedScopesJson { get; set; }
    public required string AllowedCorsOriginsJson { get; set; }
    public bool RequirePkce { get; set; }
    public bool AllowOfflineAccess { get; set; }
    public bool RequireClientSecret { get; set; }
    public bool AlwaysIncludeUserClaimsInIdToken { get; set; }
    public int AccessTokenLifetimeSeconds { get; set; }
    public int IdentityTokenLifetimeSeconds { get; set; }
    public int AuthorizationCodeLifetimeSeconds { get; set; }
    public int AbsoluteRefreshTokenLifetimeSeconds { get; set; }
    public int SlidingRefreshTokenLifetimeSeconds { get; set; }
    public int RefreshTokenUsage { get; set; }

    public static ClientEntity FromModel(OAuthClient client) => new()
    {
        PartitionKey = client.ClientId,
        RowKey = ConfigRowKey,
        ClientName = client.ClientName,
        ClientSecretHashesJson = JsonSerializer.Serialize(client.ClientSecretHashes),
        AllowedGrantTypesJson = JsonSerializer.Serialize(client.AllowedGrantTypes),
        RedirectUrisJson = JsonSerializer.Serialize(client.RedirectUris),
        PostLogoutRedirectUrisJson = JsonSerializer.Serialize(client.PostLogoutRedirectUris),
        AllowedScopesJson = JsonSerializer.Serialize(client.AllowedScopes),
        AllowedCorsOriginsJson = JsonSerializer.Serialize(client.AllowedCorsOrigins),
        RequirePkce = client.RequirePkce,
        AllowOfflineAccess = client.AllowOfflineAccess,
        RequireClientSecret = client.RequireClientSecret,
        AlwaysIncludeUserClaimsInIdToken = client.AlwaysIncludeUserClaimsInIdToken,
        AccessTokenLifetimeSeconds = client.AccessTokenLifetimeSeconds,
        IdentityTokenLifetimeSeconds = client.IdentityTokenLifetimeSeconds,
        AuthorizationCodeLifetimeSeconds = client.AuthorizationCodeLifetimeSeconds,
        AbsoluteRefreshTokenLifetimeSeconds = client.AbsoluteRefreshTokenLifetimeSeconds,
        SlidingRefreshTokenLifetimeSeconds = client.SlidingRefreshTokenLifetimeSeconds,
        RefreshTokenUsage = (int)client.RefreshTokenUsage,
    };

    public OAuthClient ToModel() => new()
    {
        ClientId = PartitionKey,
        ClientName = ClientName,
        ClientSecretHashes = JsonSerializer.Deserialize<List<string>>(ClientSecretHashesJson) ?? [],
        AllowedGrantTypes = JsonSerializer.Deserialize<List<string>>(AllowedGrantTypesJson) ?? [],
        RedirectUris = JsonSerializer.Deserialize<List<string>>(RedirectUrisJson) ?? [],
        PostLogoutRedirectUris = JsonSerializer.Deserialize<List<string>>(PostLogoutRedirectUrisJson) ?? [],
        AllowedScopes = JsonSerializer.Deserialize<List<string>>(AllowedScopesJson) ?? [],
        AllowedCorsOrigins = JsonSerializer.Deserialize<List<string>>(AllowedCorsOriginsJson) ?? [],
        RequirePkce = RequirePkce,
        AllowOfflineAccess = AllowOfflineAccess,
        RequireClientSecret = RequireClientSecret,
        AlwaysIncludeUserClaimsInIdToken = AlwaysIncludeUserClaimsInIdToken,
        AccessTokenLifetimeSeconds = AccessTokenLifetimeSeconds,
        IdentityTokenLifetimeSeconds = IdentityTokenLifetimeSeconds,
        AuthorizationCodeLifetimeSeconds = AuthorizationCodeLifetimeSeconds,
        AbsoluteRefreshTokenLifetimeSeconds = AbsoluteRefreshTokenLifetimeSeconds,
        SlidingRefreshTokenLifetimeSeconds = SlidingRefreshTokenLifetimeSeconds,
        RefreshTokenUsage = (RefreshTokenUsage)RefreshTokenUsage,
    };
}
