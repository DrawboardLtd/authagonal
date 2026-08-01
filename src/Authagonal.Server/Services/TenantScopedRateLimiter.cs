using Authagonal.Core.Services;
using Microsoft.AspNetCore.Http;

namespace Authagonal.Server.Services;

/// <summary>
/// Prefixes every rate-limit key with the current tenant, then delegates to the real limiter.
/// </summary>
/// <remarks>
/// Every call site builds its own key (<c>login|{email}</c>, <c>scim|{clientId}</c>,
/// <c>device-approve|{sub}</c>, and so on) and none of them included the tenant. On a multi-tenant host that
/// means one shared budget across all tenants: a noisy or hostile tenant exhausts another tenant's
/// allowance for the same logical key — a cross-tenant denial of service through a component whose whole
/// purpose is to prevent denial of service. Where keys embed a value both tenants can produce (an email, a
/// source IP) they collide outright and one tenant's traffic locks out the other's users.
///
/// <para>
/// Implemented as a decorator so the fix applies to every existing call site without touching any of them,
/// and so a future call site cannot forget it. Resolved per call through
/// <see cref="IHttpContextAccessor"/> because <see cref="ITenantContext"/> is scoped on multi-tenant hosts
/// while the limiter is a singleton — the same pattern the key-manager resolver uses.
/// </para>
///
/// <para>
/// <paramref name="inner"/> is the interface, not <c>InProcessRateLimiter</c>. Tenant scoping is a
/// property of the KEY and is orthogonal to where the counter lives, so it has to hold over the durable
/// limiter as well — and taking the concrete type meant a deployment that turned on cluster-wide limiting
/// would have quietly dropped the tenant prefix, which is the cross-tenant denial of service this
/// decorator exists to prevent, reintroduced by the act of hardening something else.
/// </para>
/// </remarks>
public sealed class TenantScopedRateLimiter(
    IRateLimiter inner,
    IHttpContextAccessor httpContextAccessor) : IRateLimiter
{
    public Task<bool> IsRateLimitedAsync(
        string key, int maxAttempts, TimeSpan window, CancellationToken ct = default)
    {
        var tenant = ResolveTenant();
        return inner.IsRateLimitedAsync($"{tenant}|{key}", maxAttempts, window, ct);
    }

    private string ResolveTenant()
    {
        // No request scope (a background service, or a hand-constructed host) means a single logical
        // tenant, so a constant prefix is correct rather than a fallback that silently shares budgets.
        var services = httpContextAccessor.HttpContext?.RequestServices;
        if (services is null) return "default";

        try
        {
            return services.GetService<ITenantContext>()?.TenantId ?? "default";
        }
        catch (ObjectDisposedException)
        {
            // Scope torn down mid-flight (a cancelled request racing a limiter call).
            return "default";
        }
    }
}
