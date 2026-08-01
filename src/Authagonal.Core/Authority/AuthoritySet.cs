namespace Authagonal.Core.Authority;

/// <summary>
/// A set of <see cref="AuthorityGrant"/>s — the unit the whole agentic model computes with.
/// The same type carries the admin ceiling, a user's standing consent, a task's request, and
/// the <c>authorization_details</c> claim on a minted token, so the runtime invariant
/// (<c>effective = ceiling ∩ consent ∩ request ∩ subject</c>) is literally chained
/// <see cref="Intersect"/> calls.
/// <para>
/// <see cref="Unrestricted"/> is the top element (⊤): it stands in for "no authority claim
/// present" so legacy tokens and absent requests intersect away to whatever the other side
/// allows. It is never serialized — absence of the claim IS the representation.
/// </para>
/// </summary>
public sealed class AuthoritySet
{
    private AuthoritySet(IReadOnlyList<AuthorityGrant> grants, bool isUnrestricted)
    {
        Grants = grants;
        IsUnrestricted = isUnrestricted;
    }

    public IReadOnlyList<AuthorityGrant> Grants { get; }

    /// <summary>True only for <see cref="Unrestricted"/>. An unrestricted set permits
    /// everything and is the identity of <see cref="Intersect"/>.</summary>
    public bool IsUnrestricted { get; }

    /// <summary>The top element: permits everything, intersects to the other operand.</summary>
    public static readonly AuthoritySet Unrestricted = new([], isUnrestricted: true);

    /// <summary>The bottom element: permits nothing.</summary>
    public static readonly AuthoritySet Empty = new([], isUnrestricted: false);

    public static AuthoritySet Of(params AuthorityGrant[] grants) => From(grants);

