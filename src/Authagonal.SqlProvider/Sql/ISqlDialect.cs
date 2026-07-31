using System.Data.Common;

namespace Authagonal.SqlProvider.Sql;

/// <summary>
/// The (small) set of differences between the supported SQL backends. Everything the stores do —
/// point reads, upserts, conditional updates, keyset paging, prefix scans — is expressed in SQL that
/// both PostgreSQL and SQLite accept verbatim (<c>INSERT … ON CONFLICT … DO UPDATE … WHERE</c>,
/// <c>DELETE … RETURNING</c>, row-value comparison <c>(pk, sk) &gt; (…)</c>), so a dialect only has to
/// supply the connection, the DDL, and the JSON accessor.
/// </summary>
public interface ISqlDialect
{
    /// <summary>Backend name for logs and diagnostics ("postgres" / "sqlite").</summary>
    string Name { get; }

    /// <summary>A new, unopened connection. Pooling (where the driver has it) is the driver's job.</summary>
    DbConnection CreateConnection();

    /// <summary>Per-connection setup run once after opening (SQLite pragmas; a no-op on PostgreSQL).</summary>
    Task PrepareAsync(DbConnection connection, CancellationToken ct);

    /// <summary>The quoted, schema-qualified name to reference <paramref name="table"/> by in SQL.</summary>
    string TableRef(string table);

    /// <summary>
    /// SQL expression reading a string attribute out of the <c>attrs</c> JSON column, optionally
    /// qualified by a table alias (needed inside <c>ON CONFLICT DO UPDATE … WHERE</c>, where the
    /// existing row has to be told apart from <c>excluded</c>).
    /// <paramref name="attribute"/> is always a compile-time constant from this assembly, never user
    /// input, and <see cref="SqlNames.Attribute"/> re-checks that before it reaches here.
    /// </summary>
    string AttrExpression(string attribute, string? alias = null);

    /// <summary>
    /// Placeholder for the <c>attrs</c> parameter in a write. PostgreSQL needs an explicit cast to
    /// jsonb; SQLite stores the JSON as text.
    /// </summary>
    string AttrsParameter { get; }

    /// <summary>DDL creating the generic key-value table (and its indexes) if absent. Idempotent.</summary>
    IReadOnlyList<string> CreateTableStatements(string table);

    /// <summary>
    /// Verifies that an existing table actually has the properties this code's SQL depends on, and
    /// throws with the fix if it does not. Returns without doing anything when the backend has
    /// nothing to check.
    /// </summary>
    /// <remarks>
    /// The DDL is all <c>IF NOT EXISTS</c>, so against a table provisioned out-of-band it verified
    /// nothing at all. On PostgreSQL that matters: the byte-ordinal <c>COLLATE "C"</c> pin on the key
    /// columns is load-bearing, not tidiness. Every range predicate, the prefix successor used for
    /// <c>sk</c> scans, and the ISO-8601 timestamp comparisons in the expiry sweep all assume byte
    /// ordering. A table created without the pin inherits the database's collation — and managed
    /// PostgreSQL commonly defaults to ICU or en_US.UTF-8 — under which those comparisons quietly
    /// mean something else and UNDER-match: a prefix scan for <c>"login|"</c> returns nothing,
    /// because ICU orders <c>'}'</c> before <c>'|'</c> and the half-open upper bound falls below
    /// every row it was meant to include. Nothing fails loudly; lookups simply come back empty.
    /// </remarks>
    Task VerifyTableAsync(DbConnection connection, string table, CancellationToken ct);
}
