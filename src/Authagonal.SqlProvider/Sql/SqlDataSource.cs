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
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        _provisioned[table] = true;
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
