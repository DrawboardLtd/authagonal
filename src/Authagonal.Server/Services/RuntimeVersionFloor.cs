using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services;

/// <summary>
/// Asserts, at start, that the host is running on a shared framework no older than the current
/// security patch of its major — and refuses to start when the operator has asked it to.
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
/// old patch gets a named, actionable message instead of a silent belief that a package reference
/// covered it.
/// </para>
/// <para>
/// The floor is the whole shared framework, not those two advisories. Naming only the XML pair let
/// the floor sit at 9.0.15 / 10.0.6 while three further security releases shipped on each major, so
/// a host on 9.0.16 or 10.0.9 — carrying every CVE fixed after the XML pair and before the current
/// patch — was told nothing at all. The floor tracks the latest security release of each supported
/// major and moves with it; the SAML advisories are named in the message because they are the ones
/// an anonymous request can reach directly, not because they are the only ones.
/// </para>
/// <para>
/// <b>Refuse or log</b> is the operator's call, and the default is to log. Refusing turns a version
/// bump of this package into an outage on a fleet whose runtime is one patch behind — the same
/// reasoning <see cref="KeyRingStartupCheck"/> applies to a populated key ring — so
/// <c>Auth:RequireMinimumRuntime</c> is opt-in, for the deployment that would rather not start than
/// serve SAML on an unpatched runtime. Either way it is Critical, not Warning: a warning about the
/// runtime under an unpatched identity provider reads like every other warning in the log.
/// </para>
/// </remarks>
internal sealed class RuntimeVersionFloor : IHostedService
{
    /// <summary>
    /// Lowest patch on each supported major that carries every shared-framework security fix
    /// released to date. Both are security releases dated 2026-07-14.
    /// </summary>
    private static readonly (int Major, Version Floor)[] Floors =
    [
        (9, new Version(9, 0, 18)),
        (10, new Version(10, 0, 10)),
    ];

    private readonly ILogger<RuntimeVersionFloor> _logger;
    private readonly bool _requireMinimumRuntime;
    private readonly Func<Version?> _readRunningVersion;

    public RuntimeVersionFloor(ILogger<RuntimeVersionFloor> logger, bool requireMinimumRuntime)
        : this(logger, requireMinimumRuntime, ReadRuntimeVersion)
    {
    }

    /// <summary>Test seam: the running version is otherwise whatever this process happens to be on.</summary>
    internal RuntimeVersionFloor(
        ILogger<RuntimeVersionFloor> logger,
        bool requireMinimumRuntime,
        Func<Version?> readRunningVersion)
    {
        _logger = logger;
        _requireMinimumRuntime = requireMinimumRuntime;
        _readRunningVersion = readRunningVersion;
    }

    public Task StartAsync(CancellationToken ct)
    {
        var running = _readRunningVersion();
        if (running is null)
            return Task.CompletedTask;

        if (Floors.FirstOrDefault(f => f.Major == running.Major) is not { Floor: not null } match ||
            running >= match.Floor)
        {
            return Task.CompletedTask;
        }

        var message =
            $"Running on .NET {running}, below the {match.Floor} security floor this server requires. " +
            "The shared framework supplies the XML-crypto fixes the SAML path depends on " +
            "(GHSA-37gx-xxp4-5rgx, GHSA-w3x6-4m5h-cxqf — both reachable from the anonymous SAML ACS " +
            $"endpoint) plus every security fix released since. Update the runtime to {match.Floor} or later.";

        if (_requireMinimumRuntime)
        {
            // Auth:RequireMinimumRuntime — the operator asked for a refusal rather than a log line.
            throw new InvalidOperationException(
                message + " Set Auth:RequireMinimumRuntime=false to start anyway.");
        }

        _logger.LogCritical("{Message} Set Auth:RequireMinimumRuntime=true to refuse to start instead.", message);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// The running framework version, from the description string (".NET 10.0.10"). Environment.Version
    /// reports the assembly version, which is not the patch level. Null when it cannot be parsed —
    /// an unrecognised description must not be the reason an identity provider fails to boot.
    /// </summary>
    private static Version? ReadRuntimeVersion()
    {
        var description = RuntimeInformation.FrameworkDescription;

        foreach (var token in description.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Trims any pre-release suffix (".NET 10.0.0-rc.2.24473.5").
            var numeric = token.Split('-', 2)[0];
            if (numeric.Contains('.') && Version.TryParse(numeric, out var parsed))
                return parsed;
        }

        return null;
    }
}
