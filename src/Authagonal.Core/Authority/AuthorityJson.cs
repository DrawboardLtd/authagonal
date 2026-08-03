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

    /// <summary>
    /// True when a set that structurally holds grants serializes to the empty array — every grant was
    /// dropped by <see cref="ToNode"/>.
    /// </summary>
    /// <remarks>
    /// Such a set must never be minted, and the check has to happen on the WIRE form because serialization is
    /// where the authority is lost. Counting <see cref="Grants"/> does not see it: the dropped grant is still
    /// in the structural set, and <see cref="PolicyFor(string, string)"/> reports its actions as grantable
    /// because it does not consult constraints at all.
    /// <para>
    /// `authorization_details: []` is the most dangerous value this type can produce.
    /// <see cref="AuthorityEvaluator.FromPrincipal"/> reads zero claims as
    /// <see cref="AuthoritySet.Unrestricted"/> — deliberately, so coarse scope-based tokens keep working —
    /// and a JWT-to-ClaimsPrincipal conversion flattens an empty array to exactly zero claims. Omitting the
    /// claim instead is no better for the same reason: the only safe response is to refuse to mint.
    /// </para>
    /// <para>
    /// <see cref="AuthoritySet.Intersect"/> no longer produces such a set, but intersection is not the only
    /// source. A ceiling whose actions are all <c>ask</c> becomes all-deny under
    /// <c>MapAskPolicies(…, Deny)</c> in the unattended <c>client_credentials</c> path, and every all-deny
    /// grant is dropped; a stored set can round-trip through <c>__nothing</c>. Those never touch a meet.
    /// </para>
    /// </remarks>
    public static bool SerializesToNothing(AuthoritySet set)
        => !set.IsUnrestricted && set.Grants.Count > 0 && ToNode(set).Count == 0;

    public static JsonArray ToNode(AuthoritySet set)
    {
        if (set.IsUnrestricted)
            throw new InvalidOperationException(
                "An unrestricted AuthoritySet has no wire form — omit the claim/parameter instead");

        var array = new JsonArray();
        foreach (var grant in set.Grants)
        {
            // Deny-policy actions are stripped, and a grant left with none is dropped entirely.
            //
            // RFC 9396 authorization_details is a statement of what IS granted. Emitting a denied
            // action inside `actions` and recording the denial only in the non-standard
            // `action_policies` member means any consumer reading the standard field — which is what
            // a spec-conforming resource server reads, and all it is required to read — sees the
            // action as PERMITTED. The denial was visible only to a reader that knew about this
            // product's extension.
            var grantedActions = grant.Actions
                .Where(a => grant.PolicyFor(a) != ActionPolicy.Deny)
                .ToList();

            if (grantedActions.Count == 0)
                continue;

            // A constraint that intersected to Nothing can never be satisfied, so the grant permits
            // nothing regardless of its actions. Emitting it as a positive grant carrying a
            // non-standard marker had the same problem in sharper form.
            if (grant.Constraints.Values.Any(v => v is ConstraintValue.NothingValue)
                || grant.Constraints.Values.Any(v => v is ConstraintValue.StringSet { Values.Count: 0 }))
            {
                continue;
            }

            var node = new JsonObject { ["type"] = grant.Type };

            node["actions"] = StringArray(grantedActions);

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

            if (!TryReadStringArray(obj["actions"], out var actions)) return false;
            if (!TryReadStringArray(obj["locations"], out var locations)) return false;

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

        // RFC 9396 §2 permits an authorization_details array to carry several entries of the same
        // type, and this model cannot represent that: AuthoritySet is keyed by type, so From()
        // meet-merges duplicates into their intersection. That is a REINTERPRETATION of what was
        // asked for, and §5 does not offer it as an option — an input the AS cannot represent must be
        // refused with invalid_authorization_details.
        //
        // The direction matters. Meet-merging narrows, so it never grants more than was asked; but a
        // caller that sends two independent grants of one type and gets back only what they have in
        // common has silently lost authority it believes it holds, and will not find out until the
        // resource server refuses an action the caller was sure it had been granted. Refusing says so
        // at the point the request is made.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            if (!seen.Add(grant.Type)) return false;
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

    // A member that is PRESENT but is not an array of strings is refused, not read as absent.
    //
    // Empty means "unrestricted" for locations, so reading `"locations": "https://internal"` — a bare
    // string, which is the shape a hand-written authorization_details most often gets wrong — as an
    // empty list turned a grant the caller pinned to one resource server into one that applies
    // everywhere. Dropping individual non-string elements had the same effect a slice at a time.
    // RFC 9396 §5 has a code for exactly this case: authorization_details the AS cannot represent is
    // invalid_authorization_details, which is what returning false becomes at every call site.
    private static bool TryReadStringArray(JsonNode? node, out IReadOnlyList<string> values)
    {
        values = [];
        if (node is null) return true; // absent (or JSON null) — genuinely unspecified
        if (node is not JsonArray array) return false;

        var parsed = new List<string>(array.Count);
        foreach (var element in array)
        {
            if (element is not JsonValue v || v.GetValueKind() != JsonValueKind.String) return false;
            parsed.Add(v.GetValue<string>());
        }
        values = parsed;
        return true;
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
