using Authagonal.Core.Stores;

namespace Authagonal.Core.Services;

/// <summary>
/// Narrows a set of requested scopes to the ones the signed-in user is entitled to, per each
/// scope's <see cref="Models.Scope.AllowedRoles"/>.
/// </summary>
/// <remarks>
/// The client's <c>AllowedScopes</c> check answers "may this application ask for it", which is
/// settled before anyone has logged in. This answers the other half — "may this person have it" —
/// and so can only run once the subject is known. Both gates apply; neither substitutes for the
/// other.
/// <para>
/// Applied on every path that mints a token for a human: the authorization-code flow (before
/// consent, so the user is never shown a permission they cannot be granted) and token exchange
/// (so a downscope cannot reach a scope the subject was never entitled to). Client-credentials has
/// no subject and is deliberately untouched — a machine client's authority is its registration.
/// </para>
/// </remarks>
public interface IScopeRoleGate
{
    /// <summary>
    /// Returns <paramref name="requestedScopes"/> with role-gated scopes the user does not qualify
    /// for removed, preserving order. An unregistered scope is left alone — what a client may ask
    /// for is the client store's business, and silently dropping unknown names here would mask
    /// configuration mistakes as permission problems.
    /// </summary>
    Task<IReadOnlyList<string>> FilterAsync(
        IEnumerable<string> requestedScopes,
        IEnumerable<string>? userRoles,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ScopeRoleGate(IScopeStore scopeStore) : IScopeRoleGate
{
    public async Task<IReadOnlyList<string>> FilterAsync(
        IEnumerable<string> requestedScopes,
        IEnumerable<string>? userRoles,
        CancellationToken ct = default)
    {
        var requested = requestedScopes as IReadOnlyList<string> ?? requestedScopes.ToList();

        // Ordinal, like every other role comparison in the server. A null/empty role set is a user
        // who qualifies for nothing gated — which is the safe direction when the store could not
        // resolve them.
        var roles = new HashSet<string>(userRoles ?? [], StringComparer.Ordinal);

        // Resolve against the full registered set, indexed case-INsensitively. A point-read on the exact
        // name would return null for a case variant (`Admin` vs a registered `admin`), and "unregistered"
        // means "leave alone" below — so a caller could skip the gate on a gated scope just by changing its
        // case, while downstream consumers (notably the IdentityAdmin policy) match case-insensitively and
        // honoured it. Requests are rejected earlier for a case variant, but this gate must not depend on
        // that: it is the "may this person have it" half and has to fail closed on its own.
        var all = await scopeStore.ListAsync(ct);
        var byName = new Dictionary<string, Models.Scope>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in all)
            byName[s.Name] = s;

        var kept = new List<string>(requested.Count);
        foreach (var name in requested)
        {
            // Genuinely unregistered scopes are still left alone — what a client may ask for is the client
            // store's business, and dropping unknown names here would mask configuration mistakes as
            // permission problems.
            if (!byName.TryGetValue(name, out var scope)
                || scope.AllowedRoles.Count == 0
                || scope.AllowedRoles.Any(roles.Contains))
                kept.Add(name);
        }

        return kept;
    }
}