    public static AuthoritySet From(IEnumerable<AuthorityGrant> grants)
    {
        // Duplicate types are meet-merged on entry so a set is always keyed by type — the
        // invariant Intersect and Permits rely on.
        var byType = new Dictionary<string, AuthorityGrant>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            byType[grant.Type] = byType.TryGetValue(grant.Type, out var existing)
                ? MergeSameType(existing, grant, merger: null)
                : grant;
        }
        return byType.Count == 0 ? Empty : new([.. byType.Values], isUnrestricted: false);
    }

    /// <summary>
    /// The greatest lower bound of two authority sets: the result permits an
    /// (action, context) pair only if BOTH inputs permit it. Types present on only one side
    /// are dropped; actions intersect; locations intersect — an UNSPECIFIED (empty) side carries the
    /// other's locations over, but two non-empty sides that share nothing drop the grant entirely rather
    /// than collapsing to the empty-means-unrestricted encoding;
    /// constraints meet per <see cref="ConstraintValue.Meet"/> (a constraint present on one
    /// side only carries over — it is a restriction, and restrictions never expire in an
    /// intersection); action policies take the most restrictive.
    /// </summary>
    /// <param name="merger">Optional host override for named constraints' meet semantics.</param>
    public AuthoritySet Intersect(AuthoritySet other, IConstraintMerger? merger = null)
    {
        if (IsUnrestricted) return other;
        if (other.IsUnrestricted) return this;

        var theirs = other.Grants.ToDictionary(g => g.Type, StringComparer.Ordinal);
        var result = new List<AuthorityGrant>();
        foreach (var mine in Grants)
        {
            if (!theirs.TryGetValue(mine.Type, out var its)) continue;
            var merged = MergeSameType(mine, its, merger);
            if (merged.Actions.Count > 0)
                result.Add(merged);
        }
        return result.Count == 0 ? Empty : new(result, isUnrestricted: false);
    }

    private static AuthorityGrant MergeSameType(AuthorityGrant a, AuthorityGrant b, IConstraintMerger? merger)
    {
        var actions = a.Actions.Intersect(b.Actions, StringComparer.Ordinal).ToList();

        // Empty means "unrestricted" in this model, so an intersection that EMPTIES a non-empty pair must
        // not be represented as an empty list — that would read as unrestricted and widen authority through
        // an intersection, which inverts the operation. Two disjoint location sets permit nothing in common,
        // so the grant is dropped instead (signalled by clearing Actions, which the caller already filters
        // on). Only a genuinely unspecified side carries the other's locations over.
        var locationsDisjoint = false;
        var locations = (a.Locations.Count, b.Locations.Count) switch
        {
            (0, _) => b.Locations,
            (_, 0) => a.Locations,
            _ => a.Locations.Intersect(b.Locations, StringComparer.Ordinal).ToList(),
        };
        if (a.Locations.Count > 0 && b.Locations.Count > 0 && locations.Count == 0)
            locationsDisjoint = true;

        var constraints = new Dictionary<string, ConstraintValue>(StringComparer.Ordinal);
        foreach (var (name, value) in a.Constraints)
            constraints[name] = value;
        foreach (var (name, value) in b.Constraints)
        {
            constraints[name] = constraints.TryGetValue(name, out var mine)
                ? merger?.Merge(name, mine, value) ?? ConstraintValue.Meet(mine, value)
                : value;
        }

        var policies = new Dictionary<string, ActionPolicy>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            var policy = (ActionPolicy)Math.Max((int)a.PolicyFor(action), (int)b.PolicyFor(action));

            // An explicitly-stated `auto` is recorded, not dropped as if it were absent.
            //
            // Auto is the enum's zero, so `if (policy != Auto)` discarded it — which made an explicit
            // auto indistinguishable from "nothing was said about this action". That distinction is
            // load-bearing: the documented rule is that an explicit auto beats the profile's
            // HighRiskDefault, and downstream code reads absence as "apply the default". So an admin
            // who deliberately marked a high-risk action auto had that decision erased by any Intersect
            // and the action fell back to ask (or deny) anyway — the one behaviour the explicit setting
            // exists to override.
            if (policy != ActionPolicy.Auto
                || a.ActionPolicies.ContainsKey(action)
                || b.ActionPolicies.ContainsKey(action))
            {
                policies[action] = policy;
            }
        }

        return new AuthorityGrant
        {
            Type = a.Type,
            Actions = locationsDisjoint ? [] : actions,
            Locations = locations,
            Constraints = constraints.Count > 0 ? constraints : AuthorityGrant.EmptyConstraints,
            ActionPolicies = policies.Count > 0 ? policies : AuthorityGrant.EmptyPolicies,
        };
    }

    /// <summary>
    /// The first <c>(type, member)</c> in a CLIENT-SUPPLIED <paramref name="request"/> naming a
    /// constraint this set does not define for that type, or null when every constraint the request
    /// names was already part of the granted authority.
    /// </summary>
    /// <remarks>
    /// RFC 9396 §5 requires the AS to refuse <c>authorization_details</c> it does not understand, and
    /// this server has no type schema: <see cref="AuthorityJson"/> sweeps every member it does not
    /// recognise into <see cref="AuthorityGrant.Constraints"/>. <see cref="Intersect"/> then carries a
    /// constraint present on ONE side straight into the result — correct when both operands are grants
    /// (a restriction never expires in a meet), but the request is not a grant. So a client could invent
    /// a member nobody defined, have it survive the intersection, and get it SIGNED into the
    /// authorization_details claim; a resource server reading an unrecognised member cannot tell a
    /// restriction the user imposed from authority the AS conferred, so
    /// <c>{"type":"payment","actions":["initiate"],"beneficiary":"attacker"}</c> reads as the latter.
    /// <para>
    /// Refused rather than silently dropped: dropping would hand back a token WIDER than the one the
    /// client asked for, which is the more dangerous of the two surprises. An unrestricted set states no
    /// vocabulary to check against — an admin who wrote an unrestricted ceiling said anything goes — so
    /// it admits everything here and the check applies to the ceilings that actually name types.
    /// </para>
    /// </remarks>
    public (string Type, string Member)? FindUngrantedConstraint(AuthoritySet request)
    {
        if (IsUnrestricted || request.IsUnrestricted) return null;

        foreach (var requested in request.Grants)
        {
            if (requested.Constraints.Count == 0) continue;
            var granted = GrantFor(requested.Type);
            // A type this set does not grant at all is dropped by Intersect and reported by the
            // caller's own "requested authority is not grantable" check — not this one's business.
            if (granted is null) continue;

            foreach (var (name, _) in requested.Constraints)
                if (!granted.Constraints.ContainsKey(name))
                    return (requested.Type, name);
        }

        return null;
    }

    public AuthorityGrant? GrantFor(string type) =>
        Grants.FirstOrDefault(g => string.Equals(g.Type, type, StringComparison.Ordinal));

    /// <summary>The effective policy for (type, action): <see cref="ActionPolicy.Deny"/> when
    /// the type or action isn't granted at all.</summary>
    public ActionPolicy PolicyFor(string type, string action)
    {
        if (IsUnrestricted) return ActionPolicy.Auto;
        return GrantFor(type)?.PolicyFor(action) ?? ActionPolicy.Deny;
    }

    /// <summary>
    /// Structural permission check: the action is granted for the type, its policy is not
    /// <see cref="ActionPolicy.Deny"/>, and every constraint the supplied context can be
    /// checked against is satisfied. Constraint checks are keyed by name: a context entry
    /// whose key matches a constraint is validated against it; constraints with no matching
    /// context key are skipped (the caller is responsible for supplying every context key it
    /// knows how to derive — e.g. <c>recipient_domains</c> when sending mail). A
    /// <see cref="ConstraintValue.Nothing"/> constraint denies unconditionally: it is the
    /// residue of a conflicting intersection.
    /// </summary>
    public bool Permits(string type, string action, IReadOnlyDictionary<string, string>? context = null)
        => Permits(type, action, location: null, context, strict: false);

    /// <param name="location">
    /// The RFC 9396 <c>locations</c> value the caller is acting against, when it has one. A grant
    /// that names locations permits only those (see <see cref="LocationCovers"/> for how a named
    /// location is matched against the presented one).
    /// </param>
    /// <param name="strict">
    /// When true, a constraint the caller supplied no context for DENIES instead of being skipped.
    /// </param>
    /// <remarks>
    /// Both parameters exist because the default evaluation fails open in two ways.
    /// <para>
    /// Constraints with no matching context entry were skipped, which makes the guarantee "the
    /// resource server is trusted to know which context keys matter" — so a server that simply
    /// forgets to pass <c>recipient_domains</c> gets an unconstrained grant and nothing anywhere
    /// reports it. Strict mode inverts that for callers that can enumerate what they support.
    /// </para>
    /// <para>
    /// <c>locations</c> was parsed, intersected and emitted, and consulted by no evaluator — so the
    /// one part of a grant that says WHERE the authority applies never narrowed anything. A caller
    /// that passes its own location now gets it enforced.
    /// </para>
    /// </remarks>
    public bool Permits(
        string type,
        string action,
        string? location,
        IReadOnlyDictionary<string, string>? context = null,
        bool strict = false)
    {
        if (IsUnrestricted) return true;

        var grant = GrantFor(type);
        if (grant is null) return false;
        if (grant.PolicyFor(action) == ActionPolicy.Deny) return false;

        // A grant that names locations applies only at those locations.
        if (grant.Locations.Count > 0 && location is not null
            && !grant.Locations.Any(granted => LocationCovers(granted, location)))
        {
            return false;
        }

        foreach (var (name, constraint) in grant.Constraints)
        {
            if (constraint is ConstraintValue.NothingValue) return false;

            // An allowlist that admits nothing is bottom, like Nothing — it can never be satisfied,
            // so treating it as "no constraint" inverted its meaning entirely.
            if (constraint is ConstraintValue.StringSet { Values.Count: 0 }) return false;

            if (context is null || !context.TryGetValue(name, out var contextValue))
            {
                if (strict) return false;
                continue;
            }

            if (!Satisfies(constraint, contextValue)) return false;
        }
        return true;
    }

    /// <summary>
    /// The constraint names on <paramref name="type"/>'s grant that <paramref name="context"/> carries
    /// no value for — precisely the ones the non-strict <see cref="Permits(string, string, string?,
    /// IReadOnlyDictionary{string, string}, bool)"/> skips.
    /// </summary>
    /// <remarks>
    /// A resource server calls this to find out what it is being trusted with. Skipping an unmatched
    /// constraint makes the guarantee "the caller knows which context keys matter", and nothing
    /// anywhere reported when a caller simply did not know — a server that forgets to derive
    /// <c>recipient_domains</c> spends a domain-restricted grant as an unrestricted one and no log
    /// line says so. An empty result means the whole grant was evaluated; a non-empty one names the
    /// restrictions nobody checked, so the caller can refuse, log, or re-check in strict mode.
    /// </remarks>
    public IReadOnlyList<string> UncheckedConstraints(
        string type, IReadOnlyDictionary<string, string>? context = null)
    {
        var grant = GrantFor(type);
        if (grant is null || grant.Constraints.Count == 0) return [];
        return [.. grant.Constraints.Keys.Where(name => context is null || !context.ContainsKey(name))];
    }

    // A location is an RFC 9396 resource identifier, so it is compared as one when both sides parse as
    // absolute URIs: scheme and authority case-insensitively (RFC 3986 §6.2.2.1), and the granted path
    // as a containment root on a segment boundary — "https://api.example/orders" covers
    // "https://api.example/orders/17" but never "https://api.example/orders-admin". Ordinal equality is
    // still honoured first so an opaque, non-URI location (a connector id, a queue name) works.
    //
    // Containment, not equality, because the value the caller presents is the concrete thing it is
    // acting on while the grant names the resource server: exact matching would have made every
    // real presented location miss, which is the same fail-open as never checking.
    private static bool LocationCovers(string granted, string presented)
    {
        if (string.Equals(granted, presented, StringComparison.Ordinal)) return true;

        if (!Uri.TryCreate(granted, UriKind.Absolute, out var scope)
            || !Uri.TryCreate(presented, UriKind.Absolute, out var target))
        {
            return false;
        }
        if (!string.Equals(scope.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(scope.Authority, target.Authority, StringComparison.OrdinalIgnoreCase)) return false;

        var scopePath = scope.AbsolutePath.TrimEnd('/');
        if (scopePath.Length == 0) return true;

        var targetPath = target.AbsolutePath;
        return targetPath.StartsWith(scopePath, StringComparison.Ordinal)
            && (targetPath.Length == scopePath.Length || targetPath[scopePath.Length] == '/');
    }

    private static bool Satisfies(ConstraintValue constraint, string contextValue) => constraint switch
    {
        ConstraintValue.StringSet set => set.Values.Any(entry => MatchesEntry(entry, contextValue)),
        ConstraintValue.Number cap =>
            decimal.TryParse(contextValue, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v <= cap.Value,
        ConstraintValue.Flag flag => flag.Value ||
            string.Equals(contextValue, "false", StringComparison.OrdinalIgnoreCase),
        // Opaque cannot be verified — fail closed the moment a caller presents context for it.
        _ => false,
    };

    // Allowlist entry matching: exact ordinal, "*." host-suffix wildcard ("*.partners.example"
    // matches "a.partners.example" but not "partners.example"), or "@suffix" ending match for
    // address-shaped values ("@example.com" matches "bob@example.com").
    private static bool MatchesEntry(string entry, string value)
    {
        if (string.Equals(entry, value, StringComparison.Ordinal)) return true;
        if (entry.StartsWith("*.", StringComparison.Ordinal))
            return value.Length > entry.Length - 1 &&
                   value.EndsWith(entry[1..], StringComparison.OrdinalIgnoreCase);
        if (entry.StartsWith('@'))
            return value.EndsWith(entry, StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
