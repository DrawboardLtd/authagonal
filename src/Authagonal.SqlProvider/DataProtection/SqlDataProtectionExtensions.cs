using Authagonal.SqlProvider.Sql;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.SqlProvider;

/// <summary>SQL-backed DataProtection key persistence — call after <c>AddAuthagonal</c> so the key
/// ring survives restarts and is shared across pods.</summary>
public static class SqlDataProtectionExtensions
{
    public static IServiceCollection PersistDataProtectionKeysToSql(
        this IServiceCollection services, SqlDataSource source, string table = "DataProtectionKeys")
    {
        source.EnsureTableAsync(table).GetAwaiter().GetResult();
        var keys = new SqlTable(source, table);
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = new DataProtection.SqlXmlRepository(keys));
        return services;
    }
}
