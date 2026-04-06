using System.IO.Compression;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace Authagonal.Backup;

public sealed class RestoreService(TableServiceClient serviceClient, IBackupSource source, RestoreOptions options)
{
    public async Task<RestoreResult> RunAsync(string backupId, CancellationToken ct = default)
    {
        var prefix = options.TablePrefix ?? "";
        var files = await source.ListFilesAsync(backupId, ct);
        var result = new RestoreResult();

        foreach (var fileName in files)
        {
            if (fileName.StartsWith("_")) continue; // Skip metadata files (manifest, tombstones)

            var tableName = ExtractTableName(fileName);
            if (tableName is null) continue;

            if (options.Tables is not null && !options.Tables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                continue;

            var physicalName = prefix + tableName;
            var tableClient = serviceClient.GetTableClient(physicalName);
            tableClient.CreateIfNotExists(ct);

            if (options.Mode == RestoreMode.Clean)
            {
                await CleanTableAsync(tableClient, ct);
            }

            var stream = await source.OpenReadAsync(backupId, fileName, ct);
            if (stream is null) continue;

            long restored = 0;
            long errors = 0;

            await using (stream)
            {
                Stream readStream = fileName.EndsWith(".gz") ? new GZipStream(stream, CompressionMode.Decompress) : stream;
                await using var decompressScope = fileName.EndsWith(".gz") ? readStream : null;
                using var reader = new StreamReader(readStream, System.Text.Encoding.UTF8);

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var entity = DeserializeEntity(line);
                        if (entity is null) continue;

                        if (!options.DryRun)
                        {
                            var mode = options.Mode == RestoreMode.Merge
                                ? TableUpdateMode.Merge
                                : TableUpdateMode.Replace;
                            await tableClient.UpsertEntityAsync(entity, mode, ct);
                        }

                        restored++;
                    }
                    catch (RequestFailedException)
                    {
                        errors++;
                    }
                }
            }

            result.Tables[tableName] = new RestoreTableResult { Restored = restored, Errors = errors };
        }

        return result;
    }

    private static async Task CleanTableAsync(TableClient tableClient, CancellationToken ct)
    {
        var query = tableClient.QueryAsync<TableEntity>(
            select: new[] { "PartitionKey", "RowKey" },
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            try
            {
                await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, ETag.All, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }
    }

    internal static TableEntity? DeserializeEntity(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("PartitionKey", out var pkProp) ||
            !root.TryGetProperty("RowKey", out var rkProp))
            return null;

        var entity = new TableEntity(pkProp.GetString(), rkProp.GetString());

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name is "PartitionKey" or "RowKey" or "Timestamp" or "ETag" or "odata.etag")
                continue;

            entity[prop.Name] = ConvertJsonValue(prop.Value);
        }

        return entity;
    }

    internal static object? ConvertJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => TryParseTypedString(element.GetString()!),
        JsonValueKind.Number => element.TryGetInt32(out var i32) ? i32
            : element.TryGetInt64(out var i64) ? i64
            : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };

    private static object TryParseTypedString(string value)
    {
        // Try DateTimeOffset for ISO 8601 strings (19-35 chars, starts with digit)
        if (value.Length is >= 19 and <= 35 && char.IsDigit(value[0]) &&
            DateTimeOffset.TryParse(value, out var dto))
            return dto;

        // Try Guid (36 chars with dashes)
        if (value.Length == 36 && Guid.TryParse(value, out var guid))
            return guid;

        return value;
    }

    private static string? ExtractTableName(string fileName)
    {
        // "Users.jsonl" → "Users", "Users.jsonl.gz" → "Users"
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.EndsWith(".jsonl")) name = name[..^6];
        return string.IsNullOrEmpty(name) ? null : name;
    }
}

public sealed class RestoreResult
{
    public Dictionary<string, RestoreTableResult> Tables { get; set; } = new();
    public long TotalRestored => Tables.Values.Sum(t => t.Restored);
    public long TotalErrors => Tables.Values.Sum(t => t.Errors);
}

public sealed class RestoreTableResult
{
    public long Restored { get; set; }
    public long Errors { get; set; }
}
