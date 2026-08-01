using System.Runtime.CompilerServices;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;

namespace Authagonal.AzureProvider.Stores;

// Phase B2 (sandbox env isolation): every PartitionKey value passed to
// GetEntity/UpsertEntity/DeleteEntity/QueryAsync filter and every entity
// PartitionKey assignment must be wrapped with _partitioner.PK(...).
// Live is a no-op; sandbox envs prefix with "{env}|".
public sealed class TableUserStore(
    TableClient usersTable,
    TableClient userEmailsTable,
    TableClient userLoginsTable,
    TableClient userExternalIdsTable,
    TableClient? userFirstNamesTable,
    TableClient? userLastNamesTable,
    EnvPartitioner partitioner,
    IChangeWriter? tombstoneWriter = null,
    IFieldCipher? fieldCipher = null,
    IIndexTokenizer? indexTokenizer = null,
    TableClient? userEmailDomainsTable = null,
    TableClient? userEmailLocalPrefixesTable = null,
    TableClient? userRolesTable = null) : IUserStore
{
    private readonly EnvPartitioner _partitioner = partitioner; // Phase B2 will wrap PartitionKeys with _partitioner.PK

    /// <summary>
    /// How many times a full-document write retries a row that keeps moving under it.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. Every attempt costs a read, a decrypt and an encrypt, and the writer it
    /// loses to is usually a login stamp — which an attacker in the reported password-reset race is
    /// free to generate in a tight loop. Giving up too early would hand them a way to fail an
    /// administrative write instead of merely reverting it, which is not an improvement. Each pass
    /// re-checks the caller's revision, so a long budget never lets a genuinely stale write through.
    /// </remarks>
    private const int ContendedWriteAttempts = 25;
    // Name-index tables are optional. When null (Storage:NameIndexesEnabled=false),
    // CreateAsync/UpdateAsync/DeleteAsync skip the index writes entirely and
    // SearchAsync degrades from "email + name prefix" to "email prefix only".
    // Index rows are partitioned by the (tokenized) search key — one PK per name
    // prefix, per email-local prefix, and per email domain (further bucketed by
    // userId) — so writes spread across partitions rather than one hot "all". The
    // cost is write fan-out: one row per prefix of each indexed field.

    // At-rest PII encryption (opt-in). When null, values are stored plaintext (single-tenant /
    // unconfigured hosts, and every existing deployment). When supplied (Cloud, per-tenant enc-{prefix}
    // key), the PII fields are encrypted at the entity level: on write AFTER FromModel(), on read BEFORE
    // ToModel() — CustomAttributesJson must be plaintext before ToModel() deserializes it. Email and names
    // are encrypted too (so the profile row itself leaks nothing); the blind INDEXES keep them searchable.
    // Anything that reads an entity's email/name for index-key computation (Update/Delete) must decrypt
    // it first — the model's plaintext values drive index keys, never the stored ciphertext.
    private readonly IFieldCipher _cipher = fieldCipher ?? NullFieldCipher.Instance;

    // Blind-index tokenization (opt-in). When null, index rows stay keyed on plaintext (current behavior).
    // When supplied (Cloud, per-tenant idx-{prefix} HMAC key), lookup-index PartitionKeys become HMAC
    // tokens so a dump exposes no addresses/ids. _tokenizer is passthrough when off, so the WRITE path is
    // uniform; _indexTokenized gates the migration-window read fallback (tokenized miss → legacy plaintext).
    private readonly IIndexTokenizer _tokenizer = indexTokenizer ?? NullIndexTokenizer.Instance;
    private readonly bool _indexTokenized = indexTokenizer is not null;

    // Collects every blind-index token one store operation needs (the email lookup key, domain key, and
    // the name / email-local-part prefix sets) and computes them in a single TokenizeBatchAsync call —
    // one Vault HMAC round-trip in Cloud — instead of one call per index. This batches token COMPUTATION
    // only: callers keep their existing table write/delete ordering, so the write-before-delete
    // guarantees are untouched. Usage: Add/AddRange everything up front, await RunAsync once, then read
    // tokens through the returned accessors.
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
            => _tokens = _values.Count == 0 ? [] : await tokenizer.TokenizeBatchAsync(_values, ct);
    }

    // Reserve the local-part prefix tokens for an email's local-prefix index write/delete; null when the
    // index doesn't apply (table off, tokenization off, or no local part).
    private Func<IReadOnlyList<string>>? ReserveLocalPrefixTokens(TokenBatch batch, string? normalizedEmail)
    {
        if (userEmailLocalPrefixesTable is null || !_indexTokenized) return null;
        var local = LocalPartOf(normalizedEmail);
        return local is null ? null : batch.AddRange(NamePrefixesOf(local));
    }

    // Reserve the prefix tokens for a name index write/delete; null when tokens aren't needed (no table,
    // no name, or tokenization off — the legacy scheme derives its keys from the plaintext name).
    private Func<IReadOnlyList<string>>? ReserveNameTokens(TokenBatch batch, TableClient? table, string? normalizedName)
        => table is not null && normalizedName is not null && _indexTokenized
            ? batch.AddRange(NamePrefixesOf(normalizedName))
            : null;

    // Change-log capture for incremental backups: the upsert-side mirror of the tombstone (delete) writes,
    // for the backed-up Users-family tables (Users, UserEmails, UserFirstNames, UserLastNames, UserLogins,
    // UserExternalIds). No-op when no writer is wired. Login-state-only updates (RecordSuccessful/
    // FailedLoginAsync) are deliberately NOT logged: they are the hot path and carry low-value fields
    // (LastLoginAt, lockout counters), so their backup recency rides on the periodic full-scan backstop.
    private Task LogUpsertAsync(string changeTable, string pk, string rk, CancellationToken ct)
        => tombstoneWriter?.WriteUpsertAsync(changeTable, pk, rk, ct) ?? Task.CompletedTask;

    private Task LogUpsertBatchAsync(string changeTable, IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct)
        => tombstoneWriter?.WriteUpsertBatchAsync(changeTable, keys, ct) ?? Task.CompletedTask;

    private static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.Trim().ToUpperInvariant();
    }

    // Env-wrapped PartitionKey for the email lookup index: HMAC token when tokenization is on, else the
    // plaintext normalized email (identical to the historical key). Tokenize BEFORE the env prefix so the
    // sandbox partitioner still isolates envs.
    private async Task<string> EmailIndexPkAsync(string normalizedEmail, CancellationToken ct)
        => _partitioner.PK(await _tokenizer.TokenizeAsync(normalizedEmail, ct));

    private async Task<string?> TryGetEmailIndexUserIdAsync(string pk, CancellationToken ct)
    {
        try
        {
            var e = await userEmailsTable.GetEntityAsync<UserEmailEntity>(pk, UserEmailEntity.LookupRowKey, cancellationToken: ct);
            return e.Value.UserId;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    // Delete a normalized email's lookup row (its token precomputed by the caller's TokenBatch). Removes
    // the tokenized key and, while tokenization is on, also the legacy plaintext key (a row written before
    // backfill), so an email change/delete can't orphan either.
    private async Task DeleteEmailIndexAsync(string normalizedEmail, string emailToken, CancellationToken ct)
    {
        var tokenPk = _partitioner.PK(emailToken);
        await TryDeleteEmailIndexAsync(tokenPk, ct);
        if (_indexTokenized)
        {
            var plainPk = _partitioner.PK(normalizedEmail);
            if (!string.Equals(plainPk, tokenPk, StringComparison.Ordinal))
                await TryDeleteEmailIndexAsync(plainPk, ct);
        }
    }

    private async Task TryDeleteEmailIndexAsync(string pk, CancellationToken ct)
    {
        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteAsync("UserEmails", pk, UserEmailEntity.LookupRowKey, ct);
        try
        {
            await userEmailsTable.DeleteEntityAsync(pk, UserEmailEntity.LookupRowKey, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    // externalId lookup index — same shape as email: tokenize the "{clientId}|{externalId}" composite so
    // the key exposes no external identifier, with a migration-window plaintext fallback on read.
    private async Task<string> ExternalIdIndexPkAsync(string clientId, string externalId, CancellationToken ct)
        => _partitioner.PK(await _tokenizer.TokenizeAsync($"{clientId}|{externalId}", ct));

    private async Task<string?> TryGetExternalIdUserIdAsync(string pk, CancellationToken ct)
    {
        try
        {
            var e = await userExternalIdsTable.GetEntityAsync<UserExternalIdEntity>(pk, UserExternalIdEntity.LookupRowKey, cancellationToken: ct);
            return e.Value.UserId;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    // Removes the lookup row only when it still names <paramref name="ownerUserId"/>. It used to delete
    // by key alone, so once an externalId had been repointed at another user, the ORIGINAL holder's next
    // externalId change deleted the row that now belonged to the new one — and the connector's
    // deprovisioning lookup for that user then returned nothing, silently, forever.
    //
    // Note the ordering is delete-then-tombstone here, the reverse of the unconditional deletes: a
    // tombstone written for a row we turn out not to own would delete a live index row on restore.
    private async Task TryDeleteExternalIdAsync(string pk, string ownerUserId, CancellationToken ct)
    {
        UserExternalIdEntity existing;
        try
        {
            existing = (await userExternalIdsTable.GetEntityAsync<UserExternalIdEntity>(
                pk, UserExternalIdEntity.LookupRowKey, cancellationToken: ct)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return;
        }

        if (!string.Equals(existing.UserId, ownerUserId, StringComparison.Ordinal)) return;

        try
        {
            // If-Match, so a repoint that lands between the read above and this delete wins rather than
            // being erased by a caller that no longer owns the row.
            await userExternalIdsTable.DeleteEntityAsync(pk, UserExternalIdEntity.LookupRowKey, existing.ETag, ct);
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return;
        }

        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteAsync("UserExternalIds", pk, UserExternalIdEntity.LookupRowKey, ct);
    }

    // Email-domain index ("all users @X"). Optional (null table → feature off). PartitionKey = tokenized
    // domain, RowKey = userId. Unlike email/externalId this is a NEW index, so there are no pre-existing
    // plaintext rows to migrate — but if a tenant is enabled after some rows were written plaintext, the
    // search dual-reads and the backfill rewrites, same as the other indexes.
    private async Task<string> DomainIndexPkAsync(string domain, CancellationToken ct)
        => _partitioner.PK(await _tokenizer.TokenizeAsync(domain, ct));

    // A domain's members are bucketed across DomainBuckets partitions so a big single-domain tenant
    // (e.g. a 50k-user @acme.com import) doesn't funnel every index write into one partition and hit its
    // ~2000 ops/s cap. The read (SearchByEmailDomain) fans out over the buckets; it's bounded by
    // maxResults and rarely called, so the fan-out is cheap. Bucket = stable FNV-1a of the userId
    // (string.GetHashCode is NOT stable across processes, which would strand rows on delete).
    private const int DomainBuckets = 16;

    private static int DomainBucketOf(string userId)
    {
        uint h = 2166136261u;
        foreach (var ch in userId) { h ^= ch; h *= 16777619u; }
        return (int)(h % (uint)DomainBuckets);
    }

    private static string Bucketed(string basePk, string userId) => $"{basePk}-{DomainBucketOf(userId):x}";

    // Email local-part prefix index (tokenized only). Each prefix of the local part (before '@') is an
    // HMAC-token row, so "email starts with X" is an exact lookup over encrypted emails — same trick as
    // the name index. With tokenization off, email prefix search uses the ordered range scan on the exact
    // email index, so this index is neither written nor read.
    private static string? LocalPartOf(string? normalizedEmail)
    {
        if (string.IsNullOrEmpty(normalizedEmail)) return null;
        var at = normalizedEmail.IndexOf('@');
        var local = at > 0 ? normalizedEmail[..at] : normalizedEmail;
        return string.IsNullOrEmpty(local) ? null : local;
    }

    // Local-prefix index write/delete over tokens reserved via ReserveLocalPrefixTokens (whose null
    // result encodes the "table off / tokenization off / no local part" gates — callers skip on null).
    private async Task WriteEmailLocalPrefixIndexAsync(IReadOnlyList<string> tokens, string userId, CancellationToken ct)
    {
        await Task.WhenAll(tokens.Select(token =>
            userEmailLocalPrefixesTable!.UpsertEntityAsync(
                new TableEntity(_partitioner.PK(token), userId) { ["UserId"] = userId }, TableUpdateMode.Replace, ct)));
        await LogUpsertBatchAsync("UserEmailLocalPrefixes", tokens.Select(t => (_partitioner.PK(t), userId)), ct);
    }

    private async Task DeleteEmailLocalPrefixIndexAsync(IReadOnlyList<string> tokens, string userId, CancellationToken ct)
    {
        foreach (var token in tokens)
            await TryDeleteRowAsync(userEmailLocalPrefixesTable!, _partitioner.PK(token), userId, "UserEmailLocalPrefixes", ct);
    }

    // pk is the env-wrapped token PartitionKey precomputed by SearchAsync's batch; null encodes every
    // "index not applicable" gate (table off, tokenization off, or lookup shorter than NamePrefixMin).
    private async Task<List<string>> SearchEmailLocalPrefixAsync(string? pk, int maxResults, CancellationToken ct)
    {
        if (pk is null) return [];
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var e in userEmailLocalPrefixesTable!.QueryAsync<UserEmailLocalPrefixEntity>(
            e => e.PartitionKey == pk, cancellationToken: ct).WithCancellation(ct))
        {
            if (seen.Add(e.UserId)) ids.Add(e.UserId);
            if (ids.Count >= maxResults) break;
        }
        return ids;
    }

    // Domain-index write/delete over a token reserved by the caller's TokenBatch (reserved only when the
    // domain table is on and the email has a domain — callers skip on a null reservation).
    private async Task WriteDomainIndexAsync(string domainToken, string userId, CancellationToken ct)
    {
        var pk = Bucketed(_partitioner.PK(domainToken), userId);
        var entity = new UserEmailDomainEntity { PartitionKey = pk, RowKey = userId, UserId = userId };
        await userEmailDomainsTable!.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        await LogUpsertAsync("UserEmailDomains", pk, userId, ct);
    }

    private async Task DeleteDomainIndexAsync(string domain, string domainToken, string userId, CancellationToken ct)
    {
        var tokenPk = _partitioner.PK(domainToken);
        await TryDeleteDomainAsync(Bucketed(tokenPk, userId), userId, ct); // current: bucketed
        await TryDeleteDomainAsync(tokenPk, userId, ct);                   // legacy: unbucketed (pre-bucketing rows)
        if (_indexTokenized)
        {
            var plainPk = _partitioner.PK(domain);
            if (!string.Equals(plainPk, tokenPk, StringComparison.Ordinal))
                await TryDeleteDomainAsync(plainPk, userId, ct);           // legacy: plaintext (pre-tokenization rows)
        }
    }

    private async Task TryDeleteDomainAsync(string pk, string userId, CancellationToken ct)
    {
        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteAsync("UserEmailDomains", pk, userId, ct);
        try
        {
            await userEmailDomainsTable!.DeleteEntityAsync(pk, userId, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    // ── Name prefix index (first/last) ──────────────────────────────────────────
    // A keyed HMAC destroys ordering, so the legacy scheme (2-char PartitionKey + RowKey range scan) can't
    // work once names are tokenized. Tokenized instead indexes each prefix of a name as its own token row
    // (PK = HMAC(prefix), RowKey = userId): "starts with p" becomes an exact-match lookup on HMAC(p). All of
    // a name's prefixes are HMAC'd in one batch. When tokenization is OFF the legacy scheme is used unchanged.
    // GetPartitionKey/MakeRowKey are identical between the first/last entities, so UserFirstNameEntity's serve
    // both tables. Legacy and tokenized rows never collide (different PartitionKey shape), so both coexist
    // during migration; search reads both.
    private const int NamePrefixMin = 2;   // == legacy UserFirstNameEntity.PartitionKeyLength — the min searchable length
    private const int NamePrefixMax = 16;

    /// <summary>
    /// Every indexed value writes exactly this many prefix rows, short ones included.
    /// </summary>
    /// <remarks>
    /// The count used to be (length - 1), capped — so a dump leaked the length of every name and email
    /// local-part without breaking a single token. Padding to a constant removes that channel; the
    /// extra rows are decoys that no query can produce and that a dump cannot tell from real ones.
    /// The cost is a fixed write fan-out per indexed field rather than one proportional to length.
    /// </remarks>
    private const int NamePrefixCount = NamePrefixMax - NamePrefixMin + 1;

    private static IReadOnlyList<string> PadToFixedCount(IReadOnlyList<string> prefixes, string value)
        => Authagonal.Core.Services.BlindIndexPadding.Pad(prefixes, value, NamePrefixCount);  // cap on indexed prefix length: bounds rows/name; a longer query matches the first 16 chars

    /// <remarks>
    /// Sliced on rune boundaries, not UTF-16 code units — see <see cref="TextPrefix"/>. A lone
    /// surrogate is not a legal Table Storage PartitionKey, so a first/last name holding any non-BMP
    /// character used to fail its index write outright here.
    /// </remarks>
    private static IReadOnlyList<string> NamePrefixesOf(string normalizedName)
    {
        var boundaries = TextPrefix.Boundaries(normalizedName);
        if (boundaries.Count < NamePrefixMin) return PadToFixedCount([normalizedName], normalizedName);
        var hi = Math.Min(boundaries.Count, NamePrefixMax);
        var prefixes = new List<string>(NamePrefixCount);
        for (var runes = NamePrefixMin; runes <= hi; runes++)
            prefixes.Add(normalizedName[..boundaries[runes - 1]]);
        return PadToFixedCount(prefixes, normalizedName);
    }

    // Name-index write over prefix tokens reserved via ReserveNameTokens: non-null exactly when
    // tokenization is on (a null token list selects the legacy plaintext scheme, matching the old
    // _indexTokenized branch, the reservation helper encodes that gate). changeTable names the logical
    // table ("UserFirstNames"/"UserLastNames") whose upserts are captured to the change-log.
    private async Task WriteNameIndexAsync(TableClient table, IReadOnlyList<string>? tokens, string changeTable, string normalizedName, string userId, CancellationToken ct)
    {
        if (tokens is not null)
        {
            // Up to ~15 prefix rows per name. The upserts are independent (distinct PartitionKeys, no
            // shared state), so fire them concurrently instead of one blocking round-trip each — this is
            // on every create/update and every backfilled user's reindex.
            await Task.WhenAll(tokens.Select(token =>
                table.UpsertEntityAsync(new TableEntity(_partitioner.PK(token), userId) { ["UserId"] = userId }, TableUpdateMode.Replace, ct)));
            await LogUpsertBatchAsync(changeTable, tokens.Select(t => (_partitioner.PK(t), userId)), ct);
            return;
        }
        var pk = _partitioner.PK(UserFirstNameEntity.GetPartitionKey(normalizedName));
        var rk = UserFirstNameEntity.MakeRowKey(normalizedName, userId);
        await table.UpsertEntityAsync(new TableEntity(pk, rk) { ["UserId"] = userId }, TableUpdateMode.Replace, ct);
        await LogUpsertAsync(changeTable, pk, rk, ct);
    }

    private async Task DeleteNameIndexAsync(TableClient table, IReadOnlyList<string>? tokens, string normalizedName, string userId, string tombstoneTable, CancellationToken ct)
    {
        if (tokens is not null)
        {
            foreach (var token in tokens)
                await TryDeleteRowAsync(table, _partitioner.PK(token), userId, tombstoneTable, ct);
            // Migration window: also remove any legacy row for this name (single, old-scheme row).
        }
        var legacyPk = _partitioner.PK(UserFirstNameEntity.GetPartitionKey(normalizedName));
        await TryDeleteRowAsync(table, legacyPk, UserFirstNameEntity.MakeRowKey(normalizedName, userId), tombstoneTable, ct);
    }

    /// <summary>
    /// Bring the role membership index in line with <paramref name="roles"/> for one user.
    /// </summary>
    /// <remarks>
    /// Writes before deleting, like every other index here: a throw between the two must never leave a
    /// role looking empty when its members still hold it. Callers pass the roles the user had before
    /// (null on create) so the diff only touches what actually changed — granting one role does not
    /// rewrite the rows for the five the person already had.
    /// </remarks>
    private async Task SyncRoleIndexAsync(
        IReadOnlyList<string>? previousRoles, IReadOnlyList<string>? roles, string userId, CancellationToken ct)
    {
        if (userRolesTable is null) return;

        var wanted = new HashSet<string>(roles ?? [], StringComparer.OrdinalIgnoreCase);
        var had = new HashSet<string>(previousRoles ?? [], StringComparer.OrdinalIgnoreCase);

        foreach (var role in roles ?? [])
        {
            if (had.Contains(role)) continue;
            await WriteRoleIndexAsync(role, userId, ct);
        }

        foreach (var role in previousRoles ?? [])
        {
            if (wanted.Contains(role)) continue;
            await DeleteRoleIndexAsync(role, userId, ct);
        }
    }

    private async Task WriteRoleIndexAsync(string role, string userId, CancellationToken ct)
    {
        if (userRolesTable is null || string.IsNullOrWhiteSpace(role)) return;

        var entity = UserRoleEntity.Create(role, userId);
        entity.PartitionKey = _partitioner.PK(entity.PartitionKey);
        await userRolesTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        await LogUpsertAsync("UserRoles", entity.PartitionKey, entity.RowKey, ct);
    }

    private Task DeleteRoleIndexAsync(string role, string userId, CancellationToken ct)
    {
        if (userRolesTable is null || string.IsNullOrWhiteSpace(role)) return Task.CompletedTask;

        return TryDeleteRowAsync(
            userRolesTable, _partitioner.PK(UserRoleEntity.Normalize(role)), userId, "UserRoles", ct);
    }

    private async Task TryDeleteRowAsync(TableClient table, string pk, string rk, string tombstoneTable, CancellationToken ct)
    {
        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteAsync(tombstoneTable, pk, rk, ct);
        try
        {
            await table.DeleteEntityAsync(pk, rk, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    // Collect userIds whose name (in the given first/last table) starts with `prefix`. Off: legacy range
    // scan. Tokenized: exact-match on the prefix token (capped at NamePrefixMax), plus the legacy range scan
    // for migration-window rows not yet backfilled. Queries shorter than NamePrefixMin don't hit the index.
    // tokenPk is the env-wrapped prefix-token PartitionKey precomputed by SearchAsync's batch (null when
    // tokenization is off — the same set of inputs that gated the old _indexTokenized branch).
    private async Task<List<string>> SearchNameIndexAsync(TableClient? table, string? tokenPk, string prefix, string prefixEnd, int maxResults, CancellationToken ct)
    {
        if (table is null || TextPrefix.RuneCount(prefix) < NamePrefixMin) return [];

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        async Task CollectAsync(Azure.AsyncPageable<UserFirstNameEntity> query)
        {
            await foreach (var e in query.WithCancellation(ct))
            {
                if (seen.Add(e.UserId)) ids.Add(e.UserId);
                if (ids.Count >= maxResults) break;
            }
        }

        if (tokenPk is not null)
        {
            await CollectAsync(table.QueryAsync<UserFirstNameEntity>(e => e.PartitionKey == tokenPk, cancellationToken: ct));
            if (ids.Count >= maxResults) return ids;
        }

        var legacyPk = _partitioner.PK(UserFirstNameEntity.GetPartitionKey(prefix));
        await CollectAsync(table.QueryAsync<UserFirstNameEntity>(
            e => e.PartitionKey == legacyPk && e.RowKey.CompareTo(prefix) >= 0 && e.RowKey.CompareTo(prefixEnd) < 0,
            cancellationToken: ct));
        return ids;
    }

    // "{}" is an empty attribute map and reveals nothing, so it stays plaintext — this also
    // spares a Vault round-trip on the common no-custom-attributes case.
    // A field is worth encrypting if it carries content — empty values and the "{}" attrs default pass
    // through untouched (and ResolveMany passes legacy plaintext through, so reads over un-migrated rows
    // still work).
    private static bool ShouldProtect(string? value)
        => !string.IsNullOrEmpty(value) && value != "{}";

    // Encrypt the at-rest PII fields on a freshly-mapped entity, just before a table write. Email and
    // names are encrypted alongside phone/company/attrs; the blind indexes (keyed on the plaintext, via
    // the tokenizer) are what keep them findable. Email/NormalizedEmail are required (non-empty) so they
    // always encrypt; the `?? e.X` guards only the theoretical empty case.
    private async Task EncryptEntityAsync(UserEntity e, CancellationToken ct)
    {
        // Batch the fields that need protection into ONE Vault round-trip (vs 7 sequential). Fields that
        // don't need protecting (empty, or the "{}" attrs default) are left untouched, preserving the
        // exact per-field semantics of the old ProtectFieldAsync path.
        var fields = new[] { e.Email, e.NormalizedEmail, e.FirstName, e.LastName, e.Phone, e.CompanyName, e.CustomAttributesJson, e.PendingClaimJson };
        var idx = new List<int>(fields.Length);
        var toProtect = new List<string>(fields.Length);
        for (var i = 0; i < fields.Length; i++)
            if (ShouldProtect(fields[i])) { idx.Add(i); toProtect.Add(fields[i]!); }

        if (toProtect.Count > 0)
        {
            var ciphertexts = await _cipher.ProtectManyAsync(toProtect, ct);
            for (var j = 0; j < idx.Count; j++) fields[idx[j]] = ciphertexts[j];
        }

        e.Email = fields[0] ?? e.Email;
        e.NormalizedEmail = fields[1] ?? e.NormalizedEmail;
        e.FirstName = fields[2];
        e.LastName = fields[3];
        e.Phone = fields[4];
        e.CompanyName = fields[5];
        e.CustomAttributesJson = fields[6] ?? "{}";
        // PendingClaimJson serialises the SAME fields the columns above protect — first name, last
        // name and the caller-supplied custom attributes — staged for a not-yet-confirmed sign-up. It
        // was left out of the list, so a table dump exposed in cleartext exactly the PII the scheme
        // exists to hide, for every user mid-registration. The AWS and SQL stores do not have this gap
        // because they encrypt the whole serialized document, which is also why the shared
        // provider-parity tests could never have caught it.
        e.PendingClaimJson = fields[7];
    }

    // Decrypt the at-rest PII fields on an entity read from the table, before ToModel() (or before its
    // email/name is used for index-key computation). One batch round-trip; empties/legacy plaintext pass
    // through untouched (ResolveManyAsync handles per-item passthrough).
    private async Task DecryptEntityAsync(UserEntity e, CancellationToken ct)
    {
        var fields = new[] { e.Email, e.NormalizedEmail, e.FirstName, e.LastName, e.Phone, e.CompanyName, e.CustomAttributesJson, e.PendingClaimJson };
        var idx = new List<int>(fields.Length);
        var toResolve = new List<string>(fields.Length);
        for (var i = 0; i < fields.Length; i++)
            if (!string.IsNullOrEmpty(fields[i])) { idx.Add(i); toResolve.Add(fields[i]!); }

        if (toResolve.Count > 0)
        {
            var resolved = await _cipher.ResolveManyAsync(toResolve, ct);
            for (var j = 0; j < idx.Count; j++) fields[idx[j]] = resolved[j];
        }

        e.Email = fields[0] ?? e.Email;
        e.NormalizedEmail = fields[1] ?? e.NormalizedEmail;
        e.FirstName = fields[2];
        e.LastName = fields[3];
        e.Phone = fields[4];
        e.CompanyName = fields[5];
        e.CustomAttributesJson = fields[6] ?? "{}";
        // PendingClaimJson serialises the SAME fields the columns above protect — first name, last
        // name and the caller-supplied custom attributes — staged for a not-yet-confirmed sign-up. It
        // was left out of the list, so a table dump exposed in cleartext exactly the PII the scheme
        // exists to hide, for every user mid-registration. The AWS and SQL stores do not have this gap
        // because they encrypt the whole serialized document, which is also why the shared
        // provider-parity tests could never have caught it.
        e.PendingClaimJson = fields[7];
    }

    /// <summary>
    /// Stamp login state (lockout reset, last-login time, optional password rehash) WITHOUT touching the
    /// encrypted PII columns. The entity is read raw and written back with Replace, so the ciphertext
    /// fields round-trip verbatim — zero Vault round-trips, versus the ~14 (decrypt 7 + re-encrypt 7) a
    /// full <see cref="UpdateAsync"/> would spend just to write a timestamp on every login. Email/name are
    /// unchanged, so the blind indexes need no update either.
    /// </summary>
    /// <remarks>
    /// The write is ETag-conditional with retry, mirroring <see cref="RecordFailedLoginAsync"/>. It used to
    /// be an unconditional full-entity Replace, and "last-writer-wins is fine for login-state columns" was
    /// wrong: Replace writes back EVERY column that was read, so any administrative write landing between
    /// the read above and the write below was silently reverted — including <c>IsActive</c> (undoing a SCIM
    /// deprovision), <c>MfaEnabled</c> (undoing an enrolment, which login gates on), <c>RolesJson</c>
    /// (undoing a role revocation), <c>PasswordHash</c> and <c>SecurityStamp</c>. An attacker who keeps
    /// authenticating controls one side of that race, so it was not a narrow window.
    /// </remarks>
    public async Task RecordSuccessfulLoginAsync(string userId, string? rehashedPassword = null, CancellationToken ct = default)
    {
        var pk = _partitioner.PK(userId);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            UserEntity e;
            try
            {
                e = (await usersTable.GetEntityAsync<UserEntity>(
                    pk, UserEntity.ProfileRowKey, cancellationToken: ct)).Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return; // User deleted between auth and stamp — nothing to record.
            }

            var now = DateTimeOffset.UtcNow;
            e.AccessFailedCount = 0;
            e.LockoutEnd = null;
            e.LastLoginAt = now;
            e.UpdatedAt = now;
            if (rehashedPassword is not null) e.PasswordHash = rehashedPassword;

            try
            {
                // Replace (not Merge) so LockoutEnd is actually cleared rather than left in place, and
                // If-Match so a concurrent administrative write turns into a 412 we re-read instead of
                // overwriting.
                await usersTable.UpdateEntityAsync(e, e.ETag, TableUpdateMode.Replace, ct);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Lost the race — re-read the fresh entity and re-apply the login-state columns on top.
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return; // Deleted underneath us mid-retry.
            }
        }

        // Sustained contention. Dropping the stamp loses a LastLoginAt and defers a lockout-counter reset
        // to the next successful login; silently reverting an admin's write would be far worse.
    }

    public async Task<AuthUser?> GetAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            var entity = (await usersTable.GetEntityAsync<UserEntity>(
                _partitioner.PK(userId), UserEntity.ProfileRowKey, cancellationToken: ct)).Value;
            await DecryptEntityAsync(entity, ct);
            var user = entity.ToModel();
            user.Id = _partitioner.Strip(user.Id);
            return user;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> RecordFailedLoginAsync(string userId, int maxAttempts, TimeSpan lockoutDuration, CancellationToken ct = default)
    {
        var pk = _partitioner.PK(userId);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            UserEntity entity;
            try
            {
                entity = (await usersTable.GetEntityAsync<UserEntity>(pk, UserEntity.ProfileRowKey, cancellationToken: ct)).Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }

            entity.AccessFailedCount++;
            var locked = false;
            if (entity.LockoutEnabled && entity.AccessFailedCount >= maxAttempts)
            {
                entity.LockoutEnd = DateTimeOffset.UtcNow.Add(lockoutDuration);
                entity.AccessFailedCount = 0;
                locked = true;
            }
            entity.UpdatedAt = DateTimeOffset.UtcNow;

            try
            {
                // If-Match on the ETag: a concurrent failed login that wrote first yields 412 — re-read
                // and retry so no increment is lost (closes the parallel-attempts lockout bypass).
                await usersTable.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge, ct);
                return locked;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Lost the race — loop to re-read the fresh count and retry.
            }
        }

        return false; // sustained contention; a subsequent attempt will still lock the account
    }

    public async Task<AuthUser?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalizedEmail = email.ToUpperInvariant();
        // Blind-index lookup: point-read the tokenized key; during migration fall back to the legacy
        // plaintext key for a row not yet backfilled (only meaningful while tokenization is on).
        var userId = await TryGetEmailIndexUserIdAsync(await EmailIndexPkAsync(normalizedEmail, ct), ct);
        if (userId is null && _indexTokenized)
            userId = await TryGetEmailIndexUserIdAsync(_partitioner.PK(normalizedEmail), ct);
        return userId is null ? null : await GetAsync(userId, ct);
    }

    public async Task CreateAsync(AuthUser user, CancellationToken ct = default)
    {
        var userEntity = UserEntity.FromModel(user);
        userEntity.PartitionKey = _partitioner.PK(userEntity.PartitionKey);
        await EncryptEntityAsync(userEntity, ct);

        await usersTable.AddEntityAsync(userEntity, ct);
        // Hand the caller the revision it now holds, so a create-then-update chain (registration, JIT
        // federation) writes guarded instead of falling back to an unguarded write.
        user.ConcurrencyToken = UserRevision.Of(user);
        await LogUpsertAsync("Users", userEntity.PartitionKey, userEntity.RowKey, ct);

        // The profile row is durable from here. If index writing then fails, the user exists but
        // cannot be found by email — invisible to FindByEmailAsync and so to every duplicate check,
        // yet still occupying an id and still listed. That is the split-brain state the email-index
        // hardening elsewhere exists to prevent, reachable by any input the storage service rejects
        // as a key. Undo the profile row rather than leave a record nothing can reach.
        try
        {
            await WriteProfileIndexesAsync(user.NormalizedEmail, Normalize(user.FirstName), Normalize(user.LastName), user.Id, dropLegacy: false, ct);
            await SyncRoleIndexAsync(previousRoles: null, user.Roles, user.Id, ct);
        }
        catch
        {
            try
            {
                await usersTable.DeleteEntityAsync(userEntity.PartitionKey, userEntity.RowKey, cancellationToken: ct);
            }
            catch
            {
                // Best effort. The original failure is the one worth surfacing — swallowing it here
                // to report a cleanup failure would hide what actually went wrong.
            }

            throw;
        }
    }

    /// <summary>
    /// Write the current-scheme profile-derived index rows for a user — email lookup, email-domain, and
    /// first/last name prefixes. The single source of truth shared by <see cref="CreateAsync"/> and
    /// <see cref="ReindexUserAsync"/> (UpdateAsync stays bespoke — it diffs old vs new per field). Email is
    /// written before any legacy drop, so there's no lookup gap. When <paramref name="dropLegacy"/> is set
    /// (the reindex/backfill path), also removes the matching legacy plaintext-keyed rows once tokenization
    /// is on, so at "contract" no plaintext index rows remain.
    /// </summary>
    /// <summary>
    /// Writes the email→userId binding with an insert-if-absent, and accepts an existing row only when
    /// it already names this user.
    /// </summary>
    /// <remarks>
    /// This row is the authority for <see cref="FindByEmailAsync"/> — password login, password reset,
    /// SCIM matching and federated account linking all resolve an identity through it. Every write of
    /// it was an unconditional Replace, so it would happily repoint an existing binding at a different
    /// user; uniqueness rested entirely on callers doing a FindByEmailAsync first, which is a
    /// check-then-act with several round trips of gap. Two concurrent registrations for one address
    /// could therefore both pass their check and the second silently take ownership of the first's
    /// login identifier. The store already uses the atomic primitive next door — CreateAsync writes
    /// the profile row with AddEntityAsync — so this is the same guarantee, applied to the row that
    /// actually decides who you are.
    /// </remarks>
    private async Task ClaimEmailIndexAsync(string emailPk, string normalizedEmail, string userId, CancellationToken ct)
    {
        var entity = UserEmailEntity.Create(normalizedEmail, userId);
        entity.PartitionKey = emailPk;

        try
        {
            await userEmailsTable.AddEntityAsync(entity, ct);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409)
        {
            // Already bound. Re-registering the same user's own address is ordinary (a reindex, a
            // retried write, a no-op profile update) and must stay idempotent; a different user
            // holding it is the collision this exists to catch.
            var existing = await TryGetEmailIndexUserIdAsync(emailPk, ct).ConfigureAwait(false);
            if (existing is not null && !string.Equals(existing, userId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The email address is already registered to a different user ({emailPk}).");

            // Same user, or a row with no usable UserId — rewrite so the binding is well-formed.
            await userEmailsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        }

        await LogUpsertAsync("UserEmails", emailPk, UserEmailEntity.LookupRowKey, ct);
    }

    private async Task WriteProfileIndexesAsync(
        string normalizedEmail, string? normalizedFirst, string? normalizedLast, string userId, bool dropLegacy, CancellationToken ct)
    {
        // Every index key this write needs, computed in ONE tokenizer round-trip.
        var batch = new TokenBatch(_tokenizer);
        var emailToken = batch.Add(normalizedEmail);
        var domain = userEmailDomainsTable is null ? null : UserEmailDomainEntity.DomainOf(normalizedEmail);
        var domainToken = domain is null ? null : batch.Add(domain);
        var localPrefixTokens = ReserveLocalPrefixTokens(batch, normalizedEmail);
        var firstTokens = ReserveNameTokens(batch, userFirstNamesTable, normalizedFirst);
        var lastTokens = ReserveNameTokens(batch, userLastNamesTable, normalizedLast);
        await batch.RunAsync(ct);

        // Email lookup — claimed, not overwritten. See ClaimEmailIndexAsync.
        var emailPk = _partitioner.PK(emailToken());
        await ClaimEmailIndexAsync(emailPk, normalizedEmail, userId, ct);
        if (dropLegacy && _indexTokenized)
        {
            var plainPk = _partitioner.PK(normalizedEmail);
            if (!string.Equals(plainPk, emailPk, StringComparison.Ordinal))
                await TryDeleteEmailIndexAsync(plainPk, ct);
        }

        // Email-domain index + email local-part prefix index.
        if (domainToken is not null)
        {
            await WriteDomainIndexAsync(domainToken(), userId, ct);
            if (dropLegacy && _indexTokenized)
            {
                var plainDomPk = _partitioner.PK(domain!);
                if (!string.Equals(plainDomPk, _partitioner.PK(domainToken()), StringComparison.Ordinal))
                    await TryDeleteDomainAsync(plainDomPk, userId, ct);
            }
        }
        if (localPrefixTokens is not null)
            await WriteEmailLocalPrefixIndexAsync(localPrefixTokens(), userId, ct);

        // Name prefix indexes.
        if (normalizedFirst is not null && userFirstNamesTable is not null)
        {
            await WriteNameIndexAsync(userFirstNamesTable, firstTokens?.Invoke(), "UserFirstNames", normalizedFirst, userId, ct);
            if (dropLegacy && _indexTokenized)
                await TryDeleteRowAsync(userFirstNamesTable, _partitioner.PK(UserFirstNameEntity.GetPartitionKey(normalizedFirst)), UserFirstNameEntity.MakeRowKey(normalizedFirst, userId), "UserFirstNames", ct);
        }
        if (normalizedLast is not null && userLastNamesTable is not null)
        {
            await WriteNameIndexAsync(userLastNamesTable, lastTokens?.Invoke(), "UserLastNames", normalizedLast, userId, ct);
            if (dropLegacy && _indexTokenized)
                await TryDeleteRowAsync(userLastNamesTable, _partitioner.PK(UserFirstNameEntity.GetPartitionKey(normalizedLast)), UserFirstNameEntity.MakeRowKey(normalizedLast, userId), "UserLastNames", ct);
        }
    }

    public async Task UpdateAsync(AuthUser user, CancellationToken ct = default)
    {
        // Fetch the existing entity to check if email changed
        try
        {
            // Read → check the caller's revision → conditional write, retried as a unit. The retry is
            // for the store's OWN microsecond window only: a login stamp that lands between the read and
            // the write yields 412, and re-reading is safe because the caller's revision is re-checked
            // against the fresh row each time. Without it an ordinary sign-in would fail an unrelated
            // administrative write, which is the opposite of the guarantee this is here to give.
            AuthUser? storedModel = null;
            var stored = false;
            for (var attempt = 0; attempt < ContendedWriteAttempts && !stored; attempt++)
            {
                if (attempt > 0) await Task.Delay(Random.Shared.Next(2, 12), ct);

                var existing = await usersTable.GetEntityAsync<UserEntity>(
                    _partitioner.PK(user.Id), UserEntity.ProfileRowKey, cancellationToken: ct);
                // Decrypt first: the old email/names drive old-index-key removal, and must be plaintext
                // (never the stored ciphertext) to recompute the right tokens.
                await DecryptEntityAsync(existing.Value, ct);
                storedModel = existing.Value.ToModel();

                // The write below is a full-entity Replace, so it puts back every column as it stood
                // when the CALLER read — silently reverting a password reset and its security-stamp
                // rotation, a deactivation, a role revocation, an active lockout. Record*LoginAsync were
                // already ETag-conditional, which covers this store's read-to-write window; this covers
                // the caller's, which is the one an admin reset actually races with and the only one an
                // attacker can widen by authenticating in a loop. A mismatch is refused rather than
                // merged: the store cannot know which of two conflicting intents should win.
                if (user.ConcurrencyToken is { Length: > 0 } token &&
                    !string.Equals(token, storedModel.ConcurrencyToken, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Concurrent update to user '{user.Id}': the record changed between read and " +
                        "write. Re-read and retry.");
                }

                var candidate = UserEntity.FromModel(user);
                candidate.PartitionKey = _partitioner.PK(candidate.PartitionKey);
                await EncryptEntityAsync(candidate, ct);

                try
                {
                    await usersTable.UpdateEntityAsync(candidate, existing.Value.ETag, TableUpdateMode.Replace, ct);
                    await LogUpsertAsync("Users", candidate.PartitionKey, candidate.RowKey, ct);
                    stored = true;
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    // Someone wrote between our read and our write; that costs an attempt. If they
                    // changed anything that decides something, the revision check above turns the next
                    // pass into a refusal.
                }
            }

            if (!stored || storedModel is null)
            {
                throw new InvalidOperationException(
                    $"Concurrent update to user '{user.Id}': the record kept changing between read and " +
                    "write. Re-read and retry.");
            }
            user.ConcurrencyToken = UserRevision.Of(user);

            var oldNormalizedEmail = storedModel.NormalizedEmail;
            var newNormalizedEmail = user.NormalizedEmail;
            var emailChanged = !string.Equals(oldNormalizedEmail, newNormalizedEmail, StringComparison.Ordinal);
            var localChanged = emailChanged && !string.Equals(LocalPartOf(oldNormalizedEmail), LocalPartOf(newNormalizedEmail), StringComparison.Ordinal);
            var oldDomain = UserEmailDomainEntity.DomainOf(oldNormalizedEmail);
            var newDomain = UserEmailDomainEntity.DomainOf(newNormalizedEmail);
            var domainChanged = emailChanged && !string.Equals(oldDomain, newDomain, StringComparison.Ordinal);
            var oldFirst = Normalize(storedModel.FirstName);
            var newFirst = Normalize(user.FirstName);
            var firstChanged = userFirstNamesTable is not null && !string.Equals(oldFirst, newFirst, StringComparison.Ordinal);
            var oldLast = Normalize(storedModel.LastName);
            var newLast = Normalize(user.LastName);
            var lastChanged = userLastNamesTable is not null && !string.Equals(oldLast, newLast, StringComparison.Ordinal);
            // Read from the STORED entity, not from the incoming model: the caller may have mutated
            // the same instance it read, in which case user.Roles is already the new set and there is
            // nothing left to diff against.
            var oldRoles = storedModel.Roles;

            // Every index key the changed fields need — new-side and old-side — in ONE tokenizer
            // round-trip, computed BEFORE any index write: a tokenizer throw here leaves every existing
            // lookup row intact, and the per-field write-before-delete ordering below is unchanged.
            var batch = new TokenBatch(_tokenizer);
            Func<string>? newEmailToken = null, oldEmailToken = null, newDomainToken = null, oldDomainToken = null;
            Func<IReadOnlyList<string>>? newLocalTokens = null, oldLocalTokens = null;
            if (emailChanged)
            {
                newEmailToken = batch.Add(newNormalizedEmail);
                oldEmailToken = batch.Add(oldNormalizedEmail);
                if (localChanged)
                {
                    newLocalTokens = ReserveLocalPrefixTokens(batch, newNormalizedEmail);
                    oldLocalTokens = ReserveLocalPrefixTokens(batch, oldNormalizedEmail);
                }
                if (domainChanged && userEmailDomainsTable is not null)
                {
                    newDomainToken = newDomain is null ? null : batch.Add(newDomain);
                    oldDomainToken = oldDomain is null ? null : batch.Add(oldDomain);
                }
            }
            var newFirstTokens = firstChanged ? ReserveNameTokens(batch, userFirstNamesTable, newFirst) : null;
            var oldFirstTokens = firstChanged ? ReserveNameTokens(batch, userFirstNamesTable, oldFirst) : null;
            var newLastTokens = lastChanged ? ReserveNameTokens(batch, userLastNamesTable, newLast) : null;
            var oldLastTokens = lastChanged ? ReserveNameTokens(batch, userLastNamesTable, oldLast) : null;
            await batch.RunAsync(ct);

            if (emailChanged)
            {
                // Write the NEW email index first, then remove the old (tokenized + any legacy plaintext row).
                // Write-before-delete: a throw between the two calls must never strand the user with neither
                // lookup (login-lockout). Old≠new here, so the PKs differ and the new write can't collide with
                // the row we then delete. Mirrors ReindexUserAsync's "write current FIRST (no login gap), then
                // drop legacy".
                // The new binding is CLAIMED, not written over: if another user already holds this
                // address the claim throws and the old binding is left alone, so the caller's
                // check-then-act gap cannot end in one user owning another's login identifier.
                await ClaimEmailIndexAsync(_partitioner.PK(newEmailToken!()), newNormalizedEmail, user.Id, ct);
                await DeleteEmailIndexAsync(oldNormalizedEmail, oldEmailToken!(), ct);

                // Local-part prefix index: keyed on the bit before '@', so rewrite when THAT changed
                // (a@acme → a@other keeps it; alistair@acme → wendy@acme moves it). Independent of the
                // domain check below — a same-domain local-part change still has to move the prefix rows.
                // Write-before-delete as above.
                if (localChanged)
                {
                    if (newLocalTokens is not null) await WriteEmailLocalPrefixIndexAsync(newLocalTokens(), user.Id, ct);
                    if (oldLocalTokens is not null) await DeleteEmailLocalPrefixIndexAsync(oldLocalTokens(), user.Id, ct);
                }

                // Domain index: only rewrite when the domain part actually changed (a@acme → b@acme keeps it).
                // Same write-before-delete ordering.
                if (domainChanged)
                {
                    if (newDomainToken is not null) await WriteDomainIndexAsync(newDomainToken(), user.Id, ct);
                    if (oldDomainToken is not null) await DeleteDomainIndexAsync(oldDomain!, oldDomainToken(), user.Id, ct);
                }
            }

            // Names: write the new prefix rows before dropping the old (write-before-delete, as above) so a
            // mid-call throw never leaves the user unsearchable by name. old≠new ⇒ distinct rows.
            if (firstChanged)
            {
                if (newFirst is not null) await WriteNameIndexAsync(userFirstNamesTable!, newFirstTokens?.Invoke(), "UserFirstNames", newFirst, user.Id, ct);
                if (oldFirst is not null) await DeleteNameIndexAsync(userFirstNamesTable!, oldFirstTokens?.Invoke(), oldFirst, user.Id, "UserFirstNames", ct);
            }
            if (lastChanged)
            {
                if (newLast is not null) await WriteNameIndexAsync(userLastNamesTable!, newLastTokens?.Invoke(), "UserLastNames", newLast, user.Id, ct);
                if (oldLast is not null) await DeleteNameIndexAsync(userLastNamesTable!, oldLastTokens?.Invoke(), oldLast, user.Id, "UserLastNames", ct);
            }

            await SyncRoleIndexAsync(oldRoles, user.Roles, user.Id, ct);
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
            // Decrypt first: the stored email/names must be plaintext to recompute the index keys to remove.
            await DecryptEntityAsync(existing.Value, ct);

            var normalizedEmail = existing.Value.NormalizedEmail;
            var normFirst = Normalize(existing.Value.FirstName);
            var normLast = Normalize(existing.Value.LastName);

            // Every index key the cleanup needs, computed in ONE tokenizer round-trip.
            var batch = new TokenBatch(_tokenizer);
            var emailToken = batch.Add(normalizedEmail);
            var domain = userEmailDomainsTable is null ? null : UserEmailDomainEntity.DomainOf(normalizedEmail);
            var domainToken = domain is null ? null : batch.Add(domain);
            var localPrefixTokens = ReserveLocalPrefixTokens(batch, normalizedEmail);
            var firstTokens = ReserveNameTokens(batch, userFirstNamesTable, normFirst);
            var lastTokens = ReserveNameTokens(batch, userLastNamesTable, normLast);
            await batch.RunAsync(ct);

            // Delete email index (tokenized + any legacy plaintext row) + the domain-index row
            await DeleteEmailIndexAsync(normalizedEmail, emailToken(), ct);
            if (domainToken is not null)
                await DeleteDomainIndexAsync(domain!, domainToken(), userId, ct);
            if (localPrefixTokens is not null)
                await DeleteEmailLocalPrefixIndexAsync(localPrefixTokens(), userId, ct);

            // Delete name indexes (all prefix-token rows + any legacy row)
            if (normFirst is not null && userFirstNamesTable is not null)
                await DeleteNameIndexAsync(userFirstNamesTable, firstTokens?.Invoke(), normFirst, userId, "UserFirstNames", ct);
            if (normLast is not null && userLastNamesTable is not null)
                await DeleteNameIndexAsync(userLastNamesTable, lastTokens?.Invoke(), normLast, userId, "UserLastNames", ct);

            // Drop this user's role memberships — otherwise a deleted account keeps answering
            // "who administers this", which is the one question the index exists to answer.
            await SyncRoleIndexAsync(existing.Value.ToModel().Roles, roles: null, userId, ct);

            // Delete all external login entries for this user — independent row pairs, so remove them
            // concurrently instead of one blocking round-trip each.
            var logins = await GetLoginsAsync(userId, ct);
            await Task.WhenAll(logins.Select(login => RemoveLoginAsync(userId, login.Provider, login.ProviderKey, ct)));

            // Delete user profile — tombstone-first (F24e): a crash between the delete and the
            // tombstone would drop the delete from every backup, and restore would resurrect the
            // (possibly GDPR-erased) account.
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("Users", _partitioner.PK(userId), UserEntity.ProfileRowKey, ct);
            await usersTable.DeleteEntityAsync(_partitioner.PK(userId), UserEntity.ProfileRowKey, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task ReindexUserAsync(string userId, CancellationToken ct = default)
    {
        UserEntity entity;
        try
        {
            entity = (await usersTable.GetEntityAsync<UserEntity>(_partitioner.PK(userId), UserEntity.ProfileRowKey, cancellationToken: ct)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return; }

        // Read plaintext (passthrough if a field is already plaintext) — index keys derive from plaintext.
        await DecryptEntityAsync(entity, ct);
        var normalizedEmail = entity.NormalizedEmail;
        var normFirst = Normalize(entity.FirstName);
        var normLast = Normalize(entity.LastName);

        // 1. Re-encrypt the profile in place (plaintext → ciphertext under the current cipher; idempotent).
        await EncryptEntityAsync(entity, ct);
        await usersTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        await LogUpsertAsync("Users", entity.PartitionKey, entity.RowKey, ct);

        // 2-4. Rewrite the profile-derived indexes (email lookup, domain, name prefixes) under the current
        //       keys, dropping any legacy plaintext-keyed rows. Shared with CreateAsync (dropLegacy:false).
        await WriteProfileIndexesAsync(normalizedEmail, normFirst, normLast, userId, dropLegacy: true, ct);

        // 5. Role membership. This is what backfills the index onto users who existed before it did —
        //    without it the index only ever describes accounts touched since it shipped, and a role
        //    granted years ago is invisible. Upsert-only: reindex adds what the user holds now and
        //    never removes, so it cannot race a concurrent grant into deleting a live membership.
        foreach (var role in entity.ToModel().Roles)
        {
            await WriteRoleIndexAsync(role, userId, ct);
        }
    }

    /// <summary>
    /// Re-key the <c>UserExternalIds</c> forward index from legacy plaintext-keyed rows
    /// (PK = "{clientId}|{externalId}") to blind-index tokens. This index is invisible to
    /// <see cref="ReindexUserAsync"/> — there is no userId→externalId reverse index to drive a per-user
    /// rewrite — so it is migrated by scanning the table directly. A migrated row's key is the HMAC token
    /// (lowercase hex, no separator); a legacy row's key is the plaintext composite, which always contains
    /// '|'. That makes the classification exact (a token can never contain '|'), independent of the digest
    /// length. Write-before-delete keeps <see cref="FindByExternalIdAsync"/> resolving throughout, and the
    /// scan is idempotent (token rows are skipped, so re-runs move 0). No-op when tokenization is off —
    /// plaintext keys ARE the current scheme then.
    /// </summary>
    public async Task<int> MigrateExternalIdIndexAsync(bool dryRun, CancellationToken ct = default)
    {
        if (!_indexTokenized) return 0;

        // Env-scoped scan, same shape as EnumerateUserIdsAsync: live has no range filter; a sandbox env
        // restricts to its "{env}|" PartitionKey range so the sweep never crosses env isolation.
        var range = _partitioner.RangeForEnv();
        var query = range is null
            ? userExternalIdsTable.QueryAsync<UserExternalIdEntity>(
                e => e.RowKey == UserExternalIdEntity.LookupRowKey, cancellationToken: ct)
            : userExternalIdsTable.QueryAsync<UserExternalIdEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0
                     && e.PartitionKey.CompareTo(range.Value.High) < 0
                     && e.RowKey == UserExternalIdEntity.LookupRowKey, cancellationToken: ct);

        var count = 0;
        await foreach (var row in query)
        {
            var composite = _partitioner.Strip(row.PartitionKey);
            if (!composite.Contains('|')) continue; // already a token — nothing to migrate
            count++;
            if (dryRun) continue;

            // Tokenize the whole "{clientId}|{externalId}" composite — the exact input ExternalIdIndexPkAsync
            // hashes on the write path — then move the row: write the token PK first, drop the legacy PK after.
            var tokenPk = _partitioner.PK(await _tokenizer.TokenizeAsync(composite, ct));
            if (string.Equals(tokenPk, row.PartitionKey, StringComparison.Ordinal)) continue; // defensive
            await userExternalIdsTable.UpsertEntityAsync(
                new UserExternalIdEntity { PartitionKey = tokenPk, RowKey = UserExternalIdEntity.LookupRowKey, UserId = row.UserId },
                TableUpdateMode.Replace, ct);
            await LogUpsertAsync("UserExternalIds", tokenPk, UserExternalIdEntity.LookupRowKey, ct);
            await TryDeleteExternalIdAsync(row.PartitionKey, row.UserId, ct);
        }
        return count;
    }

    /// <summary>
    /// Re-key + encrypt legacy <c>UserLogins</c> rows to the blind-index scheme. Both row shapes are
    /// scanned: the forward lookup (RK=<c>lookup</c>, legacy PK = the plaintext "{provider}|{providerKey}"
    /// composite) and the reverse per-user list (RK = <c>login|{provider}|{providerKey}</c>). A migrated
    /// row's key is the HMAC token (forward PK = token; reverse RK = <c>login|{token}</c>) — the token is
    /// hex, so a legacy key is exactly the one that still contains '|'. For each legacy row the plaintext
    /// Provider/ProviderKey columns (present because it predates encryption) drive the token and are then
    /// encrypted. Write-before-delete; idempotent (token-keyed rows skipped). No-op when tokenization off.
    /// </summary>
    public async Task<int> MigrateUserLoginsAsync(bool dryRun, CancellationToken ct = default)
    {
        if (!_indexTokenized) return 0;

        var range = _partitioner.RangeForEnv();
        var query = range is null
            ? userLoginsTable.QueryAsync<UserLoginEntity>(cancellationToken: ct)
            : userLoginsTable.QueryAsync<UserLoginEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0 && e.PartitionKey.CompareTo(range.Value.High) < 0, cancellationToken: ct);

        var count = 0;
        await foreach (var row in query)
        {
            var isForward = row.RowKey == UserLoginEntity.LookupRowKey;
            var isReverse = row.RowKey.StartsWith(UserLoginEntity.LoginRowKeyPrefix, StringComparison.Ordinal);
            if (!isForward && !isReverse) continue;

            // Legacy iff the key still carries the plaintext composite (contains '|'); a token is pure hex.
            var legacy = isForward
                ? _partitioner.Strip(row.PartitionKey).Contains('|')
                : row.RowKey[UserLoginEntity.LoginRowKeyPrefix.Length..].Contains('|');
            if (!legacy) continue;
            count++;
            if (dryRun) continue;

            // Columns are plaintext on a legacy row — recompute the token from them and encrypt for the move.
            var token = await LoginTokenAsync(row.Provider, row.ProviderKey, ct);
            var moved = new UserLoginEntity
            {
                PartitionKey = isForward ? _partitioner.PK(token) : row.PartitionKey,
                RowKey = isForward ? UserLoginEntity.LookupRowKey : LoginReverseRk(token),
                UserId = row.UserId,
                Provider = row.Provider,
                ProviderKey = row.ProviderKey,
                DisplayName = row.DisplayName,
            };
            await EncryptLoginAsync(moved, ct);
            await userLoginsTable.UpsertEntityAsync(moved, TableUpdateMode.Replace, ct);  // write new first
            await LogUpsertAsync("UserLogins", moved.PartitionKey, moved.RowKey, ct);
            await TryDeleteLoginAsync(row.PartitionKey, row.RowKey, ct);                  // then drop legacy
        }
        return count;
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
        // Tokenized lookup; migration-window fallback to the legacy plaintext key for un-backfilled rows.
        var userId = await TryGetExternalIdUserIdAsync(await ExternalIdIndexPkAsync(clientId, externalId, ct), ct);
        if (userId is null && _indexTokenized)
            userId = await TryGetExternalIdUserIdAsync(_partitioner.PK($"{clientId}|{externalId}"), ct);
        return userId is null ? null : await GetAsync(userId, ct);
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
            AuthUser user;
            try
            {
                await DecryptEntityAsync(entity, ct);
                user = entity.ToModel();
                user.Id = _partitioner.Strip(user.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One undecryptable/corrupt row (e.g. a key whose min_decryption_version was raised
                // before this row was reindexed) must not fail the whole tenant's user list — skip it
                // and keep enumerating the rest of the page.
                continue;
            }
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

        // Azure Table caps page size at 1000. Callers (the SCIM list endpoint) pass
        // int.MaxValue to mean "all rows", so count + 1 would overflow to a negative
        // maxPerPage and the service rejects it with 400 InvalidInput. Clamp with a
        // widening cast; the async enumerable still pages through every result.
        var pageSize = (int)Math.Min((long)count + 1, 1000);
        var range = _partitioner.RangeForEnv();
        var query = range is null
            ? usersTable.QueryAsync<UserEntity>(
                e => e.RowKey == UserEntity.ProfileRowKey && e.ScimProvisionedByClientId == scimClientId,
                maxPerPage: pageSize, cancellationToken: ct)
            : usersTable.QueryAsync<UserEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0
                     && e.PartitionKey.CompareTo(range.Value.High) < 0
                     && e.RowKey == UserEntity.ProfileRowKey
                     && e.ScimProvisionedByClientId == scimClientId,
                maxPerPage: pageSize, cancellationToken: ct);

        await foreach (var entity in query)
        {
            AuthUser user;
            try
            {
                await DecryptEntityAsync(entity, ct);
                user = entity.ToModel();
                user.Id = _partitioner.Strip(user.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Skip a row we can't decrypt rather than failing the entire SCIM sync (see ListAsync).
                continue;
            }

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

    public async Task<UserPage> ListPageAsync(string? organizationId, int count, string? continuationToken, CancellationToken ct = default)
    {
        var range = _partitioner.RangeForEnv();
        var query = range is null
            ? usersTable.QueryAsync<UserEntity>(
                e => e.RowKey == UserEntity.ProfileRowKey,
                maxPerPage: count, cancellationToken: ct)
            : usersTable.QueryAsync<UserEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0
                     && e.PartitionKey.CompareTo(range.Value.High) < 0
                     && e.RowKey == UserEntity.ProfileRowKey,
                maxPerPage: count, cancellationToken: ct);
        return await ReadPageAsync(query, organizationId, count, continuationToken, ct);
    }

    public async Task<UserPage> ListByScimClientPageAsync(string scimClientId, int count, string? continuationToken, CancellationToken ct = default)
    {
        var range = _partitioner.RangeForEnv();
        var query = range is null
            ? usersTable.QueryAsync<UserEntity>(
                e => e.RowKey == UserEntity.ProfileRowKey && e.ScimProvisionedByClientId == scimClientId,
                maxPerPage: count, cancellationToken: ct)
            : usersTable.QueryAsync<UserEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0
                     && e.PartitionKey.CompareTo(range.Value.High) < 0
                     && e.RowKey == UserEntity.ProfileRowKey
                     && e.ScimProvisionedByClientId == scimClientId,
                maxPerPage: count, cancellationToken: ct);
        return await ReadPageAsync(query, organizationId: null, count, continuationToken, ct);
    }

    /// <summary>
    /// F26 cursor paging core: resume from the SDK's opaque continuation token, decrypt ONLY the
    /// returned rows, and stop at a page boundary once at least <paramref name="count"/> rows are
    /// collected (a server-filtered page can come back short — or even empty with a token — so keep
    /// consuming pages until there's something to return or the listing is exhausted). Tokens are
    /// only valid at page boundaries, so pages are never split; the count is a hint.
    /// </summary>
    private async Task<UserPage> ReadPageAsync(
        Azure.AsyncPageable<UserEntity> query, string? organizationId, int count, string? continuationToken, CancellationToken ct)
    {
        var results = new List<AuthUser>();
        string? nextToken = null;
        var pagesConsumed = 0;

        await foreach (var page in query.AsPages(continuationToken))
        {
            foreach (var entity in page.Values)
            {
                AuthUser user;
                try
                {
                    await DecryptEntityAsync(entity, ct);
                    user = entity.ToModel();
                    user.Id = _partitioner.Strip(user.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One undecryptable/corrupt row must not fail the page — skip it (see ListAsync).
                    continue;
                }
                if (organizationId is not null &&
                    !string.Equals(user.OrganizationId, organizationId, StringComparison.Ordinal))
                    continue;

                results.Add(user);
            }

            nextToken = page.ContinuationToken;
            // The page cap bounds one call's work when a client-side filter (organizationId)
            // matches almost nothing — return a short page with a token rather than scanning the
            // whole tenant in a single request.
            if (results.Count >= count || nextToken is null || ++pagesConsumed >= 10)
                break;
        }

        return new UserPage(results, nextToken);
    }

    public async IAsyncEnumerable<string> EnumerateUserIdsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // Id-only stream for the cold-row backfill: select just the keys — no PII columns, so no per-row
        // decryption — and let the Tables SDK page via continuation tokens. O(N), unlike ListAsync's
        // offset re-scan that also decrypts every skipped row. One profile row per user.
        var range = _partitioner.RangeForEnv();
        var query = range is null
            ? usersTable.QueryAsync<TableEntity>(
                e => e.RowKey == UserEntity.ProfileRowKey,
                select: ["PartitionKey", "RowKey"], cancellationToken: ct)
            : usersTable.QueryAsync<TableEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0
                     && e.PartitionKey.CompareTo(range.Value.High) < 0
                     && e.RowKey == UserEntity.ProfileRowKey,
                select: ["PartitionKey", "RowKey"], cancellationToken: ct);

        await foreach (var entity in query)
            yield return _partitioner.Strip(entity.PartitionKey);
    }

    public async IAsyncEnumerable<UserLoginState> EnumerateLoginStatesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // Same shape as EnumerateUserIdsAsync, projecting only the plaintext login-state columns —
        // no encrypted field is selected, so a whole-population retention sweep never touches Vault.
        string[] columns = ["PartitionKey", "RowKey", "CreatedAt", "LastLoginAt", "IsActive"];
        var range = _partitioner.RangeForEnv();
        var query = range is null
            ? usersTable.QueryAsync<TableEntity>(
                e => e.RowKey == UserEntity.ProfileRowKey,
                select: columns, cancellationToken: ct)
            : usersTable.QueryAsync<TableEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0
                     && e.PartitionKey.CompareTo(range.Value.High) < 0
                     && e.RowKey == UserEntity.ProfileRowKey,
                select: columns, cancellationToken: ct);

        await foreach (var entity in query)
        {
            yield return new UserLoginState(
                _partitioner.Strip(entity.PartitionKey),
                entity.GetDateTimeOffset("CreatedAt") ?? default,
                entity.GetDateTimeOffset("LastLoginAt"),
                entity.GetBoolean("IsActive") ?? true);
        }
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
        // Email prefix range scan works only on plaintext keys. With blind-index tokenization on, email keys
        // are unordered HMAC tokens so prefix search over them is impossible — email search degrades to exact
        // match (already handled above via FindByEmailAsync). Name prefix search is unaffected here.
        var emailLo = _partitioner.PK(prefix);
        var emailHi = _partitioner.PK(prefixEnd);

        // Tokenized: compute the (≤2) prefix-lookup tokens — email local-part and name — in ONE
        // round-trip instead of one per index; the three index queries below still run in parallel.
        string? localPrefixPk = null, namePrefixPk = null;
        if (_indexTokenized)
        {
            var batch = new TokenBatch(_tokenizer);
            Func<string>? localToken = null, nameToken = null;
            var local = LocalPartOf(prefix) ?? prefix;
            // Counted and cut in runes, exactly as NamePrefixesOf writes them (see TextPrefix). Cutting
            // at code unit 16 instead splits a surrogate pair, so the token looked up here is an HMAC
            // over a string the write side never produced.
            if (userEmailLocalPrefixesTable is not null && TextPrefix.RuneCount(local) >= NamePrefixMin)
                localToken = batch.Add(TextPrefix.Take(local, NamePrefixMax));
            if ((userFirstNamesTable ?? userLastNamesTable) is not null && TextPrefix.RuneCount(prefix) >= NamePrefixMin)
                nameToken = batch.Add(TextPrefix.Take(prefix, NamePrefixMax));
            await batch.RunAsync(ct);
            localPrefixPk = localToken is null ? null : _partitioner.PK(localToken());
            namePrefixPk = nameToken is null ? null : _partitioner.PK(nameToken());
        }

        // Tokenized: HMAC keys are unordered, so email prefix search uses the local-part prefix index
        // (exact-match on HMAC(prefix)). Off: the ordered range scan on the exact-email index works.
        var emailTask = _indexTokenized
            ? SearchEmailLocalPrefixAsync(localPrefixPk, maxResults, ct)
            : CollectUserIdsAsync(
                userEmailsTable.QueryAsync<UserEmailEntity>(
                    e => e.PartitionKey.CompareTo(emailLo) >= 0 && e.PartitionKey.CompareTo(emailHi) < 0,
                    cancellationToken: ct),
                e => e.UserId, maxResults, ct);

        // Name search needs a query of at least NamePrefixMin chars (admin search UIs enforce this).
        // SearchNameIndexAsync picks the scheme per row: legacy range scan when off, exact prefix-token
        // lookup (+ legacy fallback for un-backfilled rows) when tokenized. Email prefix search is dropped
        // when tokenized (unordered keys) — exact email match is handled above via FindByEmailAsync.
        var firstNameTask = SearchNameIndexAsync(userFirstNamesTable, namePrefixPk, prefix, prefixEnd, maxResults, ct);
        var lastNameTask = SearchNameIndexAsync(userLastNamesTable, namePrefixPk, prefix, prefixEnd, maxResults, ct);

        await Task.WhenAll(emailTask, firstNameTask, lastNameTask);

        // Interleave: email hits first, then first-name, then last-name. Point-read the deduped candidates
        // in parallel (each is an independent read + decrypt round-trip) instead of one at a time; every
        // candidate list is capped at maxResults, so the fan-out is bounded.
        var candidateIds = emailTask.Result.Concat(firstNameTask.Result).Concat(lastNameTask.Result)
            .Where(id => seen.Add(id)).ToList();
        var fetched = await Task.WhenAll(candidateIds.Select(id => GetAsync(id, ct)));
        foreach (var user in fetched)
        {
            if (user is null) continue;
            results.Add(user);
            if (results.Count >= maxResults)
                break;
        }

        return results;
    }

    public async Task<IReadOnlyList<AuthUser>> SearchByEmailDomainAsync(string domain, int maxResults = 50, CancellationToken ct = default)
    {
        if (userEmailDomainsTable is null || string.IsNullOrWhiteSpace(domain))
            return [];

        var normDomain = domain.Trim().ToUpperInvariant();
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        async Task CollectAsync(string pk)
        {
            if (ids.Count >= maxResults) return;
            var query = userEmailDomainsTable.QueryAsync<UserEmailDomainEntity>(e => e.PartitionKey == pk, cancellationToken: ct);
            await foreach (var e in query.WithCancellation(ct))
            {
                if (seen.Add(e.UserId)) ids.Add(e.UserId);
                if (ids.Count >= maxResults) break;
            }
        }

        var basePk = await DomainIndexPkAsync(normDomain, ct);
        // Members are bucketed, so fan out over the buckets (bounded by maxResults).
        for (var b = 0; b < DomainBuckets && ids.Count < maxResults; b++)
            await CollectAsync($"{basePk}-{b:x}");
        // Migration windows: unbucketed tokenized rows (pre-bucketing), then plaintext rows (pre-tokenization).
        await CollectAsync(basePk);
        if (_indexTokenized && ids.Count < maxResults)
            await CollectAsync(_partitioner.PK(normDomain));

        var results = new List<AuthUser>();
        foreach (var id in ids)
        {
            var user = await GetAsync(id, ct);
            if (user is not null)
                results.Add(user);
            if (results.Count >= maxResults)
                break;
        }
        return results;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A single-partition query — role membership is partitioned by role, so this reads exactly the
    /// rows it returns rather than scanning users and filtering. The index key is the role name
    /// itself (see <see cref="UserRoleEntity"/>), so there is no tokenizer round-trip and no
    /// migration window to search across.
    /// <para>
    /// A membership row whose user has since vanished is skipped rather than erroring: the index is a
    /// convenience over the user store, never the authority on who exists.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AuthUser>> ListUsersInRoleAsync(string roleName, int maxResults = 200, CancellationToken ct = default)
    {
        if (userRolesTable is null)
            throw new NotSupportedException(
                "The role membership index is not configured on this store, so the users in a role cannot be listed.");

        if (string.IsNullOrWhiteSpace(roleName)) return [];

        var pk = _partitioner.PK(UserRoleEntity.Normalize(roleName));
        var ids = await CollectUserIdsAsync(
            userRolesTable.QueryAsync<UserRoleEntity>(e => e.PartitionKey == pk, cancellationToken: ct),
            e => e.UserId, maxResults, ct);

        var results = new List<AuthUser>(ids.Count);
        foreach (var id in ids)
        {
            var user = await GetAsync(id, ct);
            if (user is not null)
                results.Add(user);
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

    /// <summary>
    /// Binds (clientId, externalId) to a user, refusing to take a binding another user already holds.
    /// </summary>
    /// <remarks>
    /// The row is a single (clientId, externalId) → userId mapping and it was written with a blind
    /// Replace, so assigning user B an externalId user A already held repointed it: A's own record still
    /// claimed the value, but the index — and therefore the connector's `externalId eq` lookup and its
    /// "update A" write — resolved to B. The SCIM endpoints now check first, but that is a
    /// check-then-act; the store is the only layer that can make it stick.
    /// </remarks>
    public async Task SetExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default)
    {
        var entity = UserExternalIdEntity.Create(clientId, externalId, userId);
        entity.PartitionKey = await ExternalIdIndexPkAsync(clientId, externalId, ct);

        try
        {
            await userExternalIdsTable.AddEntityAsync(entity, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Re-asserting a user's own binding is ordinary (a retried sync, a no-op PUT) and must stay
            // idempotent; a different user holding it is the collision this exists to catch.
            var existing = await TryGetExternalIdUserIdAsync(entity.PartitionKey, ct);
            if (existing is not null && !string.Equals(existing, userId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The externalId is already assigned to a different user ({entity.PartitionKey}).");

            await userExternalIdsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        }

        await LogUpsertAsync("UserExternalIds", entity.PartitionKey, UserExternalIdEntity.LookupRowKey, ct);
    }

    public async Task RemoveExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default)
    {
        // Delete the tokenized row and, while tokenization is on, any legacy plaintext row too.
        var tokenPk = await ExternalIdIndexPkAsync(clientId, externalId, ct);
        await TryDeleteExternalIdAsync(tokenPk, userId, ct);
        if (_indexTokenized)
        {
            var plainPk = _partitioner.PK($"{clientId}|{externalId}");
            if (!string.Equals(plainPk, tokenPk, StringComparison.Ordinal))
                await TryDeleteExternalIdAsync(plainPk, userId, ct);
        }
    }

    // ── External-login index crypto (mirrors the email index) ──────────────────────────────────────
    // The lookup KEYS are blind-index tokens: forward PK = token(provider|providerKey), reverse RK =
    // "login|{token}". The recoverable VALUE columns (ProviderKey — a SAML NameId is usually an email — and
    // DisplayName — a person's name) are encrypted; Provider + UserId stay plaintext. All passthrough when
    // the tokenizer/cipher are the Null defaults, so the key stays the historical "{provider}|{providerKey}"
    // composite and columns stay plaintext (single-tenant hosts unchanged, and legacy rows keep resolving).
    private async Task<string> LoginTokenAsync(string provider, string providerKey, CancellationToken ct)
        => await _tokenizer.TokenizeAsync($"{provider}|{providerKey}", ct);

    private static string LoginReverseRk(string token) => $"{UserLoginEntity.LoginRowKeyPrefix}{token}";

    private async Task EncryptLoginAsync(UserLoginEntity e, CancellationToken ct)
    {
        e.ProviderKey = await _cipher.ProtectAsync(e.ProviderKey, ct);
        if (!string.IsNullOrEmpty(e.DisplayName)) e.DisplayName = await _cipher.ProtectAsync(e.DisplayName, ct);
    }

    private async Task DecryptLoginAsync(UserLoginEntity e, CancellationToken ct)
    {
        e.ProviderKey = await _cipher.ResolveAsync(e.ProviderKey, ct);
        if (!string.IsNullOrEmpty(e.DisplayName)) e.DisplayName = await _cipher.ResolveAsync(e.DisplayName, ct);
    }

    private async Task<UserLoginEntity?> TryGetLoginAsync(string pk, CancellationToken ct)
    {
        try { return (await userLoginsTable.GetEntityAsync<UserLoginEntity>(pk, UserLoginEntity.LookupRowKey, cancellationToken: ct)).Value; }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    private async Task TryDeleteLoginAsync(string pk, string rk, CancellationToken ct)
    {
        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteAsync("UserLogins", pk, rk, ct);
        try
        {
            await userLoginsTable.DeleteEntityAsync(pk, rk, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task AddLoginAsync(ExternalLoginInfo login, CancellationToken ct = default)
    {
        var token = await LoginTokenAsync(login.Provider, login.ProviderKey, ct);

        var forward = UserLoginEntity.FromModelForward(login);
        forward.PartitionKey = _partitioner.PK(token);           // tokenized lookup key

        var reverse = UserLoginEntity.FromModelReverse(login);
        reverse.PartitionKey = _partitioner.PK(login.UserId);
        reverse.RowKey = LoginReverseRk(token);                  // "login|{token}"

        // Encrypt ProviderKey (+ DisplayName) ONCE and share the ciphertext across the forward and
        // reverse rows — one batch round-trip instead of four singles. Decrypt-equivalent: AES-GCM
        // ciphertext is randomized, but nothing requires the two rows' ciphertexts to differ.
        var toProtect = new List<string> { forward.ProviderKey };
        if (!string.IsNullOrEmpty(forward.DisplayName)) toProtect.Add(forward.DisplayName);
        var ciphertexts = await _cipher.ProtectManyAsync(toProtect, ct);
        forward.ProviderKey = reverse.ProviderKey = ciphertexts[0];
        if (ciphertexts.Count > 1) forward.DisplayName = reverse.DisplayName = ciphertexts[1];

        await userLoginsTable.UpsertEntityAsync(forward, TableUpdateMode.Replace, ct);
        await userLoginsTable.UpsertEntityAsync(reverse, TableUpdateMode.Replace, ct);
        await LogUpsertAsync("UserLogins", forward.PartitionKey, forward.RowKey, ct);
        await LogUpsertAsync("UserLogins", reverse.PartitionKey, reverse.RowKey, ct);
    }

    public async Task RemoveLoginAsync(string userId, string provider, string providerKey, CancellationToken ct = default)
    {
        var token = await LoginTokenAsync(provider, providerKey, ct);
        var reversePk = _partitioner.PK(userId);

        await TryDeleteLoginAsync(_partitioner.PK(token), UserLoginEntity.LookupRowKey, ct);   // forward
        await TryDeleteLoginAsync(reversePk, LoginReverseRk(token), ct);                        // reverse
        if (_indexTokenized)
        {
            // Also drop any not-yet-migrated legacy rows keyed on the plaintext composite.
            await TryDeleteLoginAsync(_partitioner.PK($"{provider}|{providerKey}"), UserLoginEntity.LookupRowKey, ct);
            await TryDeleteLoginAsync(reversePk, $"{UserLoginEntity.LoginRowKeyPrefix}{provider}|{providerKey}", ct);
        }
    }

    public async Task<ExternalLoginInfo?> FindLoginAsync(string provider, string providerKey, CancellationToken ct = default)
    {
        var token = await LoginTokenAsync(provider, providerKey, ct);
        var entity = await TryGetLoginAsync(_partitioner.PK(token), ct);
        // Migration-window fallback: a not-yet-re-keyed row still lives at the legacy plaintext PK.
        if (entity is null && _indexTokenized)
            entity = await TryGetLoginAsync(_partitioner.PK($"{provider}|{providerKey}"), ct);
        if (entity is null) return null;
        await DecryptLoginAsync(entity, ct);
        return entity.ToModel();
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
            await DecryptLoginAsync(entity, ct);
            results.Add(entity.ToModel());
        }

        return results;
    }
}
