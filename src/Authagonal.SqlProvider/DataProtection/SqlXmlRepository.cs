using System.Xml.Linq;
using Authagonal.SqlProvider.Sql;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Authagonal.SqlProvider.DataProtection;

/// <summary>
/// Persists the ASP.NET DataProtection key ring to the same database (one row per key element), so the
/// ring survives restarts and is shared across pods — the self-hosted counterpart to
/// <c>PersistKeysToAzureBlobStorage</c> and the S3 repository.
/// <para>
/// Without this the ring is in-memory: cookies and antiforgery tokens break on restart, and every pod
/// mints its own keys, so a request served by a different pod fails to decrypt.
/// <see cref="IXmlRepository"/> is synchronous, so these (infrequent: startup and rotation) calls
/// block.
/// </para>
/// </summary>
public sealed class SqlXmlRepository(SqlTable table) : IXmlRepository
{
    private const string Partition = "dataprotection";

    public IReadOnlyCollection<XElement> GetAllElements() => GetAllAsync().GetAwaiter().GetResult();

    private async Task<IReadOnlyCollection<XElement>> GetAllAsync()
    {
        var elements = new List<XElement>();
        await foreach (var row in table.QueryPartitionAsync(Partition).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(row.Data)) continue;
            elements.Add(XElement.Parse(row.Data));
        }
        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        var name = string.IsNullOrWhiteSpace(friendlyName) ? Guid.NewGuid().ToString("N") : friendlyName;
        table.PutAsync(new SqlRow(Partition, name) { Data = element.ToString(SaveOptions.DisableFormatting) })
            .GetAwaiter().GetResult();
    }
}
