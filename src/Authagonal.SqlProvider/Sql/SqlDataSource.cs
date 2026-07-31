using System.Collections.Concurrent;
using System.Data.Common;

namespace Authagonal.SqlProvider.Sql;

/// <summary>
/// Owns the connection factory and the (idempotent) schema provisioning for one database — the
/// counterpart of an <c>IAmazonDynamoDB</c> plus <c>DynamoTableProvisioner</c>. Connections are
/// short-lived and pooled by the driver; nothing here holds one across an <c>await</c> that yields to
/// caller code, so a store is free to write while enumerating another table.
/// </summary>
public sealed class SqlDataSource(ISqlDialect dialect) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, bool> _provisioned = new(StringComparer.Ordinal);

    public ISqlDialect Dialect => dialect;

    /// <summary>An open, prepared connection. The caller disposes it.</summary>
    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = dialect.CreateConnection();
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await dialect.PrepareAsync(connection, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates the table and its indexes if absent. Idempotent and safe to race — every statement is
    /// <c>IF NOT EXISTS</c>, so a second pod running the same DDL concurrently is a no-op. A table
    /// already provisioned out-of-band (Terraform, a DBA's migration) is left exactly as it is.
    /// </summary>
    public async Task EnsureTableAsync(string table, CancellationToken ct = default)
    {
        if (_provisioned.ContainsKey(table)) return;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        foreach (var statement in dialect.CreateTableStatements(table))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = statement;

            try
            {
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (DbException ex) when (IsConcurrentDdlRace(ex))
            {
                // IF NOT EXISTS is not actually race-free on PostgreSQL: two backends running CREATE
                // TABLE / CREATE INDEX IF NOT EXISTS concurrently can both pass the existence check
                // and one then fails on the unique constraint over pg_class (23505 / 42P07 / 42P16).
                // The exception surfaced out of DI registration, so a rolling deploy that started two
                // pods at once crash-looped one of them — an availability failure caused by nothing
                // more than the normal startup path. The losing statement's work is done by the
                // winner, so the outcome is identical either way.
                //
                // Narrow on purpose: only the duplicate-object codes are swallowed. A permissions
                // error or a syntax error still surfaces, because those are real misconfiguration.
            }
        }

        _provisioned[table] = true;
    }

    /// <summary>
    /// True for the errors two backends produce when they race the same CREATE ... IF NOT EXISTS.
    /// </summary>
    private static bool IsConcurrentDdlRace(DbException ex)
    {
        // PostgreSQL SQLSTATEs: unique_violation (a race on the pg_class index), duplicate_table,
        // duplicate_object. SQLite serialises DDL and does not reach here.
        var sqlState = ex.SqlState;
        return sqlState is "23505" or "42P07" or "42P16" or "42710";
    }

    /// <summary>Provisions <paramref name="tables"/> and returns a <see cref="SqlTable"/> for each.</summary>
    public async Task<IReadOnlyDictionary<string, SqlTable>> EnsureTablesAsync(IEnumerable<string> tables, CancellationToken ct = default)
    {
        var result = new Dictionary<string, SqlTable>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            await EnsureTableAsync(table, ct).ConfigureAwait(false);
            result[table] = new SqlTable(this, table);
        }
        return result;
    }

    public ValueTask DisposeAsync()
        => dialect is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
}
