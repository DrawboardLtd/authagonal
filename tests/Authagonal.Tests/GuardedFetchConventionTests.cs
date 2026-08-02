using System.Text.RegularExpressions;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// Structural piece 2 — the guard must not be separable from the fetch.
//
// Every finding in this area had the same shape: OutboundUrl.IsSafe(url) on one line, and a raw
// HttpClient send on another. Both statements were present and correct when written, and then an edit
// moved the send, added a second one, or changed which url it used, and nothing in the code said the
// check upstream was meant to cover it. #52, #62, #66 and #346 are four instances of that one shape.
//
// So a file that fetches through one of the guarded named clients must go through SafeOutboundHttp,
// which carries the check inside the call. This lints for the shape rather than for the bug, because
// the bug is always new and the shape never is.
// -------------------------------------------------------------------------------------------------
public sealed class GuardedFetchConventionTests
{
    /// <summary>
    /// The clients whose target is chosen by something other than this code, so every send through them
    /// has to carry the guard.
    /// </summary>
    private static readonly string[] GuardedClients =
        ["SamlMetadata", "OidcDiscovery", "Provisioning", "AuthagonalJwks", "BackChannelLogout"];

    /// <summary>
    /// A raw send on an <c>HttpClient</c> — the thing that must not appear in a file that also creates one
    /// of the guarded clients.
    /// </summary>
    /// <remarks>
    /// Matched on the method name against a local rather than on a type, because the type is only known to
    /// the compiler; a lint that needed semantic analysis to state this rule would not be one anybody keeps.
    /// The false-positive risk is a non-HttpClient local called <c>client</c> or <c>http</c>, which is why
    /// the rule is scoped to files that create a guarded client in the first place.
    /// </remarks>
    private static readonly Regex RawSend = new(
        @"\b(?:client|http|httpClient|_client)\s*\.\s*(?:SendAsync|PostAsync|GetAsync|GetStringAsync|PutAsync|DeleteAsync|PatchAsync)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex CreatesGuardedClient = new(
        @"CreateClient\(\s*""(?<name>[^""]+)""\s*\)", RegexOptions.Compiled);

    /// <summary>
    /// Files permitted a raw send, each with the reason. Empty by design.
    /// </summary>
    /// <remarks>
    /// An entry has to explain why that send cannot go through <see cref="Core.Services.SafeOutboundHttp"/> —
    /// and "it validates the URL just above" is not a reason, it is the shape being removed.
    /// </remarks>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["Authagonal.Server/Endpoints/OidcEndpoints.cs"] =
            "The upstream token exchange and userinfo call, which are POST/GET with the connection's client "
            + "secret and access token to endpoints the DISCOVERY DOCUMENT named — and that document was "
            + "fetched through SafeOutboundHttp, https-pinned, bound to its own issuer, and had each endpoint "
            + "it names re-checked for scheme and address before any of them is used. The guard for these "
            + "sends is OidcDiscoveryClient, one layer up, and re-running it per send would re-validate "
            + "values this request cannot influence.",
        ["Authagonal.Server/Services/UserStoreOidcSubjectResolver.cs"] =
            "The upstream refresh POST, to the token_endpoint of the same already-validated discovery "
            + "document. Same reasoning as OidcEndpoints.",
        ["Authagonal.Server/Endpoints/Admin/ProvisioningEndpoints.cs"] =
            "The admin 'test this app' probe. It is an authenticated admin action against a URL that admin "
            + "just supplied, it checks it through OutboundUrlValidator immediately above, and its whole "
            + "purpose is to report the transport outcome to the caller — which SafeOutboundHttp's "
            + "exception-on-refusal would hide behind a 500.",
    };

    [Fact]
    public void AGuardedClientIsNeverFetchedRaw()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        Assert.True(Directory.Exists(src), $"Expected the source tree at '{src}'.");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (Allowed.ContainsKey(relative)) continue;

            var lines = File.ReadAllLines(file);

            // Only files that reach for a guarded client are in scope. A file that never creates one has no
            // guarded send to get wrong, and linting every HttpClient in the tree would catch the BFF proxy
            // and the Turnstile verifier, whose targets nothing but the operator chooses.
            var usesGuarded = lines.Any(l => CreatesGuardedClient.Matches(l)
                .Any(m => GuardedClients.Contains(m.Groups["name"].Value, StringComparer.Ordinal)));
            if (!usesGuarded) continue;

            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')) continue;

                if (RawSend.IsMatch(lines[i]))
                    offenders.Add($"{relative}:{i + 1}  {trimmed}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A guarded outbound client is being sent to directly. Route it through SafeOutboundHttp so the "
            + "SSRF check travels with the send: a check written as a separate statement is one an edit can "
            + "leave behind, which is what #52, #62, #66 and #346 each were. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The permitted list has to stay accurate: an entry for a file that no longer fetches raw is a licence
    /// nobody is using, and the next raw send in that file inherits it silently.
    /// </summary>
    [Fact]
    public void EveryPermittedFileStillNeedsItsPermission()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var stale = new List<string>();

        foreach (var (relative, _) in Allowed)
        {
            var path = Path.Combine(src, relative);
            Assert.True(File.Exists(path), $"Allowed entry '{relative}' names a file that does not exist.");

            if (!File.ReadAllLines(path).Any(l =>
                    !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && RawSend.IsMatch(l)))
                stale.Add(relative);
        }

        Assert.True(stale.Count == 0,
            "These files are permitted a raw guarded send and no longer make one. Remove the entry, so the "
            + "next raw send added there is caught rather than pre-authorised:"
            + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>
    /// Walks up from the test assembly to the directory holding the solution file. Fails loudly rather than
    /// skipping: a lint that quietly finds nothing to scan is worse than no lint.
    /// </summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Authagonal.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
