using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableUserStore(
    TableClient usersTable,
    TableClient userEmailsTable,
    TableClient userLoginsTable,
    TableClient userExternalIdsTable,
    ITombstoneWriter? tombstoneWriter = null) : IUserStore
{
    public async Task<AuthUser?> GetAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            var response = await usersTable.GetEntityAsync<UserEntity>(
                userId, UserEntity.ProfileRowKey, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<AuthUser?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalizedEmail = email.ToUpperInvariant();
        try
        {
            var emailEntity = await userEmailsTable.GetEntityAsync<UserEmailEntity>(
                normalizedEmail, UserEmailEntity.LookupRowKey, cancellationToken: ct);
            return await GetAsync(emailEntity.Value.UserId, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task CreateAsync(AuthUser user, CancellationToken ct = default)
    {
        var userEntity = UserEntity.FromModel(user);
        var emailEntity = UserEmailEntity.Create(user.NormalizedEmail, user.Id);

        await usersTable.AddEntityAsync(userEntity, ct);
        await userEmailsTable.UpsertEntityAsync(emailEntity, TableUpdateMode.Replace, ct);
    }

    public async Task UpdateAsync(AuthUser user, CancellationToken ct = default)
    {
        // Fetch the existing entity to check if email changed
        try
        {
            var existing = await usersTable.GetEntityAsync<UserEntity>(
                user.Id, UserEntity.ProfileRowKey, cancellationToken: ct);

            var oldNormalizedEmail = existing.Value.NormalizedEmail;
            var newNormalizedEmail = user.NormalizedEmail;

            var userEntity = UserEntity.FromModel(user);
            await usersTable.UpsertEntityAsync(userEntity, TableUpdateMode.Replace, ct);

            if (!string.Equals(oldNormalizedEmail, newNormalizedEmail, StringComparison.Ordinal))
            {
                // Remove old email index, add new one
                try
                {
                    await userEmailsTable.DeleteEntityAsync(oldNormalizedEmail, UserEmailEntity.LookupRowKey, cancellationToken: ct);
                    if (tombstoneWriter is not null)
                        await tombstoneWriter.WriteAsync("UserEmails", oldNormalizedEmail, UserEmailEntity.LookupRowKey, ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Old email index didn't exist, that's fine
                }

                var emailEntity = UserEmailEntity.Create(newNormalizedEmail, user.Id);
                await userEmailsTable.UpsertEntityAsync(emailEntity, TableUpdateMode.Replace, ct);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // User doesn't exist, create instead
            await CreateAsync(user, ct);
        }
    }

    public async Task DeleteAsync(string userId, CancellationToken ct = default)
    {
        // Get user to find email for index cleanup
        try
        {
            var existing = await usersTable.GetEntityAsync<UserEntity>(
                userId, UserEntity.ProfileRowKey, cancellationToken: ct);

            // Delete email index
            try
            {
                await userEmailsTable.DeleteEntityAsync(
                    existing.Value.NormalizedEmail, UserEmailEntity.LookupRowKey, cancellationToken: ct);
                if (tombstoneWriter is not null)
                    await tombstoneWriter.WriteAsync("UserEmails", existing.Value.NormalizedEmail, UserEmailEntity.LookupRowKey, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete all external login entries for this user
            var logins = await GetLoginsAsync(userId, ct);
            foreach (var login in logins)
            {
                await RemoveLoginAsync(userId, login.Provider, login.ProviderKey, ct);
            }

            // Delete user profile
            await usersTable.DeleteEntityAsync(userId, UserEntity.ProfileRowKey, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("Users", userId, UserEntity.ProfileRowKey, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task<bool> ExistsAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            await usersTable.GetEntityAsync<UserEntity>(
                userId, UserEntity.ProfileRowKey, cancellationToken: ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public async Task<AuthUser?> FindByExternalIdAsync(string clientId, string externalId, CancellationToken ct = default)
    {
        try
        {
            var indexEntity = await userExternalIdsTable.GetEntityAsync<UserExternalIdEntity>(
                $"{clientId}|{externalId}", UserExternalIdEntity.LookupRowKey, cancellationToken: ct);
            return await GetAsync(indexEntity.Value.UserId, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<(IReadOnlyList<AuthUser> Users, int TotalCount)> ListAsync(
        string? organizationId, int startIndex, int count, CancellationToken ct = default)
    {
        var allUsers = new List<AuthUser>();
        var query = usersTable.QueryAsync<UserEntity>(
            e => e.RowKey == UserEntity.ProfileRowKey,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            var user = entity.ToModel();
            if (organizationId is null || string.Equals(user.OrganizationId, organizationId, StringComparison.Ordinal))
            {
                allUsers.Add(user);
            }
        }

        var totalCount = allUsers.Count;
        var paged = allUsers
            .OrderBy(u => u.CreatedAt)
            .Skip(startIndex - 1)
            .Take(count)
            .ToList();

        return (paged, totalCount);
    }

    public async Task SetExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default)
    {
        var entity = UserExternalIdEntity.Create(clientId, externalId, userId);
        await userExternalIdsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task RemoveExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default)
    {
        var pk = $"{clientId}|{externalId}";
        try
        {
            await userExternalIdsTable.DeleteEntityAsync(pk, UserExternalIdEntity.LookupRowKey, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("UserExternalIds", pk, UserExternalIdEntity.LookupRowKey, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task AddLoginAsync(ExternalLoginInfo login, CancellationToken ct = default)
    {
        var forwardEntity = UserLoginEntity.FromModelForward(login);
        var reverseEntity = UserLoginEntity.FromModelReverse(login);

        await userLoginsTable.UpsertEntityAsync(forwardEntity, TableUpdateMode.Replace, ct);
        await userLoginsTable.UpsertEntityAsync(reverseEntity, TableUpdateMode.Replace, ct);
    }

    public async Task RemoveLoginAsync(string userId, string provider, string providerKey, CancellationToken ct = default)
    {
        var forwardPk = $"{provider}|{providerKey}";
        var reverseRk = $"{UserLoginEntity.LoginRowKeyPrefix}{provider}|{providerKey}";

        try
        {
            await userLoginsTable.DeleteEntityAsync(forwardPk, UserLoginEntity.LookupRowKey, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("UserLogins", forwardPk, UserLoginEntity.LookupRowKey, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }

        try
        {
            await userLoginsTable.DeleteEntityAsync(userId, reverseRk, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("UserLogins", userId, reverseRk, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task<ExternalLoginInfo?> FindLoginAsync(string provider, string providerKey, CancellationToken ct = default)
    {
        var forwardPk = $"{provider}|{providerKey}";
        try
        {
            var response = await userLoginsTable.GetEntityAsync<UserLoginEntity>(
                forwardPk, UserLoginEntity.LookupRowKey, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ExternalLoginInfo>> GetLoginsAsync(string userId, CancellationToken ct = default)
    {
        var results = new List<ExternalLoginInfo>();
        var query = userLoginsTable.QueryAsync<UserLoginEntity>(
            e => e.PartitionKey == userId && e.RowKey.CompareTo(UserLoginEntity.LoginRowKeyPrefix) >= 0
                 && e.RowKey.CompareTo("login\uffff") < 0,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            results.Add(entity.ToModel());
        }

        return results;
    }
}
