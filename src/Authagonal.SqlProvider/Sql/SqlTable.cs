using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Authagonal.SqlProvider.Sql;

/// <summary>
/// Thin wrapper over one generic key-value table, giving the Authagonal stores the same ergonomics as
/// Azure's <c>TableClient</c> and AWS's <c>DynamoTable</c>: point reads, upserts, conditional
/// single-use deletes, partition queries, bounded scans, and keyset paging.
///
/// <para>
/// Three primitives carry the safety-critical semantics, and each is a single statement — no
/// read-modify-write window, no explicit transaction, no lock held across a round trip:
/// </para>
/// <list type="bullet">
/// <item><b>Single-use redemption</b> (<see cref="DeleteIfExistsReturningAsync"/>) is
/// <c>DELETE … RETURNING</c>. Exactly one concurrent caller gets the row back; everyone else sees
/// nothing. Same anti-replay guarantee as Azure's ETag delete and DynamoDB's conditional delete.</item>
/// <item><b>Compare-and-set</b> (<see cref="UpdateIfAttrNullAsync"/>, <see cref="UpdateAttrsAsync"/>)
/// is an <c>UPDATE … WHERE</c> on the guard attribute or the version column, with the affected-row
/// count as the verdict — an update that matches nothing means someone else won.</item>
/// <item><b>Acquire-or-renew</b> (<see cref="TryAcquireLeaseAsync"/>) is an upsert whose
/// <c>DO UPDATE … WHERE</c> admits only an expired lease or the current holder, which is what gives
/// leader election at-most-one-holder without any lease primitive in the database.</item>
/// </list>
///
/// <para>
/// Enumeration is chunked rather than streamed from a live reader. That is deliberate: the stores
/// routinely delete rows while walking a query (index cleanup, expiry sweeps), and on SQLite a write
/// issued under an open reader on the same database would contend with itself. Buffering a page at a
/// time keeps those loops correct on every backend and bounds memory on large tables.
/// </para>
/// </summary>
public sealed class SqlTable(SqlDataSource source, string name)
{
    /// <summary>Rows fetched per round trip when enumerating. Bounds memory without chattiness.</summary>
    private const int ChunkSize = 500;

    private readonly string _ref = source.Dialect.TableRef(name);

    /// <summary>The logical table name, as used in change-log/tombstone records.</summary>
    public string Name => name;

    /// <summary>The owning data source, for the few callers that need their own command.</summary>
    public SqlDataSource Source => source;

