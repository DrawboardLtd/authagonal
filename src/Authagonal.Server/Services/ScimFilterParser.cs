namespace Authagonal.Server.Services;

/// <summary>
/// Minimal SCIM filter parser supporting:
/// - attr eq "value"
/// - attr co "value"
/// </summary>
public static class ScimFilterParser
{
    public sealed record ScimFilter(string Attribute, string Operator, string Value);

    /// <summary>Outcome of parsing a <c>?filter=</c> query parameter.</summary>
    public enum ScimFilterStatus
    {
        /// <summary>No filter was supplied — list everything the caller may see.</summary>
        Absent,
        /// <summary>A filter WAS supplied and this parser cannot represent it.</summary>
        /// <remarks>
        /// Callers must answer 400 <c>invalidFilter</c> (RFC 7644 §3.4.2.2) rather than fall back to an
        /// unfiltered list. Silently ignoring the filter answers a different question than the one asked:
        /// a provisioning agent checking "does this user exist?" gets a non-empty list and concludes yes.
        /// </remarks>
        Unsupported,
        /// <summary>A filter was supplied and parsed.</summary>
        Parsed,
    }

    /// <summary>Parse result, distinguishing "no filter" from "filter we do not understand".</summary>
    public readonly record struct ScimFilterResult(ScimFilterStatus Status, ScimFilter? Filter)
    {
        public static readonly ScimFilterResult Absent = new(ScimFilterStatus.Absent, null);
        public static readonly ScimFilterResult Unsupported = new(ScimFilterStatus.Unsupported, null);
        public static ScimFilterResult Ok(ScimFilter filter) => new(ScimFilterStatus.Parsed, filter);
    }

    /// <summary>Attributes <see cref="Matches"/> can evaluate. Keep in step with its switch.</summary>
    public static readonly IReadOnlySet<string> UserFilterAttributes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "userName", "externalId", "displayName" };

    /// <summary>Attributes <see cref="MatchesGroup"/> can evaluate. Keep in step with its switch.</summary>
    public static readonly IReadOnlySet<string> GroupFilterAttributes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "displayName", "externalId" };

    /// <summary>Human-readable statement of the supported grammar, for the 400 body.</summary>
    public const string SupportedSyntax =
        "Supported filters are a single 'attribute eq \"value\"' or 'attribute co \"value\"' term "
        + "(users: userName, externalId, displayName; groups: displayName, externalId). "
        + "Compound filters (and/or/not) and other operators are not supported.";

    /// <summary>
    /// Parses a filter, reporting whether an unparseable one was present. Prefer this over
    /// <see cref="Parse"/>: it is the only overload that lets a caller tell "no filter" apart from
    /// "filter I could not read", which are opposite answers.
    /// </summary>
    /// <param name="supportedAttributes">
    /// The attributes the caller can actually evaluate (<see cref="UserFilterAttributes"/> /
    /// <see cref="GroupFilterAttributes"/>). An attribute outside the set is Unsupported rather than
    /// Parsed: <see cref="Matches"/> answers false for every record, so it would otherwise return an
    /// empty list — indistinguishable from "no such user", which is how duplicates get created.
    /// Pass null to skip the check (syntax only).
    /// </param>
    public static ScimFilterResult TryParse(string? filter, IReadOnlySet<string>? supportedAttributes = null)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return ScimFilterResult.Absent;

        filter = filter.Trim();

        // Try to parse: attribute operator "value"
        var parts = SplitFilter(filter);
        if (parts is null)
            return ScimFilterResult.Unsupported;

        var (attr, op, value) = parts.Value;

        // Normalize operator to lowercase
        op = op.ToLowerInvariant();
        if (op is not ("eq" or "co"))
            return ScimFilterResult.Unsupported;

        if (supportedAttributes is not null && !supportedAttributes.Contains(attr))
            return ScimFilterResult.Unsupported;

        return ScimFilterResult.Ok(new ScimFilter(attr, op, value));
    }

    /// <summary>
    /// Lenient parse: null for both "absent" and "unsupported". Retained for compatibility with hosts
    /// that embed the SCIM surface; endpoints in this library use <see cref="TryParse"/> so an
    /// unsupported filter fails loudly instead of widening the result set.
    /// </summary>
    public static ScimFilter? Parse(string? filter) => TryParse(filter).Filter;

    private static (string Attr, string Op, string Value)? SplitFilter(string filter)
    {
        // Find the first space to get the attribute
        var firstSpace = filter.IndexOf(' ');
        if (firstSpace <= 0)
            return null;

        var attr = filter[..firstSpace];

        var rest = filter[(firstSpace + 1)..].TrimStart();

        // Find the second space to get the operator
        var secondSpace = rest.IndexOf(' ');
        if (secondSpace <= 0)
            return null;

        var op = rest[..secondSpace];
        var valueStr = rest[(secondSpace + 1)..].Trim();

        // The value must be ONE quoted string and nothing else. The old check only looked at the first
        // and last character, so a compound filter passed straight through it:
        //   userName eq "a@x.com" and active eq "true"
        // starts and ends with a quote, so it "parsed" as userName eq `a@x.com" and active eq "true`.
        // That matched no one and returned an EMPTY list — which a provisioning agent reads as "no such
        // user" and answers by creating a duplicate. Failing the parse turns that into a 400 instead.
        if (valueStr.Length < 2 || valueStr[0] != '"' || valueStr[^1] != '"')
            return null;

        var inner = valueStr[1..^1];

        // Reject an interior unescaped quote: it means the value ended early and something followed it
        // (a conjunction, another term), i.e. a filter richer than one term.
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '"' && (i == 0 || inner[i - 1] != '\\'))
                return null;
        }

        return (attr, op, inner.Replace("\\\"", "\""));
    }

    /// <summary>Applies a parsed SCIM filter to a user-like object.</summary>
    public static bool Matches(ScimFilter filter, string? userName, string? externalId, string? displayName)
    {
        var targetValue = filter.Attribute.ToLowerInvariant() switch
        {
            "username" => userName,
            "externalid" => externalId,
            "displayname" => displayName,
            _ => null
        };

        if (targetValue is null)
            return false;

        return filter.Operator switch
        {
            "eq" => string.Equals(targetValue, filter.Value, StringComparison.OrdinalIgnoreCase),
            "co" => targetValue.Contains(filter.Value, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <summary>Applies a parsed SCIM filter to a group-like object.</summary>
    public static bool MatchesGroup(ScimFilter filter, string? displayName, string? externalId)
    {
        var targetValue = filter.Attribute.ToLowerInvariant() switch
        {
            "displayname" => displayName,
            "externalid" => externalId,
            _ => null
        };

        if (targetValue is null)
            return false;

        return filter.Operator switch
        {
            "eq" => string.Equals(targetValue, filter.Value, StringComparison.OrdinalIgnoreCase),
            "co" => targetValue.Contains(filter.Value, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
