using Authagonal.SqlProvider.Sql;
using Testcontainers.PostgreSql;

namespace Authagonal.Tests.Infrastructure;

/// <summary>
/// Shared PostgreSQL container for the SQL-provider suites — the counterpart to
/// <see cref="AzuriteFixture"/> and <see cref="DynamoFixture"/>.
/// <para>
/// The database is created with an ICU <c>en-US</c> collation on purpose. A linguistic collation is
/// the common default on real installs (and on managed PostgreSQL), and it orders punctuation and
/// case differently from bytes — which is exactly what would break the provider's prefix bounds,
/// env-partition ranges and expiry sweeps if the key columns were not pinned to <c>COLLATE "C"</c>.
/// Testing against the hostile collation is what keeps that pin honest.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await _container.ExecScriptAsync(
            "CREATE DATABASE authagonal_icu LOCALE_PROVIDER icu ICU_LOCALE 'en-US' LOCALE 'en-US' TEMPLATE template0 ENCODING UTF8;");

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = "authagonal_icu",
        };
        ConnectionString = builder.ToString();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture> { }

/// <summary>
/// One isolated database per test: a private schema on the shared PostgreSQL container, or a private
/// shared-cache in-memory SQLite database. Both are torn down with the test instance.
/// </summary>
public static class SqlTestSource
{
    public static SqlDataSource Sqlite()
        => new(new SqliteDialect($"Data Source=authagonal-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared"));

    public static SqlDataSource Postgres(string connectionString)
        => new(new PostgresDialect(connectionString, schema: $"t{Guid.NewGuid():N}"));
}
