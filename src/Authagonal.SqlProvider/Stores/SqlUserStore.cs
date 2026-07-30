using System.Runtime.CompilerServices;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IUserStore"/> — the largest store. Layout mirrors the other backends: a primary
/// "Users" table plus email / external-id / login indexes, optional first/last-name prefix indexes,
/// and optional email-domain + email-local-part-prefix indexes.
///
/// <para>
/// At-rest crypto goes through the same Core seams: the whole <see cref="AuthUser"/> document is
/// encrypted via <see cref="IFieldCipher"/>, and lookup keys become blind-index tokens via
/// <see cref="IIndexTokenizer"/> with a plaintext-key dual-read for rows written before tokenization
/// was switched on. Both default to passthrough, so an unconfigured host keeps a plain layout — and
/// the dual-read plus <see cref="ReindexUserAsync"/> are what let a running deployment turn
/// encryption on without downtime.
/// </para>
///
/// <para>
/// Non-PII fields the queries and login paths need are promoted to attributes: <c>org</c> and
/// <c>scimClient</c> back the list filters, <c>created</c>/<c>active</c> back the login-state scan,
/// and the mutable login-state group (<c>failedCount</c>, <c>lockoutEnd</c>, <c>lastLogin</c>,
/// <c>updated</c>, <c>pwd</c>, <c>lockEnabled</c>) is stamped attribute-only by
/// <see cref="RecordSuccessfulLoginAsync"/>/<see cref="RecordFailedLoginAsync"/> — the document is
/// neither read nor written, so zero cipher round-trips on the hot path — and overlaid onto the
/// document on read.
/// </para>
/// </summary>
public sealed class SqlUserStore(
    SqlTable users,
    SqlTable userEmails,
    SqlTable userLogins,
    SqlTable userExternalIds,
    SqlTable? userFirstNames,
    SqlTable? userLastNames,
    EnvPartitioner partitioner,
    IChangeWriter? tombstones = null,
    SqlTable? userEmailDomains = null,
    SqlTable? userEmailLocalPrefixes = null,
    IFieldCipher? fieldCipher = null,
    IIndexTokenizer? indexTokenizer = null) : IUserStore
{
    private const string Profile = "profile";
    private const string Lookup = "lookup";
    private const string LoginPrefix = "login|";

    private readonly IFieldCipher _cipher = fieldCipher ?? NullFieldCipher.Instance;
    private readonly IIndexTokenizer _tokenizer = indexTokenizer ?? NullIndexTokenizer.Instance;
    // Tokenization "on" means dual-read fallbacks and per-prefix token rows; the Null default keeps
    // the plaintext key scheme byte-for-byte (same rule as the other backends).
    private readonly bool _indexTokenized = indexTokenizer is not null;

    // Same prefix-index bounds as the other stores: queries shorter than Min don't hit the index;
    // prefixes longer than Max match on their first Max chars.
    private const int NamePrefixMin = 2;
    private const int NamePrefixMax = 16;

    // A domain's members are bucketed so one big-domain tenant doesn't funnel every index write into a
    // single partition. Must stay identical to the other backends' constants/hash so semantics (and
    // tests) agree. Bucket = stable FNV-1a of the userId (string.GetHashCode is not stable across
    // processes).
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

    // Collects every value a write needs tokens for and computes them in ONE TokenizeBatchAsync call
    // (one Vault HMAC round-trip in Cloud) instead of one call per index. Token COMPUTATION only —
    // write/delete ordering (write-before-delete) is unchanged.
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

    private Func<IReadOnlyList<string>>? ReserveNameTokens(TokenBatch batch, SqlTable? table, string? normalizedName)
        => table is not null && normalizedName is not null && _indexTokenized
            ? batch.AddRange(NamePrefixesOf(normalizedName))
            : null;

    // Change-log capture for incremental backups — upsert-side mirror of the tombstone writes, same
    // table set as the other backends. Login-state-only stamps are deliberately NOT logged (hot path,
    // low-value fields; the periodic full-scan backstop covers them).
    private Task LogUpsertAsync(string changeTable, string pk, string sk, CancellationToken ct)
        => tombstones?.WriteUpsertAsync(changeTable, pk, sk, ct) ?? Task.CompletedTask;

    private Task LogUpsertBatchAsync(string changeTable, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct)
        => tombstones?.WriteUpsertBatchAsync(changeTable, keys, ct) ?? Task.CompletedTask;

    // ── document crypto + login-state overlay ────────────────────────────────────

    private async Task<SqlRow> UserRowAsync(AuthUser user, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(user, SqlJsonContext.Default.AuthUser);
        var row = new SqlRow(partitioner.PK(user.Id), Profile)
        {
            Data = await _cipher.ProtectAsync(json, ct).ConfigureAwait(false),
        };
        row.PutS("org", user.OrganizationId);
        row.PutS("scimClient", user.ScimProvisionedByClientId);
        // Non-PII projections: created/active back the login-state scan; the failedCount group is the
        // attribute-only login-stamp target (see Record*LoginAsync) and is overlaid on read. Every full
        // write stamps the whole group, which is the invariant the overlay and the stamps rely on.
        row.PutDate("created", user.CreatedAt);
        row.PutBool("active", user.IsActive);
        row.PutBool("lockEnabled", user.LockoutEnabled);
        row.PutN("failedCount", user.AccessFailedCount);
        row.PutDate("lockoutEnd", user.LockoutEnd);
        row.PutDate("lastLogin", user.LastLoginAt);
        row.PutDate("updated", user.UpdatedAt);
        row.PutS("pwd", user.PasswordHash);
        row.PutS("pwdPending", user.PendingPasswordHash);
        return row;
    }

    private async Task<AuthUser> ReadUserAsync(SqlRow row, CancellationToken ct)
    {
        // ResolveAsync passes plaintext JSON through unchanged, so rows written before encryption was
        // enabled keep deserializing — the lazy-migration contract shared with the other backends.
        var json = await _cipher.ResolveAsync(row.DataOrEmpty, ct).ConfigureAwait(false);
        var user = JsonSerializer.Deserialize(json, SqlJsonContext.Default.AuthUser)!;

        // Login-state overlay: Record*LoginAsync stamp only these attributes (never the document), so
        // when the marker is present the attributes are at least as new as the document.
        if (row.Has("failedCount"))
        {
            user.AccessFailedCount = (int)row.GetN("failedCount");
            user.LockoutEnd = row.GetDateOrNull("lockoutEnd");
            user.LastLoginAt = row.GetDateOrNull("lastLogin");
            if (row.GetDateOrNull("updated") is { } updated) user.UpdatedAt = updated;
            user.PasswordHash = row.GetS("pwd");
            user.PendingPasswordHash = row.GetS("pwdPending");
        }
        return user;
    }

    // ── point reads ──────────────────────────────────────────────────────────────

    public async Task<AuthUser?> GetAsync(string userId, CancellationToken ct = default)
    {
        var row = await users.GetAsync(partitioner.PK(userId), Profile, ct: ct).ConfigureAwait(false);
        return row is null ? null : await ReadUserAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(string userId, CancellationToken ct = default)
        => await users.GetAsync(partitioner.PK(userId), Profile, includeData: false, ct).ConfigureAwait(false) is not null;

    public async Task<AuthUser?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalized = email.ToUpperInvariant();
        // Blind-index point read on the tokenized key; during migration fall back to the plaintext key
        // for rows not yet backfilled (only meaningful while tokenization is on).
        var idx = await userEmails.GetAsync(
            partitioner.PK(await _tokenizer.TokenizeAsync(normalized, ct).ConfigureAwait(false)), Lookup, ct: ct).ConfigureAwait(false);
        if (idx is null && _indexTokenized)
            idx = await userEmails.GetAsync(partitioner.PK(normalized), Lookup, ct: ct).ConfigureAwait(false);
        return idx is null ? null : await GetAsync(idx.GetStr("userId"), ct).ConfigureAwait(false);
    }

    public async Task<AuthUser?> FindByExternalIdAsync(string clientId, string externalId, CancellationToken ct = default)
    {
        var composite = $"{clientId}|{externalId}";
        var idx = await userExternalIds.GetAsync(
            partitioner.PK(await _tokenizer.TokenizeAsync(composite, ct).ConfigureAwait(false)), Lookup, ct: ct).ConfigureAwait(false);
        if (idx is null && _indexTokenized)
            idx = await userExternalIds.GetAsync(partitioner.PK(composite), Lookup, ct: ct).ConfigureAwait(false);
        return idx is null ? null : await GetAsync(idx.GetStr("userId"), ct).ConfigureAwait(false);
    }

    // ── create / update / delete ─────────────────────────────────────────────────

    public async Task CreateAsync(AuthUser user, CancellationToken ct = default)
    {
        await users.PutAsync(await UserRowAsync(user, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await LogUpsertAsync("Users", partitioner.PK(user.Id), Profile, ct).ConfigureAwait(false);
        await WriteProfileIndexesAsync(
            user.NormalizedEmail, Normalize(user.FirstName), Normalize(user.LastName), user.Id, dropLegacy: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write the current-scheme profile-derived index rows — email lookup, email-domain, local-part
    /// prefixes, and first/last name prefixes. Shared by <see cref="CreateAsync"/> and
    /// <see cref="ReindexUserAsync"/>; <paramref name="dropLegacy"/> (the reindex path) also removes
    /// the matching plaintext-keyed rows once tokenization is on. Email is written before any legacy
    /// drop, so there is no lookup gap.
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
        await userEmails.PutAsync(LookupRow(emailPk, userId), ct).ConfigureAwait(false);
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
        var existing = await users.GetAsync(partitioner.PK(user.Id), Profile, ct: ct).ConfigureAwait(false);
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

        await users.PutAsync(await UserRowAsync(user, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await LogUpsertAsync("Users", partitioner.PK(user.Id), Profile, ct).ConfigureAwait(false);

        // Every index key the changed fields need — old-side and new-side — in one tokenizer round
        // trip, computed before any index write (a tokenizer throw leaves every existing lookup row
        // intact). Write-before-delete per field.
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
            await userEmails.PutAsync(LookupRow(newPk, user.Id), ct).ConfigureAwait(false);
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
        var existing = await users.GetAsync(pk, Profile, ct: ct).ConfigureAwait(false);
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

        // Tombstone-first (F24e): a crash between delete and tombstone would drop the delete from every
        // backup, and a restore would resurrect the (possibly GDPR-erased) account.
        if (tombstones is not null) await tombstones.WriteAsync("Users", pk, Profile, ct).ConfigureAwait(false);
        await users.DeleteAsync(pk, Profile, ct).ConfigureAwait(false);
    }

    // ── login-state stamps (attribute-only; no document rewrite, no cipher round-trips) ──

    public async Task RecordSuccessfulLoginAsync(string userId, string? rehashedPassword = null, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var stamped = await users.UpdateAttrsAsync(pk, Profile, row =>
        {
            // A row missing the promoted group can only come from outside this store (a restore that
            // wrote raw rows). Stamping it partially would leave the overlay reading half-populated
            // attributes — and clearing the password hash with them — so bail out and rewrite the whole
            // profile below instead.
            if (!row.Has("failedCount")) return false;

            var now = DateTimeOffset.UtcNow;
            row.PutN("failedCount", 0);
            row.Attrs.Remove("lockoutEnd");
            row.PutDate("lastLogin", now);
            row.PutDate("updated", now);
            if (rehashedPassword is not null) row.PutS("pwd", rehashedPassword);
            return true;
        }, ct: ct).ConfigureAwait(false);

        if (stamped) return;

        var user = await GetAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return; // deleted between auth and stamp — nothing to record
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        if (rehashedPassword is not null) user.PasswordHash = rehashedPassword;
        await users.PutAsync(await UserRowAsync(user, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    public async Task<bool> RecordFailedLoginAsync(string userId, int maxAttempts, TimeSpan lockoutDuration, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var locked = false;
        var promoted = true;

        // Optimistic concurrency on the counter itself: a concurrent failed login that wrote first
        // fails the version check, and UpdateAttrsAsync re-reads and retries, so no increment is lost
        // and an attacker cannot exceed the threshold with parallel requests.
        var stamped = await users.UpdateAttrsAsync(pk, Profile, row =>
        {
            if (!row.Has("failedCount") || !row.Has("lockEnabled")) { promoted = false; return false; }

            var failed = (int)row.GetN("failedCount") + 1;
            var lockEnabled = row.GetBool("lockEnabled");
            locked = false;
            if (lockEnabled && failed >= maxAttempts)
            {
                row.PutDate("lockoutEnd", DateTimeOffset.UtcNow.Add(lockoutDuration));
                failed = 0;
                locked = true;
            }
            row.PutN("failedCount", failed);
            row.PutDate("updated", DateTimeOffset.UtcNow);
            return true;
        }, ct: ct).ConfigureAwait(false);

        if (promoted) return stamped && locked;

        // Rewrite the whole profile so the promoted group exists, then the fast path applies from here on.
        var user = await GetAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return false;

        user.AccessFailedCount++;
        locked = false;
        if (user.LockoutEnabled && user.AccessFailedCount >= maxAttempts)
        {
            user.LockoutEnd = DateTimeOffset.UtcNow.Add(lockoutDuration);
            user.AccessFailedCount = 0;
            locked = true;
        }
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await users.PutAsync(await UserRowAsync(user, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return locked;
    }

    // ── listing ──────────────────────────────────────────────────────────────────

    public Task<(IReadOnlyList<AuthUser> Users, bool HasMore)> ListAsync(
        string? organizationId, int startIndex, int count, CancellationToken ct = default)
        => PageProfilesAsync(
            SqlFilters.Config(partitioner, Profile), startIndex, count,
            u => organizationId is null || string.Equals(u.OrganizationId, organizationId, StringComparison.Ordinal), ct);

    public Task<(IReadOnlyList<AuthUser> Users, bool HasMore)> ListByScimClientAsync(
        string scimClientId, int startIndex, int count, CancellationToken ct = default)
        => PageProfilesAsync(
            SqlFilters.Config(partitioner, Profile).WithAttr("scimClient", scimClientId), startIndex, count, _ => true, ct);

    public Task<UserPage> ListPageAsync(string? organizationId, int count, string? continuationToken, CancellationToken ct = default)
        => ReadPageAsync(
            SqlFilters.Config(partitioner, Profile),
            u => organizationId is null || string.Equals(u.OrganizationId, organizationId, StringComparison.Ordinal),
            count, continuationToken, ct);

    public Task<UserPage> ListByScimClientPageAsync(string scimClientId, int count, string? continuationToken, CancellationToken ct = default)
        => ReadPageAsync(
            SqlFilters.Config(partitioner, Profile).WithAttr("scimClient", scimClientId), _ => true, count, continuationToken, ct);

    // Native-continuation cursor page: resumes the scan from the token's key, so page N costs one page
    // instead of re-walking (and re-decrypting) every skipped row — the exact O(N²) the offset
    // emulation had. The page cap bounds one call's work when a client-side filter matches almost
    // nothing.
    private async Task<UserPage> ReadPageAsync(
        SqlKeyFilter filter, Func<AuthUser, bool> keep, int count, string? continuationToken, CancellationToken ct)
    {
        var results = new List<AuthUser>();
        var token = continuationToken;
        var pagesConsumed = 0;

        while (true)
        {
            var (rows, next) = await users.ScanPageAsync(filter, token, Math.Max(count, 25), ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                AuthUser user;
                try
                {
                    user = await ReadUserAsync(row, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    continue; // one undecryptable/corrupt row must not fail the page
                }
                if (keep(user)) results.Add(user);
            }

            token = next;
            if (token is null) break;
            if (results.Count >= count || ++pagesConsumed >= 10) break;
        }

        return new UserPage(results, token);
    }

    private async Task<(IReadOnlyList<AuthUser> Users, bool HasMore)> PageProfilesAsync(
        SqlKeyFilter filter, int startIndex, int count, Func<AuthUser, bool> keep, CancellationToken ct)
    {
        var results = new List<AuthUser>();
        var skipped = 0;
        var start = Math.Max(0, startIndex);

        await foreach (var row in users.QueryAsync(filter, ct).ConfigureAwait(false))
        {
            var user = await ReadUserAsync(row, ct).ConfigureAwait(false);
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
        // Key-only projection — the document column is not selected, so no ciphertext is read and
        // nothing is decrypted, and the walk is O(N) rather than the offset re-scan's O(N²).
        var filter = SqlFilters.Config(partitioner, Profile) with { IncludeData = false };
        await foreach (var row in users.QueryAsync(filter, ct).ConfigureAwait(false))
            yield return partitioner.Strip(row.Pk);
    }

    public async IAsyncEnumerable<UserLoginState> EnumerateLoginStatesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var filter = SqlFilters.Config(partitioner, Profile) with { IncludeData = false };
        await foreach (var row in users.QueryAsync(filter, ct).ConfigureAwait(false))
        {
            var id = partitioner.Strip(row.Pk);
            if (row.Has("created"))
            {
                yield return new UserLoginState(id, row.GetDate("created"), row.GetDateOrNull("lastLogin"), row.GetBool("active"));
                continue;
            }
            // A row without the promoted group (an out-of-band restore) — fall back to the document.
            var user = await GetAsync(id, ct).ConfigureAwait(false);
            if (user is not null)
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

        // Tokenized: compute the (≤2) prefix-lookup tokens — email local-part and name — in one round
        // trip. HMAC keys are unordered, so email prefix search uses the local-part prefix index; with
        // tokenization off, the ordered prefix scan on the exact-email index works.
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
            : CollectAsync(userEmails.QueryAsync(
                new SqlKeyFilter { Sk = Lookup, PkPrefix = partitioner.PK(prefix) }, ct), maxResults, ct);

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
            await foreach (var row in userEmailDomains.QueryPartitionAsync(pk, ct).ConfigureAwait(false))
            {
                if (seen.Add(row.GetStr("userId"))) ids.Add(row.GetStr("userId"));
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

    // Collect userIds whose name starts with `prefix`. Tokenized: exact-match on the prefix token, plus
    // the plaintext range scan for migration-window rows. Off: the plaintext scheme only.
    private async Task<List<string>> SearchNameIndexAsync(SqlTable? table, string? tokenPk, string prefix, int maxResults, CancellationToken ct)
    {
        if (table is null || prefix.Length < NamePrefixMin) return [];

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (tokenPk is not null)
        {
            await foreach (var row in table.QueryPartitionAsync(tokenPk, ct).ConfigureAwait(false))
            {
                if (seen.Add(row.GetStr("userId"))) ids.Add(row.GetStr("userId"));
                if (ids.Count >= maxResults) break;
            }
            if (ids.Count >= maxResults) return ids;
        }

        // Plaintext scheme (the only scheme when tokenization is off): 2-char bucket partition,
        // sk = "{name}|{userId}", prefix via a sort-key range.
        var filter = SqlKeyFilter.Partition(LegacyNamePk(prefix)) with { SkPrefix = prefix };
        await foreach (var row in table.QueryAsync(filter, ct).ConfigureAwait(false))
        {
            if (seen.Add(row.GetStr("userId"))) ids.Add(row.GetStr("userId"));
            if (ids.Count >= maxResults) break;
        }
        return ids;
    }

    private async Task<List<string>> CollectPartitionUserIdsAsync(SqlTable? table, string? pk, int cap, CancellationToken ct)
    {
        if (table is null || pk is null) return [];
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var row in table.QueryPartitionAsync(pk, ct).ConfigureAwait(false))
        {
            if (seen.Add(row.GetStr("userId"))) ids.Add(row.GetStr("userId"));
            if (ids.Count >= cap) break;
        }
        return ids;
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<SqlRow> query, int cap, CancellationToken ct)
    {
        var ids = new List<string>();
        await foreach (var row in query.WithCancellation(ct).ConfigureAwait(false))
        {
            ids.Add(row.GetStr("userId"));
            if (ids.Count >= cap) break;
        }
        return ids;
    }

    // ── reindex + migrations (the cold-row encryption backfill surface) ─────────

    public async Task ReindexUserAsync(string userId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var existing = await users.GetAsync(pk, Profile, ct: ct).ConfigureAwait(false);
        if (existing is null) return;

        var user = await ReadUserAsync(existing, ct).ConfigureAwait(false);

        // 1. Re-write the profile under the current cipher (plaintext → ciphertext; idempotent).
        await users.PutAsync(await UserRowAsync(user, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await LogUpsertAsync("Users", pk, Profile, ct).ConfigureAwait(false);

        // 2. Rewrite the profile-derived indexes under the current keys, dropping the old rows.
        await WriteProfileIndexesAsync(
            user.NormalizedEmail, Normalize(user.FirstName), Normalize(user.LastName), userId, dropLegacy: true, ct).ConfigureAwait(false);
    }

    public async Task<int> MigrateExternalIdIndexAsync(bool dryRun, CancellationToken ct = default)
    {
        if (!_indexTokenized) return 0;

        var count = 0;
        await foreach (var row in userExternalIds.QueryAsync(SqlFilters.Config(partitioner, Lookup), ct).ConfigureAwait(false))
        {
            var composite = partitioner.Strip(row.Pk);
            if (!composite.Contains('|')) continue; // already a token (tokens are hex — never contain '|')
            count++;
            if (dryRun) continue;

            var tokenPk = partitioner.PK(await _tokenizer.TokenizeAsync(composite, ct).ConfigureAwait(false));
            if (string.Equals(tokenPk, row.Pk, StringComparison.Ordinal)) continue; // defensive
            await userExternalIds.PutAsync(LookupRow(tokenPk, row.GetStr("userId")), ct).ConfigureAwait(false);
            await LogUpsertAsync("UserExternalIds", tokenPk, Lookup, ct).ConfigureAwait(false);
            await TryDeleteRowAsync(userExternalIds, row.Pk, Lookup, "UserExternalIds", ct).ConfigureAwait(false);
        }
        return count;
    }

    public async Task<int> MigrateUserLoginsAsync(bool dryRun, CancellationToken ct = default)
    {
        if (!_indexTokenized) return 0;

        var count = 0;
        await foreach (var row in userLogins.QueryAsync(SqlFilters.Env(partitioner), ct).ConfigureAwait(false))
        {
            var sk = row.Sk;
            var isForward = sk == Lookup;
            var isReverse = sk.StartsWith(LoginPrefix, StringComparison.Ordinal);
            if (!isForward && !isReverse) continue;

            // Legacy iff the key still carries the plaintext composite (contains '|'); a token is hex.
            var legacy = isForward
                ? partitioner.Strip(row.Pk).Contains('|')
                : sk[LoginPrefix.Length..].Contains('|');
            if (!legacy) continue;
            count++;
            if (dryRun) continue;

            // Columns are plaintext on a legacy row — recompute the token and encrypt for the move.
            var login = new ExternalLoginInfo
            {
                UserId = row.GetStr("userId"),
                Provider = row.GetStr("provider"),
                ProviderKey = row.GetStr("providerKey"),
                DisplayName = row.GetS("displayName"),
            };
            var token = await _tokenizer.TokenizeAsync($"{login.Provider}|{login.ProviderKey}", ct).ConfigureAwait(false);
            var moved = await EncryptedLoginRowAsync(
                isForward ? partitioner.PK(token) : row.Pk,
                isForward ? Lookup : $"{LoginPrefix}{token}",
                login, ct).ConfigureAwait(false);
            await userLogins.PutAsync(moved, ct).ConfigureAwait(false);                                  // write new first
            await LogUpsertAsync("UserLogins", moved.Pk, moved.Sk, ct).ConfigureAwait(false);
            await TryDeleteRowAsync(userLogins, row.Pk, sk, "UserLogins", ct).ConfigureAwait(false);      // then drop legacy
        }
        return count;
    }

    // ── external ids ─────────────────────────────────────────────────────────────

    public async Task SetExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(await _tokenizer.TokenizeAsync($"{clientId}|{externalId}", ct).ConfigureAwait(false));
        await userExternalIds.PutAsync(LookupRow(pk, userId), ct).ConfigureAwait(false);
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
    // the seams are the Null defaults.

    public async Task AddLoginAsync(ExternalLoginInfo login, CancellationToken ct = default)
    {
        var token = await _tokenizer.TokenizeAsync($"{login.Provider}|{login.ProviderKey}", ct).ConfigureAwait(false);
        var forward = await EncryptedLoginRowAsync(partitioner.PK(token), Lookup, login, ct).ConfigureAwait(false);
        var reverse = new SqlRow(partitioner.PK(login.UserId), $"{LoginPrefix}{token}")
        {
            Data = forward.Data,
            Attrs = new Dictionary<string, string>(forward.Attrs),
        };
        await userLogins.PutAsync(forward, ct).ConfigureAwait(false);
        await userLogins.PutAsync(reverse, ct).ConfigureAwait(false);
        await LogUpsertBatchAsync("UserLogins", [(forward.Pk, forward.Sk), (reverse.Pk, reverse.Sk)], ct).ConfigureAwait(false);
    }

    public async Task RemoveLoginAsync(string userId, string provider, string providerKey, CancellationToken ct = default)
    {
        var token = await _tokenizer.TokenizeAsync($"{provider}|{providerKey}", ct).ConfigureAwait(false);
        var reversePk = partitioner.PK(userId);

        await TryDeleteRowAsync(userLogins, partitioner.PK(token), Lookup, "UserLogins", ct).ConfigureAwait(false);
        await TryDeleteRowAsync(userLogins, reversePk, $"{LoginPrefix}{token}", "UserLogins", ct).ConfigureAwait(false);
        if (_indexTokenized)
        {
            // Also drop any not-yet-migrated rows keyed on the plaintext composite.
            await TryDeleteRowAsync(userLogins, partitioner.PK($"{provider}|{providerKey}"), Lookup, "UserLogins", ct).ConfigureAwait(false);
            await TryDeleteRowAsync(userLogins, reversePk, $"{LoginPrefix}{provider}|{providerKey}", "UserLogins", ct).ConfigureAwait(false);
        }
    }

    public async Task<ExternalLoginInfo?> FindLoginAsync(string provider, string providerKey, CancellationToken ct = default)
    {
        var token = await _tokenizer.TokenizeAsync($"{provider}|{providerKey}", ct).ConfigureAwait(false);
        var row = await userLogins.GetAsync(partitioner.PK(token), Lookup, ct: ct).ConfigureAwait(false);
        if (row is null && _indexTokenized)
            row = await userLogins.GetAsync(partitioner.PK($"{provider}|{providerKey}"), Lookup, ct: ct).ConfigureAwait(false);
        return row is null ? null : await ReadLoginAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExternalLoginInfo>> GetLoginsAsync(string userId, CancellationToken ct = default)
    {
        var results = new List<ExternalLoginInfo>();
        var filter = SqlKeyFilter.Partition(partitioner.PK(userId)) with { SkPrefix = LoginPrefix };
        await foreach (var row in userLogins.QueryAsync(filter, ct).ConfigureAwait(false))
            results.Add(await ReadLoginAsync(row, ct).ConfigureAwait(false));
        return results;
    }

    // ── index-row helpers ────────────────────────────────────────────────────────

    private static SqlRow LookupRow(string pk, string userId)
    {
        var row = new SqlRow(pk, Lookup);
        row.PutS("userId", userId);
        return row;
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
        var row = new SqlRow(pk, userId);
        row.PutS("userId", userId);
        await userEmailDomains!.PutAsync(row, ct).ConfigureAwait(false);
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
        foreach (var token in tokens)
        {
            var row = new SqlRow(partitioner.PK(token), userId);
            row.PutS("userId", userId);
            await userEmailLocalPrefixes!.PutAsync(row, ct).ConfigureAwait(false);
        }
        await LogUpsertBatchAsync("UserEmailLocalPrefixes", tokens.Select(t => (partitioner.PK(t), userId)), ct).ConfigureAwait(false);
    }

    private async Task DeleteEmailLocalPrefixIndexAsync(IReadOnlyList<string> tokens, string userId, CancellationToken ct)
    {
        foreach (var token in tokens)
            await TryDeleteRowAsync(userEmailLocalPrefixes!, partitioner.PK(token), userId, "UserEmailLocalPrefixes", ct).ConfigureAwait(false);
    }

    // Tokenized: one row per name prefix, pk = HMAC(prefix), sk = userId — "starts with p" becomes an
    // exact partition lookup. Off: the single plaintext row (2-char bucket pk, sk = "{name}|{userId}").
    // The two shapes never collide, so both coexist during migration; search reads both.
    private async Task WriteNameIndexAsync(
        SqlTable table, IReadOnlyList<string>? tokens, string changeTable, string normalizedName, string userId, CancellationToken ct)
    {
        if (tokens is not null)
        {
            foreach (var token in tokens)
            {
                var tokenRow = new SqlRow(partitioner.PK(token), userId);
                tokenRow.PutS("userId", userId);
                await table.PutAsync(tokenRow, ct).ConfigureAwait(false);
            }
            await LogUpsertBatchAsync(changeTable, tokens.Select(t => (partitioner.PK(t), userId)), ct).ConfigureAwait(false);
            return;
        }

        var row = new SqlRow(LegacyNamePk(normalizedName), $"{normalizedName}|{userId}");
        row.PutS("userId", userId);
        await table.PutAsync(row, ct).ConfigureAwait(false);
        await LogUpsertAsync(changeTable, LegacyNamePk(normalizedName), $"{normalizedName}|{userId}", ct).ConfigureAwait(false);
    }

    private async Task DeleteNameIndexAsync(
        SqlTable table, IReadOnlyList<string>? tokens, string normalizedName, string userId, string tombstoneTable, CancellationToken ct)
    {
        if (tokens is not null)
        {
            foreach (var token in tokens)
                await TryDeleteRowAsync(table, partitioner.PK(token), userId, tombstoneTable, ct).ConfigureAwait(false);
            // Migration window: also remove any plaintext row for this name.
        }
        await TryDeleteRowAsync(table, LegacyNamePk(normalizedName), $"{normalizedName}|{userId}", tombstoneTable, ct).ConfigureAwait(false);
    }

    private string LegacyNamePk(string normalizedName)
        => partitioner.PK(normalizedName.Length >= NamePrefixMin ? normalizedName[..NamePrefixMin] : normalizedName);

    // Tombstone-first (F24e), then an unconditional delete (succeeds even if the row is already gone).
    private async Task TryDeleteRowAsync(SqlTable table, string pk, string sk, string tombstoneTable, CancellationToken ct)
    {
        if (tombstones is not null)
            await tombstones.WriteAsync(tombstoneTable, pk, sk, ct).ConfigureAwait(false);
        await table.DeleteAsync(pk, sk, ct).ConfigureAwait(false);
    }

    private async Task<SqlRow> EncryptedLoginRowAsync(string pk, string sk, ExternalLoginInfo login, CancellationToken ct)
    {
        // Encrypt providerKey (+ displayName) once per call; passthrough under the Null cipher.
        var toProtect = new List<string> { login.ProviderKey };
        if (!string.IsNullOrEmpty(login.DisplayName)) toProtect.Add(login.DisplayName);
        var ciphertexts = await _cipher.ProtectManyAsync(toProtect, ct).ConfigureAwait(false);

        var row = new SqlRow(pk, sk);
        row.PutS("userId", login.UserId);
        row.PutS("provider", login.Provider);
        row.PutS("providerKey", ciphertexts[0]);
        row.PutS("displayName", ciphertexts.Count > 1 ? ciphertexts[1] : login.DisplayName);
        return row;
    }

    private async Task<ExternalLoginInfo> ReadLoginAsync(SqlRow row, CancellationToken ct)
    {
        var providerKey = await _cipher.ResolveAsync(row.GetStr("providerKey"), ct).ConfigureAwait(false);
        var displayName = row.GetS("displayName");
        if (!string.IsNullOrEmpty(displayName))
            displayName = await _cipher.ResolveAsync(displayName, ct).ConfigureAwait(false);
        return new ExternalLoginInfo
        {
            UserId = row.GetStr("userId"),
            Provider = row.GetStr("provider"),
            ProviderKey = providerKey,
            DisplayName = displayName,
        };
    }
}
