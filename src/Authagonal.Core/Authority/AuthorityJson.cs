using System.Text.Json;
using System.Text.Json.Nodes;

namespace Authagonal.Core.Authority;

/// <summary>
/// RFC 9396 wire format for <see cref="AuthoritySet"/>: a JSON array of
/// authorization-details objects. Standard members <c>type</c>, <c>actions</c> and
/// <c>locations</c> map to their <see cref="AuthorityGrant"/> slots; the custom
/// <c>action_policies</c> member carries per-action auto/ask/deny; every other member is a
/// named constraint, typed by its JSON shape (string/string-array → allowlist, number → cap,
/// bool → gate, anything else → opaque, preserved verbatim). Built on JsonNode so it is
/// trim-safe and shape-tolerant — the same parser reads the request parameter, the stored
/// ceiling/consent, and the token claim.
/// </summary>
public static class AuthorityJson
{
    /// <summary>Serialize to the RFC 9396 array form. <see cref="AuthoritySet.Unrestricted"/>
    /// has no wire form (absence of the claim/parameter is its representation) — serializing
    /// it is a caller bug and throws.</summary>
    public static string Serialize(AuthoritySet set) => ToNode(set).ToJsonString();

    public static JsonArray ToNode(AuthoritySet set)
    {
        if (set.IsUnrestricted)
            throw new InvalidOperationException(
                "An unrestricted AuthoritySet has no wire form — omit the claim/parameter instead");

        var array = new JsonArray();
        foreach (var grant in set.Grants)
        {
            var node = new JsonObject { ["type"] = grant.Type };

            node["actions"] = StringArray(grant.Actions);

            if (grant.Locations.Count > 0)
                node["locations"] = StringArray(grant.Locations);

            if (grant.ActionPolicies.Count > 0)
            {
                var policies = new JsonObject();
                foreach (var (action, policy) in grant.ActionPolicies.OrderBy(p => p.Key, StringComparer.Ordinal))
                    policies[action] = PolicyName(policy);
                node["action_policies"] = policies;
            }

            foreach (var (name, value) in grant.Constraints.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                node[name] = value switch
                {
                    ConstraintValue.StringSet set2 => StringArray(set2.Values),
                    ConstraintValue.Number number => JsonValue.Create(number.Value),
                    ConstraintValue.Flag flag => JsonValue.Create(flag.Value),
                    ConstraintValue.Opaque opaque => JsonNode.Parse(opaque.RawJson),
                    // Nothing is representable so a conflicted intersection survives storage
                    // round-trips without silently un-denying itself.
                    _ => new JsonObject { ["__nothing"] = true },
                };
            }

            array.Add((JsonNode)node);
        }
        return array;
    }

    /// <summary>Parse the RFC 9396 array form. Returns false for anything that isn't a JSON
    /// array of objects each carrying a string <c>type</c> — callers turn that into
    /// <c>invalid_authorization_details</c> / a 400, never into a wider grant.</summary>
    public static bool TryParse(string json, out AuthoritySet set)
    {
        set = AuthoritySet.Empty;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }
        return TryParse(node, out set);
    }

    public static bool TryParse(JsonNode? node, out AuthoritySet set)
    {
        set = AuthoritySet.Empty;
        if (node is not JsonArray array) return false;

        var grants = new List<AuthorityGrant>();
        foreach (var element in array)
        {
            if (element is not JsonObject obj) return false;
            if (obj["type"] is not JsonValue typeValue || typeValue.GetValueKind() != JsonValueKind.String)
                return false;

            var actions = ReadStringArray(obj["actions"]);
            var locations = ReadStringArray(obj["locations"]);

            var policies = new Dictionary<string, ActionPolicy>(StringComparer.Ordinal);
            if (obj["action_policies"] is JsonObject policyObj)
            {
                foreach (var (action, value) in policyObj)
                {
                    if (TryParsePolicy(value, out var policy))
                        policies[action] = policy;
                    else
                        return false;
                }
            }

            var constraints = new Dictionary<string, ConstraintValue>(StringComparer.Ordinal);
            foreach (var (name, value) in obj)
            {
                if (name is "type" or "actions" or "locations" or "action_policies") continue;
                constraints[name] = ReadConstraint(value);
            }

            grants.Add(new AuthorityGrant
            {
                Type = typeValue.GetValue<string>(),
                Actions = actions,
                Locations = locations,
                ActionPolicies = policies.Count > 0 ? policies : AuthorityGrant.EmptyPolicies,
                Constraints = constraints.Count > 0 ? constraints : AuthorityGrant.EmptyConstraints,
            });
        }

        set = AuthoritySet.From(grants);
        return true;
    }

    private static ConstraintValue ReadConstraint(JsonNode? value)
    {
        switch (value)
        {
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                return new ConstraintValue.StringSet([v.GetValue<string>()]);
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                return new ConstraintValue.Number(v.GetValue<decimal>());
            case JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                return new ConstraintValue.Flag(v.GetValue<bool>());
            case JsonArray arr when arr.All(e =>
                e is JsonValue ev && ev.GetValueKind() == JsonValueKind.String):
                return new ConstraintValue.StringSet(
                    arr.Select(e => e!.GetValue<string>()).ToList());
            case JsonObject obj when obj.Count == 1 &&
                obj["__nothing"] is JsonValue flag && flag.GetValueKind() == JsonValueKind.True:
                return ConstraintValue.Nothing;
            default:
                return new ConstraintValue.Opaque(value?.ToJsonString() ?? "null");
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array) return [];
        var values = new List<string>(array.Count);
        foreach (var element in array)
        {
            if (element is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                values.Add(v.GetValue<string>());
        }
        return values;
    }

    private static bool TryParsePolicy(JsonNode? node, out ActionPolicy policy)
    {
        policy = ActionPolicy.Deny;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String) return false;
        switch (value.GetValue<string>())
        {
            case "auto": policy = ActionPolicy.Auto; return true;
            case "ask": policy = ActionPolicy.Ask; return true;
            case "deny": policy = ActionPolicy.Deny; return true;
            default: return false;
        }
    }

    public static string PolicyName(ActionPolicy policy) => policy switch
    {
        ActionPolicy.Auto => "auto",
        ActionPolicy.Ask => "ask",
        _ => "deny",
    };

    /// <summary>Parse a policy wire name; anything unrecognized reads as the safe middle
    /// (<see cref="ActionPolicy.Ask"/>) rather than silently widening to auto.</summary>
    public static ActionPolicy ParsePolicyName(string? policy) => policy switch
    {
        "auto" => ActionPolicy.Auto,
        "deny" => ActionPolicy.Deny,
        _ => ActionPolicy.Ask,
    };

    private static JsonArray StringArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        // The non-generic Add(JsonNode) overload — the generic Add<T> is not trim-safe.
        foreach (var value in values) array.Add((JsonNode)JsonValue.Create(value));
        return array;
    }
}
