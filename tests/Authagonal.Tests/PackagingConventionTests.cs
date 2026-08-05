using System.Text.Json;
using System.Text.RegularExpressions;
using Authagonal.Migration;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// What a published package PROMISES about its environment, versus what it actually needs from it.
//
// Both defects here are the same shape: a build that treats something as the host's responsibility,
// and a manifest that says otherwise. Neither shows up in a build, a test run or `dotnet pack` /
// `npm publish` — only in the consumer's application, after install, as a failure with no obvious
// connection to its cause.
//
// Enforced as conventions over the whole set rather than as two assertions about two files, because
// in both cases the defect WAS the one-of-N omission: eight packages declared IsTrimmable and the
// ninth did not, and the vite config externalized four modules while the manifest peer-declared
// none of them. The next package added, and the next module externalized, are the ones nobody will
// think to check.
// -------------------------------------------------------------------------------------------------
public sealed class PackagingConventionTests
{
    // ── #68/#73: the one package nobody had trim-analyzed ────────────────────

    /// <summary>
    /// <c>Authagonal.Migration</c> was the only shipped package that declared no <c>IsTrimmable</c>.
    /// </summary>
    /// <remarks>
    /// The property is what turns the trim analyzer on, so the one package without it is the one whose
    /// reflection was never examined — and this one serialized through the reflection-based resolver. A host
    /// that trims or publishes AOT therefore got a <c>DuendeMigrationReport</c> with no property metadata:
    /// <c>JsonSerializer.Serialize(report)</c> writes the run marker's <c>StatsJson</c>, which is what the
    /// status endpoint reads and what an operator uses to confirm a cutover moved the rows it was supposed
    /// to. Losing it turns the migration's own record of what it did into <c>{}</c> while the run reports
    /// success.
    /// </remarks>
    [Fact]
    public void EveryPackableProjectDeclaresIsTrimmable()
    {
        var projects = Directory.GetFiles(
            Path.Combine(RepositoryRoot(), "src"), "*.csproj", SearchOption.AllDirectories);

        Assert.NotEmpty(projects);

        var missing = new List<string>();
        foreach (var project in projects)
        {
            var text = File.ReadAllText(project);
            if (!text.Contains("<IsPackable>true</IsPackable>", StringComparison.Ordinal)) continue;
            if (!text.Contains("<IsTrimmable>true</IsTrimmable>", StringComparison.Ordinal))
                missing.Add(Path.GetFileName(project));
        }

        Assert.Empty(missing);
    }

