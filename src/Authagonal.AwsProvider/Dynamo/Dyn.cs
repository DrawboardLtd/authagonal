using System.Globalization;
using Amazon.DynamoDBv2.Model;

namespace Authagonal.AwsProvider.Dynamo;

/// <summary>
/// Mapping helpers between Authagonal models and DynamoDB items. Every item uses a composite key —
/// <c>pk</c> (partition) + <c>sk</c> (sort) — which mirror Azure Table Storage's PartitionKey / RowKey,
/// so the AWS stores keep the same key shapes as their <c>Authagonal.AzureProvider</c> counterparts.
/// DateTimeOffsets are stored as round-trip ("O") UTC strings: human-readable and lexicographically
/// sortable, which the expiry-index range scans rely on.
/// </summary>
internal static class Dyn
{
    public const string Pk = "pk";
    public const string Sk = "sk";

    /// <summary>Start a new item with the composite key set.</summary>
    public static Dictionary<string, AttributeValue> Item(string pk, string sk) => new()
    {
        [Pk] = new AttributeValue { S = pk },
        [Sk] = new AttributeValue { S = sk },
    };

    // ── writers (omit nulls; DynamoDB has no concept of a null-valued attribute) ──
    public static void PutS(this Dictionary<string, AttributeValue> item, string name, string? value)
    {
        if (value is not null) item[name] = new AttributeValue { S = value };
    }

    public static void PutN(this Dictionary<string, AttributeValue> item, string name, long value)
        => item[name] = new AttributeValue { N = value.ToString(CultureInfo.InvariantCulture) };

    public static void PutBool(this Dictionary<string, AttributeValue> item, string name, bool value)
        => item[name] = new AttributeValue { BOOL = value };

    public static void PutDate(this Dictionary<string, AttributeValue> item, string name, DateTimeOffset value)
        => item[name] = new AttributeValue { S = Iso(value) };

    public static void PutDate(this Dictionary<string, AttributeValue> item, string name, DateTimeOffset? value)
    {
        if (value.HasValue) item[name] = new AttributeValue { S = Iso(value.Value) };
    }

    // ── readers (tolerant: a missing attribute reads as null/default) ──
    public static string? GetS(this Dictionary<string, AttributeValue> item, string name)
        => item.TryGetValue(name, out var v) ? v.S : null;

    public static string GetStr(this Dictionary<string, AttributeValue> item, string name)
        => (item.TryGetValue(name, out var v) ? v.S : null) ?? string.Empty;

    public static bool GetBool(this Dictionary<string, AttributeValue> item, string name)
        => item.TryGetValue(name, out var v) && v.BOOL == true;

    public static long GetN(this Dictionary<string, AttributeValue> item, string name)
        => item.TryGetValue(name, out var v) && v.N is not null
            ? long.Parse(v.N, CultureInfo.InvariantCulture)
            : 0;

    public static DateTimeOffset GetDate(this Dictionary<string, AttributeValue> item, string name)
        => item.TryGetValue(name, out var v) && v.S is not null
            ? DateTimeOffset.Parse(v.S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : default;

    public static DateTimeOffset? GetDateOrNull(this Dictionary<string, AttributeValue> item, string name)
        => item.TryGetValue(name, out var v) && v.S is not null
            ? DateTimeOffset.Parse(v.S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

    private static string Iso(DateTimeOffset v)
        => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