    // ── reads ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Point read. Returns null when the row is absent. <paramref name="includeData"/> = false skips
    /// the document column entirely — the login-stamp path uses it so a hot-path write never reads
    /// (or decrypts) the profile.
    /// </summary>
    public async Task<SqlRow?> GetAsync(string pk, string sk, bool includeData = true, CancellationToken ct = default)
    {
        await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns(includeData)} FROM {_ref} WHERE pk = @pk AND sk = @sk";
        Add(cmd, "@pk", pk);
        Add(cmd, "@sk", sk);

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRow(reader, includeData) : null;
    }

    /// <summary>
    /// Every row matching <paramref name="filter"/>, in (pk, sk) order, fetched a chunk at a time.
    /// </summary>
    public async IAsyncEnumerable<SqlRow> QueryAsync(SqlKeyFilter filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        (string Pk, string Sk)? after = null;
        while (true)
        {
            var page = await FetchAsync(filter, after, ChunkSize, ct).ConfigureAwait(false);
            foreach (var row in page) yield return row;
            if (page.Count < ChunkSize) yield break;
            var last = page[^1];
            after = (last.Pk, last.Sk);
        }
    }

    /// <summary>Every row in one partition, in sort-key order.</summary>
    public IAsyncEnumerable<SqlRow> QueryPartitionAsync(string pk, CancellationToken ct = default)
        => QueryAsync(SqlKeyFilter.Partition(pk), ct);

    /// <summary>
    /// One page with an explicit resume key — the native-continuation primitive behind cursor paging
    /// (<c>IUserStore.ListPageAsync</c>). Resuming is a seek on the primary key, so page N costs the
    /// same as page 1 rather than re-walking (and re-decrypting) everything skipped.
    /// </summary>
    public async Task<(IReadOnlyList<SqlRow> Rows, string? NextToken)> ScanPageAsync(
        SqlKeyFilter filter, string? continuationToken, int limit, CancellationToken ct = default)
    {
        var after = DecodeToken(continuationToken);
        var rows = await FetchAsync(filter, after, limit, ct).ConfigureAwait(false);
        // A short page means the filter is exhausted; a full one may or may not be, and the next call
        // settles it. Never claiming "exhausted" on a full page is what keeps paging complete.
        var next = rows.Count < limit ? null : EncodeToken(rows[^1].Pk, rows[^1].Sk);
        return (rows, next);
    }

    // ── writes ───────────────────────────────────────────────────────────────────

    /// <summary>Upsert (replace). Bumps the row's version.</summary>
    public async Task PutAsync(SqlRow row, CancellationToken ct = default)
    {
        await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {_ref} AS t (pk, sk, data, attrs, version, expires_at)
            VALUES (@pk, @sk, @data, {source.Dialect.AttrsParameter}, 0, @expires)
            ON CONFLICT (pk, sk) DO UPDATE
               SET data = excluded.data,
                   attrs = excluded.attrs,
                   expires_at = excluded.expires_at,
                   version = t.version + 1
            """;
        BindRow(cmd, row);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Insert only if the key is free. True for the caller that inserted, false if the row already
    /// existed — the "first sighting?" primitive behind SAML assertion-id replay detection.
    /// </summary>
    public async Task<bool> PutIfAbsentAsync(SqlRow row, CancellationToken ct = default)
    {
        await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {_ref} (pk, sk, data, attrs, version, expires_at)
            VALUES (@pk, @sk, @data, {source.Dialect.AttrsParameter}, 0, @expires)
            ON CONFLICT (pk, sk) DO NOTHING
            """;
        BindRow(cmd, row);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Atomic compare-and-set on a guard attribute: writes the row only if it exists and the named
    /// attribute is currently absent/null. True for the one caller that made the transition — the
    /// refresh-rotation "mark consumed, exactly once" primitive. Never inserts.
    /// </summary>
    public async Task<bool> UpdateIfAttrNullAsync(SqlRow row, string guardAttribute, CancellationToken ct = default)
    {
        await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {_ref}
               SET data = @data,
                   attrs = {source.Dialect.AttrsParameter},
                   expires_at = @expires,
                   version = version + 1
             WHERE pk = @pk AND sk = @sk AND {source.Dialect.AttrExpression(guardAttribute)} IS NULL
            """;
        BindRow(cmd, row);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Read-modify-write of the promoted attributes under optimistic concurrency, retrying on a lost
    /// race. The document column is never read or written, so an encrypting store pays no cipher
    /// round-trip to stamp a login timestamp.
    /// <para>
    /// <paramref name="mutate"/> receives the current row and returns false to abandon the write.
    /// Returns true if an update landed; false if the row is gone, the mutation declined, or
    /// contention outlived <paramref name="maxAttempts"/>.
    /// </para>
    /// </summary>
    public async Task<bool> UpdateAttrsAsync(
        string pk, string sk, Func<SqlRow, bool> mutate, int maxAttempts = 5, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var row = await GetAsync(pk, sk, includeData: false, ct).ConfigureAwait(false);
            if (row is null) return false;
            if (!mutate(row)) return false;

            await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                UPDATE {_ref}
                   SET attrs = {source.Dialect.AttrsParameter},
                       version = version + 1
                 WHERE pk = @pk AND sk = @sk AND version = @version
                """;
            Add(cmd, "@pk", row.Pk);
            Add(cmd, "@sk", row.Sk);
            Add(cmd, "@attrs", SerializeAttrs(row.Attrs));
            Add(cmd, "@version", row.Version);

            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1) return true;
            // Someone else wrote first — re-read and reapply, so no increment is lost.
        }

        return false;
    }

    /// <summary>
    /// Acquire or renew a lease: insert when unheld, or take over an expired one, or extend our own.
    /// A live lease held by another node fails the <c>WHERE</c> and returns false. Single statement,
    /// so two nodes cannot both win.
    /// <para>
    /// Expiry is each writer's own clock against the stored timestamp; with a TTL well above realistic
    /// inter-node skew a brief overlap at most delays a renewal, and never yields two holders.
    /// </para>
    /// </summary>
    public async Task<bool> TryAcquireLeaseAsync(
        string pk, string sk, string owner, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        var row = new SqlRow(pk, sk) { ExpiresAt = expiresAt };
        row.PutS("owner", owner);

        await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {_ref} AS l (pk, sk, data, attrs, version, expires_at)
            VALUES (@pk, @sk, @data, {source.Dialect.AttrsParameter}, 0, @expires)
            ON CONFLICT (pk, sk) DO UPDATE
               SET attrs = excluded.attrs,
                   expires_at = excluded.expires_at,
                   version = l.version + 1
             WHERE l.expires_at < @now OR {source.Dialect.AttrExpression("owner", "l")} = @owner
            """;
        BindRow(cmd, row);
        Add(cmd, "@now", SqlAttrs.Iso(DateTimeOffset.UtcNow));
        Add(cmd, "@owner", owner);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    // ── deletes ──────────────────────────────────────────────────────────────────

    /// <summary>Unconditional delete; succeeds even if the row is already gone.</summary>
    public async Task DeleteAsync(string pk, string sk, CancellationToken ct = default)
    {
        await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_ref} WHERE pk = @pk AND sk = @sk";
        Add(cmd, "@pk", pk);
        Add(cmd, "@sk", sk);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomic single-use delete: removes the row and returns what it held, or null if it was already
    /// gone. Exactly one concurrent caller can win, which is what gives single-use grants, MFA
    /// challenges and OIDC state their anti-replay guarantee.
    /// </summary>
    public async Task<SqlRow?> DeleteIfExistsReturningAsync(string pk, string sk, CancellationToken ct = default)
    {
        await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_ref} WHERE pk = @pk AND sk = @sk RETURNING {Columns(includeData: true)}";
        Add(cmd, "@pk", pk);
        Add(cmd, "@sk", sk);

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRow(reader, includeData: true) : null;
    }

    /// <summary>
    /// Deletes the row only if the named attribute currently equals <paramref name="value"/> — the
    /// "release only what I still hold" primitive. False when the row is gone or someone else owns it.
    /// </summary>
    public async Task<bool> DeleteIfAttrEqualsAsync(
        string pk, string sk, string attribute, string value, CancellationToken ct = default)
    {
        await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"DELETE FROM {_ref} WHERE pk = @pk AND sk = @sk AND {source.Dialect.AttrExpression(attribute)} = @value";
        Add(cmd, "@pk", pk);
        Add(cmd, "@sk", sk);
        Add(cmd, "@value", value);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Deletes every row whose <c>expires_at</c> has passed. Backs <see cref="SqlExpiryReaper"/> —
    /// neither backend reaps on its own the way DynamoDB TTL does, so without this the transient
    /// tables (SAML replay, OIDC state, MFA challenges) would grow without bound. Returns the count.
    /// </summary>
    public async Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_ref} WHERE expires_at IS NOT NULL AND expires_at < @cutoff";
        Add(cmd, "@cutoff", SqlAttrs.Iso(cutoff));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ── query construction ───────────────────────────────────────────────────────

    private async Task<IReadOnlyList<SqlRow>> FetchAsync(
        SqlKeyFilter filter, (string Pk, string Sk)? after, int limit, CancellationToken ct)
    {
        var sql = new StringBuilder("SELECT ").Append(Columns(filter.IncludeData)).Append(" FROM ").Append(_ref);
        var parameters = new List<(string Name, object Value)>();
        var predicates = new List<string>();

        void Bind(string sqlFragment, string parameterName, string value)
        {
            predicates.Add(sqlFragment);
            parameters.Add((parameterName, value));
        }

        if (filter.Pk is not null) Bind("pk = @f_pk", "@f_pk", filter.Pk);
        if (filter.PkPrefix is { Length: > 0 } pkPrefix)
        {
            Bind("pk >= @f_pkp", "@f_pkp", pkPrefix);
            if (UpperBound(pkPrefix) is { } hi) Bind("pk < @f_pkph", "@f_pkph", hi);
        }
        if (filter.PkFrom is not null) Bind("pk >= @f_pkf", "@f_pkf", filter.PkFrom);
        if (filter.PkUntil is not null) Bind("pk < @f_pku", "@f_pku", filter.PkUntil);

        if (filter.Sk is not null) Bind("sk = @f_sk", "@f_sk", filter.Sk);
        if (filter.SkPrefix is { Length: > 0 } skPrefix)
        {
            Bind("sk >= @f_skp", "@f_skp", skPrefix);
            if (UpperBound(skPrefix) is { } hi) Bind("sk < @f_skph", "@f_skph", hi);
        }
        if (filter.SkAfter is not null) Bind("sk > @f_ska", "@f_ska", filter.SkAfter);
        if (filter.SkAtMost is not null) Bind("sk <= @f_skm", "@f_skm", filter.SkAtMost);
        if (filter.SkBefore is not null) Bind("sk < @f_skb", "@f_skb", filter.SkBefore);

        if (filter.AttrEquals is not null)
        {
            for (var i = 0; i < filter.AttrEquals.Count; i++)
            {
                var (attr, value) = (filter.AttrEquals[i].Key, filter.AttrEquals[i].Value);
                Bind($"{source.Dialect.AttrExpression(attr)} = @f_a{i}", $"@f_a{i}", value);
            }
        }

        if (after is { } cursor)
        {
            // Row-value comparison keeps the resume a single index seek on (pk, sk).
            predicates.Add("(pk, sk) > (@f_cpk, @f_csk)");
            parameters.Add(("@f_cpk", cursor.Pk));
            parameters.Add(("@f_csk", cursor.Sk));
        }

        if (predicates.Count > 0) sql.Append(" WHERE ").AppendJoin(" AND ", predicates);
        sql.Append(" ORDER BY pk, sk LIMIT @f_limit");

        await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql.ToString();
        foreach (var (parameterName, value) in parameters) Add(cmd, parameterName, value);
        Add(cmd, "@f_limit", limit);

        var rows = new List<SqlRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(ReadRow(reader, filter.IncludeData));
        return rows;
    }

    /// <summary>
    /// Exclusive upper bound for a prefix range: the prefix with its final character incremented, so
    /// "AB" bounds to "AC" and the scan stays a range seek instead of a LIKE. Null when there is no
    /// representable successor (a trailing U+FFFF), which just leaves the range open at the top.
    /// </summary>
    private static string? UpperBound(string prefix)
    {
        var last = prefix[^1];
        return last == char.MaxValue ? null : prefix[..^1] + (char)(last + 1);
    }

    private static string Columns(bool includeData)
        => includeData ? "pk, sk, data, attrs, version, expires_at" : "pk, sk, attrs, version, expires_at";

    private static SqlRow ReadRow(DbDataReader reader, bool includeData)
    {
        var i = 0;
        var pk = reader.GetString(i++);
        var sk = reader.GetString(i++);
        string? data = null;
        if (includeData)
        {
            data = reader.IsDBNull(i) ? null : reader.GetString(i);
            i++;
        }
        var attrs = DeserializeAttrs(reader.IsDBNull(i) ? null : reader.GetString(i));
        i++;
        var version = reader.GetInt64(i++);
        var expires = reader.IsDBNull(i) ? null : reader.GetString(i);

        return new SqlRow(pk, sk)
        {
            Data = data,
            Attrs = attrs,
            Version = version,
            ExpiresAt = expires is null ? null : DateTimeOffset.Parse(expires, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind),
        };
    }

    private void BindRow(DbCommand cmd, SqlRow row)
    {
        Add(cmd, "@pk", row.Pk);
        Add(cmd, "@sk", row.Sk);
        Add(cmd, "@data", (object?)row.Data ?? DBNull.Value);
        Add(cmd, "@attrs", SerializeAttrs(row.Attrs));
        Add(cmd, "@expires", row.ExpiresAt is { } e ? SqlAttrs.Iso(e) : DBNull.Value);
    }

    private static string SerializeAttrs(Dictionary<string, string> attrs)
        => JsonSerializer.Serialize(attrs, SqlJsonContext.Default.DictionaryStringString);

    private static Dictionary<string, string> DeserializeAttrs(string? json)
        => string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize(json, SqlJsonContext.Default.DictionaryStringString) ?? [];

    private static void Add(DbCommand cmd, string parameterName, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

    // ── continuation tokens ──────────────────────────────────────────────────────

    private static string EncodeToken(string pk, string sk)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{pk} {sk}"));

    private static (string Pk, string Sk)? DecodeToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(token)).Split(' ', 2);
            return parts.Length == 2 ? (parts[0], parts[1]) : null;
        }
        catch (FormatException)
        {
            return null; // malformed token → restart from the beginning, same as the other backends
        }
    }
}
