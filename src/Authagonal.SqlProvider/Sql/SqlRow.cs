using System.Globalization;

namespace Authagonal.SqlProvider.Sql;

/// <summary>
/// One row of the generic key-value table: the composite key (<c>pk</c>/<c>sk</c>, mirroring Azure
/// Table Storage's PartitionKey/RowKey and DynamoDB's HASH/RANGE), the document column, and a bag of
/// promoted attributes for the handful of fields queries filter on.
/// <para>
/// <see cref="Data"/> is a first-class column rather than an attribute because every store writes it
/// and it is the one field that can be large and encrypted — keeping it out of the JSON avoids
/// double-escaping a whole user document, and lets the projected scans (id enumeration, login-state
/// streaming) simply not select it, so no ciphertext is read and nothing is decrypted.
/// </para>
/// <para>
/// Attribute values are strings throughout. Numbers, bools and timestamps are stored in
/// round-trippable, lexicographically ordered forms ("O" UTC for dates), so range predicates on an
/// attribute mean what they say without any per-backend type mapping.
/// </para>
/// </summary>
public sealed class SqlRow(string pk, string sk)
{
    public string Pk { get; } = pk;
    public string Sk { get; } = sk;

    /// <summary>The document column — typically serialized JSON, possibly ciphertext.</summary>
    public string? Data { get; set; }

    /// <summary>Promoted, queryable fields. Never holds the document.</summary>
    public Dictionary<string, string> Attrs { get; init; } = [];

    /// <summary>Optimistic-concurrency counter, bumped by every write. 0 for a row not yet read back.</summary>
    public long Version { get; init; }

    /// <summary>
    /// When set, the row is eligible for reaping by <see cref="SqlExpiryReaper"/>. Expiry is still
    /// enforced on read by the stores that care — this only stops dead rows accumulating, it is not
    /// the correctness mechanism.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>The document, or empty when absent — the common case for JSON deserialization.</summary>
    public string DataOrEmpty => Data ?? string.Empty;
}

/// <summary>
/// Attribute read/write helpers. Writers omit nulls (an absent attribute and a null one are the same
/// thing); readers are tolerant, so a missing attribute reads as null/default rather than throwing.
/// </summary>
public static class SqlAttrs
{
    public static SqlRow Row(string pk, string sk) => new(pk, sk);

    // ── writers ──

    public static void PutS(this SqlRow row, string name, string? value)
    {
        if (value is not null) row.Attrs[name] = value;
    }

    public static void PutN(this SqlRow row, string name, long value)
        => row.Attrs[name] = value.ToString(CultureInfo.InvariantCulture);

    public static void PutBool(this SqlRow row, string name, bool value)
        => row.Attrs[name] = value ? "true" : "false";

    public static void PutDate(this SqlRow row, string name, DateTimeOffset value)
        => row.Attrs[name] = Iso(value);

    public static void PutDate(this SqlRow row, string name, DateTimeOffset? value)
    {
        if (value.HasValue) row.Attrs[name] = Iso(value.Value);
    }

    // ── readers ──

    public static string? GetS(this SqlRow row, string name)
        => row.Attrs.TryGetValue(name, out var v) ? v : null;

    public static string GetStr(this SqlRow row, string name)
        => row.Attrs.TryGetValue(name, out var v) ? v : string.Empty;

    public static bool Has(this SqlRow row, string name) => row.Attrs.ContainsKey(name);

    public static bool GetBool(this SqlRow row, string name)
        => row.Attrs.TryGetValue(name, out var v) && string.Equals(v, "true", StringComparison.Ordinal);

    public static long GetN(this SqlRow row, string name)
        => row.Attrs.TryGetValue(name, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : 0;

    public static DateTimeOffset GetDate(this SqlRow row, string name)
        => row.Attrs.TryGetValue(name, out var v) && TryParseIso(v, out var d) ? d : default;

    public static DateTimeOffset? GetDateOrNull(this SqlRow row, string name)
        => row.Attrs.TryGetValue(name, out var v) && TryParseIso(v, out var d) ? d : null;

    public static string Iso(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool TryParseIso(string value, out DateTimeOffset result)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
}

/// <summary>
/// Guards the two places a string is interpolated into SQL rather than parameterized: table/schema
/// names and JSON attribute names. Both are compile-time constants inside this assembly today; the
/// check exists so that stays true — a name reaching here from configuration or a request would
/// throw rather than concatenate.
/// </summary>
internal static class SqlNames
{
    public static string Identifier(string value) => Checked(value, nameof(Identifier));

    public static string Attribute(string value) => Checked(value, nameof(Attribute));

    private static string Checked(string value, string kind)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"SQL {kind} may not be empty.", nameof(value));

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                throw new ArgumentException(
                    $"SQL {kind} '{value}' contains an unsupported character '{c}'. " +
                    "Only ASCII letters, digits and underscores are allowed.", nameof(value));
        }

        return value;
    }
}
