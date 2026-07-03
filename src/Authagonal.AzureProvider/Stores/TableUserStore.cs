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
    ITombstoneWriter? tombstoneWriter = null,
    IFieldCipher? fieldCipher = null,
    IIndexTokenizer? indexTokenizer = null,
    TableClient? userEmailDomainsTable = null) : IUserStore
{
    private readonly EnvPartitioner _partitioner = partitioner; // Phase B2 will wrap PartitionKeys with _partitioner.PK
    // Name-index tables are optional. When null (Storage:NameIndexesEnabled=false),
    // CreateAsync/UpdateAsync/DeleteAsync skip the index writes entirely and
    // SearchAsync degrades from "email + name prefix" to "email prefix only".
    // The index entities all share PartitionKey="all", so at multi-million-user
    // scale the writes go through a single hot partition — disabling them is the
    // right call when name search isn't a product feature.

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

    // Delete a normalized email's lookup row. Removes the tokenized key and, while tokenization is on, also
    // the legacy plaintext key (a row written before backfill), so an email change/delete can't orphan either.
    private async Task DeleteEmailIndexAsync(string normalizedEmail, CancellationToken ct)
    {
        var tokenPk = await EmailIndexPkAsync(normalizedEmail, ct);
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
        try
        {
            await userEmailsTable.DeleteEntityAsync(pk, UserEmailEntity.LookupRowKey, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("UserEmails", pk, UserEmailEntity.LookupRowKey, ct);
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

    private async Task TryDeleteExternalIdAsync(string pk, CancellationToken ct)
    {
        try
        {
            await userExternalIdsTable.DeleteEntityAsync(pk, UserExternalIdEntity.LookupRowKey, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("UserExternalIds", pk, UserExternalIdEntity.LookupRowKey, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    // Email-domain index ("all users @X"). Optional (null table → feature off). PartitionKey = tokenized
    // domain, RowKey = userId. Unlike email/externalId this is a NEW index, so there are no pre-existing
    // plaintext rows to migrate — but if a tenant is enabled after some rows were written plaintext, the
    // search dual-reads and the backfill rewrites, same as the other indexes.
    private async Task<string> DomainIndexPkAsync(string domain, CancellationToken ct)
        => _partitioner.PK(await _tokenizer.TokenizeAsync(domain, ct));

    private async Task WriteDomainIndexAsync(string? normalizedEmail, string userId, CancellationToken ct)
    {
        if (userEmailDomainsTable is null) return;
        var domain = UserEmailDomainEntity.DomainOf(normalizedEmail);
        if (domain is null) return;
        var entity = new UserEmailDomainEntity { PartitionKey = await DomainIndexPkAsync(domain, ct), RowKey = userId, UserId = userId };
        await userEmailDomainsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    private async Task DeleteDomainIndexAsync(string? normalizedEmail, string userId, CancellationToken ct)
    {
        if (userEmailDomainsTable is null) return;
        var domain = UserEmailDomainEntity.DomainOf(normalizedEmail);
        if (domain is null) return;
        var tokenPk = await DomainIndexPkAsync(domain, ct);
        await TryDeleteDomainAsync(tokenPk, userId, ct);
        if (_indexTokenized)
        {
            var plainPk = _partitioner.PK(domain);
            if (!string.Equals(plainPk, tokenPk, StringComparison.Ordinal))
                await TryDeleteDomainAsync(plainPk, userId, ct);
        }
    }

    private async Task TryDeleteDomainAsync(string pk, string userId, CancellationToken ct)
    {
        try
        {
            await userEmailDomainsTable!.DeleteEntityAsync(pk, userId, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("UserEmailDomains", pk, userId, ct);
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
    private const int NamePrefixMax = 16;  // cap on indexed prefix length: bounds rows/name; a longer query matches the first 16 chars

    private static IReadOnlyList<string> NamePrefixesOf(string normalizedName)
    {
        if (normalizedName.Length < NamePrefixMin) return [normalizedName];
        var hi = Math.Min(normalizedName.Length, NamePrefixMax);
        var prefixes = new List<string>(hi - NamePrefixMin + 1);
        for (var len = NamePrefixMin; len <= hi; len++)
            prefixes.Add(normalizedName[..len]);
        return prefixes;
    }

    private async Task WriteNameIndexAsync(TableClient table, string normalizedName, string userId, CancellationToken ct)
    {
        if (_indexTokenized)
        {
            var tokens = await _tokenizer.TokenizeBatchAsync(NamePrefixesOf(normalizedName), ct);
            foreach (var token in tokens)
                await table.UpsertEntityAsync(new TableEntity(_partitioner.PK(token), userId) { ["UserId"] = userId }, TableUpdateMode.Replace, ct);
            return;
        }
        var pk = _partitioner.PK(UserFirstNameEntity.GetPartitionKey(normalizedName));
        var rk = UserFirstNameEntity.MakeRowKey(normalizedName, userId);
        await table.UpsertEntityAsync(new TableEntity(pk, rk) { ["UserId"] = userId }, TableUpdateMode.Replace, ct);
    }

    private async Task DeleteNameIndexAsync(TableClient table, string normalizedName, string userId, string tombstoneTable, CancellationToken ct)
    {
        if (_indexTokenized)
        {
            var tokens = await _tokenizer.TokenizeBatchAsync(NamePrefixesOf(normalizedName), ct);
            foreach (var token in tokens)
                await TryDeleteRowAsync(table, _partitioner.PK(token), userId, tombstoneTable, ct);
            // Migration window: also remove any legacy row for this name (single, old-scheme row).
        }
        var legacyPk = _partitioner.PK(UserFirstNameEntity.GetPartitionKey(normalizedName));
        await TryDeleteRowAsync(table, legacyPk, UserFirstNameEntity.MakeRowKey(normalizedName, userId), tombstoneTable, ct);
    }

    private async Task TryDeleteRowAsync(TableClient table, string pk, string rk, string tombstoneTable, CancellationToken ct)
    {
        try
        {
            await table.DeleteEntityAsync(pk, rk, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync(tombstoneTable, pk, rk, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    // Collect userIds whose name (in the given first/last table) starts with `prefix`. Off: legacy range
    // scan. Tokenized: exact-match on the prefix token (capped at NamePrefixMax), plus the legacy range scan
    // for migration-window rows not yet backfilled. Queries shorter than NamePrefixMin don't hit the index.
    private async Task<List<string>> SearchNameIndexAsync(TableClient? table, string prefix, string prefixEnd, int maxResults, CancellationToken ct)
    {
        if (table is null || prefix.Length < NamePrefixMin) return [];

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

        if (_indexTokenized)
        {
            var lookup = prefix.Length > NamePrefixMax ? prefix[..NamePrefixMax] : prefix;
            var tokenPk = _partitioner.PK(await _tokenizer.TokenizeAsync(lookup, ct));
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
    private static bool ShouldProtect(string? value)
        => !string.IsNullOrEmpty(value) && value != "{}";

    private async Task<string?> ProtectFieldAsync(string? plaintext, CancellationToken ct)
        => ShouldProtect(plaintext) ? await _cipher.ProtectAsync(plaintext!, ct) : plaintext;

    // ResolveAsync passes legacy plaintext through unchanged, so this is safe on un-migrated rows.
    private async Task<string?> ResolveFieldAsync(string? stored, CancellationToken ct)
        => string.IsNullOrEmpty(stored) ? stored : await _cipher.ResolveAsync(stored, ct);

    // Encrypt the at-rest PII fields on a freshly-mapped entity, just before a table write. Email and
    // names are encrypted alongside phone/company/attrs; the blind indexes (keyed on the plaintext, via
    // the tokenizer) are what keep them findable. Email/NormalizedEmail are required (non-empty) so they
    // always encrypt; the `?? e.X` guards only the theoretical empty case.
    private async Task EncryptEntityAsync(UserEntity e, CancellationToken ct)
    {
        e.Email = await ProtectFieldAsync(e.Email, ct) ?? e.Email;
        e.NormalizedEmail = await ProtectFieldAsync(e.NormalizedEmail, ct) ?? e.NormalizedEmail;
        e.FirstName = await ProtectFieldAsync(e.FirstName, ct);
        e.LastName = await ProtectFieldAsync(e.LastName, ct);
        e.Phone = await ProtectFieldAsync(e.Phone, ct);
        e.CompanyName = await ProtectFieldAsync(e.CompanyName, ct);
        e.CustomAttributesJson = await ProtectFieldAsync(e.CustomAttributesJson, ct) ?? "{}";
    }

    // Decrypt the at-rest PII fields on an entity read from the table, before ToModel() (or before its
    // email/name is used for index-key computation).
    private async Task DecryptEntityAsync(UserEntity e, CancellationToken ct)
    {
        e.Email = await ResolveFieldAsync(e.Email, ct) ?? e.Email;
        e.NormalizedEmail = await ResolveFieldAsync(e.NormalizedEmail, ct) ?? e.NormalizedEmail;
        e.FirstName = await ResolveFieldAsync(e.FirstName, ct);
        e.LastName = await ResolveFieldAsync(e.LastName, ct);
        e.Phone = await ResolveFieldAsync(e.Phone, ct);
        e.CompanyName = await ResolveFieldAsync(e.CompanyName, ct);
        e.CustomAttributesJson = await ResolveFieldAsync(e.CustomAttributesJson, ct) ?? "{}";
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
        var emailEntity = UserEmailEntity.Create(user.NormalizedEmail, user.Id);
        emailEntity.PartitionKey = await EmailIndexPkAsync(user.NormalizedEmail, ct);

        await usersTable.AddEntityAsync(userEntity, ct);
        await userEmailsTable.UpsertEntityAsync(emailEntity, TableUpdateMode.Replace, ct);
        await WriteDomainIndexAsync(user.NormalizedEmail, user.Id, ct);

        var normFirst = Normalize(user.FirstName);
        if (normFirst is not null && userFirstNamesTable is not null)
            await WriteNameIndexAsync(userFirstNamesTable, normFirst, user.Id, ct);

        var normLast = Normalize(user.LastName);
        if (normLast is not null && userLastNamesTable is not null)
            await WriteNameIndexAsync(userLastNamesTable, normLast, user.Id, ct);
    }

    public async Task UpdateAsync(AuthUser user, CancellationToken ct = default)
    {
        // Fetch the existing entity to check if email changed
        try
        {
            var existing = await usersTable.GetEntityAsync<UserEntity>(
                _partitioner.PK(user.Id), UserEntity.ProfileRowKey, cancellationToken: ct);
            // Decrypt first: the old email/names drive old-index-key removal, and must be plaintext (never
            // the stored ciphertext) to recompute the right tokens.
            await DecryptEntityAsync(existing.Value, ct);

            var oldNormalizedEmail = existing.Value.NormalizedEmail;
            var newNormalizedEmail = user.NormalizedEmail;

            var userEntity = UserEntity.FromModel(user);
            userEntity.PartitionKey = _partitioner.PK(userEntity.PartitionKey);
            await EncryptEntityAsync(userEntity, ct);
            await usersTable.UpsertEntityAsync(userEntity, TableUpdateMode.Replace, ct);

            if (!string.Equals(oldNormalizedEmail, newNormalizedEmail, StringComparison.Ordinal))
            {
                // Remove the old email index (tokenized + any legacy plaintext row), add the new one.
                await DeleteEmailIndexAsync(oldNormalizedEmail, ct);

                var emailEntity = UserEmailEntity.Create(newNormalizedEmail, user.Id);
                emailEntity.PartitionKey = await EmailIndexPkAsync(newNormalizedEmail, ct);
                await userEmailsTable.UpsertEntityAsync(emailEntity, TableUpdateMode.Replace, ct);

                // Domain index: only rewrite when the domain part actually changed (a@acme → b@acme keeps it).
                if (!string.Equals(UserEmailDomainEntity.DomainOf(oldNormalizedEmail), UserEmailDomainEntity.DomainOf(newNormalizedEmail), StringComparison.Ordinal))
                {
                    await DeleteDomainIndexAsync(oldNormalizedEmail, user.Id, ct);
                    await WriteDomainIndexAsync(newNormalizedEmail, user.Id, ct);
                }
            }

            var oldFirst = Normalize(existing.Value.FirstName);
            var newFirst = Normalize(user.FirstName);
            if (userFirstNamesTable is not null && !string.Equals(oldFirst, newFirst, StringComparison.Ordinal))
            {
                if (oldFirst is not null) await DeleteNameIndexAsync(userFirstNamesTable, oldFirst, user.Id, "UserFirstNames", ct);
                if (newFirst is not null) await WriteNameIndexAsync(userFirstNamesTable, newFirst, user.Id, ct);
            }

            var oldLast = Normalize(existing.Value.LastName);
            var newLast = Normalize(user.LastName);
            if (userLastNamesTable is not null && !string.Equals(oldLast, newLast, StringComparison.Ordinal))
            {
                if (oldLast is not null) await DeleteNameIndexAsync(userLastNamesTable, oldLast, user.Id, "UserLastNames", ct);
                if (newLast is not null) await WriteNameIndexAsync(userLastNamesTable, newLast, user.Id, ct);
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
            // Decrypt first: the stored email/names must be plaintext to recompute the index keys to remove.
            await DecryptEntityAsync(existing.Value, ct);

            // Delete email index (tokenized + any legacy plaintext row) + the domain-index row
            await DeleteEmailIndexAsync(existing.Value.NormalizedEmail, ct);
            await DeleteDomainIndexAsync(existing.Value.NormalizedEmail, userId, ct);

            // Delete name indexes (all prefix-token rows + any legacy row)
            var normFirst = Normalize(existing.Value.FirstName);
            if (normFirst is not null && userFirstNamesTable is not null)
                await DeleteNameIndexAsync(userFirstNamesTable, normFirst, userId, "UserFirstNames", ct);

            var normLast = Normalize(existing.Value.LastName);
            if (normLast is not null && userLastNamesTable is not null)
                await DeleteNameIndexAsync(userLastNamesTable, normLast, userId, "UserLastNames", ct);

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

        // 2. Email lookup — write the current-scheme row FIRST (no login gap), then drop the legacy row.
        var emailEntity = UserEmailEntity.Create(normalizedEmail, userId);
        emailEntity.PartitionKey = await EmailIndexPkAsync(normalizedEmail, ct);
        await userEmailsTable.UpsertEntityAsync(emailEntity, TableUpdateMode.Replace, ct);
        if (_indexTokenized)
        {
            var plainPk = _partitioner.PK(normalizedEmail);
            if (!string.Equals(plainPk, emailEntity.PartitionKey, StringComparison.Ordinal))
                await TryDeleteEmailIndexAsync(plainPk, ct);
        }

        // 3. Domain — write current, drop legacy.
        await WriteDomainIndexAsync(normalizedEmail, userId, ct);
        if (_indexTokenized && userEmailDomainsTable is not null)
        {
            var domain = UserEmailDomainEntity.DomainOf(normalizedEmail);
            if (domain is not null)
            {
                var plainDomPk = _partitioner.PK(domain);
                if (!string.Equals(plainDomPk, await DomainIndexPkAsync(domain, ct), StringComparison.Ordinal))
                    await TryDeleteDomainAsync(plainDomPk, userId, ct);
            }
        }

        // 4. Names — write current-scheme prefix rows, drop the single legacy row.
        if (normFirst is not null && userFirstNamesTable is not null)
        {
            await WriteNameIndexAsync(userFirstNamesTable, normFirst, userId, ct);
            if (_indexTokenized)
                await TryDeleteRowAsync(userFirstNamesTable, _partitioner.PK(UserFirstNameEntity.GetPartitionKey(normFirst)), UserFirstNameEntity.MakeRowKey(normFirst, userId), "UserFirstNames", ct);
        }
        if (normLast is not null && userLastNamesTable is not null)
        {
            await WriteNameIndexAsync(userLastNamesTable, normLast, userId, ct);
            if (_indexTokenized)
                await TryDeleteRowAsync(userLastNamesTable, _partitioner.PK(UserFirstNameEntity.GetPartitionKey(normLast)), UserFirstNameEntity.MakeRowKey(normLast, userId), "UserLastNames", ct);
        }
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
            await DecryptEntityAsync(entity, ct);
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
            await DecryptEntityAsync(entity, ct);
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
        // Email prefix range scan works only on plaintext keys. With blind-index tokenization on, email keys
        // are unordered HMAC tokens so prefix search over them is impossible — email search degrades to exact
        // match (already handled above via FindByEmailAsync). Name prefix search is unaffected here.
        var emailLo = _partitioner.PK(prefix);
        var emailHi = _partitioner.PK(prefixEnd);
        var emailTask = _indexTokenized
            ? Task.FromResult(new List<string>())
            : CollectUserIdsAsync(
                userEmailsTable.QueryAsync<UserEmailEntity>(
                    e => e.PartitionKey.CompareTo(emailLo) >= 0 && e.PartitionKey.CompareTo(emailHi) < 0,
                    cancellationToken: ct),
                e => e.UserId, maxResults, ct);

        // Name search needs a query of at least NamePrefixMin chars (admin search UIs enforce this).
        // SearchNameIndexAsync picks the scheme per row: legacy range scan when off, exact prefix-token
        // lookup (+ legacy fallback for un-backfilled rows) when tokenized. Email prefix search is dropped
        // when tokenized (unordered keys) — exact email match is handled above via FindByEmailAsync.
        var firstNameTask = SearchNameIndexAsync(userFirstNamesTable, prefix, prefixEnd, maxResults, ct);
        var lastNameTask = SearchNameIndexAsync(userLastNamesTable, prefix, prefixEnd, maxResults, ct);

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

    public async Task<IReadOnlyList<AuthUser>> SearchByEmailDomainAsync(string domain, int maxResults = 50, CancellationToken ct = default)
    {
        if (userEmailDomainsTable is null || string.IsNullOrWhiteSpace(domain))
            return [];

        var normDomain = domain.Trim().ToUpperInvariant();
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        async Task CollectAsync(string pk)
        {
            var query = userEmailDomainsTable.QueryAsync<UserEmailDomainEntity>(e => e.PartitionKey == pk, cancellationToken: ct);
            await foreach (var e in query.WithCancellation(ct))
            {
                if (seen.Add(e.UserId)) ids.Add(e.UserId);
                if (ids.Count >= maxResults) break;
            }
        }

        await CollectAsync(await DomainIndexPkAsync(normDomain, ct));
        // Migration window: also sweep any legacy plaintext-keyed rows written before tokenization.
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
        entity.PartitionKey = await ExternalIdIndexPkAsync(clientId, externalId, ct);
        await userExternalIdsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task RemoveExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default)
    {
        // Delete the tokenized row and, while tokenization is on, any legacy plaintext row too.
        var tokenPk = await ExternalIdIndexPkAsync(clientId, externalId, ct);
        await TryDeleteExternalIdAsync(tokenPk, ct);
        if (_indexTokenized)
        {
            var plainPk = _partitioner.PK($"{clientId}|{externalId}");
            if (!string.Equals(plainPk, tokenPk, StringComparison.Ordinal))
                await TryDeleteExternalIdAsync(plainPk, ct);
        }
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
