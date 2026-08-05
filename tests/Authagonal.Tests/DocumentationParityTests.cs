using System.Reflection;
using Authagonal.Server.Services.Cluster;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// Configuration an operator cannot discover, and locale counts the docs disagree with themselves on.
//
// Two of this pass's findings were the same shape from opposite ends: a key that governs a security
// control and appears in no document (`Cluster:AllowLoopbackWithoutSecret`, which is the only way to
// restore the previous `/_internal` behaviour, findable only by reading the source), and a locale the
// docs advertise that the build does not ship (`tlh`, removed deliberately, with docs/localization.md
// never updated — while docs/index.md's count was right, so the two pages contradicted each other).
//
// Enforced over the whole set rather than asserted about the two cases, because both were one-of-N: the
// next option added and the next locale dropped are the ones nobody will think to document.
// -------------------------------------------------------------------------------------------------
public sealed class DocumentationParityTests
{
    /// <summary>
    /// Every `Cluster:*` option is named in the configuration reference.
    /// </summary>
    /// <remarks>
    /// <c>AllowLoopbackWithoutSecret</c> and <c>RunLeaderElection</c> appeared in no <c>docs/</c> file.
    /// The first decides whether <c>/_internal/*</c> authorizes anybody at all when <c>Cluster:Secret</c>
    /// is unset — so an operator whose fan-out started answering 404 had no documented remedy, because the
    /// key that is the remedy was not written down. The second is the only way to say "join the cluster but
    /// never hold leadership", and it existed as a parameter that <c>AddAuthagonal</c> never exposed.
    /// <para>
    /// Reflected off the options class rather than a hand-written list, so adding a cluster option without
    /// documenting it fails here.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryClusterOptionIsInTheConfigurationReference()
    {
        var reference = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "configuration.md"));

        var undocumented = typeof(ClusterOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => $"Cluster:{p.Name}")
            .Where(key => !reference.Contains(key, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(undocumented);
    }

    /// <summary>
    /// No document tells an operator that <c>/_internal/*</c> is reachable by source address.
    /// </summary>
    /// <remarks>
    /// <c>InternalEndpointGuard</c> authorizes nobody without <c>Cluster:Secret</c>, and refuses private
    /// ranges even with the loopback opt-in. Three pages in seven locales each described the removed
    /// behaviour — "reachable only from loopback / private (RFC 1918 / link-local / ULA) source IPs" — so
    /// the documented deployment silently answered 404 to every internal call. Checked as the absence of
    /// the RFC that only the old rule cited, across every locale, since that is where a stale claim
    /// survives a fix to the English page.
    /// </remarks>
    [Fact]
    public void NoDocumentClaimsPrivateRangeAddressesReachTheInternalEndpoints()
    {
        var root = RepositoryRoot();

        var offenders = Directory
            .GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith("review-", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("RFC 1918", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The documented locale count matches the locales the login app actually ships.
    /// </summary>
    /// <remarks>
    /// <c>docs/localization.md</c> advertised eleven, including a Klingon (<c>tlh</c>) novelty locale that
    /// had been removed deliberately along with the whole novelty-locale mechanism. Ten ship.
    /// <c>docs/index.md</c> said ten, so the two pages disagreed and the code agreed with the one nobody
    /// was reading. An operator narrowing <c>branding.languages</c> to the documented list gets a picker
    /// entry with no resource bundle behind it: every string falls back to English, silently.
    /// </remarks>
    [Fact]
    public void TheDocumentedLocaleCountMatchesWhatShips()
    {
        var root = RepositoryRoot();

        var shipped = Directory
            .GetFiles(Path.Combine(root, "login-app", "src", "i18n"), "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // The registry is the single source of truth for both i18next and every picker, so a locale file
        // that is not registered would not be reachable either.
        var registry = File.ReadAllText(
            Path.Combine(root, "login-app", "src", "i18n", "index.ts"));
        foreach (var locale in shipped)
            Assert.Contains($"code: '{locale}'", registry);

        Assert.Contains(
            $"localized into {shipped.Count} languages",
            File.ReadAllText(Path.Combine(root, "docs", "index.md")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// No locale of the localization page advertises a locale the build does not contain.
    /// </summary>
    [Fact]
    public void NoLocalizationPageAdvertisesAnUnshippedLocale()
    {
        var root = RepositoryRoot();

        var shipped = Directory
            .GetFiles(Path.Combine(root, "login-app", "src", "i18n"), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);

        var pages = Directory.GetFiles(Path.Combine(root, "docs"), "localization.md", SearchOption.AllDirectories);
        Assert.NotEmpty(pages);

        var offenders = new List<string>();
        foreach (var page in pages)
        {
            foreach (var line in File.ReadAllLines(page))
            {
                // The Supported Languages table: one row per locale, code first in backticks.
                if (!line.StartsWith("| `", StringComparison.Ordinal)) continue;

                var code = line[3..].Split('`')[0];
                if (code.Length is 0 or > 12) continue;
                if (!shipped.Contains(code))
                    offenders.Add($"{Path.GetRelativePath(root, page)}: advertises '{code}'");
            }
        }

        Assert.Empty(offenders);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Authagonal.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
