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
    public string? Description { get; set; }
    public string? ClientUri { get; set; }
    public string? LogoUri { get; set; }
    // Nullable with default-true semantics — a row written before this field existed
    // should not read as disabled. ToModel() coerces null → true.
    public bool? Enabled { get; set; } = true;
    public required string ClientSecretHashesJson { get; set; }
    public required string AllowedGrantTypesJson { get; set; }
    public required string RedirectUrisJson { get; set; }
    public required string PostLogoutRedirectUrisJson { get; set; }
    public string? BackChannelLogoutUri { get; set; }
    // Same default-true pattern as Enabled.
    public bool? BackChannelLogoutSessionRequired { get; set; } = true;
    public string? FrontChannelLogoutUri { get; set; }
    public bool FrontChannelLogoutSessionRequired { get; set; } = true;
    public string AudiencesJson { get; set; } = "[]";
    public required string AllowedScopesJson { get; set; }
    public required string AllowedCorsOriginsJson { get; set; }
    public bool RequirePkce { get; set; }
    public bool AllowOfflineAccess { get; set; }
    public bool RequireClientSecret { get; set; }
    public bool AlwaysIncludeUserClaimsInIdToken { get; set; }
    public bool IncludeGroupsInTokens { get; set; }
    public bool RequireConsent { get; set; }
    public int AccessTokenLifetimeSeconds { get; set; }
    public int IdentityTokenLifetimeSeconds { get; set; }
    public int AuthorizationCodeLifetimeSeconds { get; set; }
    public int AbsoluteRefreshTokenLifetimeSeconds { get; set; }
    public int SlidingRefreshTokenLifetimeSeconds { get; set; }
    public int RefreshTokenUsage { get; set; }
    public int RefreshTokenExpiration { get; set; }
    public string ProvisioningAppsJson { get; set; } = "[]";
    public int MfaPolicy { get; set; }

    public static ClientEntity FromModel(OAuthClient client) => new()
    {
        PartitionKey = client.ClientId,
        RowKey = ConfigRowKey,
        ClientName = client.ClientName,
        Description = client.Description,
        ClientUri = client.ClientUri,
        LogoUri = client.LogoUri,
        Enabled = client.Enabled,
        ClientSecretHashesJson = JsonSerializer.Serialize(client.ClientSecretHashes, StorageJsonContext.Default.ListString),
        AllowedGrantTypesJson = JsonSerializer.Serialize(client.AllowedGrantTypes, StorageJsonContext.Default.ListString),
        RedirectUrisJson = JsonSerializer.Serialize(client.RedirectUris, StorageJsonContext.Default.ListString),
        PostLogoutRedirectUrisJson = JsonSerializer.Serialize(client.PostLogoutRedirectUris, StorageJsonContext.Default.ListString),
        BackChannelLogoutUri = client.BackChannelLogoutUri,
        BackChannelLogoutSessionRequired = client.BackChannelLogoutSessionRequired,
        FrontChannelLogoutUri = client.FrontChannelLogoutUri,
        FrontChannelLogoutSessionRequired = client.FrontChannelLogoutSessionRequired,
        AudiencesJson = JsonSerializer.Serialize(client.Audiences, StorageJsonContext.Default.ListString),
        AllowedScopesJson = JsonSerializer.Serialize(client.AllowedScopes, StorageJsonContext.Default.ListString),
        AllowedCorsOriginsJson = JsonSerializer.Serialize(client.AllowedCorsOrigins, StorageJsonContext.Default.ListString),
        RequirePkce = client.RequirePkce,
        AllowOfflineAccess = client.AllowOfflineAccess,
        RequireClientSecret = client.RequireClientSecret,
        AlwaysIncludeUserClaimsInIdToken = client.AlwaysIncludeUserClaimsInIdToken,
        IncludeGroupsInTokens = client.IncludeGroupsInTokens,
        RequireConsent = client.RequireConsent,
        AccessTokenLifetimeSeconds = client.AccessTokenLifetimeSeconds,
        IdentityTokenLifetimeSeconds = client.IdentityTokenLifetimeSeconds,
        AuthorizationCodeLifetimeSeconds = client.AuthorizationCodeLifetimeSeconds,
        AbsoluteRefreshTokenLifetimeSeconds = client.AbsoluteRefreshTokenLifetimeSeconds,
        SlidingRefreshTokenLifetimeSeconds = client.SlidingRefreshTokenLifetimeSeconds,
        RefreshTokenUsage = (int)client.RefreshTokenUsage,
        RefreshTokenExpiration = (int)client.RefreshTokenExpiration,
        ProvisioningAppsJson = JsonSerializer.Serialize(client.ProvisioningApps, StorageJsonContext.Default.ListString),
        MfaPolicy = (int)client.MfaPolicy,
    };

    public OAuthClient ToModel() => new()
    {
        ClientId = PartitionKey,
        ClientName = ClientName,
        Description = Description,
        ClientUri = ClientUri,
        LogoUri = LogoUri,
        Enabled = Enabled ?? true,
        ClientSecretHashes = JsonSerializer.Deserialize(ClientSecretHashesJson, StorageJsonContext.Default.ListString) ?? [],
        AllowedGrantTypes = JsonSerializer.Deserialize(AllowedGrantTypesJson, StorageJsonContext.Default.ListString) ?? [],
        RedirectUris = JsonSerializer.Deserialize(RedirectUrisJson, StorageJsonContext.Default.ListString) ?? [],
        PostLogoutRedirectUris = JsonSerializer.Deserialize(PostLogoutRedirectUrisJson, StorageJsonContext.Default.ListString) ?? [],
        BackChannelLogoutUri = BackChannelLogoutUri,
        BackChannelLogoutSessionRequired = BackChannelLogoutSessionRequired ?? true,
        FrontChannelLogoutUri = FrontChannelLogoutUri,
        FrontChannelLogoutSessionRequired = FrontChannelLogoutSessionRequired,
        Audiences = JsonSerializer.Deserialize(AudiencesJson, StorageJsonContext.Default.ListString) ?? [],
        AllowedScopes = JsonSerializer.Deserialize(AllowedScopesJson, StorageJsonContext.Default.ListString) ?? [],
        AllowedCorsOrigins = JsonSerializer.Deserialize(AllowedCorsOriginsJson, StorageJsonContext.Default.ListString) ?? [],
        RequirePkce = RequirePkce,
        AllowOfflineAccess = AllowOfflineAccess,
        RequireClientSecret = RequireClientSecret,
        AlwaysIncludeUserClaimsInIdToken = AlwaysIncludeUserClaimsInIdToken,
        IncludeGroupsInTokens = IncludeGroupsInTokens,
        RequireConsent = RequireConsent,
        AccessTokenLifetimeSeconds = AccessTokenLifetimeSeconds,
        IdentityTokenLifetimeSeconds = IdentityTokenLifetimeSeconds,
        AuthorizationCodeLifetimeSeconds = AuthorizationCodeLifetimeSeconds,
        AbsoluteRefreshTokenLifetimeSeconds = AbsoluteRefreshTokenLifetimeSeconds,
        SlidingRefreshTokenLifetimeSeconds = SlidingRefreshTokenLifetimeSeconds,
        RefreshTokenUsage = (RefreshTokenUsage)RefreshTokenUsage,
        RefreshTokenExpiration = (RefreshTokenExpiration)RefreshTokenExpiration,
        ProvisioningApps = JsonSerializer.Deserialize(ProvisioningAppsJson, StorageJsonContext.Default.ListString) ?? [],
        MfaPolicy = (MfaPolicy)MfaPolicy,
    };
}
