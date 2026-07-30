using System.Text.Json.Nodes;
using System.Text.Json;
using System.Security.Claims;

namespace Authagonal.Core.Authority;

/// <summary>
/// Resource-side helper: answers "may this validated token do <c>action</c> on connector
/// <c>type</c> with these parameters" from the token's <c>authorization_details</c> claim.
/// Used by host resource servers and by the BFF proxy's per-route authority checks. A token
/// with no authority claim evaluates as <see cref="AuthoritySet.Unrestricted"/> — coarse
/// scope-based tokens keep working; the claim only ever narrows.
/// </summary>
public static class AuthorityEvaluator
{
    /// <summary>Extract the authority carried by a validated principal.</summary>
    /// <remarks>
    /// Reassembles the claim from EVERY matching claim, not just the first. <c>authorization_details</c> is a
    /// JSON array, and a JWT-to-ClaimsPrincipal conversion flattens an array claim into one claim PER
    /// ELEMENT — so <c>FindFirst(...).Value</c> is a bare object like <c>{"type":"email",...}</c>, never the
    /// array that <see cref="AuthorityJson.TryParse"/> requires. Parsing therefore always failed, and a
    /// failed parse is deliberately a deny, so this method returned deny-all for every token that actually
    /// carried authority — legitimately granted authority never evaluated, and the same root cause corrupted
    /// the introspection response, which rebuilds the claim from the split identity.
    /// </remarks>
    public static AuthoritySet FromPrincipal(ClaimsPrincipal principal)
    {
        var claims = principal.FindAll(AuthorityClaims.AuthorizationDetails).ToList();
        if (claims.Count == 0) return AuthoritySet.Unrestricted;

        // One claim holding the whole array (the un-flattened case) parses directly.
        if (claims.Count == 1)
        {
            var only = claims[0].Value?.TrimStart();
            if (only is not null && only.StartsWith('['))
                return ParseOrDeny(claims[0].Value);
        }

        // Otherwise each claim is one element; splice them back into an array. A single element that is
        // itself an object lands here too, which is the common one-grant case.
        var elements = new JsonArray();
        foreach (var c in claims)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(c.Value); }
            catch (JsonException) { return AuthoritySet.Empty; }

            switch (node)
            {
                case JsonArray nested:
                    // Defensive: a value that is itself an array (some handlers keep it whole) — flatten.
                    foreach (var inner in nested.ToList())
                    {
                        nested.Remove(inner);
                        elements.Add(inner);
                    }
                    break;
                case JsonObject:
                    elements.Add(node);
                    break;
                default:
                    return AuthoritySet.Empty; // present but not an authority element: deny, never widen
            }
        }

        return AuthorityJson.TryParse(elements, out var set) ? set : AuthoritySet.Empty;
    }

    /// <summary>Extract the authority from a raw claim value (e.g. out of an introspection
    /// response or a hand-validated JWT payload). Null/empty = no claim = unrestricted.</summary>
    public static AuthoritySet FromClaimValue(string? authorizationDetailsJson) =>
        string.IsNullOrEmpty(authorizationDetailsJson)
            ? AuthoritySet.Unrestricted
            : ParseOrDeny(authorizationDetailsJson);

    /// <summary>Convenience for the common one-shot check. See
    /// <see cref="AuthoritySet.Permits"/> for context semantics.</summary>
    public static bool Permits(
        ClaimsPrincipal principal, string type, string action,
        IReadOnlyDictionary<string, string>? context = null) =>
        FromPrincipal(principal).Permits(type, action, context);

    // A present-but-unparseable claim is a deny, not an unrestricted fallback: a garbled
    // narrow token must never evaluate wider than it was minted.
    private static AuthoritySet ParseOrDeny(string json) =>
        AuthorityJson.TryParse(json, out var set) ? set : AuthoritySet.Empty;
}
