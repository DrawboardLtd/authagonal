using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.AzureProvider.Entities;

public sealed class UserEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string ProfileRowKey = "profile";

    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public string? PasswordHash { get; set; }
    public string? PendingPasswordHash { get; set; }
    public string? PendingClaimJson { get; set; }
    public bool EmailConfirmed { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? CompanyName { get; set; }
    public string? Phone { get; set; }
    public string? Locale { get; set; }
    public string? OrganizationId { get; set; }
    public int AccessFailedCount { get; set; }
    public bool LockoutEnabled { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public string? SecurityStamp { get; set; }
    public bool MfaEnabled { get; set; }
    public string? ExternalId { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ScimProvisionedByClientId { get; set; }
    public DateTimeOffset? ScimDeletedAt { get; set; }
    public string RolesJson { get; set; } = "[]";
    public string CustomAttributesJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public static UserEntity FromModel(AuthUser user) => new()
    {
        PartitionKey = user.Id,
        RowKey = ProfileRowKey,
        Email = user.Email,
        NormalizedEmail = user.NormalizedEmail,
        PasswordHash = user.PasswordHash,
        PendingPasswordHash = user.PendingPasswordHash,
        PendingClaimJson = user.PendingClaimJson,
        EmailConfirmed = user.EmailConfirmed,
        FirstName = user.FirstName,
        LastName = user.LastName,
        CompanyName = user.CompanyName,
        Phone = user.Phone,
        Locale = user.Locale,
        OrganizationId = user.OrganizationId,
        AccessFailedCount = user.AccessFailedCount,
        LockoutEnabled = user.LockoutEnabled,
        LockoutEnd = user.LockoutEnd,
        SecurityStamp = user.SecurityStamp,
        MfaEnabled = user.MfaEnabled,
        ExternalId = user.ExternalId,
        IsActive = user.IsActive,
        ScimProvisionedByClientId = user.ScimProvisionedByClientId,
        ScimDeletedAt = user.ScimDeletedAt,
        RolesJson = JsonSerializer.Serialize(user.Roles, AzureJsonContext.Default.ListString),
        CustomAttributesJson = JsonSerializer.Serialize(user.CustomAttributes, AzureJsonContext.Default.DictionaryStringString),
        CreatedAt = user.CreatedAt,
        UpdatedAt = user.UpdatedAt,
        LastLoginAt = user.LastLoginAt,
    };

    public AuthUser ToModel()
    {
        var model = ToModelCore();
        // The revision the caller is holding. TableUserStore.UpdateAsync refuses a write whose token no
        // longer matches the stored state, so a stale snapshot cannot revert what landed in between.
        // Stamped here rather than at each read site so no read path can forget it — a null token means
        // "the caller built this instance", which is the only case the store writes unguarded.
        model.ConcurrencyToken = Authagonal.Core.Services.UserRevision.Of(model);
        return model;
    }

    private AuthUser ToModelCore() => new()
    {
        Id = PartitionKey,
        Email = Email,
        NormalizedEmail = NormalizedEmail,
        PasswordHash = PasswordHash,
        PendingPasswordHash = PendingPasswordHash,
        PendingClaimJson = PendingClaimJson,
        EmailConfirmed = EmailConfirmed,
        FirstName = FirstName,
        LastName = LastName,
        CompanyName = CompanyName,
        Phone = Phone,
        Locale = Locale,
        OrganizationId = OrganizationId,
        AccessFailedCount = AccessFailedCount,
        LockoutEnabled = LockoutEnabled,
        LockoutEnd = LockoutEnd,
        SecurityStamp = SecurityStamp,
        MfaEnabled = MfaEnabled,
        ExternalId = ExternalId,
        IsActive = IsActive,
        ScimProvisionedByClientId = ScimProvisionedByClientId,
        ScimDeletedAt = ScimDeletedAt,
        Roles = JsonSerializer.Deserialize(RolesJson, AzureJsonContext.Default.ListString) ?? [],
        CustomAttributes = JsonSerializer.Deserialize(CustomAttributesJson, AzureJsonContext.Default.DictionaryStringString) ?? [],
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        LastLoginAt = LastLoginAt,
    };
}
