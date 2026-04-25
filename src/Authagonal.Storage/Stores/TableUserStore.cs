using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

// Phase B2 (sandbox env isolation): every PartitionKey value passed to
// GetEntity/UpsertEntity/DeleteEntity/QueryAsync filter and every entity
// PartitionKey assignment must be wrapped with _partitioner.PK(...).
// Live is a no-op; sandbox envs prefix with "{env}|".
public sealed class TableUserStore(
    TableClient usersTable,
    TableClient userEmailsTable,
    TableClient userLoginsTable,
    TableClient userExternalIdsTable,
    TableClient userFirstNamesTable,
    TableClient userLastNamesTable,
    EnvPartitioner partitioner,
    ITombstoneWriter? tombstoneWriter = null) : IUserStore
{
    private readonly EnvPartitioner _partitioner = partitioner; // Phase B2 will wrap PartitionKeys with _partitioner.PK

    private static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.Trim().ToUpperInvariant();
    }

    public async Task<AuthUser?> GetAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            var response = await usersTable.GetEntityAsync<UserEntity>(
                _partitioner.PK(userId), UserEntity.ProfileRowKey, cancellationToken: ct);
            var user = response.Value.ToModel();
            user.Id = _partitioner.Strip(user.Id);
            return user;
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
                _partitioner.PK(normalizedEmail), UserEmailEntity.LookupRowKey, cancellationToken: ct);
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
        userEntity.PartitionKey = _partitioner.PK(userEntity.PartitionKey);
        var emailEntity = UserEmailEntity.Create(user.NormalizedEmail, user.Id);
        emailEntity.PartitionKey = _partitioner.PK(emailEntity.PartitionKey);

        await usersTable.AddEntityAsync(userEntity, ct);
        await userEmailsTable.UpsertEntityAsync(emailEntity, TableUpdateMode.Replace, ct);

        var normFirst = Normalize(user.FirstName);
        if (normFirst is not null)
        {
            var firstEntity = UserFirstNameEntity.Create(normFirst, user.Id);
            firstEntity.PartitionKey = _partitioner.PK(firstEntity.PartitionKey);
            await userFirstNamesTable.UpsertEntityAsync(firstEntity, TableUpdateMode.Replace, ct);
        }

        var normLast = Normalize(user.LastName);
        if (normLast is not null)
        {
            var lastEntity = UserLastNameEntity.Create(normLast, user.Id);
            lastEntity.PartitionKey = _partitioner.PK(lastEntity.PartitionKey);
            await userLastNamesTable.UpsertEntityAsync(lastEntity, TableUpdateMode.Replace, ct);
        }
    }

    public async Task UpdateAsync(AuthUser user, CancellationToken ct = default)
    {
        // Fetch the existing entity to check if email changed
        try
        {
            var existing = await usersTable.GetEntityAsync<UserEntity>(
                _partitioner.PK(user.Id), UserEntity.ProfileRowKey, cancellationToken: ct);

            var oldNormalizedEmail = existing.Value.NormalizedEmail;
            var newNormalizedEmail = user.NormalizedEmail;

            var userEntity = UserEntity.FromModel(user);
            userEntity.PartitionKey = _partitioner.PK(userEntity.PartitionKey);
            await usersTable.UpsertEntityAsync(userEntity, TableUpdateMode.Replace, ct);

            if (!string.Equals(oldNormalizedEmail, newNormalizedEmail, StringComparison.Ordinal))
            {
                // Remove old email index, add new one
                try
                {
                    await userEmailsTable.DeleteEntityAsync(_partitioner.PK(oldNormalizedEmail), UserEmailEntity.LookupRowKey, cancellationToken: ct);
                    if (tombstoneWriter is not null)
                        await tombstoneWriter.WriteAsync("UserEmails", _partitioner.PK(oldNormalizedEmail), UserEmailEntity.LookupRowKey, ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Old email index didn't exist, that's fine
                }

                var emailEntity = UserEmailEntity.Create(newNormalizedEmail, user.Id);
                emailEntity.PartitionKey = _partitioner.PK(emailEntity.PartitionKey);
                await userEmailsTable.UpsertEntityAsync(emailEntity, TableUpdateMode.Replace, ct);
            }

            var oldFirst = Normalize(existing.Value.FirstName);
            var newFirst = Normalize(user.FirstName);
            if (!string.Equals(oldFirst, newFirst, StringComparison.Ordinal))
            {
                if (oldFirst is not null)
                {
                    var oldRk = UserFirstNameEntity.MakeRowKey(oldFirst, user.Id);
                    try
                    {
                        await userFirstNamesTable.DeleteEntityAsync(
                            _partitioner.PK(UserFirstNameEntity.AllPartitionKey), oldRk, cancellationToken: ct);
                        if (tombstoneWriter is not null)
                            await tombstoneWriter.WriteAsync("UserFirstNames", _partitioner.PK(UserFirstNameEntity.AllPartitionKey), oldRk, ct);
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404) { }
                }

                if (newFirst is not null)
                {
                    var firstEntity = UserFirstNameEntity.Create(newFirst, user.Id);
                    firstEntity.PartitionKey = _partitioner.PK(firstEntity.PartitionKey);
                    await userFirstNamesTable.UpsertEntityAsync(firstEntity, TableUpdateMode.Replace, ct);
                }
            }

            var oldLast = Normalize(existing.Value.LastName);
            var newLast = Normalize(user.LastName);
            if (!string.Equals(oldLast, newLast, StringComparison.Ordinal))
            {
                if (oldLast is not null)
                {
                    var oldRk = UserLastNameEntity.MakeRowKey(oldLast, user.Id);
                    try
                    {
                        await userLastNamesTable.DeleteEntityAsync(
                            _partitioner.PK(UserLastNameEntity.AllPartitionKey), oldRk, cancellationToken: ct);
                        if (tombstoneWriter is not null)
                            await tombstoneWriter.WriteAsync("UserLastNames", _partitioner.PK(UserLastNameEntity.AllPartitionKey), oldRk, ct);
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404) { }
                }

                if (newLast is not null)
                {
                    var lastEntity = UserLastNameEntity.Create(newLast, user.Id);
                    lastEntity.PartitionKey = _partitioner.PK(lastEntity.PartitionKey);
                    await userLastNamesTable.UpsertEntityAsync(lastEntity, TableUpdateMode.Replace, ct);
                }
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
                _partitioner.PK(userId), UserEntity.ProfileRowKey, cancellationToken: ct);

            // Delete email index
            try
            {
                await userEmailsTable.DeleteEntityAsync(
                    _partitioner.PK(existing.Value.NormalizedEmail), UserEmailEntity.LookupRowKey, cancellationToken: ct);
                if (tombstoneWriter is not null)
                    await tombstoneWriter.WriteAsync("UserEmails", _partitioner.PK(existing.Value.NormalizedEmail), UserEmailEntity.LookupRowKey, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete first-name index
            var normFirst = Normalize(existing.Value.FirstName);
            if (normFirst is not null)
            {
                var rk = UserFirstNameEntity.MakeRowKey(normFirst, userId);
                try
                {
                    await userFirstNamesTable.DeleteEntityAsync(
                        _partitioner.PK(UserFirstNameEntity.AllPartitionKey), rk, cancellationToken: ct);
                    if (tombstoneWriter is not null)
                        await tombstoneWriter.WriteAsync("UserFirstNames", _partitioner.PK(UserFirstNameEntity.AllPartitionKey), rk, ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 404) { }
            }

            // Delete last-name index
            var normLast = Normalize(existing.Value.LastName);
            if (normLast is not null)
            {
                var rk = UserLastNameEntity.MakeRowKey(normLast, userId);
                try
                {
                    await userLastNamesTable.DeleteEntityAsync(
                        _partitioner.PK(UserLastNameEntity.AllPartitionKey), rk, cancellationToken: ct);
                    if (tombstoneWriter is not null)
                        await tombstoneWriter.WriteAsync("UserLastNames", _partitioner.PK(UserLastNameEntity.AllPartitionKey), rk, ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 404) { }
            }

            // Delete all external login entries for this user
            var logins = await GetLoginsAsync(userId, ct);
            foreach (var login in logins)
            {
                await RemoveLoginAsync(userId, login.Provider, login.ProviderKey, ct);
            }

            // Delete user profile
            await usersTable.DeleteEntityAsync(_partitioner.PK(userId), UserEntity.ProfileRowKey, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("Users", _partitioner.PK(userId), UserEntity.ProfileRowKey, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task<bool> ExistsAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            await usersTable.GetEntityAsync<UserEntity>(
                _partitioner.PK(userId), UserEntity.ProfileRowKey, cancellationToken: ct);
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
                _partitioner.PK($"{clientId}|{externalId}"), UserExternalIdEntity.LookupRowKey, cancellationToken: ct);
            return await GetAsync(indexEntity.Value.UserId, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<(IReadOnlyList<AuthUser> Users, bool HasMore)> ListAsync(
        string? organizationId, int startIndex, int count, CancellationToken ct = default)
    {
        var results = new List<AuthUser>();
        var skipped = 0;
        var start = Math.Max(0, startIndex);

        // Live env: scan the dedicated live table (no env prefix needed).
        // Sandbox env: scan only this env's rows in the shared sandbox table
        // by ranging PartitionKey on "{env}|" prefix.
        var range = _partitioner.RangeForEnv();
        var query = range is null
            ? usersTable.QueryAsync<UserEntity>(
                e => e.RowKey == UserEntity.ProfileRowKey,
                maxPerPage: count + 1, cancellationToken: ct)
            : usersTable.QueryAsync<UserEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0
                     && e.PartitionKey.CompareTo(range.Value.High) < 0
                     && e.RowKey == UserEntity.ProfileRowKey,
                maxPerPage: count + 1, cancellationToken: ct);

        await foreach (var entity in query)
        {
            var user = entity.ToModel();
            user.Id = _partitioner.Strip(user.Id);
            if (organizationId is not null &&
                !string.Equals(user.OrganizationId, organizationId, StringComparison.Ordinal))
                continue;

            if (skipped < start)
            {
                skipped++;
                continue;
            }

            results.Add(user);

            // Fetch one extra to determine hasMore, then stop
            if (results.Count > count)
                break;
        }

        var hasMore = results.Count > count;
        if (hasMore)
            results.RemoveAt(results.Count - 1);

        return (results, hasMore);
    }

    public async Task<(IReadOnlyList<AuthUser> Users, bool HasMore)> ListByScimClientAsync(
        string scimClientId, int startIndex, int count, CancellationToken ct = default)
    {
        var results = new List<AuthUser>();
        var skipped = 0;
        var start = Math.Max(0, startIndex);

        var range = _partitioner.RangeForEnv();
        var query = range is null
            ? usersTable.QueryAsync<UserEntity>(
                e => e.RowKey == UserEntity.ProfileRowKey && e.ScimProvisionedByClientId == scimClientId,
                maxPerPage: count + 1, cancellationToken: ct)
            : usersTable.QueryAsync<UserEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0
                     && e.PartitionKey.CompareTo(range.Value.High) < 0
                     && e.RowKey == UserEntity.ProfileRowKey
                     && e.ScimProvisionedByClientId == scimClientId,
                maxPerPage: count + 1, cancellationToken: ct);

        await foreach (var entity in query)
        {
            var user = entity.ToModel();
            user.Id = _partitioner.Strip(user.Id);

            if (skipped < start)
            {
                skipped++;
                continue;
            }

            results.Add(user);

            // Fetch one extra to determine hasMore, then stop
            if (results.Count > count)
                break;
        }

        var hasMore = results.Count > count;
        if (hasMore)
            results.RemoveAt(results.Count - 1);

        return (results, hasMore);
    }

    public async Task<IReadOnlyList<AuthUser>> SearchAsync(
        string query, int maxResults = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        query = query.Trim();
        var results = new List<AuthUser>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 1. Try exact userId match (point read)
        var byId = await GetAsync(query, ct);
        if (byId is not null && seen.Add(byId.Id))
            results.Add(byId);

        // 2. Try exact email match
        var byEmail = await FindByEmailAsync(query, ct);
        if (byEmail is not null && seen.Add(byEmail.Id))
            results.Add(byEmail);

        if (results.Count >= maxResults)
            return results;

        // 3. Prefix search — run email, first-name, and last-name range queries in parallel,
        //    then point-read the matching user ids (deduped) up to maxResults.
        var prefix = query.ToUpperInvariant();
        var prefixEnd = prefix + "\uffff";
        // For sandbox env, prefix the partition keys with "{env}|" so the range
        // queries stay within this env's slice of the shared sandbox tables.
        var emailLo = _partitioner.PK(prefix);
        var emailHi = _partitioner.PK(prefixEnd);
        var firstNamesPK = _partitioner.PK(UserFirstNameEntity.AllPartitionKey);
        var lastNamesPK = _partitioner.PK(UserLastNameEntity.AllPartitionKey);

        var emailTask = CollectUserIdsAsync(
            userEmailsTable.QueryAsync<UserEmailEntity>(
                e => e.PartitionKey.CompareTo(emailLo) >= 0 && e.PartitionKey.CompareTo(emailHi) < 0,
                cancellationToken: ct),
            e => e.UserId, maxResults, ct);

        var firstNameTask = CollectUserIdsAsync(
            userFirstNamesTable.QueryAsync<UserFirstNameEntity>(
                e => e.PartitionKey == firstNamesPK
                     && e.RowKey.CompareTo(prefix) >= 0 && e.RowKey.CompareTo(prefixEnd) < 0,
                cancellationToken: ct),
            e => e.UserId, maxResults, ct);

        var lastNameTask = CollectUserIdsAsync(
            userLastNamesTable.QueryAsync<UserLastNameEntity>(
                e => e.PartitionKey == lastNamesPK
                     && e.RowKey.CompareTo(prefix) >= 0 && e.RowKey.CompareTo(prefixEnd) < 0,
                cancellationToken: ct),
            e => e.UserId, maxResults, ct);

        await Task.WhenAll(emailTask, firstNameTask, lastNameTask);

        // Interleave: email hits first, then first-name, then last-name.
        foreach (var id in emailTask.Result.Concat(firstNameTask.Result).Concat(lastNameTask.Result))
        {
            if (!seen.Add(id)) continue;
            var user = await GetAsync(id, ct);
            if (user is not null)
                results.Add(user);
            if (results.Count >= maxResults)
                break;
        }

        return results;
    }

    private static async Task<List<string>> CollectUserIdsAsync<T>(
        Azure.AsyncPageable<T> query,
        Func<T, string> extractUserId,
        int cap,
        CancellationToken ct) where T : class
    {
        var ids = new List<string>();
        await foreach (var entity in query.WithCancellation(ct))
        {
            ids.Add(extractUserId(entity));
            if (ids.Count >= cap) break;
        }
        return ids;
    }

    public async Task SetExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default)
    {
        var entity = UserExternalIdEntity.Create(clientId, externalId, userId);
        entity.PartitionKey = _partitioner.PK(entity.PartitionKey);
        await userExternalIdsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task RemoveExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default)
    {
        var pk = _partitioner.PK($"{clientId}|{externalId}");
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
        forwardEntity.PartitionKey = _partitioner.PK(forwardEntity.PartitionKey);
        var reverseEntity = UserLoginEntity.FromModelReverse(login);
        reverseEntity.PartitionKey = _partitioner.PK(reverseEntity.PartitionKey);

        await userLoginsTable.UpsertEntityAsync(forwardEntity, TableUpdateMode.Replace, ct);
        await userLoginsTable.UpsertEntityAsync(reverseEntity, TableUpdateMode.Replace, ct);
    }

    public async Task RemoveLoginAsync(string userId, string provider, string providerKey, CancellationToken ct = default)
    {
        var forwardPk = _partitioner.PK($"{provider}|{providerKey}");
        var reversePk = _partitioner.PK(userId);
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
            await userLoginsTable.DeleteEntityAsync(reversePk, reverseRk, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("UserLogins", reversePk, reverseRk, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task<ExternalLoginInfo?> FindLoginAsync(string provider, string providerKey, CancellationToken ct = default)
    {
        var forwardPk = _partitioner.PK($"{provider}|{providerKey}");
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
        var pk = _partitioner.PK(userId);
        var results = new List<ExternalLoginInfo>();
        var query = userLoginsTable.QueryAsync<UserLoginEntity>(
            e => e.PartitionKey == pk && e.RowKey.CompareTo(UserLoginEntity.LoginRowKeyPrefix) >= 0
                 && e.RowKey.CompareTo("login\uffff") < 0,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            results.Add(entity.ToModel());
        }

        return results;
    }
}
