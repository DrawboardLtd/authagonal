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
}
