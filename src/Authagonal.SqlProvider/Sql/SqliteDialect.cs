using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Authagonal.SqlProvider.Sql;

/// <summary>
/// SQLite dialect — the zero-dependency single-node backend: one file, no server, no emulator. Suits
/// the quick-start, embedded library hosts, CI, and small self-hosted deployments.
/// <para>
/// Writers are serialized by SQLite itself, so this is a single-node backend by construction: the
/// clustering layer's in-process lease/bus are the right pairing (leader election across pods needs
/// PostgreSQL). WAL plus a busy timeout keeps concurrent readers off the writer's back; a shared
/// in-memory database is kept alive by one held-open connection, since the last connection closing
/// would otherwise drop the schema and every row.
/// </para>
/// </summary>
public sealed class SqliteDialect : ISqlDialect, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAlive;

    public SqliteDialect(string connectionString)
    {
        _connectionString = connectionString;

        // "Data Source=:memory:" is per-connection; only the shared cache form is addressable from
        // more than one connection, and only for as long as one stays open.
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode == SqliteOpenMode.Memory || builder.DataSource is ":memory:")
        {
            builder.Mode = SqliteOpenMode.Memory;
            builder.Cache = SqliteCacheMode.Shared;
            _connectionString = builder.ToString();
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
        }
    }

    public string Name => "sqlite";

    public DbConnection CreateConnection() => new SqliteConnection(_connectionString);

    public async Task PrepareAsync(DbConnection connection, CancellationToken ct)
    {
        // WAL lets readers run while a write is in flight; busy_timeout makes a contended writer wait
        // rather than throw SQLITE_BUSY. NORMAL synchronous is the standard WAL pairing — a crash can
        // lose the tail of the last transaction only on power loss, not on process death.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public string TableRef(string table) => $"\"{SqlNames.Identifier(table)}\"";

    public string AttrExpression(string attribute, string? alias = null)
    {
        var column = alias is null ? "attrs" : $"{SqlNames.Identifier(alias)}.attrs";
        return $"json_extract({column}, '$.{SqlNames.Attribute(attribute)}')";
    }

    public string AttrsParameter => "@attrs";

    /// <summary>
    /// Nothing to verify. SQLite's default BINARY collation is byte-ordinal already, and there is no
    /// database-wide default that a table could inherit something else from.
    /// </summary>
    public Task VerifyTableAsync(DbConnection connection, string table, CancellationToken ct) => Task.CompletedTask;

    public IReadOnlyList<string> CreateTableStatements(string table)
    {
        var name = SqlNames.Identifier(table);
        var reference = TableRef(table);
        return
        [
            $$"""
              CREATE TABLE IF NOT EXISTS {{reference}} (
                  pk         TEXT    NOT NULL,
                  sk         TEXT    NOT NULL,
                  data       TEXT,
                  attrs      TEXT    NOT NULL DEFAULT '{}',
                  version    INTEGER NOT NULL DEFAULT 0,
                  expires_at TEXT,
                  PRIMARY KEY (pk, sk)
              )
              """,
            $"CREATE INDEX IF NOT EXISTS \"ix_{name}_sk_pk\" ON {reference} (sk, pk)",
            $"CREATE INDEX IF NOT EXISTS \"ix_{name}_expires\" ON {reference} (expires_at) WHERE expires_at IS NOT NULL",
        ];
    }

    public async ValueTask DisposeAsync()
    {
        if (_keepAlive is not null) await _keepAlive.DisposeAsync().ConfigureAwait(false);
    }
}
