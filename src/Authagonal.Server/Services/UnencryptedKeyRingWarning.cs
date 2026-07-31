using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services;

/// <summary>
/// Restates, once per start and at Critical, that the DataProtection key ring is persisted without
/// encryption because the operator opted in.
/// </summary>
/// <remarks>
/// ASP.NET Core already warns about an unencrypted ring — at Information level, which the shipped
/// log configuration (<c>Microsoft.AspNetCore: Warning</c>) discards. So the one signal that the
/// cookie-protecting keys are sitting in the clear was invisible in every default deployment.
/// </remarks>
internal sealed class UnencryptedKeyRingWarning(ILogger<UnencryptedKeyRingWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        logger.LogCritical(
            "DataProtection:AllowUnencryptedKeyRing is set. The key ring is persisted as plaintext XML. " +
            "It protects the authentication cookie, so anyone who can read the key store can forge a " +
            "session for any user. Configure DataProtection:KeyVaultKeyId or " +
            "DataProtection:CertificateThumbprint.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
