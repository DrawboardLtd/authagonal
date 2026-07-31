using Authagonal.Core.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services;

/// <summary>
/// Names, once per start, any client whose secret is still stored as a bare unsalted digest.
/// </summary>
/// <remarks>
/// The Duende migration tags an imported secret <c>SHA256$</c>/<c>SHA512$</c> purely by digest
/// length, and until now nothing ever moved it off that format: the rehash signal was discarded, the
/// migration report only mentioned secrets it SKIPPED, and the admin API redacts hashes from every
/// response — so an operator could not find out which clients were affected even if they suspected
/// it. An unsalted SHA-256 of a client secret is recoverable from a store dump by rainbow table.
/// <para>
/// The verifier now upgrades the hash on first successful authentication, which fixes every client
/// that is actually in use. This exists for the ones that are not: a client that never authenticates
/// never triggers the upgrade, so its secret sits in the weak format indefinitely with nothing
/// saying so.
/// </para>
/// </remarks>
internal sealed class LegacySecretHashWarning(
    IClientStore clientStore,
    ILogger<LegacySecretHashWarning> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        List<string> affected;
        try
        {
            var clients = await clientStore.GetAllAsync(ct).ConfigureAwait(false);
            affected = clients
                .Where(c => c.ClientSecretHashes.Any(PasswordHasher.IsUnsaltedDigestHash))
                .Select(c => c.ClientId)
                .ToList();
        }
        catch (Exception ex)
        {
            // A store that is not reachable at startup must not stop the host from starting; the
            // warning is diagnostic, not load-bearing.
            logger.LogDebug(ex, "Could not audit client secret hash formats at startup");
            return;
        }

        if (affected.Count == 0) return;

        logger.LogWarning(
            "{Count} client(s) still hold a client secret as an unsalted digest, which is recoverable " +
            "from a store dump by rainbow table: {ClientIds}. The hash is upgraded automatically the " +
            "next time each one authenticates; a client that never authenticates should have its " +
            "secret rotated.",
            affected.Count, string.Join(", ", affected));
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
