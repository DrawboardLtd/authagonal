namespace Authagonal.Core.Authority;

/// <summary>
/// One fine-grained authority grant — the typed form of a single RFC 9396
/// authorization-details object. A grant names a connector type, the actions permitted on
/// it, and the restrictions that ride along.
/// </summary>
public sealed record AuthorityGrant
{
    /// <summary>Connector identifier (the RFC 9396 <c>type</c> member) — e.g. <c>email</c>,
    /// <c>mcp:tools.internal</c>. Matched ordinally.</summary>
    public required string Type { get; init; }

    /// <summary>Explicit action allowlist. An action not listed is not permitted; an empty
    /// list grants nothing for this type. There is no wildcard — width lives only in
    /// <see cref="AuthoritySet.Unrestricted"/>, never inside a grant.</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>Optional audience restriction (the RFC 9396 <c>locations</c> member).
    /// Empty = unrestricted; non-empty = the token may only be presented at these
    /// resources.</summary>
    public IReadOnlyList<string> Locations { get; init; } = [];

    /// <summary>Named restrictions carried as custom members of the authorization-details
    /// object (allowlists, numeric caps, boolean gates). See <see cref="ConstraintValue"/>
    /// for shapes and meet semantics.</summary>
    public IReadOnlyDictionary<string, ConstraintValue> Constraints { get; init; } = EmptyConstraints;

    /// <summary>Per-action execution policy (<c>auto</c>/<c>ask</c>/<c>deny</c>), carried as
    /// the custom <c>action_policies</c> member. An action without an entry defaults to
    /// <see cref="ActionPolicy.Auto"/>.</summary>
    public IReadOnlyDictionary<string, ActionPolicy> ActionPolicies { get; init; } = EmptyPolicies;

    internal static readonly IReadOnlyDictionary<string, ConstraintValue> EmptyConstraints =
        new Dictionary<string, ConstraintValue>(StringComparer.Ordinal);

    internal static readonly IReadOnlyDictionary<string, ActionPolicy> EmptyPolicies =
        new Dictionary<string, ActionPolicy>(StringComparer.Ordinal);

    /// <summary>The effective policy for one action: the recorded entry, or
    /// <see cref="ActionPolicy.Auto"/> when the action is permitted but unlisted, or
    /// <see cref="ActionPolicy.Deny"/> when the action isn't in <see cref="Actions"/> at all.</summary>
    public ActionPolicy PolicyFor(string action)
    {
        if (!Actions.Contains(action, StringComparer.Ordinal))
            return ActionPolicy.Deny;
        return ActionPolicies.TryGetValue(action, out var policy) ? policy : ActionPolicy.Auto;
    }
}
