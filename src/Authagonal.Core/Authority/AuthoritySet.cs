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
    /// that names locations permits only those.
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
            && !grant.Locations.Contains(location, StringComparer.Ordinal))
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
