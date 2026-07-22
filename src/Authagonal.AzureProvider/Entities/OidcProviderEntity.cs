using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.AzureProvider.Entities;

public sealed class OidcProviderEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string ConfigRowKey = "config";

    public required string ConnectionName { get; set; }
    public required string MetadataLocation { get; set; }
    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }
    public required string RedirectUrl { get; set; }
    public required string AllowedDomainsJson { get; set; }
    public string? IconUrl { get; set; }
    public bool DisableJitProvisioning { get; set; }
    public bool UseUpstreamSubjectAsUserId { get; set; }
    // Negative form (like DisableJitProvisioning): existing rows lacking the column read false → shown.
    public bool HiddenFromLogin { get; set; }
    // Negative form: rows lacking the column read false → the local MFA challenge stays on.
    public bool SkipMfaAfterFederatedLogin { get; set; }
    // Rows lacking the column read false → no auto-link (the safe default).
    public bool AutoLinkExistingByEmail { get; set; }
    public string? SessionExpClaim { get; set; }
    public string? InteractionPath { get; set; }
    public string? PassthroughParamsJson { get; set; }
    public string? ProvisioningAttributeParamsJson { get; set; }
    // Rows lacking the column read false → no upstream-refresh revalidation (the safe default).
    public bool RevalidateOnRefresh { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public static OidcProviderEntity FromModel(OidcProviderConfig config) => new()
    {
        PartitionKey = config.ConnectionId,
        RowKey = ConfigRowKey,
        ConnectionName = config.ConnectionName,
        MetadataLocation = config.MetadataLocation,
        ClientId = config.ClientId,
        ClientSecret = config.ClientSecret,
        RedirectUrl = config.RedirectUrl,
        AllowedDomainsJson = JsonSerializer.Serialize(config.AllowedDomains, AzureJsonContext.Default.ListString),
        IconUrl = config.IconUrl,
        DisableJitProvisioning = config.DisableJitProvisioning,
        UseUpstreamSubjectAsUserId = config.UseUpstreamSubjectAsUserId,
        HiddenFromLogin = config.HiddenFromLogin,
        SkipMfaAfterFederatedLogin = config.SkipMfaAfterFederatedLogin,
        AutoLinkExistingByEmail = config.AutoLinkExistingByEmail,
        SessionExpClaim = config.SessionExpClaim,
        InteractionPath = config.InteractionPath,
        PassthroughParamsJson = config.PassthroughParams.Count > 0
            ? JsonSerializer.Serialize(config.PassthroughParams, AzureJsonContext.Default.ListString)
            : null,
        ProvisioningAttributeParamsJson = config.ProvisioningAttributeParams.Count > 0
            ? JsonSerializer.Serialize(config.ProvisioningAttributeParams, AzureJsonContext.Default.ListString)
            : null,
        RevalidateOnRefresh = config.RevalidateOnRefresh,
        CreatedAt = config.CreatedAt,
        UpdatedAt = config.UpdatedAt,
    };

    public OidcProviderConfig ToModel() => new()
    {
        ConnectionId = PartitionKey,
        ConnectionName = ConnectionName,
        MetadataLocation = MetadataLocation,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        RedirectUrl = RedirectUrl,
        AllowedDomains = JsonSerializer.Deserialize(AllowedDomainsJson, AzureJsonContext.Default.ListString) ?? [],
        IconUrl = IconUrl,
        DisableJitProvisioning = DisableJitProvisioning,
        UseUpstreamSubjectAsUserId = UseUpstreamSubjectAsUserId,
        HiddenFromLogin = HiddenFromLogin,
        SkipMfaAfterFederatedLogin = SkipMfaAfterFederatedLogin,
        AutoLinkExistingByEmail = AutoLinkExistingByEmail,
        SessionExpClaim = SessionExpClaim,
        InteractionPath = InteractionPath,
        PassthroughParams = string.IsNullOrEmpty(PassthroughParamsJson)
            ? []
            : JsonSerializer.Deserialize(PassthroughParamsJson, AzureJsonContext.Default.ListString) ?? [],
        ProvisioningAttributeParams = string.IsNullOrEmpty(ProvisioningAttributeParamsJson)
            ? []
            : JsonSerializer.Deserialize(ProvisioningAttributeParamsJson, AzureJsonContext.Default.ListString) ?? [],
        RevalidateOnRefresh = RevalidateOnRefresh,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
