using Authagonal.Core.Clustering;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.Bff;

/// <summary>
/// Says so when this BFF looks like a multi-instance deployment and has no cross-replica refresh lock.
/// </summary>
/// <remarks>
/// The hazard is described in full on <see cref="IBffRefreshLockStore"/>: without a cross-process lock
/// two replicas can redeem the same rotating refresh token, the IdP reads the second redemption as a
/// stolen-token replay, and the whole grant family is revoked — the user is signed out everywhere, from
/// nothing but concurrent load.
/// <para>
/// That was already documented on <c>AddAuthagonalBff</c> and on the coordinator, in detail, and
/// documentation is the wrong medium for it: an XML remark reaches whoever reads the source, and this
/// reaches whoever ships the deployment. It is also the shape this project has been converting to startup
/// diagnostics everywhere else — a guarantee the code describes and the configuration does not provide —
/// alongside the plaintext-signing-key, null-audit-logger, publish-ahead and undeclared-proxy warnings.
/// The BFF refresh gate was the one that kept the silent degrade.
/// </para>
/// <para>
/// The second remedy the docs offer, a non-zero <c>Auth:RefreshTokenReuseGraceSeconds</c>, is not
/// checkable from here — it is configuration on the identity provider, which may not be this process or
/// even this product — so it is named in the message rather than tested for. It is also off in the
/// pairing this repository ships: <c>AuthOptions.RefreshTokenReuseGraceSeconds</c> defaults to 0, strict.
/// </para>
/// <para>
/// Deliberately not gated on <c>IsDevelopment</c>, unlike <c>PlaintextSigningKeyWarning</c>. That one
/// reports how a secret is stored, which is a production concern by definition; this reports the shape of
/// a deployment, and a developer pointing a host at shared infrastructure is exactly who benefits from
/// hearing it before it reaches production. The detection below is narrow enough that the single-process
/// default — the in-memory cache <c>AddAuthagonalBff</c> installs — never triggers it.
/// </para>
/// </remarks>
internal sealed class BffRefreshGateWarning(
    IServiceProvider services,
    ILogger<BffRefreshGateWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        // Resolved in a scope, not through constructor injection. A hosted service is constructed from
        // the ROOT provider during Host.StartAsync, so taking a store that a multi-tenant host registered
        // per scope would throw before StartAsync is even entered — which is how LegacySecretHashWarning
        // once stopped those hosts from starting at all. The cost of being wrong here is a diagnostic;
        // the cost of that shape is the process.
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        if (sp.GetService<ILeaseProvider>() is not null) return Task.CompletedTask;

        var store = sp.GetService<IBffSessionStore>();
        if (store is IBffRefreshLockStore) return Task.CompletedTask;

        if (!LooksShared(sp, store)) return Task.CompletedTask;

        logger.LogWarning(
            "This BFF has a session store shared across instances but no cross-replica refresh lock, so " +
            "refresh single-flight is per-process only. Two replicas can redeem the same rotating refresh " +
            "token, which the identity provider reads as a stolen-token replay — it revokes the whole grant " +
            "family and the user is signed out everywhere, under ordinary concurrent load. Fix it by " +
            "registering an ILeaseProvider (AddAuthagonalClustering with UseAzureStorage / the AWS or SQL " +
            "equivalent), or by implementing IBffRefreshLockStore on the session store (a conditional write " +
            "with a TTL — SET NX PX on Redis). Failing both, set a non-zero Auth:RefreshTokenReuseGraceSeconds " +
            "on the identity provider, which absorbs the double redemption; note that Authagonal.Server's own " +
            "default for it is 0, i.e. strict. Ignore this if you genuinely run a single instance.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether the sessions this BFF writes are visible to another replica — the precondition for the
    /// race, and the thing that separates a real deployment from a developer running one process.
    /// </summary>
    /// <remarks>
    /// Two shapes count, and both are inferences rather than facts the container can state:
    /// <list type="bullet">
    /// <item>The default store over an <c>IDistributedCache</c> that is not the in-memory one. Swapping
    /// that cache is the documented way to go multi-instance, so it is the intent expressed as
    /// configuration.</item>
    /// <item>Any other <see cref="IBffSessionStore"/>. A host does not write one of those to run a single
    /// process — the in-memory default already covers that — so the reasonable reading of a custom store
    /// is shared infrastructure.</item>
    /// </list>
    /// Both can be wrong, and the asymmetry is what settles it: a false positive is one log line at boot
    /// on a host that can ignore it, and a false negative is silence in front of the failure this class
    /// exists to announce.
    /// </remarks>
    private static bool LooksShared(IServiceProvider sp, IBffSessionStore? store)
    {
        if (store is not DistributedCacheBffSessionStore) return store is not null;

        return sp.GetService<IDistributedCache>() is not (null or MemoryDistributedCache);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
