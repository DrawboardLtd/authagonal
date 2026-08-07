using Microsoft.AspNetCore.Http;

namespace Authagonal.Server.Services;

/// <summary>
/// Lets a host vouch for origins that may make CREDENTIALED cross-origin calls to the interactive auth API
/// (<c>/api/auth/*</c>). Consulted per request, per origin. Nothing is permitted by default.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DynamicCorsPolicyProvider"/> refuses that surface to every cross-origin caller, because for the
/// deployment this library assumes the surface is first-party by construction: it is driven by the login app
/// served from the same origin, which needs no CORS grant, and granting one exposes endpoints like
/// <c>POST /api/auth/mfa/recovery/generate</c> — plaintext recovery codes — to any origin in the list.
/// </para>
/// <para>
/// That assumption does not hold for a host that lets a tenant build its OWN login screen and post to this
/// server from it. There the cross-origin call IS the feature, and refusing it removes a capability rather
/// than closing a hole. Such a host implements this and answers for the origins it has independently
/// established a relationship with — a verified custom domain, an explicit per-tenant opt-in, a same-site
/// check — which is knowledge this library does not have and must not guess at.
/// </para>
/// <para>
/// An implementation is the whole of the gate: return true and that origin can read authenticated responses
/// from the account, session, profile and MFA-setup endpoints for whoever is signed in. Answer for origins
/// the host controls or has verified, never for one taken from the request. The default implementation
/// (<see cref="DenyInteractiveCorsOriginPolicy"/>) returns false for everything, so a host that does not
/// register one keeps the closed posture exactly.
/// </para>
/// </remarks>
public interface IInteractiveCorsOriginPolicy
{
    /// <summary>
    /// Whether <paramref name="origin"/> may make a credentialed cross-origin call to <paramref name="path"/>
    /// on the interactive auth API.
    /// </summary>
    /// <param name="context">The request. Tenant resolution has already run.</param>
    /// <param name="origin">The browser-supplied <c>Origin</c> header, already validated as an origin.</param>
    /// <param name="path">The request path, always under one of the interactive prefixes.</param>
    ValueTask<bool> IsAllowedAsync(HttpContext context, string origin, string path);
}

/// <summary>The default: no cross-origin caller reaches the interactive auth API with credentials.</summary>
internal sealed class DenyInteractiveCorsOriginPolicy : IInteractiveCorsOriginPolicy
{
    public ValueTask<bool> IsAllowedAsync(HttpContext context, string origin, string path)
        => ValueTask.FromResult(false);
}
