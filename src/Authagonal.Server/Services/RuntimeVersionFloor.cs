using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services;

/// <summary>
/// Warns, once per start, when the host is running on a shared framework older than the patch that
/// fixes the XML-crypto CVEs this server's SAML path can reach.
/// </summary>
/// <remarks>
/// GHSA-37gx-xxp4-5rgx (EncryptedXml infinite loop) and GHSA-w3x6-4m5h-cxqf (uncontrolled resource
/// consumption plus XXE) are both reachable from <c>SamlResponseParser</c>, which processes
/// unauthenticated XML on the ACS endpoint. They were addressed with a
/// <c>System.Security.Cryptography.Xml</c> PackageReference — which never travelled, because this is
/// an <c>Sdk.Web</c> library and the SDK prunes a dependency the shared framework already supplies.
/// <para>
/// The assembly comes from <c>Microsoft.AspNetCore.App</c>, so the version that matters is the
/// runtime's, and a library cannot pin that. It can say so, which is what this does: a consumer on an
/// old patch gets a named, actionable warning instead of a silent belief that a package reference
/// covered it.
/// </para>
/// </remarks>
internal sealed class RuntimeVersionFloor(ILogger<RuntimeVersionFloor> logger) : IHostedService
{
    /// <summary>Lowest patch on each supported major that carries the fix.</summary>
    private static readonly (int Major, Version Floor)[] Floors =
    [
        (9, new Version(9, 0, 15)),
        (10, new Version(10, 0, 6)),
    ];

    public Task StartAsync(CancellationToken ct)
    {
        if (TryReadRuntimeVersion(out var running) &&
            Floors.FirstOrDefault(f => f.Major == running.Major) is { Floor: not null } match &&
            running < match.Floor)
        {
            logger.LogWarning(
                "Running on .NET {Running}. The XML-crypto fixes this server's SAML path depends on " +
                "(GHSA-37gx-xxp4-5rgx, GHSA-w3x6-4m5h-cxqf — both reachable from the anonymous SAML ACS " +
                "endpoint) ship in {Floor} or later. Update the runtime.",
                running, match.Floor);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// The running framework version, from the description string (".NET 10.0.10"). Environment.Version
    /// reports the assembly version, which is not the patch level.
    /// </summary>
    private static bool TryReadRuntimeVersion(out Version version)
    {
        version = new Version(0, 0);
        var description = RuntimeInformation.FrameworkDescription;

        foreach (var token in description.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Trims any pre-release suffix (".NET 10.0.0-rc.2.24473.5").
            var numeric = token.Split('-', 2)[0];
            if (numeric.Contains('.') && Version.TryParse(numeric, out var parsed))
            {
                version = parsed;
                return true;
            }
        }

        return false;
    }
}
