using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

/// <summary>
/// Says so, loudly, when the DataProtection key ring has nowhere durable to live.
/// </summary>
/// <remarks>
/// <c>AddDataProtection()</c> is called unconditionally, but persistence is attached only when
/// <c>DataProtection:BlobUri</c> is set or <c>Storage:ConnectionString</c> names a real Azure account.
/// The DOCUMENTED production Azure path is managed identity via <c>Storage:TableServiceUri</c>, which
/// leaves the connection string null — so an operator who follows the recommended configuration and
/// omits <c>DataProtection:BlobUri</c> matches neither branch and falls through to the framework's
/// per-machine file repository. In a container that is <c>/root/.aspnet/DataProtection-Keys</c>,
/// writable only because the image runs as root, and destroyed on every restart.
/// <para>
/// The consequence is not subtle but it is easy to misattribute: the ring protects the authentication
/// cookie, so every restart silently signs every user out, and no two pods agree on a cookie at all.
/// The framework's own warning about this is Information level, which the shipped log configuration
/// discards — so the only signal was the symptom.
/// </para>
/// <para>
/// The check runs against the resolved options rather than the registration branch, so a SQL or S3
/// host that attached its own repository — before or after <c>AddAuthagonal</c>, which the
/// registration code cannot see — is correctly treated as persisted.
/// </para>
/// </remarks>
internal sealed class EphemeralKeyRingWarning(
    IOptions<KeyManagementOptions> keyManagement,
    IHostEnvironment environment,
    ILogger<EphemeralKeyRingWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        // Development is expected to run on the file repository; saying this every F5 would train
        // people to ignore it.
        if (environment.IsDevelopment()) return Task.CompletedTask;

        IXmlRepository? repository;
        try
        {
            repository = keyManagement.Value.XmlRepository;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not inspect the DataProtection key repository");
            return Task.CompletedTask;
        }

        if (repository is not null) return Task.CompletedTask;

        logger.LogCritical(
            "No DataProtection key repository is configured, so the key ring falls back to a " +
            "per-machine file store that is not shared between instances and does not survive a " +
            "restart. The ring protects the authentication cookie: every restart signs every user " +
            "out, and no two instances accept each other's cookies. Set DataProtection:BlobUri " +
            "(Azure), or use the SQL/S3 key-ring helpers, before running in production.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
