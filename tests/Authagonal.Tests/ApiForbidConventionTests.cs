using System.Text.RegularExpressions;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// F334, generalised — Results.Forbid() must not reach an API surface.
//
// Results.Forbid() does not write a 403. It delegates to the authentication scheme's forbid handler,
// and on a cookie scheme that handler answers 302 to the login path. An admin API client that had
// authenticated perfectly well therefore received a login page for an authorization failure: it
// could not tell "your token expired" from "you may not do that", and an automated caller followed
// the redirect and parsed HTML as its API response.
//
// The create path in ClientEndpoints was fixed for exactly this. The update path was missed and sat
// there undetected, because the shipped IClientScopeGuard grants everything and no test could reach
// either branch. ClientScopeGuardDenialTests now covers those two branches behaviourally; this test
// covers the defect CLASS, so the next handler to reach for Forbid() fails here instead of shipping.
//
// The correct replacement is an explicit TypedResults.Json(..., statusCode: 403) carrying a reason.
// -------------------------------------------------------------------------------------------------
public sealed class ApiForbidConventionTests
{
    /// <summary>
    /// Files permitted to call <c>Results.Forbid()</c>, each with the reason. Empty by design: there
    /// is currently no endpoint in this server for which a redirect-to-login is the right answer to
    /// an authorization failure. Adding an entry is a deliberate act that must state why.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal);

    [Fact]
    public void NoEndpointReturnsResultsForbid()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        Assert.True(Directory.Exists(src), $"Expected the source tree at '{src}'.");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (Allowed.ContainsKey(relative)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // Comments discuss this rule by name (including the one explaining the fix), so a
                // line that is itself a comment is not an occurrence. A trailing comment on a real
                // call still trips, which is intended.
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith('*')
                    || trimmed.StartsWith("/*", StringComparison.Ordinal))
                    continue;

                if (Regex.IsMatch(lines[i], @"\bResults\.Forbid\s*\("))
                    offenders.Add($"{relative}:{i + 1}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Results.Forbid() runs the authentication scheme's forbid handler — on the cookie scheme "
            + "that is a 302 to the login page, not a 403, so an API caller cannot distinguish an "
            + "expired token from an authorization refusal. Return TypedResults.Json(..., statusCode: 403) "
            + "with a reason instead. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Walks up from the test assembly to the directory holding the solution file. Fails loudly
    /// rather than skipping: a lint that quietly finds nothing to scan is worse than no lint.
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
