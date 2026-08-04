using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services;

/// <summary>
/// Says so when a host has registered <see cref="VaultTransitCryptoProvider"/> expecting it to sign tokens.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/extensibility.md</c> carried a complete DI snippet — register an HTTP client, register
/// <c>VaultTransitClient</c>, register <see cref="VaultTransitCryptoProvider"/>, call <c>AddAuthagonal</c> —
/// under the heading "Authagonal can delegate JWT signing to HashiCorp Vault's Transit secrets engine.
/// Private keys never leave Vault." The README, <c>docs/index.md</c>, <c>docs/configuration.md</c> and
/// <c>docs/backup-restore.md</c> all repeated the capability.
/// </para>
/// <para>
/// Nothing implemented it. <c>ProtocolKeyManager</c> calls
/// <c>ProtocolSigningKeyOps.BuildSigningCredentials</c>, which constructs an <c>ECDsaSecurityKey</c> from the
/// material in <c>ISigningKeyStore</c>; no seam substitutes a <see cref="VaultTransitSecurityKey"/>, and the
/// provider was never attached to a <c>CryptoProviderFactory</c>. So an operator with an HSM compliance
/// requirement followed the documentation exactly, saw <c>/connect/token</c> issue ES256 tokens that verified
/// against JWKS, and concluded Vault was signing them — while the private key had been generated locally on
/// first boot and written to the primary data store, in plaintext unless an <c>IFieldCipher</c> happened to be
/// registered. Read access to that store is complete impersonation of the issuer.
/// </para>
/// <para>
/// Error rather than Warning, and it names the belief rather than the symptom, because there IS no symptom:
/// everything works, which is precisely why the misconception survives. Not a refusal to start — the
/// deployment is functional and failing it would be an outage over a documentation defect.
/// </para>
/// </remarks>
internal sealed class VaultTransitSigningWarning(
    IServiceProvider services,
    ILogger<VaultTransitSigningWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        if (services.GetService(typeof(VaultTransitCryptoProvider)) is null)
            return Task.CompletedTask;

        logger.LogError(
            "VaultTransitCryptoProvider is registered, but JWT signing is NOT delegated to Vault Transit. "
            + "ProtocolKeyManager signs with the key in ISigningKeyStore, and nothing substitutes a "
            + "VaultTransitSecurityKey for it — so tokens are being signed by a locally generated private key "
            + "held in your primary data store, in the clear unless an IFieldCipher is registered. Earlier "
            + "documentation presented this registration as enabling remote signing; it does not. If you have "
            + "a requirement that signing keys never leave an HSM, this deployment does not meet it. "
            + "Registering an IFieldCipher at least removes the plaintext copy.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
