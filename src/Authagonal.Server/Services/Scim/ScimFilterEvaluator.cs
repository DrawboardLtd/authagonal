using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Authagonal.Server.Services.Scim;

/// <summary>
/// Evaluates a parsed SCIM filter against a resource, represented as the JSON we would return for it.
/// </summary>
/// <remarks>
/// Evaluating against the serialized resource rather than the domain object is deliberate: it gives
/// sub-attributes, multi-valued attributes and value paths for free, and it cannot drift from what the
/// API actually returns — a filter can only ever match on something a client can see.
/// <para>
/// Semantics follow RFC 7644 §3.4.2.2: string comparisons are case-insensitive (SCIM attributes are
/// caseExact=false unless the schema says otherwise), a multi-valued attribute matches when ANY element
/// matches, and an absent attribute makes a comparison false — except <c>ne</c>, where "not equal to x"
/// is true of a resource that has no such attribute at all.
/// </para>
/// </remarks>
public static class ScimFilterEvaluator
{
    /// <summary>
    /// Evaluates against a SCIM resource DTO by serializing it first — so a filter can only match on
    /// something the client would actually be shown.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "SCIM resources are plain DTOs, serialized reflectively as elsewhere in this surface")]
    public static bool Matches(ScimFilterExpression expression, object resource) =>
        Matches(expression, JsonSerializer.SerializeToNode(resource));

    public static bool Matches(ScimFilterExpression expression, JsonNode? resource) => expression switch
    {
        ScimFilterExpression.Logical { Operator: LogicalOperator.And } and_ =>
            Matches(and_.Left, resource) && Matches(and_.Right, resource),
        ScimFilterExpression.Logical { Operator: LogicalOperator.Or } or_ =>
            Matches(or_.Left, resource) || Matches(or_.Right, resource),
        ScimFilterExpression.Not not => !Matches(not.Inner, resource),
        ScimFilterExpression.Present present => Resolve(present.Path, resource).Any(IsPresent),
        ScimFilterExpression.ValuePathExists exists => Resolve(exists.Path, resource).Any(),
        ScimFilterExpression.Comparison cmp => EvaluateComparison(cmp, resource),
        _ => false,
    };

    private static bool EvaluateComparison(ScimFilterExpression.Comparison cmp, JsonNode? resource)
    {
        var values = Resolve(cmp.Path, resource).ToList();

        // Absent attribute: every comparison is false, because the resource cannot satisfy a claim about
        // a value it does not have. `ne` is the exception — a user with no title genuinely is not titled
        // "Manager" — and treating it as false there would make `x ne "y"` and `not (x eq "y")` disagree.
        if (values.Count == 0)
            return cmp.Operator == ComparisonOperator.Ne;

        return values.Any(v => Compare(v, cmp.Operator, cmp.Value));
    }

    private static bool Compare(JsonNode? node, ComparisonOperator op, ScimComparisonValue expected)
    {
        if (expected.IsNull)
            return op switch
            {
                ComparisonOperator.Eq => !IsPresent(node),
                ComparisonOperator.Ne => IsPresent(node),
                _ => false,
            };

        if (expected.Boolean is { } expectedBool)
        {
            var actual = AsBoolean(node);
            return op switch
            {
                ComparisonOperator.Eq => actual == expectedBool,
                ComparisonOperator.Ne => actual != expectedBool,
                _ => false, // ordering/substring operators are meaningless on a boolean
            };
        }

        if (expected.Number is { } expectedNumber)
        {
            var actual = AsNumber(node);
            if (actual is null) return op == ComparisonOperator.Ne;
            return op switch
            {
                ComparisonOperator.Eq => actual == expectedNumber,
                ComparisonOperator.Ne => actual != expectedNumber,
                ComparisonOperator.Gt => actual > expectedNumber,
                ComparisonOperator.Ge => actual >= expectedNumber,
                ComparisonOperator.Lt => actual < expectedNumber,
                ComparisonOperator.Le => actual <= expectedNumber,
                _ => false,
            };
        }

        var text = AsString(node);
        if (text is null) return op == ComparisonOperator.Ne;
        var target = expected.String ?? "";

        return op switch
        {
            ComparisonOperator.Eq => text.Equals(target, StringComparison.OrdinalIgnoreCase),
            ComparisonOperator.Ne => !text.Equals(target, StringComparison.OrdinalIgnoreCase),
            ComparisonOperator.Co => text.Contains(target, StringComparison.OrdinalIgnoreCase),
            ComparisonOperator.Sw => text.StartsWith(target, StringComparison.OrdinalIgnoreCase),
            ComparisonOperator.Ew => text.EndsWith(target, StringComparison.OrdinalIgnoreCase),
            // Ordinal ordering. ISO-8601 UTC timestamps (meta.created, meta.lastModified) order correctly
            // lexicographically, which is why SCIM can get away with one comparison for both.
            ComparisonOperator.Gt => string.CompareOrdinal(text, target) > 0,
            ComparisonOperator.Ge => string.CompareOrdinal(text, target) >= 0,
            ComparisonOperator.Lt => string.CompareOrdinal(text, target) < 0,
            ComparisonOperator.Le => string.CompareOrdinal(text, target) <= 0,
            _ => false,
        };
    }

    /// <summary>
    /// Walks an attribute path, yielding every value it selects. A multi-valued attribute fans out, and a
    /// value filter narrows that fan-out before the remaining segments are applied.
    /// </summary>
    private static IEnumerable<JsonNode?> Resolve(ScimAttributePath path, JsonNode? root)
    {
        IEnumerable<JsonNode?> current = [root];

        for (var i = 0; i < path.Segments.Count; i++)
        {
            var segment = path.Segments[i];
            current = current.SelectMany(node => Descend(node, segment)).ToList();

            if (path.ValueFilter is not null && i == path.ValueFilterSegmentIndex)
            {
                // emails[type eq "work"] — keep only the elements the inner filter accepts.
                current = current
                    .SelectMany(Flatten)
                    .Where(element => Matches(path.ValueFilter, element))
                    .ToList();
            }
        }

        // Flatten any remaining arrays so `emails.value co "@acme"` tests each element.
        return current.SelectMany(Flatten);
    }

    private static IEnumerable<JsonNode?> Descend(JsonNode? node, string segment)
    {
        switch (node)
        {
            case JsonObject obj:
                // SCIM attribute names are case-insensitive.
                foreach (var (key, value) in obj)
                {
                    if (string.Equals(key, segment, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return value;
                        yield break;
                    }
                }
                yield break;
            case JsonArray array:
                foreach (var element in array)
                {
                    foreach (var match in Descend(element, segment))
                        yield return match;
                }
                yield break;
        }
    }

    private static IEnumerable<JsonNode?> Flatten(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var element in array)
                yield return element;
            yield break;
        }
        yield return node;
    }

    /// <summary>SCIM "present" means a non-null value that is not the empty string or an empty array.</summary>
    private static bool IsPresent(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonValue value => !(value.TryGetValue<string>(out var s) && string.IsNullOrEmpty(s)),
        _ => true,
    };

    private static string? AsString(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var s)) return s;
        if (value.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        if (value.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static bool? AsBoolean(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var b) ? b : null;

    private static double? AsNumber(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var d) ? d : null;
}
