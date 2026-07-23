using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>
/// DynamoDB <see cref="IUserStore"/> — the largest store. Layout mirrors the Azure one: a primary
/// "Users" table plus email / external-id / login indexes, optional first/last-name prefix indexes,
/// and (like Azure) optional email-domain + email-local-part-prefix indexes.
///
/// At-rest crypto mirrors <c>TableUserStore</c> through the same Core seams, adapted to the
/// document layout: the whole <see cref="AuthUser"/> JSON document is encrypted via
/// <see cref="IFieldCipher"/> (Azure encrypts per column; here the document IS the column), and the
/// lookup keys become blind-index tokens via <see cref="IIndexTokenizer"/> with a plaintext-key
/// dual-read for un-migrated rows. Both default to passthrough, so unconfigured hosts keep the
/// historical plaintext layout unchanged.
///
/// Non-PII fields the queries and login paths need are promoted to item attributes: <c>org</c> and
/// <c>scimClient</c> back the list filters, <c>created</c>/<c>active</c> back the login-state scan,
/// and the mutable login-state group (<c>failedCount</c>, <c>lockoutEnd</c>, <c>lastLogin</c>,
/// <c>updated</c>, <c>pwd</c>, <c>lockEnabled</c>) is stamped attribute-only by
/// <see cref="RecordSuccessfulLoginAsync"/>/<see cref="RecordFailedLoginAsync"/> — no document
/// rewrite, so zero cipher round-trips on the hot path — and overlaid onto the document on read.
/// A numeric <c>_v</c> version backs optimistic full-document writes.
/// </summary>
public sealed class DynamoUserStore(
    DynamoTable users,
    DynamoTable userEmails,
    DynamoTable userLogins,
    DynamoTable userExternalIds,
    DynamoTable? userFirstNames,
    DynamoTable? userLastNames,
    EnvPartitioner partitioner,
    IChangeWriter? tombstones = null,
    DynamoTable? userEmailDomains = null,
    DynamoTable? userEmailLocalPrefixes = null,
    IFieldCipher? fieldCipher = null,
    IIndexTokenizer? indexTokenizer = null) : IUserStore
{
    private const string Profile = "profile";
    private const string Lookup = "lookup";
    private const string LoginPrefix = "login|";
    private const string DataAttr = "data";

    private readonly IFieldCipher _cipher = fieldCipher ?? NullFieldCipher.Instance;
    private readonly IIndexTokenizer _tokenizer = indexTokenizer ?? NullIndexTokenizer.Instance;
    // Tokenization "on" means dual-read fallbacks and per-prefix token rows; the Null default keeps
    // the historical plaintext key scheme byte-for-byte (same rule as the Azure store).
    private readonly bool _indexTokenized = indexTokenizer is not null;

    // Same prefix-index bounds as the Azure store: queries shorter than Min don't hit the index;
    // prefixes longer than Max match on their first Max chars.
    private const int NamePrefixMin = 2;
    private const int NamePrefixMax = 16;

    // A domain's members are bucketed so one big-domain tenant doesn't funnel every index write into
    // a single partition. Must stay identical to the Azure constants/hash so semantics (and tests)
    // agree. Bucket = stable FNV-1a of the userId (string.GetHashCode is not stable across processes).
    private const int DomainBuckets = 16;

    private static int DomainBucketOf(string userId)
    {
        uint h = 2166136261u;
        foreach (var ch in userId) { h ^= ch; h *= 16777619u; }
        return (int)(h % (uint)DomainBuckets);
    }

    private static string Bucketed(string basePk, string userId) => $"{basePk}-{DomainBucketOf(userId):x}";

    private static string? Normalize(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : name.Trim().ToUpperInvariant();

    private static string? DomainOf(string? normalizedEmail)
    {
        if (string.IsNullOrEmpty(normalizedEmail)) return null;
        var at = normalizedEmail.LastIndexOf('@');
        return at >= 0 && at < normalizedEmail.Length - 1 ? normalizedEmail[(at + 1)..] : null;
    }

    private static string? LocalPartOf(string? normalizedEmail)
    {
        if (string.IsNullOrEmpty(normalizedEmail)) return null;
        var at = normalizedEmail.IndexOf('@');
        var local = at > 0 ? normalizedEmail[..at] : normalizedEmail;
        return string.IsNullOrEmpty(local) ? null : local;
    }

    private static IReadOnlyList<string> NamePrefixesOf(string normalizedName)
    {
        if (normalizedName.Length < NamePrefixMin) return [normalizedName];
        var hi = Math.Min(normalizedName.Length, NamePrefixMax);
        var prefixes = new List<string>(hi - NamePrefixMin + 1);
        for (var len = NamePrefixMin; len <= hi; len++)
            prefixes.Add(normalizedName[..len]);
        return prefixes;
    }

    private static string Iso(DateTimeOffset v) => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    // Collects every value a write needs tokens for and computes them in ONE TokenizeBatchAsync call
    // (one Vault HMAC round-trip in Cloud) instead of one call per index. Same helper as the Azure
    // store; token COMPUTATION only — write/delete ordering (write-before-delete) is unchanged.
    private sealed class TokenBatch(IIndexTokenizer tokenizer)
    {
        private readonly List<string> _values = [];
        private IReadOnlyList<string>? _tokens;

        public Func<string> Add(string value)
        {
            var i = _values.Count;
            _values.Add(value);
            return () => _tokens![i];
        }

        public Func<IReadOnlyList<string>> AddRange(IReadOnlyList<string> values)
        {
            var start = _values.Count;
            var count = values.Count;
            _values.AddRange(values);
            return () =>
            {
                var slice = new string[count];
                for (var i = 0; i < count; i++) slice[i] = _tokens![start + i];
                return slice;
            };
        }

        public async Task RunAsync(CancellationToken ct)
            => _tokens = _values.Count == 0 ? [] : await tokenizer.TokenizeBatchAsync(_values, ct).ConfigureAwait(false);
    }

    private Func<IReadOnlyList<string>>? ReserveLocalPrefixTokens(TokenBatch batch, string? normalizedEmail)
    {
        if (userEmailLocalPrefixes is null || !_indexTokenized) return null;
        var local = LocalPartOf(normalizedEmail);
        return local is null ? null : batch.AddRange(NamePrefixesOf(local));
    }

    private Func<IReadOnlyList<string>>? ReserveNameTokens(TokenBatch batch, DynamoTable? table, string? normalizedName)
        => table is not null && normalizedName is not null && _indexTokenized
            ? batch.AddRange(NamePrefixesOf(normalizedName))
            : null;

    // Change-log capture for incremental backups — upsert-side mirror of the tombstone writes, same
    // table set as Azure. Login-state-only stamps are deliberately NOT logged (hot path, low-value
    // fields; the periodic full-scan backstop covers them).
    private Task LogUpsertAsync(string changeTable, string pk, string sk, CancellationToken ct)
        => tombstones?.WriteUpsertAsync(changeTable, pk, sk, ct) ?? Task.CompletedTask;

    private Task LogUpsertBatchAsync(string changeTable, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct)
        => tombstones?.WriteUpsertBatchAsync(changeTable, keys, ct) ?? Task.CompletedTask;

    // ── document crypto + login-state overlay ────────────────────────────────────

    private async Task<Dictionary<string, AttributeValue>> UserItemAsync(AuthUser user, long version, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(user, AwsJsonContext.Default.AuthUser);
        var item = Dyn.Item(partitioner.PK(user.Id), Profile);
        item.PutS(DataAttr, await _cipher.ProtectAsync(json, ct).ConfigureAwait(false));
        item.PutS("org", user.OrganizationId);
        item.PutS("scimClient", user.ScimProvisionedByClientId);
        item.PutN("_v", version);
        // Non-PII projections: created/active back the login-state scan; the failedCount group is the
        // attribute-only login-stamp target (see Record*LoginAsync) and is overlaid on read.
        item.PutDate("created", user.CreatedAt);
        item.PutBool("active", user.IsActive);
        item.PutBool("lockEnabled", user.LockoutEnabled);
        item.PutN("failedCount", user.AccessFailedCount);
        item.PutDate("lockoutEnd", user.LockoutEnd);
        item.PutDate("lastLogin", user.LastLoginAt);
        item.PutDate("updated", user.UpdatedAt);
        item.PutS("pwd", user.PasswordHash);
        item.PutS("pwdPending", user.PendingPasswordHash);
        return item;
    }

    private async Task<AuthUser> ReadUserAsync(Dictionary<string, AttributeValue> item, CancellationToken ct)
    {
        // ResolveAsync passes legacy plaintext JSON through unchanged, so pre-encryption rows keep
        // deserializing — the same lazy-migration contract as the Azure column crypto.
        var json = await _cipher.ResolveAsync(item.GetStr(DataAttr), ct).ConfigureAwait(false);
        var user = JsonSerializer.Deserialize(json, AwsJsonContext.Default.AuthUser)!;

        // Login-state overlay: Record*LoginAsync stamp only these attributes (never the document), so
        // when the marker is present the attributes are at least as new as the document.
        if (item.ContainsKey("failedCount"))
        {
            user.AccessFailedCount = (int)item.GetN("failedCount");
            user.LockoutEnd = item.GetDateOrNull("lockoutEnd");
            user.LastLoginAt = item.GetDateOrNull("lastLogin");
            if (item.GetDateOrNull("updated") is { } updated) user.UpdatedAt = updated;
            user.PasswordHash = item.GetS("pwd");
            user.PendingPasswordHash = item.GetS("pwdPending");
        }
        return user;
    }

    // ── point reads ──────────────────────────────────────────────────────────────

    public async Task<AuthUser?> GetAsync(string userId, CancellationToken ct = default)
    {
        var item = await users.GetAsync(partitioner.PK(userId), Profile, ct).ConfigureAwait(false);
        return item is null ? null : await ReadUserAsync(item, ct).ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(string userId, CancellationToken ct = default)
        => await users.GetAsync(partitioner.PK(userId), Profile, ct).ConfigureAwait(false) is not null;

    public async Task<AuthUser?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalized = email.ToUpperInvariant();
        // Blind-index point read on the tokenized key; during migration fall back to the legacy
        // plaintext key for rows not yet backfilled (only meaningful while tokenization is on).
        var idx = await userEmails.GetAsync(
            partitioner.PK(await _tokenizer.TokenizeAsync(normalized, ct).ConfigureAwait(false)), Lookup, ct).ConfigureAwait(false);
        if (idx is null && _indexTokenized)
            idx = await userEmails.GetAsync(partitioner.PK(normalized), Lookup, ct).ConfigureAwait(false);
        return idx is null ? null : await GetAsync(idx.GetStr("userId"), ct).ConfigureAwait(false);
    }

    public async Task<AuthUser?> FindByExternalIdAsync(string clientId, string externalId, CancellationToken ct = default)
    {
        var composite = $"{clientId}|{externalId}";
        var idx = await userExternalIds.GetAsync(
            partitioner.PK(await _tokenizer.TokenizeAsync(composite, ct).ConfigureAwait(false)), Lookup, ct).ConfigureAwait(false);
        if (idx is null && _indexTokenized)
            idx = await userExternalIds.GetAsync(partitioner.PK(composite), Lookup, ct).ConfigureAwait(false);
        return idx is null ? null : await GetAsync(idx.GetStr("userId"), ct).ConfigureAwait(false);
    }

    // ── create / update / delete ─────────────────────────────────────────────────

    public async Task CreateAsync(AuthUser user, CancellationToken ct = default)
    {
        await users.PutAsync(await UserItemAsync(user, version: 0, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await LogUpsertAsync("Users", partitioner.PK(user.Id), Profile, ct).ConfigureAwait(false);
        await WriteProfileIndexesAsync(user.NormalizedEmail, Normalize(user.FirstName), Normalize(user.LastName), user.Id, dropLegacy: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write the current-scheme profile-derived index rows — email lookup, email-domain, local-part
    /// prefixes, and first/last name prefixes. Shared by <see cref="CreateAsync"/> and
    /// <see cref="ReindexUserAsync"/>; <paramref name="dropLegacy"/> (the reindex path) also removes
    /// the matching legacy plaintext-keyed rows once tokenization is on. Email is written before any
    /// legacy drop, so there is no lookup gap.
    /// </summary>
    private async Task WriteProfileIndexesAsync(
        string normalizedEmail, string? normalizedFirst, string? normalizedLast, string userId, bool dropLegacy, CancellationToken ct)
    {
        var batch = new TokenBatch(_tokenizer);
        var emailToken = batch.Add(normalizedEmail);
        var domain = userEmailDomains is null ? null : DomainOf(normalizedEmail);
        var domainToken = domain is null ? null : batch.Add(domain);
        var localPrefixTokens = ReserveLocalPrefixTokens(batch, normalizedEmail);
        var firstTokens = ReserveNameTokens(batch, userFirstNames, normalizedFirst);
        var lastTokens = ReserveNameTokens(batch, userLastNames, normalizedLast);
        await batch.RunAsync(ct).ConfigureAwait(false);

        // Email lookup.
        var emailPk = partitioner.PK(emailToken());
        await userEmails.PutAsync(LookupItem(emailPk, userId), ct).ConfigureAwait(false);
        await LogUpsertAsync("UserEmails", emailPk, Lookup, ct).ConfigureAwait(false);
        if (dropLegacy && _indexTokenized)
        {
            var plainPk = partitioner.PK(normalizedEmail);
            if (!string.Equals(plainPk, emailPk, StringComparison.Ordinal))
                await TryDeleteRowAsync(userEmails, plainPk, Lookup, "UserEmails", ct).ConfigureAwait(false);
        }

        // Email-domain + local-part prefix indexes.
        if (domainToken is not null)
        {
            await WriteDomainIndexAsync(domainToken(), userId, ct).ConfigureAwait(false);
            if (dropLegacy && _indexTokenized)
            {
                var plainDomPk = partitioner.PK(domain!);
                if (!string.Equals(plainDomPk, partitioner.PK(domainToken()), StringComparison.Ordinal))
                    await TryDeleteRowAsync(userEmailDomains!, plainDomPk, userId, "UserEmailDomains", ct).ConfigureAwait(false);
            }
        }
        if (localPrefixTokens is not null)
            await WriteEmailLocalPrefixIndexAsync(localPrefixTokens(), userId, ct).ConfigureAwait(false);

        // Name prefix indexes.
        if (normalizedFirst is not null && userFirstNames is not null)
        {
            await WriteNameIndexAsync(userFirstNames, firstTokens?.Invoke(), "UserFirstNames", normalizedFirst, userId, ct).ConfigureAwait(false);
            if (dropLegacy && _indexTokenized)
                await TryDeleteRowAsync(userFirstNames, LegacyNamePk(normalizedFirst), $"{normalizedFirst}|{userId}", "UserFirstNames", ct).ConfigureAwait(false);
        }
        if (normalizedLast is not null && userLastNames is not null)
        {
            await WriteNameIndexAsync(userLastNames, lastTokens?.Invoke(), "UserLastNames", normalizedLast, userId, ct).ConfigureAwait(false);
            if (dropLegacy && _indexTokenized)
                await TryDeleteRowAsync(userLastNames, LegacyNamePk(normalizedLast), $"{normalizedLast}|{userId}", "UserLastNames", ct).ConfigureAwait(false);
        }
    }

    public async Task UpdateAsync(AuthUser user, CancellationToken ct = default)
    {
        var existing = await users.GetAsync(partitioner.PK(user.Id), Profile, ct).ConfigureAwait(false);
        if (existing is null)
        {
            await CreateAsync(user, ct).ConfigureAwait(false);
            return;
        }

        // Old plaintext values drive old-index-key removal.
        var old = await ReadUserAsync(existing, ct).ConfigureAwait(false);
        var emailChanged = !string.Equals(old.NormalizedEmail, user.NormalizedEmail, StringComparison.Ordinal);
        var localChanged = emailChanged && !string.Equals(LocalPartOf(old.NormalizedEmail), LocalPartOf(user.NormalizedEmail), StringComparison.Ordinal);
        var oldDomain = DomainOf(old.NormalizedEmail);
        var newDomain = DomainOf(user.NormalizedEmail);
        var domainChanged = emailChanged && !string.Equals(oldDomain, newDomain, StringComparison.Ordinal);
        var oldFirst = Normalize(old.FirstName);
        var newFirst = Normalize(user.FirstName);
        var firstChanged = userFirstNames is not null && !string.Equals(oldFirst, newFirst, StringComparison.Ordinal);
        var oldLast = Normalize(old.LastName);
        var newLast = Normalize(user.LastName);
        var lastChanged = userLastNames is not null && !string.Equals(oldLast, newLast, StringComparison.Ordinal);

        await users.PutAsync(await UserItemAsync(user, existing.GetN("_v") + 1, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await LogUpsertAsync("Users", partitioner.PK(user.Id), Profile, ct).ConfigureAwait(false);

        // Every index key the changed fields need — old-side and new-side — in one tokenizer
        // round-trip, computed before any index write (a tokenizer throw leaves every existing lookup
        // row intact). Write-before-delete per field, mirroring the Azure store.
        var batch = new TokenBatch(_tokenizer);
        Func<string>? newEmailToken = null, oldEmailToken = null, newDomainToken = null, oldDomainToken = null;
        Func<IReadOnlyList<string>>? newLocalTokens = null, oldLocalTokens = null;
        if (emailChanged)
        {
            newEmailToken = batch.Add(user.NormalizedEmail);
            oldEmailToken = batch.Add(old.NormalizedEmail);
            if (localChanged)
            {
                newLocalTokens = ReserveLocalPrefixTokens(batch, user.NormalizedEmail);
                oldLocalTokens = ReserveLocalPrefixTokens(batch, old.NormalizedEmail);
            }
            if (domainChanged && userEmailDomains is not null)
            {
                newDomainToken = newDomain is null ? null : batch.Add(newDomain);
                oldDomainToken = oldDomain is null ? null : batch.Add(oldDomain);
            }
        }
        var newFirstTokens = firstChanged ? ReserveNameTokens(batch, userFirstNames, newFirst) : null;
        var oldFirstTokens = firstChanged ? ReserveNameTokens(batch, userFirstNames, oldFirst) : null;
        var newLastTokens = lastChanged ? ReserveNameTokens(batch, userLastNames, newLast) : null;
        var oldLastTokens = lastChanged ? ReserveNameTokens(batch, userLastNames, oldLast) : null;
        await batch.RunAsync(ct).ConfigureAwait(false);

        if (emailChanged)
        {
            var newPk = partitioner.PK(newEmailToken!());
            await userEmails.PutAsync(LookupItem(newPk, user.Id), ct).ConfigureAwait(false);
            await LogUpsertAsync("UserEmails", newPk, Lookup, ct).ConfigureAwait(false);
            await DeleteEmailIndexAsync(old.NormalizedEmail, oldEmailToken!(), ct).ConfigureAwait(false);

            if (localChanged)
            {
                if (newLocalTokens is not null) await WriteEmailLocalPrefixIndexAsync(newLocalTokens(), user.Id, ct).ConfigureAwait(false);
                if (oldLocalTokens is not null) await DeleteEmailLocalPrefixIndexAsync(oldLocalTokens(), user.Id, ct).ConfigureAwait(false);
            }
            if (domainChanged)
            {
                if (newDomainToken is not null) await WriteDomainIndexAsync(newDomainToken(), user.Id, ct).ConfigureAwait(false);
                if (oldDomainToken is not null) await DeleteDomainIndexAsync(oldDomain!, oldDomainToken(), user.Id, ct).ConfigureAwait(false);
            }
        }

        if (firstChanged)
        {
            if (newFirst is not null) await WriteNameIndexAsync(userFirstNames!, newFirstTokens?.Invoke(), "UserFirstNames", newFirst, user.Id, ct).ConfigureAwait(false);
            if (oldFirst is not null) await DeleteNameIndexAsync(userFirstNames!, oldFirstTokens?.Invoke(), oldFirst, user.Id, "UserFirstNames", ct).ConfigureAwait(false);
        }
        if (lastChanged)
        {
            if (newLast is not null) await WriteNameIndexAsync(userLastNames!, newLastTokens?.Invoke(), "UserLastNames", newLast, user.Id, ct).ConfigureAwait(false);
            if (oldLast is not null) await DeleteNameIndexAsync(userLastNames!, oldLastTokens?.Invoke(), oldLast, user.Id, "UserLastNames", ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(string userId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var existing = await users.GetAsync(pk, Profile, ct).ConfigureAwait(false);
        if (existing is null) return;

        var user = await ReadUserAsync(existing, ct).ConfigureAwait(false);
        var normFirst = Normalize(user.FirstName);
        var normLast = Normalize(user.LastName);

        var batch = new TokenBatch(_tokenizer);
        var emailToken = batch.Add(user.NormalizedEmail);
        var domain = userEmailDomains is null ? null : DomainOf(user.NormalizedEmail);
        var domainToken = domain is null ? null : batch.Add(domain);
        var localPrefixTokens = ReserveLocalPrefixTokens(batch, user.NormalizedEmail);
        var firstTokens = ReserveNameTokens(batch, userFirstNames, normFirst);
        var lastTokens = ReserveNameTokens(batch, userLastNames, normLast);
        await batch.RunAsync(ct).ConfigureAwait(false);

        await DeleteEmailIndexAsync(user.NormalizedEmail, emailToken(), ct).ConfigureAwait(false);
        if (domainToken is not null)
            await DeleteDomainIndexAsync(domain!, domainToken(), userId, ct).ConfigureAwait(false);
        if (localPrefixTokens is not null)
            await DeleteEmailLocalPrefixIndexAsync(localPrefixTokens(), userId, ct).ConfigureAwait(false);

        if (normFirst is not null && userFirstNames is not null)
            await DeleteNameIndexAsync(userFirstNames, firstTokens?.Invoke(), normFirst, userId, "UserFirstNames", ct).ConfigureAwait(false);
        if (normLast is not null && userLastNames is not null)
            await DeleteNameIndexAsync(userLastNames, lastTokens?.Invoke(), normLast, userId, "UserLastNames", ct).ConfigureAwait(false);

        foreach (var login in await GetLoginsAsync(userId, ct).ConfigureAwait(false))
            await RemoveLoginAsync(userId, login.Provider, login.ProviderKey, ct).ConfigureAwait(false);

        // Tombstone-first (F24e): a crash between delete and tombstone would drop the delete from
        // every backup, and a restore would resurrect the (possibly GDPR-erased) account.
        if (tombstones is not null) await tombstones.WriteAsync("Users", pk, Profile, ct).ConfigureAwait(false);
        await users.DeleteAsync(pk, Profile, ct).ConfigureAwait(false);
    }

    // ── login-state stamps (attribute-only; no document rewrite, no cipher round-trips) ──

    public async Task RecordSuccessfulLoginAsync(string userId, string? rehashedPassword = null, CancellationToken ct = default)
    {
        var now = Iso(DateTimeOffset.UtcNow);
        var values = new Dictionary<string, AttributeValue>
        {
            [":zero"] = new() { N = "0" },
            [":now"] = new() { S = now },
        };
        var set = "SET failedCount = :zero, lastLogin = :now, updated = :now";
        if (rehashedPassword is not null)
        {
            set += ", pwd = :pwd";
            values[":pwd"] = new AttributeValue { S = rehashedPassword };
        }

        try
        {
            await users.Client.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = users.Name,
                Key = KeyOf(partitioner.PK(userId), Profile),
                UpdateExpression = set + " REMOVE lockoutEnd",
                ConditionExpression = "attribute_exists(pk)",
                ExpressionAttributeValues = values,
            }, ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            // User deleted between auth and stamp — nothing to record.
        }
    }

    public async Task<bool> RecordFailedLoginAsync(string userId, int maxAttempts, TimeSpan lockoutDuration, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var item = await users.GetAsync(pk, Profile, ct).ConfigureAwait(false);
            if (item is null) return false;

            // Fast path: the promoted login-state attributes are authoritative when both are present
            // (every full write stamps them). Otherwise fall back to the (overlay-aware) document —
            // one legacy row per user, at most once.
            int failed;
            bool lockEnabled;
            DateTimeOffset? lockoutEnd;
            var hasCount = item.ContainsKey("failedCount");
            if (hasCount && item.ContainsKey("lockEnabled"))
            {
                failed = (int)item.GetN("failedCount");
                lockEnabled = item.GetBool("lockEnabled");
                lockoutEnd = item.GetDateOrNull("lockoutEnd");
            }
            else
            {
                var user = await ReadUserAsync(item, ct).ConfigureAwait(false);
                failed = user.AccessFailedCount;
                lockEnabled = user.LockoutEnabled;
                lockoutEnd = user.LockoutEnd;
            }

            failed++;
            var locked = false;
            if (lockEnabled && failed >= maxAttempts)
            {
                lockoutEnd = DateTimeOffset.UtcNow.Add(lockoutDuration);
                failed = 0;
                locked = true;
            }

            var values = new Dictionary<string, AttributeValue>
            {
                [":fc"] = new() { N = failed.ToString(CultureInfo.InvariantCulture) },
                [":le"] = new() { BOOL = lockEnabled },
                [":now"] = new() { S = Iso(DateTimeOffset.UtcNow) },
            };
            var set = "SET failedCount = :fc, lockEnabled = :le, updated = :now";
            // Stamp lockoutEnd when newly locked, or when first materializing the attribute group on a
            // legacy row that still carries an active lockout in its document.
            if (lockoutEnd is not null && (locked || !hasCount))
            {
                set += ", lockoutEnd = :end";
                values[":end"] = new AttributeValue { S = Iso(lockoutEnd.Value) };
            }
            // Optimistic concurrency on the counter itself: a concurrent failed login that wrote first
            // fails the condition — re-read and retry so no increment is lost.
            string condition;
            if (hasCount)
            {
                condition = "attribute_exists(pk) AND failedCount = :old";
                values[":old"] = new AttributeValue { N = item.GetN("failedCount").ToString(CultureInfo.InvariantCulture) };
            }
            else
            {
                condition = "attribute_exists(pk) AND attribute_not_exists(failedCount)";
            }

            try
            {
                await users.Client.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = users.Name,
                    Key = KeyOf(pk, Profile),
                    UpdateExpression = set,
                    ConditionExpression = condition,
                    ExpressionAttributeValues = values,
                }, ct).ConfigureAwait(false);
                return locked;
            }
            catch (ConditionalCheckFailedException)
            {
                // Lost the race (or the user vanished) — loop to re-read and retry.
            }
        }

        return false; // sustained contention; a later attempt will still lock the account
    }

    // ── listing ──────────────────────────────────────────────────────────────────

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

    public Task<UserPage> ListPageAsync(string? organizationId, int count, string? continuationToken, CancellationToken ct = default)
    {
        var (filter, values) = DynamoClientStore.ConfigScanFilter(partitioner, Profile);
        return ReadPageAsync(filter, values,
            u => organizationId is null || string.Equals(u.OrganizationId, organizationId, StringComparison.Ordinal),
            count, continuationToken, ct);
    }

    public Task<UserPage> ListByScimClientPageAsync(string scimClientId, int count, string? continuationToken, CancellationToken ct = default)
    {
        var (filter, values) = DynamoClientStore.ConfigScanFilter(partitioner, Profile);
        var v = new Dictionary<string, AttributeValue>(values) { [":sc"] = new() { S = scimClientId } };
        return ReadPageAsync($"{filter} AND scimClient = :sc", v, _ => true, count, continuationToken, ct);
    }

    // Native-continuation cursor page: resumes the scan from the token's ExclusiveStartKey, so page N
    // costs one scan segment instead of re-walking (and re-decrypting) every skipped row — the exact
    // O(N²) the offset emulation had. The page cap bounds one call's work when a client-side filter
    // matches almost nothing.
    private async Task<UserPage> ReadPageAsync(
        string filter, IReadOnlyDictionary<string, AttributeValue> values, Func<AuthUser, bool> keep,
        int count, string? continuationToken, CancellationToken ct)
    {
        var results = new List<AuthUser>();
        var startKey = DecodeContinuation(continuationToken);
        string? nextToken = null;
        var pagesConsumed = 0;

        while (true)
        {
            var (items, lastKey) = await users.ScanPageAsync(filter, values, startKey, Math.Max(count, 25), ct).ConfigureAwait(false);
            foreach (var item in items)
            {
                AuthUser user;
                try
                {
                    user = await ReadUserAsync(item, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    continue; // one undecryptable/corrupt row must not fail the page
                }
                if (keep(user)) results.Add(user);
            }

            if (lastKey is null) { nextToken = null; break; }
            startKey = lastKey;
            nextToken = EncodeContinuation(lastKey);
            if (results.Count >= count || ++pagesConsumed >= 10) break;
        }

        return new UserPage(results, nextToken);
    }

    private static string? EncodeContinuation(Dictionary<string, AttributeValue>? lastKey)
    {
        if (lastKey is null) return null;
        var flat = lastKey.ToDictionary(kv => kv.Key, kv => kv.Value.S ?? string.Empty);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(flat, AwsJsonContext.Default.DictionaryStringString)));
    }

    private static Dictionary<string, AttributeValue>? DecodeContinuation(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var flat = JsonSerializer.Deserialize(
                Encoding.UTF8.GetString(Convert.FromBase64String(token)), AwsJsonContext.Default.DictionaryStringString);
            return flat?.ToDictionary(kv => kv.Key, kv => new AttributeValue { S = kv.Value });
        }
        catch (FormatException) { return null; } // malformed token → restart from the beginning
        catch (JsonException) { return null; }
    }

    private async Task<(IReadOnlyList<AuthUser> Users, bool HasMore)> PageProfilesAsync(
        string filter, IReadOnlyDictionary<string, AttributeValue> values, int startIndex, int count, Func<AuthUser, bool> keep, CancellationToken ct)
    {
        var results = new List<AuthUser>();
        var skipped = 0;
        var start = Math.Max(0, startIndex);

        await foreach (var item in users.ScanAsync(filter, values, ct).ConfigureAwait(false))
        {
            var user = await ReadUserAsync(item, ct).ConfigureAwait(false);
            if (!keep(user)) continue;
            if (skipped < start) { skipped++; continue; }
            results.Add(user);
            if (results.Count > count) break; // one extra → hasMore
        }

        var hasMore = results.Count > count;
        if (hasMore) results.RemoveAt(results.Count - 1);
        return (results, hasMore);
    }

    // ── whole-population streams ─────────────────────────────────────────────────

    public async IAsyncEnumerable<string> EnumerateUserIdsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // Key-only projection — no document attribute, so no per-row decryption; DynamoDB pages via
        // its native continuation. O(N), unlike the offset re-scan.
        var (filter, values) = DynamoClientStore.ConfigScanFilter(partitioner, Profile);
        await foreach (var item in users.ScanProjectedAsync("pk", filter, values, ct: ct).ConfigureAwait(false))
            yield return partitioner.Strip(item.GetStr("pk"));
    }

    public async IAsyncEnumerable<UserLoginState> EnumerateLoginStatesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // Project the promoted non-PII attributes; rows written before the attribute promotion fall
        // back to the document (which is plaintext on such legacy rows — ResolveAsync passthrough).
        var (filter, values) = DynamoClientStore.ConfigScanFilter(partitioner, Profile);
        await foreach (var item in users.ScanProjectedAsync(
            "pk, created, lastLogin, active, failedCount, #d", filter, values,
            names: new Dictionary<string, string> { ["#d"] = DataAttr }, ct: ct).ConfigureAwait(false))
        {
            var id = partitioner.Strip(item.GetStr("pk"));
            if (item.ContainsKey("created"))
            {
                yield return new UserLoginState(id, item.GetDate("created"), item.GetDateOrNull("lastLogin"), item.GetBool("active"));
                continue;
            }
            var user = await ReadUserAsync(item, ct).ConfigureAwait(false);
            yield return new UserLoginState(id, user.CreatedAt, user.LastLoginAt, user.IsActive);
        }
    }

    // ── search ───────────────────────────────────────────────────────────────────

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

        // Tokenized: compute the (≤2) prefix-lookup tokens — email local-part and name — in one
        // round-trip. HMAC keys are unordered, so email prefix search uses the local-part prefix
        // index; with tokenization off, the ordered begins_with scan on the exact-email index works.
        string? localPrefixPk = null, namePrefixPk = null;
        if (_indexTokenized)
        {
            var batch = new TokenBatch(_tokenizer);
            Func<string>? localToken = null, nameToken = null;
            var local = LocalPartOf(prefix) ?? prefix;
            if (userEmailLocalPrefixes is not null && local.Length >= NamePrefixMin)
                localToken = batch.Add(local.Length > NamePrefixMax ? local[..NamePrefixMax] : local);
            if ((userFirstNames ?? userLastNames) is not null && prefix.Length >= NamePrefixMin)
                nameToken = batch.Add(prefix.Length > NamePrefixMax ? prefix[..NamePrefixMax] : prefix);
            await batch.RunAsync(ct).ConfigureAwait(false);
            localPrefixPk = localToken is null ? null : partitioner.PK(localToken());
            namePrefixPk = nameToken is null ? null : partitioner.PK(nameToken());
        }

        var emailTask = _indexTokenized
            ? CollectPartitionUserIdsAsync(userEmailLocalPrefixes, localPrefixPk, maxResults, ct)
            : CollectAsync(userEmails.ScanAsync(
                "sk = :lk AND begins_with(pk, :p)",
                new Dictionary<string, AttributeValue> { [":lk"] = new() { S = Lookup }, [":p"] = new() { S = partitioner.PK(prefix) } },
                ct), maxResults, ct);

        var firstTask = SearchNameIndexAsync(userFirstNames, namePrefixPk, prefix, maxResults, ct);
        var lastTask = SearchNameIndexAsync(userLastNames, namePrefixPk, prefix, maxResults, ct);

        await Task.WhenAll(emailTask, firstTask, lastTask).ConfigureAwait(false);

        var candidateIds = emailTask.Result.Concat(firstTask.Result).Concat(lastTask.Result)
            .Where(id => seen.Add(id)).ToList();
        var fetched = await Task.WhenAll(candidateIds.Select(id => GetAsync(id, ct))).ConfigureAwait(false);
        foreach (var user in fetched)
        {
            if (user is null) continue;
            results.Add(user);
            if (results.Count >= maxResults) break;
        }

        return results;
    }

    public async Task<IReadOnlyList<AuthUser>> SearchByEmailDomainAsync(string domain, int maxResults = 50, CancellationToken ct = default)
    {
        if (userEmailDomains is null || string.IsNullOrWhiteSpace(domain)) return [];

        var normDomain = domain.Trim().ToUpperInvariant();
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        async Task CollectPartitionAsync(string pk)
        {
            if (ids.Count >= maxResults) return;
            await foreach (var item in userEmailDomains.QueryAsync(pk, ct: ct).ConfigureAwait(false))
            {
                if (seen.Add(item.GetStr("userId"))) ids.Add(item.GetStr("userId"));
                if (ids.Count >= maxResults) break;
            }
        }

        var basePk = partitioner.PK(await _tokenizer.TokenizeAsync(normDomain, ct).ConfigureAwait(false));
        for (var b = 0; b < DomainBuckets && ids.Count < maxResults; b++)
            await CollectPartitionAsync($"{basePk}-{b:x}").ConfigureAwait(false);
        // Migration windows: unbucketed tokenized rows, then plaintext rows (pre-tokenization).
        await CollectPartitionAsync(basePk).ConfigureAwait(false);
        if (_indexTokenized && ids.Count < maxResults)
            await CollectPartitionAsync(partitioner.PK(normDomain)).ConfigureAwait(false);

        var results = new List<AuthUser>();
        foreach (var id in ids)
        {
            var user = await GetAsync(id, ct).ConfigureAwait(false);
            if (user is not null) results.Add(user);
            if (results.Count >= maxResults) break;
        }
        return results;
    }

    // Collect userIds whose name starts with `prefix`. Tokenized: exact-match on the prefix token,
    // plus the legacy range scan for migration-window rows. Off: legacy scheme only.
    private async Task<List<string>> SearchNameIndexAsync(DynamoTable? table, string? tokenPk, string prefix, int maxResults, CancellationToken ct)
    {
        if (table is null || prefix.Length < NamePrefixMin) return [];

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (tokenPk is not null)
        {
            await foreach (var item in table.QueryAsync(tokenPk, ct: ct).ConfigureAwait(false))
            {
                if (seen.Add(item.GetStr("userId"))) ids.Add(item.GetStr("userId"));
                if (ids.Count >= maxResults) break;
            }
            if (ids.Count >= maxResults) return ids;
        }

        // Legacy scheme (the only scheme when tokenization is off): 2-char bucket partition,
        // sk = "{name}|{userId}", prefix via begins_with.
        await foreach (var item in table.QueryAsync(
            LegacyNamePk(prefix),
            sortKeyCondition: "begins_with(sk, :p)",
            values: new Dictionary<string, AttributeValue> { [":p"] = new() { S = prefix } },
            ct: ct).ConfigureAwait(false))
        {
            if (seen.Add(item.GetStr("userId"))) ids.Add(item.GetStr("userId"));
            if (ids.Count >= maxResults) break;
        }
        return ids;
    }

    private async Task<List<string>> CollectPartitionUserIdsAsync(DynamoTable? table, string? pk, int cap, CancellationToken ct)
    {
        if (table is null || pk is null) return [];
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in table.QueryAsync(pk, ct: ct).ConfigureAwait(false))
        {
            if (seen.Add(item.GetStr("userId"))) ids.Add(item.GetStr("userId"));
            if (ids.Count >= cap) break;
        }
        return ids;
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

    // ── reindex + migrations (the cold-row encryption backfill surface) ─────────

    public async Task ReindexUserAsync(string userId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var existing = await users.GetAsync(pk, Profile, ct).ConfigureAwait(false);
        if (existing is null) return;

        var user = await ReadUserAsync(existing, ct).ConfigureAwait(false);

        // 1. Re-write the profile under the current cipher (plaintext → ciphertext; idempotent).
        await users.PutAsync(await UserItemAsync(user, existing.GetN("_v") + 1, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await LogUpsertAsync("Users", pk, Profile, ct).ConfigureAwait(false);

        // 2. Rewrite the profile-derived indexes under the current keys, dropping legacy rows.
        await WriteProfileIndexesAsync(user.NormalizedEmail, Normalize(user.FirstName), Normalize(user.LastName), userId, dropLegacy: true, ct).ConfigureAwait(false);
    }

    public async Task<int> MigrateExternalIdIndexAsync(bool dryRun, CancellationToken ct = default)
    {
        if (!_indexTokenized) return 0;

        var (filter, values) = LookupScanFilter();
        var count = 0;
        await foreach (var item in userExternalIds.ScanAsync(filter, values, ct).ConfigureAwait(false))
        {
            var composite = partitioner.Strip(item.GetStr("pk"));
            if (!composite.Contains('|')) continue; // already a token (tokens are hex — never contain '|')
            count++;
            if (dryRun) continue;

            var tokenPk = partitioner.PK(await _tokenizer.TokenizeAsync(composite, ct).ConfigureAwait(false));
            if (string.Equals(tokenPk, item.GetStr("pk"), StringComparison.Ordinal)) continue; // defensive
            await userExternalIds.PutAsync(LookupItem(tokenPk, item.GetStr("userId")), ct).ConfigureAwait(false);
            await LogUpsertAsync("UserExternalIds", tokenPk, Lookup, ct).ConfigureAwait(false);
            await TryDeleteRowAsync(userExternalIds, item.GetStr("pk"), Lookup, "UserExternalIds", ct).ConfigureAwait(false);
        }
        return count;
    }

    public async Task<int> MigrateUserLoginsAsync(bool dryRun, CancellationToken ct = default)
    {
        if (!_indexTokenized) return 0;

        var range = partitioner.RangeForEnv();
        var (filter, values) = range is null
            ? ((string?)null, (IReadOnlyDictionary<string, AttributeValue>?)null)
            : ("pk >= :lo AND pk < :hi", new Dictionary<string, AttributeValue>
            {
                [":lo"] = new() { S = range.Value.Low },
                [":hi"] = new() { S = range.Value.High },
            });

        var count = 0;
        await foreach (var item in userLogins.ScanAsync(filter, values, ct).ConfigureAwait(false))
        {
            var sk = item.GetStr("sk");
            var isForward = sk == Lookup;
            var isReverse = sk.StartsWith(LoginPrefix, StringComparison.Ordinal);
            if (!isForward && !isReverse) continue;

            // Legacy iff the key still carries the plaintext composite (contains '|'); a token is hex.
            var legacy = isForward
                ? partitioner.Strip(item.GetStr("pk")).Contains('|')
                : sk[LoginPrefix.Length..].Contains('|');
            if (!legacy) continue;
            count++;
            if (dryRun) continue;

            // Columns are plaintext on a legacy row — recompute the token and encrypt for the move.
            var login = new ExternalLoginInfo
            {
                UserId = item.GetStr("userId"),
                Provider = item.GetStr("provider"),
                ProviderKey = item.GetStr("providerKey"),
                DisplayName = item.GetS("displayName"),
            };
            var token = await _tokenizer.TokenizeAsync($"{login.Provider}|{login.ProviderKey}", ct).ConfigureAwait(false);
            var moved = await EncryptedLoginItemAsync(
                isForward ? partitioner.PK(token) : item.GetStr("pk"),
                isForward ? Lookup : $"{LoginPrefix}{token}",
                login, ct).ConfigureAwait(false);
            await userLogins.PutAsync(moved, ct).ConfigureAwait(false);                                    // write new first
            await LogUpsertAsync("UserLogins", moved.GetStr("pk"), moved.GetStr("sk"), ct).ConfigureAwait(false);
            await TryDeleteRowAsync(userLogins, item.GetStr("pk"), sk, "UserLogins", ct).ConfigureAwait(false); // then drop legacy
        }
        return count;
    }

    // ── external ids ─────────────────────────────────────────────────────────────

    public async Task SetExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(await _tokenizer.TokenizeAsync($"{clientId}|{externalId}", ct).ConfigureAwait(false));
        await userExternalIds.PutAsync(LookupItem(pk, userId), ct).ConfigureAwait(false);
        await LogUpsertAsync("UserExternalIds", pk, Lookup, ct).ConfigureAwait(false);
    }

    public async Task RemoveExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default)
    {
        var composite = $"{clientId}|{externalId}";
        var tokenPk = partitioner.PK(await _tokenizer.TokenizeAsync(composite, ct).ConfigureAwait(false));
        await TryDeleteRowAsync(userExternalIds, tokenPk, Lookup, "UserExternalIds", ct).ConfigureAwait(false);
        if (_indexTokenized)
        {
            var plainPk = partitioner.PK(composite);
            if (!string.Equals(plainPk, tokenPk, StringComparison.Ordinal))
                await TryDeleteRowAsync(userExternalIds, plainPk, Lookup, "UserExternalIds", ct).ConfigureAwait(false);
        }
    }

    // ── external logins ──────────────────────────────────────────────────────────
    // Lookup KEYS are blind-index tokens: forward pk = token(provider|providerKey), reverse
    // sk = "login|{token}". The recoverable VALUE columns (providerKey — a SAML NameId is usually an
    // email — and displayName) are encrypted; provider + userId stay plaintext. All passthrough when
    // the seams are the Null defaults, so single-tenant hosts keep the historical layout.

    public async Task AddLoginAsync(ExternalLoginInfo login, CancellationToken ct = default)
    {
        var token = await _tokenizer.TokenizeAsync($"{login.Provider}|{login.ProviderKey}", ct).ConfigureAwait(false);
        var forward = await EncryptedLoginItemAsync(partitioner.PK(token), Lookup, login, ct).ConfigureAwait(false);
        var reverse = new Dictionary<string, AttributeValue>(forward)
        {
            [Dyn.Pk] = new AttributeValue { S = partitioner.PK(login.UserId) },
            [Dyn.Sk] = new AttributeValue { S = $"{LoginPrefix}{token}" },
        };
        await userLogins.PutAsync(forward, ct).ConfigureAwait(false);
        await userLogins.PutAsync(reverse, ct).ConfigureAwait(false);
        await LogUpsertBatchAsync("UserLogins", [
            (forward.GetStr("pk"), forward.GetStr("sk")),
            (reverse.GetStr("pk"), reverse.GetStr("sk")),
        ], ct).ConfigureAwait(false);
    }

    public async Task RemoveLoginAsync(string userId, string provider, string providerKey, CancellationToken ct = default)
    {
        var token = await _tokenizer.TokenizeAsync($"{provider}|{providerKey}", ct).ConfigureAwait(false);
        var reversePk = partitioner.PK(userId);

        await TryDeleteRowAsync(userLogins, partitioner.PK(token), Lookup, "UserLogins", ct).ConfigureAwait(false);
        await TryDeleteRowAsync(userLogins, reversePk, $"{LoginPrefix}{token}", "UserLogins", ct).ConfigureAwait(false);
        if (_indexTokenized)
        {
            // Also drop any not-yet-migrated legacy rows keyed on the plaintext composite.
            await TryDeleteRowAsync(userLogins, partitioner.PK($"{provider}|{providerKey}"), Lookup, "UserLogins", ct).ConfigureAwait(false);
            await TryDeleteRowAsync(userLogins, reversePk, $"{LoginPrefix}{provider}|{providerKey}", "UserLogins", ct).ConfigureAwait(false);
        }
    }

    public async Task<ExternalLoginInfo?> FindLoginAsync(string provider, string providerKey, CancellationToken ct = default)
    {
        var token = await _tokenizer.TokenizeAsync($"{provider}|{providerKey}", ct).ConfigureAwait(false);
        var item = await userLogins.GetAsync(partitioner.PK(token), Lookup, ct).ConfigureAwait(false);
        if (item is null && _indexTokenized)
            item = await userLogins.GetAsync(partitioner.PK($"{provider}|{providerKey}"), Lookup, ct).ConfigureAwait(false);
        return item is null ? null : await ReadLoginAsync(item, ct).ConfigureAwait(false);
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
            results.Add(await ReadLoginAsync(item, ct).ConfigureAwait(false));
        }
        return results;
    }

    // ── index-row helpers ────────────────────────────────────────────────────────

    private static Dictionary<string, AttributeValue> LookupItem(string pk, string userId)
    {
        var item = Dyn.Item(pk, Lookup);
        item.PutS("userId", userId);
        return item;
    }

    private async Task DeleteEmailIndexAsync(string normalizedEmail, string emailToken, CancellationToken ct)
    {
        var tokenPk = partitioner.PK(emailToken);
        await TryDeleteRowAsync(userEmails, tokenPk, Lookup, "UserEmails", ct).ConfigureAwait(false);
        if (_indexTokenized)
        {
            var plainPk = partitioner.PK(normalizedEmail);
            if (!string.Equals(plainPk, tokenPk, StringComparison.Ordinal))
                await TryDeleteRowAsync(userEmails, plainPk, Lookup, "UserEmails", ct).ConfigureAwait(false);
        }
    }

    private async Task WriteDomainIndexAsync(string domainToken, string userId, CancellationToken ct)
    {
        var pk = Bucketed(partitioner.PK(domainToken), userId);
        var item = Dyn.Item(pk, userId);
        item.PutS("userId", userId);
        await userEmailDomains!.PutAsync(item, ct).ConfigureAwait(false);
        await LogUpsertAsync("UserEmailDomains", pk, userId, ct).ConfigureAwait(false);
    }

    private async Task DeleteDomainIndexAsync(string domain, string domainToken, string userId, CancellationToken ct)
    {
        var tokenPk = partitioner.PK(domainToken);
        await TryDeleteRowAsync(userEmailDomains!, Bucketed(tokenPk, userId), userId, "UserEmailDomains", ct).ConfigureAwait(false); // current: bucketed
        await TryDeleteRowAsync(userEmailDomains!, tokenPk, userId, "UserEmailDomains", ct).ConfigureAwait(false);                   // legacy: unbucketed
        if (_indexTokenized)
        {
            var plainPk = partitioner.PK(domain);
            if (!string.Equals(plainPk, tokenPk, StringComparison.Ordinal))
                await TryDeleteRowAsync(userEmailDomains!, plainPk, userId, "UserEmailDomains", ct).ConfigureAwait(false);           // legacy: plaintext
        }
    }

    private async Task WriteEmailLocalPrefixIndexAsync(IReadOnlyList<string> tokens, string userId, CancellationToken ct)
    {
        await Task.WhenAll(tokens.Select(token =>
        {
            var item = Dyn.Item(partitioner.PK(token), userId);
            item.PutS("userId", userId);
            return userEmailLocalPrefixes!.PutAsync(item, ct);
        })).ConfigureAwait(false);
        await LogUpsertBatchAsync("UserEmailLocalPrefixes", tokens.Select(t => (partitioner.PK(t), userId)), ct).ConfigureAwait(false);
    }

    private async Task DeleteEmailLocalPrefixIndexAsync(IReadOnlyList<string> tokens, string userId, CancellationToken ct)
    {
        foreach (var token in tokens)
            await TryDeleteRowAsync(userEmailLocalPrefixes!, partitioner.PK(token), userId, "UserEmailLocalPrefixes", ct).ConfigureAwait(false);
    }

    // Tokenized: one row per name prefix, pk = HMAC(prefix), sk = userId — "starts with p" becomes an
    // exact partition lookup. Off: the legacy single row (2-char bucket pk, sk = "{name}|{userId}").
    // The two shapes never collide, so both coexist during migration; search reads both.
    private async Task WriteNameIndexAsync(DynamoTable table, IReadOnlyList<string>? tokens, string changeTable, string normalizedName, string userId, CancellationToken ct)
    {
        if (tokens is not null)
        {
            await Task.WhenAll(tokens.Select(token =>
            {
                var item = Dyn.Item(partitioner.PK(token), userId);
                item.PutS("userId", userId);
                return table.PutAsync(item, ct);
            })).ConfigureAwait(false);
            await LogUpsertBatchAsync(changeTable, tokens.Select(t => (partitioner.PK(t), userId)), ct).ConfigureAwait(false);
            return;
        }
        var item = Dyn.Item(LegacyNamePk(normalizedName), $"{normalizedName}|{userId}");
        item.PutS("userId", userId);
        await table.PutAsync(item, ct).ConfigureAwait(false);
        await LogUpsertAsync(changeTable, LegacyNamePk(normalizedName), $"{normalizedName}|{userId}", ct).ConfigureAwait(false);
    }

    private async Task DeleteNameIndexAsync(DynamoTable table, IReadOnlyList<string>? tokens, string normalizedName, string userId, string tombstoneTable, CancellationToken ct)
    {
        if (tokens is not null)
        {
            foreach (var token in tokens)
                await TryDeleteRowAsync(table, partitioner.PK(token), userId, tombstoneTable, ct).ConfigureAwait(false);
            // Migration window: also remove any legacy row for this name.
        }
        await TryDeleteRowAsync(table, LegacyNamePk(normalizedName), $"{normalizedName}|{userId}", tombstoneTable, ct).ConfigureAwait(false);
    }

    private string LegacyNamePk(string normalizedName)
        => partitioner.PK(normalizedName.Length >= NamePrefixMin ? normalizedName[..NamePrefixMin] : normalizedName);

    // Tombstone-first (F24e), then an unconditional delete (succeeds even if the row is already gone).
    private async Task TryDeleteRowAsync(DynamoTable table, string pk, string sk, string tombstoneTable, CancellationToken ct)
    {
        if (tombstones is not null)
            await tombstones.WriteAsync(tombstoneTable, pk, sk, ct).ConfigureAwait(false);
        await table.DeleteAsync(pk, sk, ct).ConfigureAwait(false);
    }

    private (string? Filter, IReadOnlyDictionary<string, AttributeValue>? Values) LookupScanFilter()
    {
        var range = partitioner.RangeForEnv();
        if (range is null)
            return ("sk = :lk", new Dictionary<string, AttributeValue> { [":lk"] = new() { S = Lookup } });
        return ("sk = :lk AND pk >= :lo AND pk < :hi", new Dictionary<string, AttributeValue>
        {
            [":lk"] = new() { S = Lookup },
            [":lo"] = new() { S = range.Value.Low },
            [":hi"] = new() { S = range.Value.High },
        });
    }

    private async Task<Dictionary<string, AttributeValue>> EncryptedLoginItemAsync(string pk, string sk, ExternalLoginInfo login, CancellationToken ct)
    {
        // Encrypt providerKey (+ displayName) once per call; passthrough under the Null cipher.
        var toProtect = new List<string> { login.ProviderKey };
        if (!string.IsNullOrEmpty(login.DisplayName)) toProtect.Add(login.DisplayName);
        var ciphertexts = await _cipher.ProtectManyAsync(toProtect, ct).ConfigureAwait(false);

        var item = Dyn.Item(pk, sk);
        item.PutS("userId", login.UserId);
        item.PutS("provider", login.Provider);
        item.PutS("providerKey", ciphertexts[0]);
        item.PutS("displayName", ciphertexts.Count > 1 ? ciphertexts[1] : login.DisplayName);
        return item;
    }

    private async Task<ExternalLoginInfo> ReadLoginAsync(Dictionary<string, AttributeValue> item, CancellationToken ct)
    {
        var providerKey = await _cipher.ResolveAsync(item.GetStr("providerKey"), ct).ConfigureAwait(false);
        var displayName = item.GetS("displayName");
        if (!string.IsNullOrEmpty(displayName))
            displayName = await _cipher.ResolveAsync(displayName, ct).ConfigureAwait(false);
        return new ExternalLoginInfo
        {
            UserId = item.GetStr("userId"),
            Provider = item.GetStr("provider"),
            ProviderKey = providerKey,
            DisplayName = displayName,
        };
    }

    private static Dictionary<string, AttributeValue> KeyOf(string pk, string sk) => new()
    {
        [Dyn.Pk] = new AttributeValue { S = pk },
        [Dyn.Sk] = new AttributeValue { S = sk },
    };
}
