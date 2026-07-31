using Authagonal.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services;

/// <summary>
/// Says so when the token-signing private key is persisted in the clear.
/// </summary>
/// <remarks>
/// The signing key stores now route <c>KeyMaterialJson</c> — the full JWK, private scalar included —
/// through <see cref="IFieldCipher"/>, the same seam the user and grant stores use. With no cipher
/// registered that seam is a passthrough, which is the historical layout and stays the default so an
/// existing deployment keeps starting.
/// <para>
/// It is worth naming out loud rather than leaving implicit, because this is the one secret whose
/// exposure is not degraded access but complete impersonation of the issuer: anyone who can read the
/// primary data store can mint a token this server would sign for, for any user, any scope, any
/// session — and it sat in the same store as the data it protects. Every other credential-bearing
/// value in the product already had somewhere better to be.
/// </para>
/// </remarks>
internal sealed class PlaintextSigningKeyWarning(
    IServiceProvider services,
    IHostEnvironment environment,
    ILogger<PlaintextSigningKeyWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        if (environment.IsDevelopment()) return Task.CompletedTask;

        // A registered cipher means the stores encrypt; its absence is what this reports.
        if (services.GetService(typeof(IFieldCipher)) is not null) return Task.CompletedTask;

        logger.LogWarning(
            "Token-signing private keys are persisted without at-rest encryption: no IFieldCipher is " +
            "registered, so KeyMaterialJson (which contains the private key) is stored in the clear " +
            "alongside the data it protects. Anyone who can read the store can mint tokens this server " +
            "would be trusted to have signed. Register an IFieldCipher backed by Key Vault, KMS or " +
            "Vault Transit, or move signing to the Vault Transit provider so the key never lands here.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
