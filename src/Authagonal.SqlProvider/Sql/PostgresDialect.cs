using System.Data.Common;
using Npgsql;

namespace Authagonal.SqlProvider.Sql;

/// <summary>
/// PostgreSQL dialect — the production self-hosted backend. Attributes live in a <c>jsonb</c> column
/// so the promoted-field filters (<c>attrs -&gt;&gt; 'clientId' = …</c>) are expressible without a
/// per-table schema, and a partial index on <c>expires_at</c> keeps the TTL sweep cheap.
/// Connection pooling is Npgsql's.
/// </summary>
public sealed class PostgresDialect : ISqlDialect
{
    private readonly string _connectionString;

    public PostgresDialect(string connectionString, string schema = "public", bool allowUnverifiedTls = false)
    {
        Schema = SqlNames.Identifier(schema);
        _connectionString = allowUnverifiedTls
            ? connectionString
            : RequireVerifiedTls(connectionString);
    }

    public string Name => "postgres";

    /// <summary>The schema the tables live in. Created on first provision if absent.</summary>
    public string Schema { get; }

    public DbConnection CreateConnection() => new NpgsqlConnection(_connectionString);

    /// <summary>
    /// Upgrades a connection string that says nothing about TLS to <c>SslMode=VerifyFull</c>.
    /// </summary>
    /// <remarks>
    /// Npgsql defaults to <c>SSL Mode=Prefer</c>: TLS is used only if the server offers it, the
    /// server certificate is NOT validated, and the connection silently falls back to plaintext if
    /// the server declines. Since Npgsql 6.0 <c>Require</c> does not validate either — only
    /// <c>VerifyCA</c> and <c>VerifyFull</c> do. The connection string in the package README and the
    /// install guide names no SslMode, so every documented deployment landed on Prefer, carrying the
    /// signing keys, the DataProtection key ring and every credential in the store over a link an
    /// on-path attacker can strip or impersonate.
    /// <para>
    /// An explicit choice is left alone — an operator who wrote <c>SslMode=Disable</c> for a local
    /// socket means it — but silence now resolves to the safe end rather than the unsafe one.
    /// </para>
    /// </remarks>
    internal static string RequireVerifiedTls(string connectionString)
    {
        // The untyped builder holds only the keywords actually present, which is what distinguishes
        // "not stated" from "explicitly set". NpgsqlConnectionStringBuilder cannot: being strongly
        // typed, it reports ContainsKey true for every keyword it knows about, set or not.
        var stated = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (stated.ContainsKey("ssl mode") || stated.ContainsKey("sslmode"))
            return connectionString;

        var builder = new NpgsqlConnectionStringBuilder(connectionString) { SslMode = SslMode.VerifyFull };
        return builder.ConnectionString;
    }

    public Task PrepareAsync(DbConnection connection, CancellationToken ct) => Task.CompletedTask;

    public string TableRef(string table) => $"\"{Schema}\".\"{SqlNames.Identifier(table)}\"";

    /// <remarks>
    /// The extracted text is pinned to <c>COLLATE "C"</c> for the same reason the key columns are:
    /// it comes out of jsonb carrying the database's default collation, and the comparisons built on
    /// it should mean byte equality regardless of how the database was created.
    /// </remarks>
    public string AttrExpression(string attribute, string? alias = null)
    {
        var column = alias is null ? "attrs" : $"{SqlNames.Identifier(alias)}.attrs";
        return $"(({column} ->> '{SqlNames.Attribute(attribute)}') COLLATE \"C\")";
    }

    public string AttrsParameter => "@attrs::jsonb";

    public IReadOnlyList<string> CreateTableStatements(string table)
    {
        var name = SqlNames.Identifier(table);
        var reference = TableRef(table);
        return
        [
            $"CREATE SCHEMA IF NOT EXISTS \"{Schema}\"",
            // COLLATE "C" on every column a predicate ranges over is load-bearing, not tidiness.
            // The key scheme is byte-ordinal throughout — prefix bounds ("AB" ≤ x < "AC"), the
            // env-partition range ("{env}|" ≤ pk < "{env}|~"), the grant expiry sweep
            // (sk ≤ "{day}#~"), keyset paging — all inherited from Azure Tables and DynamoDB, which
            // compare bytes. A database created with a linguistic collation (en_US.UTF-8 and ICU
            // locales are the common defaults) orders punctuation and case differently, and those
            // scans then silently return the wrong rows: expired grants stop being reaped, prefix
            // searches miss matches. Pinning the collation per column makes the layout independent
            // of however the DBA created the database.
            $$"""
              CREATE TABLE IF NOT EXISTS {{reference}} (
                  pk         TEXT   COLLATE "C" NOT NULL,
                  sk         TEXT   COLLATE "C" NOT NULL,
                  data       TEXT,
                  attrs      JSONB  NOT NULL DEFAULT '{}'::jsonb,
                  version    BIGINT NOT NULL DEFAULT 0,
                  expires_at TEXT   COLLATE "C",
                  PRIMARY KEY (pk, sk)
              )
              """,
            // Backs the "one config row per natural key" scans (sk = 'config' AND pk in env range),
            // which are otherwise sequential scans of the whole table.
            $"CREATE INDEX IF NOT EXISTS \"ix_{name}_sk_pk\" ON {reference} (sk, pk)",
            // TTL sweep: only the rows that actually carry an expiry are indexed.
            $"CREATE INDEX IF NOT EXISTS \"ix_{name}_expires\" ON {reference} (expires_at) WHERE expires_at IS NOT NULL",
        ];
    }
}
