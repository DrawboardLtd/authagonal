using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.Storage.Entities;

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
    public bool EmailConfirmed { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? CompanyName { get; set; }
    public string? Phone { get; set; }
    public string? OrganizationId { get; set; }
    public int AccessFailedCount { get; set; }
    public bool LockoutEnabled { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public string? SecurityStamp { get; set; }
    public bool MfaEnabled { get; set; }
    public string? ExternalId { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ScimProvisionedByClientId { get; set; }
    public string RolesJson { get; set; } = "[]";
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
        EmailConfirmed = user.EmailConfirmed,
        FirstName = user.FirstName,
        LastName = user.LastName,
        CompanyName = user.CompanyName,
        Phone = user.Phone,
        OrganizationId = user.OrganizationId,
        AccessFailedCount = user.AccessFailedCount,
        LockoutEnabled = user.LockoutEnabled,
        LockoutEnd = user.LockoutEnd,
        SecurityStamp = user.SecurityStamp,
        MfaEnabled = user.MfaEnabled,
        ExternalId = user.ExternalId,
        IsActive = user.IsActive,
        ScimProvisionedByClientId = user.ScimProvisionedByClientId,
        RolesJson = JsonSerializer.Serialize(user.Roles, StorageJsonContext.Default.ListString),
        CreatedAt = user.CreatedAt,
        UpdatedAt = user.UpdatedAt,
        LastLoginAt = user.LastLoginAt,
    };

    public AuthUser ToModel() => new()
    {
        Id = PartitionKey,
        Email = Email,
        NormalizedEmail = NormalizedEmail,
        PasswordHash = PasswordHash,
        EmailConfirmed = EmailConfirmed,
        FirstName = FirstName,
        LastName = LastName,
        CompanyName = CompanyName,
        Phone = Phone,
        OrganizationId = OrganizationId,
        AccessFailedCount = AccessFailedCount,
        LockoutEnabled = LockoutEnabled,
        LockoutEnd = LockoutEnd,
        SecurityStamp = SecurityStamp,
        MfaEnabled = MfaEnabled,
        ExternalId = ExternalId,
        IsActive = IsActive,
        ScimProvisionedByClientId = ScimProvisionedByClientId,
        Roles = JsonSerializer.Deserialize(RolesJson, StorageJsonContext.Default.ListString) ?? [],
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        LastLoginAt = LastLoginAt,
    };
}
