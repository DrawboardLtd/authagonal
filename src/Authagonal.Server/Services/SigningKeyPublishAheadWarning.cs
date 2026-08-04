using Authagonal.Core.Clustering;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

/// <summary>
/// Says so when this node can never publish a signing key ahead of its use.
/// </summary>
/// <remarks>
/// <para>
/// <c>ProtocolSigningKeyOps.PublishSuccessorIfDueAsync</c> is what stops a <c>kid</c> first appearing in
/// JWKS at the instant it starts signing, and both JWKS endpoints send <c>Cache-Control: max-age=3600</c>
/// on the strength of it. It is lease-guarded, because a cluster must publish ONE successor rather than
/// one per node — and a refused lease means it does nothing.
/// </para>
/// <para>
/// <see cref="NeverLeaseProvider"/> refuses always. It is installed container-wide by
/// <c>Cluster:RunLeaderElection=false</c> and by each of <c>UseAzureStorageBus</c> /
/// <c>UseAwsDynamoBus</c> / <c>UseSqlBus</c>, so an entire Deployment sharing one ConfigMap can be in
/// this state. Nothing distinguishes it from a healthy deployment until the active key expires, up to
/// <c>SigningKeyLifetimeDays</c> later — at which point every node mints its own replacement and
/// relying parties reject tokens until their key caches lapse. The asymmetry is deliberate and worth
/// naming: on the same refused lease, generation proceeds anyway (an IdP that cannot sign is completely
/// down), while publishing cannot.
/// </para>
/// <para>
/// A warning rather than a refusal: the documented Portal/Auth split in <c>docs/scaling.md</c> runs
/// bus-only nodes alongside a node that does hold the election, and that deployment is correct — the
/// leader publishes for the cluster. This cannot tell that case from the one where no node anywhere runs
/// the election, so it states the local fact and leaves the judgement to the operator.
/// </para>
/// </remarks>
internal sealed class SigningKeyPublishAheadWarning(
    IServiceProvider services,
    IOptions<AuthOptions> authOptions,
    ILogger<SigningKeyPublishAheadWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        if (services.GetService(typeof(ILeaseProvider)) is not NeverLeaseProvider)
            return Task.CompletedTask;

        var auth = authOptions.Value;

        logger.LogWarning(
            "Signing-key publish-ahead is disabled on this node: its lease provider never grants, so it "
            + "can never publish the next signing key. This is what Cluster:RunLeaderElection=false and "
            + "the UseAzureStorageBus/UseAwsDynamoBus/UseSqlBus helpers install. If no node in the "
            + "cluster runs leader election, then when the active key expires (lifetime "
            + "{Lifetime} days) every node will mint its own replacement and relying parties will reject "
            + "the resulting tokens until their JWKS caches expire — up to the 1 hour both JWKS endpoints "
            + "advertise. Leave leader election enabled on at least one node.",
            auth.SigningKeyLifetimeDays);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
