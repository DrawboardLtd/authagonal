using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>
/// DynamoDB <see cref="IUserStore"/> — the largest store. Layout mirrors the Azure one: a primary
/// "Users" table plus email / external-id / login indexes, and optional first/last-name indexes for
/// prefix search. The <see cref="AuthUser"/> profile is stored as a JSON document; <c>org</c> and
/// <c>scimClient</c> are promoted to attributes for the list filters, and a numeric <c>_v</c> version
/// backs the atomic <see cref="RecordFailedLoginAsync"/> (DynamoDB's substitute for Azure's ETag).
/// </summary>
public sealed class DynamoUserStore(
    DynamoTable users,
    DynamoTable userEmails,
    DynamoTable userLogins,
    DynamoTable userExternalIds,
    DynamoTable? userFirstNames,
    DynamoTable? userLastNames,
    EnvPartitioner partitioner,
    IChangeWriter? tombstones = null) : IUserStore
{
    private const string Profile = "profile";
    private const string Lookup = "lookup";
    private const string LoginPrefix = "login|";

    private static string? Normalize(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : name.Trim().ToUpperInvariant();

    public async Task<AuthUser?> GetAsync(string userId, CancellationToken ct = default)
    {
        var item = await users.GetAsync(partitioner.PK(userId), Profile, ct).ConfigureAwait(false);
        return item is null ? null : ReadUser(item);
    }

    public async Task<bool> ExistsAsync(string userId, CancellationToken ct = default)
        => await users.GetAsync(partitioner.PK(userId), Profile, ct).ConfigureAwait(false) is not null;

    public async Task<AuthUser?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var idx = await userEmails.GetAsync(partitioner.PK(email.ToUpperInvariant()), Lookup, ct).ConfigureAwait(false);
        return idx is null ? null : await GetAsync(idx.GetStr("userId"), ct).ConfigureAwait(false);
    }

    public async Task<AuthUser?> FindByExternalIdAsync(string clientId, string externalId, CancellationToken ct = default)
    {
        var idx = await userExternalIds.GetAsync(partitioner.PK($"{clientId}|{externalId}"), Lookup, ct).ConfigureAwait(false);
        return idx is null ? null : await GetAsync(idx.GetStr("userId"), ct).ConfigureAwait(false);
    }

    public async Task CreateAsync(AuthUser user, CancellationToken ct = default)
    {
        await users.PutAsync(UserItem(user, version: 0), ct).ConfigureAwait(false);
        await userEmails.PutAsync(EmailItem(user.NormalizedEmail, user.Id), ct).ConfigureAwait(false);

        var first = Normalize(user.FirstName);
        if (first is not null && userFirstNames is not null)
            await userFirstNames.PutAsync(NameItem(first, user.Id), ct).ConfigureAwait(false);

        var last = Normalize(user.LastName);
        if (last is not null && userLastNames is not null)
            await userLastNames.PutAsync(NameItem(last, user.Id), ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(AuthUser user, CancellationToken ct = default)
    {
        var existing = await users.GetAsync(partitioner.PK(user.Id), Profile, ct).ConfigureAwait(false);
        if (existing is null)
        {
            await CreateAsync(user, ct).ConfigureAwait(false);
            return;
        }

        var old = ReadUser(existing);
        await users.PutAsync(UserItem(user, existing.GetN("_v") + 1), ct).ConfigureAwait(false);

        // Email index: re-point only if the normalized email changed.
        if (!string.Equals(old.NormalizedEmail, user.NormalizedEmail, StringComparison.Ordinal))
        {
            await DeleteEmailIndexAsync(old.NormalizedEmail, ct).ConfigureAwait(false);
            await userEmails.PutAsync(EmailItem(user.NormalizedEmail, user.Id), ct).ConfigureAwait(false);
        }

        await ReindexNameAsync(userFirstNames, "UserFirstNames", Normalize(old.FirstName), Normalize(user.FirstName), user.Id, ct).ConfigureAwait(false);
        await ReindexNameAsync(userLastNames, "UserLastNames", Normalize(old.LastName), Normalize(user.LastName), user.Id, ct).ConfigureAwait(false);
    }

    public async Task<bool> RecordFailedLoginAsync(string userId, int maxAttempts, TimeSpan lockoutDuration, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var item = await users.GetAsync(pk, Profile, ct).ConfigureAwait(false);
            if (item is null) return false;

            var user = ReadUser(item);
            var version = item.GetN("_v");

            user.AccessFailedCount++;
            var locked = false;
            if (user.LockoutEnabled && user.AccessFailedCount >= maxAttempts)
            {
                user.LockoutEnd = DateTimeOffset.UtcNow.Add(lockoutDuration);
                user.AccessFailedCount = 0;
                locked = true;
            }
            user.UpdatedAt = DateTimeOffset.UtcNow;

            // Conditional write on the version: a concurrent failed login that wrote first fails the
            // condition — re-read and retry so no increment is lost (closes the parallel-attempts bypass).
            if (await TryWriteVersionedAsync(user, version, ct).ConfigureAwait(false))
                return locked;
        }

        return false; // sustained contention; a later attempt will still lock the account
    }

    public async Task DeleteAsync(string userId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var existing = await users.GetAsync(pk, Profile, ct).ConfigureAwait(false);
        if (existing is null) return;

        var user = ReadUser(existing);

        await DeleteEmailIndexAsync(user.NormalizedEmail, ct).ConfigureAwait(false);

        var first = Normalize(user.FirstName);
        if (first is not null && userFirstNames is not null)
            await DeleteNameIndexAsync(userFirstNames, "UserFirstNames", first, userId, ct).ConfigureAwait(false);
        var last = Normalize(user.LastName);
        if (last is not null && userLastNames is not null)
            await DeleteNameIndexAsync(userLastNames, "UserLastNames", last, userId, ct).ConfigureAwait(false);

        foreach (var login in await GetLoginsAsync(userId, ct).ConfigureAwait(false))
            await RemoveLoginAsync(userId, login.Provider, login.ProviderKey, ct).ConfigureAwait(false);

        await users.DeleteAsync(pk, Profile, ct).ConfigureAwait(false);
        if (tombstones is not null) await tombstones.WriteAsync("Users", pk, Profile, ct).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AuthUser> Users, bool HasMore)> ListAsync(
        string? organizationId, int startIndex, int count, CancellationToken ct = default)
    {
        var (filter, values) = DynamoClientStore.ConfigScanFilter(partitioner, Profile);
        return await PageProfilesAsync(filter, values, startIndex, count,
            u => organizationId is null || string.Equals(u.OrganizationId, organizationId, StringComparison.Ordinal), ct).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AuthUser> Users, bool HasMore)> ListByScimClientAsync(
        string scimClientId, int startIndex, int count, CancellationToken ct = default)
    {
        var (filter, values) = DynamoClientStore.ConfigScanFilter(partitioner, Profile);
        var v = new Dictionary<string, AttributeValue>(values) { [":sc"] = new() { S = scimClientId } };
        return await PageProfilesAsync($"{filter} AND scimClient = :sc", v, startIndex, count, _ => true, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuthUser>> SearchAsync(string query, int maxResults = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        query = query.Trim();

        var results = new List<AuthUser>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var byId = await GetAsync(query, ct).ConfigureAwait(false);
        if (byId is not null && seen.Add(byId.Id)) results.Add(byId);

        var byEmail = await FindByEmailAsync(query, ct).ConfigureAwait(false);
        if (byEmail is not null && seen.Add(byEmail.Id)) results.Add(byEmail);

        if (results.Count >= maxResults) return results;

        var prefix = query.ToUpperInvariant();

        // Email prefix — a scan with begins_with on the (env-prefixed) pk, mirroring the Azure range scan.
        var emailTask = CollectAsync(userEmails.ScanAsync(
            "sk = :lk AND begins_with(pk, :p)",
            new Dictionary<string, AttributeValue> { [":lk"] = new() { S = Lookup }, [":p"] = new() { S = partitioner.PK(prefix) } },
            ct), maxResults, ct);

        // Name indexes — single-partition prefix queries; only consulted once the query is long enough
        // to land in one partition (same constraint as the Azure store).
        var firstTask = NamePrefixTask(userFirstNames, prefix, maxResults, ct);
        var lastTask = NamePrefixTask(userLastNames, prefix, maxResults, ct);

        await Task.WhenAll(emailTask, firstTask, lastTask).ConfigureAwait(false);

        foreach (var id in emailTask.Result.Concat(firstTask.Result).Concat(lastTask.Result))
        {
            if (!seen.Add(id)) continue;
            var user = await GetAsync(id, ct).ConfigureAwait(false);
            if (user is not null) results.Add(user);
            if (results.Count >= maxResults) break;
        }

        return results;
    }

    public Task SetExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default)
    {
        var item = Dyn.Item(partitioner.PK($"{clientId}|{externalId}"), Lookup);
        item.PutS("userId", userId);
        return userExternalIds.PutAsync(item, ct);
    }

    public async Task RemoveExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default)
    {
        var pk = partitioner.PK($"{clientId}|{externalId}");
        await userExternalIds.DeleteAsync(pk, Lookup, ct).ConfigureAwait(false);
        if (tombstones is not null) await tombstones.WriteAsync("UserExternalIds", pk, Lookup, ct).ConfigureAwait(false);
    }

    public async Task AddLoginAsync(ExternalLoginInfo login, CancellationToken ct = default)
    {
        await userLogins.PutAsync(LoginItem(partitioner.PK($"{login.Provider}|{login.ProviderKey}"), Lookup, login), ct).ConfigureAwait(false);
        await userLogins.PutAsync(LoginItem(partitioner.PK(login.UserId), $"{LoginPrefix}{login.Provider}|{login.ProviderKey}", login), ct).ConfigureAwait(false);
    }

    public async Task RemoveLoginAsync(string userId, string provider, string providerKey, CancellationToken ct = default)
    {
        var forwardPk = partitioner.PK($"{provider}|{providerKey}");
        await userLogins.DeleteAsync(forwardPk, Lookup, ct).ConfigureAwait(false);
        if (tombstones is not null) await tombstones.WriteAsync("UserLogins", forwardPk, Lookup, ct).ConfigureAwait(false);

        var reversePk = partitioner.PK(userId);
        var reverseSk = $"{LoginPrefix}{provider}|{providerKey}";
        await userLogins.DeleteAsync(reversePk, reverseSk, ct).ConfigureAwait(false);
        if (tombstones is not null) await tombstones.WriteAsync("UserLogins", reversePk, reverseSk, ct).ConfigureAwait(false);
    }

    public async Task<ExternalLoginInfo?> FindLoginAsync(string provider, string providerKey, CancellationToken ct = default)
    {
        var item = await userLogins.GetAsync(partitioner.PK($"{provider}|{providerKey}"), Lookup, ct).ConfigureAwait(false);
        return item is null ? null : ReadLogin(item);
    }

    public async Task<IReadOnlyList<ExternalLoginInfo>> GetLoginsAsync(string userId, CancellationToken ct = default)
    {
        var results = new List<ExternalLoginInfo>();
        await foreach (var item in userLogins.QueryAsync(
            partitioner.PK(userId),
            sortKeyCondition: "begins_with(sk, :p)",
            values: new Dictionary<string, AttributeValue> { [":p"] = new() { S = LoginPrefix } },
            ct: ct).ConfigureAwait(false))
        {
            results.Add(ReadLogin(item));
        }
        return results;
    }

    // ── helpers ──

    private Dictionary<string, AttributeValue> UserItem(AuthUser user, long version)
    {
        var item = Dyn.Item(partitioner.PK(user.Id), Profile);
        item.PutS("data", JsonSerializer.Serialize(user, AwsJsonContext.Default.AuthUser));
        item.PutS("org", user.OrganizationId);
        item.PutS("scimClient", user.ScimProvisionedByClientId);
        item.PutN("_v", version);
        return item;
    }

    private async Task<bool> TryWriteVersionedAsync(AuthUser user, long expectedVersion, CancellationToken ct)
    {
        try
        {
            await users.Client.PutItemAsync(new PutItemRequest
            {
                TableName = users.Name,
                Item = UserItem(user, expectedVersion + 1),
                ConditionExpression = "#v = :old",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#v"] = "_v" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":old"] = new() { N = expectedVersion.ToString(CultureInfo.InvariantCulture) } },
            }, ct).ConfigureAwait(false);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    private async Task<(IReadOnlyList<AuthUser> Users, bool HasMore)> PageProfilesAsync(
        string filter, IReadOnlyDictionary<string, AttributeValue> values, int startIndex, int count, Func<AuthUser, bool> keep, CancellationToken ct)
    {
        var results = new List<AuthUser>();
        var skipped = 0;
        var start = Math.Max(0, startIndex);

        await foreach (var item in users.ScanAsync(filter, values, ct).ConfigureAwait(false))
        {
            var user = ReadUser(item);
            if (!keep(user)) continue;
            if (skipped < start) { skipped++; continue; }
            results.Add(user);
            if (results.Count > count) break; // one extra → hasMore
        }

        var hasMore = results.Count > count;
        if (hasMore) results.RemoveAt(results.Count - 1);
        return (results, hasMore);
    }

    private Task<List<string>> NamePrefixTask(DynamoTable? table, string prefix, int cap, CancellationToken ct)
    {
        if (table is null || prefix.Length < 2) return Task.FromResult(new List<string>());
        var pk = partitioner.PK(prefix[..2]); // GetPartitionKey: first 2 chars
        return CollectAsync(table.QueryAsync(
            pk,
            sortKeyCondition: "begins_with(sk, :p)",
            values: new Dictionary<string, AttributeValue> { [":p"] = new() { S = prefix } },
            ct: ct), cap, ct);
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<Dictionary<string, AttributeValue>> query, int cap, CancellationToken ct)
    {
        var ids = new List<string>();
        await foreach (var item in query.WithCancellation(ct).ConfigureAwait(false))
        {
            ids.Add(item.GetStr("userId"));
            if (ids.Count >= cap) break;
        }
        return ids;
    }

    private async Task DeleteEmailIndexAsync(string normalizedEmail, CancellationToken ct)
    {
        var pk = partitioner.PK(normalizedEmail);
        await userEmails.DeleteAsync(pk, Lookup, ct).ConfigureAwait(false);
        if (tombstones is not null) await tombstones.WriteAsync("UserEmails", pk, Lookup, ct).ConfigureAwait(false);
    }

    private async Task ReindexNameAsync(DynamoTable? table, string tableName, string? oldName, string? newName, string userId, CancellationToken ct)
    {
        if (table is null || string.Equals(oldName, newName, StringComparison.Ordinal)) return;
        if (oldName is not null) await DeleteNameIndexAsync(table, tableName, oldName, userId, ct).ConfigureAwait(false);
        if (newName is not null) await table.PutAsync(NameItem(newName, userId), ct).ConfigureAwait(false);
    }

    private async Task DeleteNameIndexAsync(DynamoTable table, string tableName, string normalizedName, string userId, CancellationToken ct)
    {
        var pk = partitioner.PK(normalizedName.Length >= 2 ? normalizedName[..2] : normalizedName);
        var sk = $"{normalizedName}|{userId}";
        await table.DeleteAsync(pk, sk, ct).ConfigureAwait(false);
        if (tombstones is not null) await tombstones.WriteAsync(tableName, pk, sk, ct).ConfigureAwait(false);
    }

    private Dictionary<string, AttributeValue> EmailItem(string normalizedEmail, string userId)
    {
        var item = Dyn.Item(partitioner.PK(normalizedEmail), Lookup);
        item.PutS("userId", userId);
        return item;
    }

    private Dictionary<string, AttributeValue> NameItem(string normalizedName, string userId)
    {
        // pk = first 2 chars of the normalized name (or the whole name if shorter); sk = "{name}|{userId}".
        var pk = partitioner.PK(normalizedName.Length >= 2 ? normalizedName[..2] : normalizedName);
        var item = Dyn.Item(pk, $"{normalizedName}|{userId}");
        item.PutS("userId", userId);
        return item;
    }

    private static Dictionary<string, AttributeValue> LoginItem(string pk, string sk, ExternalLoginInfo login)
    {
        var item = Dyn.Item(pk, sk);
        item.PutS("userId", login.UserId);
        item.PutS("provider", login.Provider);
        item.PutS("providerKey", login.ProviderKey);
        item.PutS("displayName", login.DisplayName);
        return item;
    }

    private static AuthUser ReadUser(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.AuthUser)!;

    private static ExternalLoginInfo ReadLogin(Dictionary<string, AttributeValue> item) => new()
    {
        UserId = item.GetStr("userId"),
        Provider = item.GetStr("provider"),
        ProviderKey = item.GetStr("providerKey"),
        DisplayName = item.GetS("displayName"),
    };
}