    /// <summary>
    /// The two places <c>Authagonal.Migration</c> serializes must name the source-generated context.
    /// </summary>
    /// <remarks>
    /// A source check because the failure it guards is invisible in an untrimmed test run: the reflective
    /// overloads produce identical JSON here and empty JSON in a trimmed host, so the only decidable
    /// question is which overload the code calls.
    /// </remarks>
    [Fact]
    public void TheMigrationPackageSerializesThroughItsSourceGeneratedContext()
    {
        string[] files =
        [
            "src/Authagonal.Migration/DuendeMigrationHostedRunner.cs",
            "src/Authagonal.Migration/MigrationStatusEndpoint.cs",
        ];

        foreach (var file in files)
        {
            var text = File.ReadAllText(
                Path.Combine(RepositoryRoot(), file.Replace('/', Path.DirectorySeparatorChar)));

            Assert.Contains("MigrationJsonContext.Default", text, StringComparison.Ordinal);
            Assert.DoesNotContain("JsonSerializer.Serialize(report)", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Deserialize<DuendeMigrationReport>", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The admin response's field names are unchanged, which is what <c>JsonSerializerDefaults.Web</c> buys.
    /// </summary>
    /// <remarks>
    /// <c>Results.Json</c> already serialized through <c>JsonSerializerOptions.Web</c>, so a context built on
    /// the plain defaults would have silently renamed every field of a documented admin endpoint from
    /// <c>dryRun</c> to <c>DryRun</c> — trading a trimming bug for a wire break.
    /// </remarks>
    [Fact]
    public void TheStatusResponseKeepsItsCamelCaseFieldNames()
    {
        var json = JsonSerializer.Serialize(
            new MigrationStatusResponse { Version = "1", Status = "Completed", DryRun = true },
            MigrationJsonContext.Default.MigrationStatusResponse);

        Assert.Contains("\"dryRun\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"DryRun\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A marker written by an earlier version still reads, since <c>StatsJson</c>'s casing moved with it.
    /// </summary>
    /// <remarks>
    /// The persisted blob was PascalCase and is now camelCase. Web defaults are case-insensitive on read,
    /// which is what makes that a non-event — and this pins it, because the alternative is a deployment whose
    /// completed migration reports no counts at all after an upgrade.
    /// </remarks>
    [Fact]
    public void AMarkerWrittenByAnEarlierVersionStillDeserializes()
    {
        const string pascal = """{"DryRun":false,"UsersCreated":5,"Warnings":["one"]}""";

        var report = JsonSerializer.Deserialize(pascal, MigrationJsonContext.Default.DuendeMigrationReport);

        Assert.NotNull(report);
        Assert.Equal(5, report!.UsersCreated);
        Assert.Equal(["one"], report.Warnings);
    }

    // ── #46: @authagonal/login shipped its own React ─────────────────────────

    /// <summary>
    /// Every module the library build externalizes has to be a peer dependency, not a dependency.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>vite.config.ts</c> externalizes <c>react</c>, <c>react-dom</c>, <c>react/jsx-runtime</c> and
    /// <c>react-router</c>, so the published <c>dist/index.js</c> imports them as bare specifiers and expects
    /// the host application to supply them — the definition of a peer dependency. The manifest listed all
    /// three as ordinary <c>dependencies</c>, which tells npm to install a copy for the package.
    /// </para>
    /// <para>
    /// The two disagree exactly when the consumer's version does not satisfy this package's range: npm nests
    /// its own <c>react</c> under <c>node_modules/@authagonal/login</c>, and the exported components then run
    /// their hooks against a different React instance than the one rendering them — "Invalid hook call", from
    /// a correctly-written host. <c>react-router</c> is worse than a maybe: the exported pages call
    /// <c>useSearchParams</c>, <c>useNavigate</c> and render <c>Link</c>, so a duplicated router means
    /// "useNavigate() may be used only in the context of a &lt;Router&gt; component" thrown from inside the
    /// consumer's <c>BrowserRouter</c>. The sibling package <c>@authagonal/bff</c> already declares its
    /// <c>react</c> as an optional peer.
    /// </para>
    /// <para>
    /// Read out of the vite config rather than hardcoded, so the rule is the build's own externals list.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryModuleTheLoginBuildExternalizesIsAPeerDependency()
    {
        var root = RepositoryRoot();
        var vite = File.ReadAllText(Path.Combine(root, "login-app", "vite.config.ts"));

        var externalList = Regex.Match(vite, @"external:\s*\[(?<items>[^\]]*)\]");
        Assert.True(externalList.Success, "vite.config.ts no longer declares an externals list");

        // 'react/jsx-runtime' is supplied by whatever provides 'react', so the package root is what a
        // manifest can declare.
        var externals = Regex.Matches(externalList.Groups["items"].Value, @"'(?<name>[^']+)'")
            .Select(m => m.Groups["name"].Value)
            .Select(name => name.StartsWith('@') ? string.Join('/', name.Split('/').Take(2)) : name.Split('/')[0])
            .Distinct()
            .ToList();

        Assert.NotEmpty(externals);

        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "login-app", "package.json")));
        var peers = Names(manifest.RootElement, "peerDependencies");
        var runtime = Names(manifest.RootElement, "dependencies");

        foreach (var external in externals)
        {
            Assert.Contains(external, peers);

            // And not both: a dependency wins, so leaving it there defeats the peer declaration.
            Assert.DoesNotContain(external, runtime);
        }
    }

    /// <summary>
    /// The same modules stay in <c>devDependencies</c>, or the SPA build and the tests have no React.
    /// </summary>
    /// <remarks>
    /// A peer dependency is not installed for the package itself, and this one is also built as a standalone
    /// SPA (<c>build:spa</c>, which bundles React rather than externalizing it) and tested under vitest. CI
    /// installs with <c>npm ci</c>, which includes dev dependencies — that is what keeps both working.
    /// </remarks>
    [Fact]
    public void ThePeerDependenciesAreAlsoAvailableToTheLoginAppsOwnBuild()
    {
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "login-app", "package.json")));

        var dev = Names(manifest.RootElement, "devDependencies");

        foreach (var peer in Names(manifest.RootElement, "peerDependencies"))
            Assert.Contains(peer, dev);
    }

    /// <summary>
    /// No documentation may tell a consumer to install or import <c>react-router-dom</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The package moved to <c>react-router</c> v8, and the changelog records why in the strongest terms
    /// available — "BREAKING for consumers: <c>react-router-dom</c> leaves the dependency tree entirely (v8
    /// removes the package and ships one)". The consumer walkthrough was never updated: seven locales of
    /// <c>custom-server.md</c> plus the package's own npm README each told the reader to
    /// <c>npm install react-router-dom</c> and then import <c>BrowserRouter</c> from it. Both steps fail
    /// outright — there is no such package to install at v8, and nothing to import from it.
    /// </para>
    /// <para>
    /// Checked across every locale rather than the English page, because the drift was that six
    /// translations kept a name the English page had also kept: whatever fixes one has to fix all seven, and
    /// a locale is exactly where a stale name survives unnoticed.
    /// </para>
    /// <para>
    /// Instructions only, not the name: telling the reader that v8 removed <c>react-router-dom</c> is the
    /// useful thing to say, and CHANGELOG.md's breaking-change entry has to name it too. What must not
    /// survive is a line that installs it or imports from it.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoDocumentationInstallsOrImportsTheRemovedReactRouterDomPackage()
    {
        var root = RepositoryRoot();

        var docs = Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "login-app", "README.md"))
            .Append(Path.Combine(root, "README.md"))
            .Where(File.Exists);

        string[] instructions = ["npm install", "npm i ", "yarn add", "pnpm add", "from '", "from \"", "require("];

        var offenders = new List<string>();
        foreach (var file in docs)
        {
            foreach (var line in File.ReadAllLines(file))
            {
                if (!line.Contains("react-router-dom", StringComparison.Ordinal)) continue;
                if (!instructions.Any(i => line.Contains(i, StringComparison.Ordinal))) continue;

                offenders.Add($"{Path.GetRelativePath(root, file)}: {line.Trim()}");
            }
        }

        Assert.Empty(offenders);
    }

    private static List<string> Names(JsonElement manifest, string section) =>
        manifest.TryGetProperty(section, out var node)
            ? node.EnumerateObject().Select(p => p.Name).ToList()
            : [];

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Authagonal.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
