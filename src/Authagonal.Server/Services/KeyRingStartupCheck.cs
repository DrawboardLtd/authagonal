using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

/// <summary>
/// The single startup verdict on the DataProtection key ring: is it durable, and is it encrypted.
/// </summary>
/// <remarks>
/// <para>
/// Both questions are answered from the RESOLVED <see cref="KeyManagementOptions"/> rather than from
/// configuration, and that is the whole point of this class. <c>AddAuthagonal</c> can only see the
/// repository it attaches itself — the Azure one, keyed off <c>DataProtection:BlobUri</c> or
/// <c>Storage:ConnectionString</c>. A SQL or S3 host attaches its own through
/// <c>PersistDataProtectionKeysToSql</c> / <c>PersistDataProtectionKeysToS3</c>, before or after
/// <c>AddAuthagonal</c>, which the registration code cannot observe in either ordering. So the previous
/// encryption check — a registration-time throw guarded on the two Azure settings — was correct for
/// Azure and silent for every other persistent backend: a SQL deployment with no DataProtection key
/// started happily with the ring in plaintext, which is the exact state the check existed to prevent.
/// Reading the resolved options is right for Azure, AWS, SQL and any repository a host registers itself.
/// </para>
/// <para>
/// The ring protects the authentication cookie. Persisted without an <see cref="IXmlEncryptor"/> it is
/// stored as plaintext XML including the master key, so read access to the store — a database dump, a
/// bucket listing, an analytics grant — is the ability to forge a session for any user. ASP.NET Core
/// does warn about this, at Information level, which the shipped log configuration
/// (<c>Microsoft.AspNetCore: Warning</c>) discards.
/// </para>
/// <para>
/// <b>Refuse or warn</b> turns on whether the ring already has keys in it. An empty repository means
/// persistence was just configured and nothing depends on it yet, so starting is refused and the
/// insecure state never comes into existence. A populated one means an existing deployment is being
/// upgraded: its users' cookies are already encrypted under those keys, and a version bump that
/// suddenly refuses to boot is an outage, so that case is a Critical log with the exact remedy. If the
/// repository cannot be read at all, the two cases are indistinguishable and it degrades to the
/// warning — availability is the safer default when the alternative is guessing.
/// </para>
/// </remarks>
internal sealed class KeyRingStartupCheck(
    IOptions<KeyManagementOptions> keyManagement,
    IHostEnvironment environment,
    ILogger<KeyRingStartupCheck> logger,
    bool allowUnencryptedKeyRing) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        KeyManagementOptions options;
        try
        {
            options = keyManagement.Value;
        }
        catch (Exception ex)
        {
            // Never let the diagnostic be the thing that stops the server booting.
            logger.LogDebug(ex, "Could not inspect the DataProtection key ring configuration");
            return Task.CompletedTask;
        }

        if (options.XmlRepository is null)
        {
            ReportEphemeral();
            return Task.CompletedTask;
        }

        // Encrypted and durable: the healthy case, and the only one that says nothing. Note this
        // returns before touching the repository, so a correctly configured deployment pays no
        // startup I/O for the check.
        if (options.XmlEncryptor is not null) return Task.CompletedTask;

        if (allowUnencryptedKeyRing)
        {
            logger.LogCritical(
                "DataProtection:AllowUnencryptedKeyRing is set. The key ring is persisted as plaintext XML. " +
                "It protects the authentication cookie, so anyone who can read the key store can forge a " +
                "session for any user. Configure DataProtection:KeyVaultKeyId or " +
                "DataProtection:CertificateThumbprint.");
            return Task.CompletedTask;
        }

        // Development runs on SQLite or the file repository as a matter of course, and the quick start
        // configures no key. Refusing there would break `docker compose up` for everyone, and saying it
        // every F5 would train people to ignore the message when it matters.
        if (environment.IsDevelopment()) return Task.CompletedTask;

        if (RingAlreadyHasKeys(options.XmlRepository))
        {
            logger.LogCritical(
                "The DataProtection key ring is persisted but NOT encrypted. It protects the " +
                "authentication cookie, so anyone who can read the key store can forge a session for any " +
                "user. This deployment already has keys, so startup continues rather than breaking a " +
                "running system — but it is not a safe state to stay in. Set " +
                "DataProtection:KeyVaultKeyId or DataProtection:CertificateThumbprint, or set " +
                "DataProtection:AllowUnencryptedKeyRing=true to accept the risk explicitly.");
            return Task.CompletedTask;
        }

        throw new InvalidOperationException(
            "The DataProtection key ring is persisted but not encrypted. This ring protects the " +
            "authentication cookie, so a store read yields the ability to forge sessions. Set " +
            "DataProtection:KeyVaultKeyId or DataProtection:CertificateThumbprint, or set " +
            "DataProtection:AllowUnencryptedKeyRing=true to accept the risk explicitly.");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Whether this ring is already in service. Any read failure answers "yes", because the caller
    /// uses this to decide between refusing to start and logging — and refusing to start on the
    /// strength of a failed read would turn a transient store blip into an outage.
    /// </summary>
    private bool RingAlreadyHasKeys(IXmlRepository repository)
    {
        try
        {
            return repository.GetAllElements().Count > 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not read the DataProtection key repository to tell whether this is a new or an " +
                "existing key ring; assuming existing and continuing with a warning.");
            return true;
        }
    }

    private void ReportEphemeral()
    {
        // Development is expected to run on the file repository; saying this every F5 would train
        // people to ignore it.
        if (environment.IsDevelopment()) return;

        logger.LogCritical(
            "No DataProtection key repository is configured, so the key ring falls back to a " +
            "per-machine file store that is not shared between instances and does not survive a " +
            "restart. The ring protects the authentication cookie: every restart signs every user " +
            "out, and no two instances accept each other's cookies. Set DataProtection:BlobUri " +
            "(Azure), or use the SQL/S3 key-ring helpers, before running in production.");
    }
}
