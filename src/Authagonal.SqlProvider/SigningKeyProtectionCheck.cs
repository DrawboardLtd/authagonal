using Authagonal.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.SqlProvider;

/// <summary>
/// Startup verdict on the token-signing key material this backend stores: is it encrypted at rest.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to the DataProtection key-ring check, for the key that is worth strictly more. The
/// ring protects the authentication cookie; <c>SigningKeys.keyMaterialJson</c> is the full JWK
/// including <c>d</c>, the private scalar, so reading it is not degraded access but complete
/// impersonation of the issuer — access tokens and id_tokens for any subject, scope and client. On
/// Azure that material sits in Table Storage with the ring in a separate Blob container, each
/// independently grantable; on AWS, DynamoDB and S3. Here it is one database behind one connection
/// string, alongside every other store, so a <c>pg_dump</c>, a read replica, an analytics <c>SELECT</c>
/// or a restored backup carries it.
/// </para>
/// <para>
/// Passthrough remains the DEFAULT and is a supported layout — the OSS quick start has no key
/// management to hang a cipher off, and refusing to start would break it for everyone. What was wrong
/// is that the state was silent: an unencrypted key ring shouts at every boot while the more valuable
/// secret beside it said nothing at all. This says it once per start, outside Development, and
/// otherwise costs nothing.
/// </para>
/// </remarks>
internal sealed class SigningKeyProtectionCheck(
    IFieldCipher? fieldCipher,
    IHostEnvironment? environment,
    ILogger<SigningKeyProtectionCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        // A cipher is registered: the healthy case, and the only one that says nothing.
        if (fieldCipher is not null && fieldCipher is not NullFieldCipher) return Task.CompletedTask;

        // The quick start runs on SQLite with no key management by design; saying this every F5 would
        // train people to ignore it where it matters.
        if (environment?.IsDevelopment() == true) return Task.CompletedTask;

        logger.LogWarning(
            "Token-signing private keys are stored unencrypted in the SigningKeys table, because no " +
            "IFieldCipher is registered. That column holds the full JWK including the private scalar, " +
            "so anything that yields read access to this database — a dump, a read replica, an " +
            "analytics grant, a restored backup — yields the ability to mint tokens this server would " +
            "sign for, for any subject and any client. Treat the connection string as equivalent to " +
            "the signing key, or register an IFieldCipher before AddAuthagonalPostgres / " +
            "AddAuthagonalSqlite; existing keys keep loading and re-protect themselves at the next " +
            "rotation.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
