using System.Runtime.CompilerServices;

namespace Authagonal.Tests.Infrastructure;

/// <summary>
/// Process-wide host settings applied before any test builds a host.
/// </summary>
internal static class TestHostDefaults
{
    /// <summary>
    /// Turns off configuration reload-on-change for every host this assembly builds.
    /// </summary>
    /// <remarks>
    /// The default host configuration adds appsettings.json with <c>reloadOnChange: true</c>, which
    /// registers a <c>FileSystemWatcher</c> — one inotify instance per host. This suite builds
    /// hundreds of <c>WebApplicationFactory</c> hosts, xUnit runs classes in parallel, and Linux caps
    /// <c>fs.inotify.max_user_instances</c> at 128 by default. Past that cap the host constructor
    /// throws IOException, and because it fails during construction every test in the affected class
    /// fails at once, in about a millisecond, with no relation to what it was testing.
    ///
    /// That is a nasty failure to diagnose: it looks exactly like a container or fixture problem, it
    /// moves between classes from run to run, and it disappears when you re-run the class in
    /// isolation — which is precisely the evidence you would use to call it an infrastructure flake
    /// and move on. Nothing in this suite watches configuration files, so the watchers are pure cost.
    /// </remarks>
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "true");
    }
}
