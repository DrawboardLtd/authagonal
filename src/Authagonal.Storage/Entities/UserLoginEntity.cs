using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.Storage.Entities;

public sealed class UserLoginEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string LookupRowKey = "lookup";
    public const string LoginRowKeyPrefix = "login|";

    public required string UserId { get; set; }
    public required string Provider { get; set; }
    public required string ProviderKey { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>
    /// Creates the forward-index entity: PK = "{provider}|{providerKey}", RK = "lookup".
    /// Used to find a user by external login.
    /// </summary>
    public static UserLoginEntity FromModelForward(ExternalLoginInfo login) => new()
    {
        PartitionKey = $"{login.Provider}|{login.ProviderKey}",
        RowKey = LookupRowKey,
        UserId = login.UserId,
        Provider = login.Provider,
        ProviderKey = login.ProviderKey,
        DisplayName = login.DisplayName,
    };

    /// <summary>
    /// Creates the reverse-index entity: PK = userId, RK = "login|{provider}|{providerKey}".
    /// Used to list all logins for a user.
    /// </summary>
    public static UserLoginEntity FromModelReverse(ExternalLoginInfo login) => new()
    {
        PartitionKey = login.UserId,
        RowKey = $"{LoginRowKeyPrefix}{login.Provider}|{login.ProviderKey}",
        UserId = login.UserId,
        Provider = login.Provider,
        ProviderKey = login.ProviderKey,
        DisplayName = login.DisplayName,
    };

    public ExternalLoginInfo ToModel() => new()
    {
        UserId = UserId,
        Provider = Provider,
        ProviderKey = ProviderKey,
        DisplayName = DisplayName,
    };
}
