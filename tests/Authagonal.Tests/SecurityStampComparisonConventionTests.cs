using System.Text.RegularExpressions;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// F278, generalised — the security stamp must never be compared with an ordinal string compare.
//
// The stamp is the whole of the authorisation on the email-confirmation paths: the token is
// base64(stamp || email || exp) and carries no MAC, so the only thing standing between a forged
// token and a state change is the stamp compare. An ordinal compare short-circuits on the first
// differing byte, and the email half of the token is attacker-chosen, so the request can be replayed
// freely while the guess is refined a byte at a time.
//
// F278 named two sites and both were fixed. A third — the anonymous, unthrottled GET at
// AuthEndpoints.ConfirmEmailPageAsync — post-dated that sweep and was found by audit, not by a test,
// because no test can distinguish FixedTimeEquals from == behaviourally. That is precisely the shape
// a convention test exists for: this covers the defect CLASS, so the next site fails here rather
// than shipping and waiting for the next audit.
// -------------------------------------------------------------------------------------------------
public sealed class SecurityStampComparisonConventionTests
{
    /// <summary>
    /// Sites permitted to compare a stamp with an ordinal compare, each with the reason. Empty by
    /// design. Adding an entry is a deliberate act that must state why the value being compared is
    /// not attacker-supplied.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["Authagonal.Server/AuthagonalExtensions.cs"] =
            "Cookie-validation stamp revalidation. The stamp it compares against comes out of the "
            + "DataProtection-signed auth cookie, so it is server-issued and integrity-protected — a "
            + "caller cannot choose it, and cannot retry with a refined guess without forging the "
            + "cookie first. F278 excused this site explicitly and the audit agreed.",
    };

    /// <summary>
    /// Matches an ordinal comparison whose operands include a SecurityStamp — <c>string.Equals(a.
    /// SecurityStamp, …)</c>, <c>x.SecurityStamp == y</c>, and the <c>!=</c> form F278 was named for.
    /// </summary>
    private static readonly Regex OrdinalStampCompare = new(
        @"string\.Equals\s*\([^)]*SecurityStamp|SecurityStamp\s*(==|!=)|(==|!=)\s*[A-Za-z_.]*SecurityStamp",
        RegexOptions.Compiled);

    [Fact]
    public void SecurityStampIsNeverComparedWithAnOrdinalCompare()
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
                // The comments explaining this rule name the pattern, so a comment line is not an
                // occurrence. A trailing comment on a real comparison still trips, which is intended.
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith('*')
                    || trimmed.StartsWith("/*", StringComparison.Ordinal))
                    continue;

                // Assignment and null tests are not comparisons of the secret.
                if (Regex.IsMatch(lines[i], @"SecurityStamp\s*(==|!=)\s*null")) continue;
                if (Regex.IsMatch(lines[i], @"IsNullOrEmpty\s*\([^)]*SecurityStamp")) continue;

                if (OrdinalStampCompare.IsMatch(lines[i]))
                    offenders.Add($"{relative}:{i + 1}");
            }
        }

        Assert.True(offenders.Count == 0,
            "The security stamp authorises the email-confirmation paths on its own, and the token "
            + "carrying it has no MAC, so an ordinal compare that short-circuits on the first differing "
            + "byte is a timing oracle against a value an anonymous caller may retry without limit. Use "
            + "CryptographicOperations.FixedTimeEquals over the UTF-8 bytes. Offenders:"
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
