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
    public bool IncludeGroupsInTokens { get; set; }
    public int AccessTokenLifetimeSeconds { get; set; }
    public int IdentityTokenLifetimeSeconds { get; set; }
    public int AuthorizationCodeLifetimeSeconds { get; set; }
    public int AbsoluteRefreshTokenLifetimeSeconds { get; set; }
    public int SlidingRefreshTokenLifetimeSeconds { get; set; }
    public int RefreshTokenUsage { get; set; }
    public string ProvisioningAppsJson { get; set; } = "[]";
    public int MfaPolicy { get; set; }

    public static ClientEntity FromModel(OAuthClient client) => new()
    {
        PartitionKey = client.ClientId,
        RowKey = ConfigRowKey,
        ClientName = client.ClientName,
        ClientSecretHashesJson = JsonSerializer.Serialize(client.ClientSecretHashes, StorageJsonContext.Default.ListString),
        AllowedGrantTypesJson = JsonSerializer.Serialize(client.AllowedGrantTypes, StorageJsonContext.Default.ListString),
        RedirectUrisJson = JsonSerializer.Serialize(client.RedirectUris, StorageJsonContext.Default.ListString),
        PostLogoutRedirectUrisJson = JsonSerializer.Serialize(client.PostLogoutRedirectUris, StorageJsonContext.Default.ListString),
        AllowedScopesJson = JsonSerializer.Serialize(client.AllowedScopes, StorageJsonContext.Default.ListString),
        AllowedCorsOriginsJson = JsonSerializer.Serialize(client.AllowedCorsOrigins, StorageJsonContext.Default.ListString),
        RequirePkce = client.RequirePkce,
        AllowOfflineAccess = client.AllowOfflineAccess,
        RequireClientSecret = client.RequireClientSecret,
        AlwaysIncludeUserClaimsInIdToken = client.AlwaysIncludeUserClaimsInIdToken,
        IncludeGroupsInTokens = client.IncludeGroupsInTokens,
        AccessTokenLifetimeSeconds = client.AccessTokenLifetimeSeconds,
        IdentityTokenLifetimeSeconds = client.IdentityTokenLifetimeSeconds,
        AuthorizationCodeLifetimeSeconds = client.AuthorizationCodeLifetimeSeconds,
        AbsoluteRefreshTokenLifetimeSeconds = client.AbsoluteRefreshTokenLifetimeSeconds,
        SlidingRefreshTokenLifetimeSeconds = client.SlidingRefreshTokenLifetimeSeconds,
        RefreshTokenUsage = (int)client.RefreshTokenUsage,
        ProvisioningAppsJson = JsonSerializer.Serialize(client.ProvisioningApps, StorageJsonContext.Default.ListString),
        MfaPolicy = (int)client.MfaPolicy,
    };

    public OAuthClient ToModel() => new()
    {
        ClientId = PartitionKey,
        ClientName = ClientName,
        ClientSecretHashes = JsonSerializer.Deserialize(ClientSecretHashesJson, StorageJsonContext.Default.ListString) ?? [],
        AllowedGrantTypes = JsonSerializer.Deserialize(AllowedGrantTypesJson, StorageJsonContext.Default.ListString) ?? [],
        RedirectUris = JsonSerializer.Deserialize(RedirectUrisJson, StorageJsonContext.Default.ListString) ?? [],
        PostLogoutRedirectUris = JsonSerializer.Deserialize(PostLogoutRedirectUrisJson, StorageJsonContext.Default.ListString) ?? [],
        AllowedScopes = JsonSerializer.Deserialize(AllowedScopesJson, StorageJsonContext.Default.ListString) ?? [],
        AllowedCorsOrigins = JsonSerializer.Deserialize(AllowedCorsOriginsJson, StorageJsonContext.Default.ListString) ?? [],
        RequirePkce = RequirePkce,
        AllowOfflineAccess = AllowOfflineAccess,
        RequireClientSecret = RequireClientSecret,
        AlwaysIncludeUserClaimsInIdToken = AlwaysIncludeUserClaimsInIdToken,
        IncludeGroupsInTokens = IncludeGroupsInTokens,
        AccessTokenLifetimeSeconds = AccessTokenLifetimeSeconds,
        IdentityTokenLifetimeSeconds = IdentityTokenLifetimeSeconds,
        AuthorizationCodeLifetimeSeconds = AuthorizationCodeLifetimeSeconds,
        AbsoluteRefreshTokenLifetimeSeconds = AbsoluteRefreshTokenLifetimeSeconds,
        SlidingRefreshTokenLifetimeSeconds = SlidingRefreshTokenLifetimeSeconds,
        RefreshTokenUsage = (RefreshTokenUsage)RefreshTokenUsage,
        ProvisioningApps = JsonSerializer.Deserialize(ProvisioningAppsJson, StorageJsonContext.Default.ListString) ?? [],
        MfaPolicy = (MfaPolicy)MfaPolicy,
    };
}
