namespace Authagonal.Server.Services;

/// <summary>
/// Minimal SCIM filter parser supporting:
/// - attr eq "value"
/// - attr co "value"
/// </summary>
public static class ScimFilterParser
{
    public sealed record ScimFilter(string Attribute, string Operator, string Value);

    public static ScimFilter? Parse(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return null;

        filter = filter.Trim();

        // Try to parse: attribute operator "value"
        var parts = SplitFilter(filter);
        if (parts is null)
            return null;

        var (attr, op, value) = parts.Value;

        // Normalize operator to lowercase
        op = op.ToLowerInvariant();
        if (op is not ("eq" or "co"))
            return null;

        return new ScimFilter(attr, op, value);
    }

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

        // Value should be quoted
        if (valueStr.Length >= 2 && valueStr[0] == '"' && valueStr[^1] == '"')
        {
            valueStr = valueStr[1..^1];
        }

        return (attr, op, valueStr);
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
