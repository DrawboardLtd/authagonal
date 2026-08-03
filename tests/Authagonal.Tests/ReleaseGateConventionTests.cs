using System.Text.RegularExpressions;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// Nothing may reach a public registry without this suite passing first.
//
// No workflow ran `dotnet test` at all. The frontend audit covered npm advisories and locale drift,
// the release workflow scanned the server image with Trivy — the checks that existed were the ones
// AROUND the .NET code. And `publish-nuget` declared no `needs:` whatsoever, so every package on
// nuget.org was published on the strength of `dotnet pack` exiting zero.
//
// Which means the regression tests that pin this repository's security fixes were decorative at
// release time. A consumed MFA challenge spendable six times, `authorization_details: []` evaluating
// as unrestricted, a federated login inheriting a squatter's binding: each of those has a test, and
// every one of them could have been red on the commit that got tagged.
//
// This covers the class rather than the three jobs, because the next publishing job added is the one
// nobody will think to gate — which is exactly how `publish-nuget` ended up with no gate while its
// two siblings had one.
// -------------------------------------------------------------------------------------------------
public sealed class ReleaseGateConventionTests
{
    /// <summary>The job that runs the .NET suite. Reaching it transitively is what "gated" means.</summary>
    private const string GateJob = "test";

    /// <summary>
    /// What counts as publishing: a step that puts an artifact somewhere the public can install from.
    /// </summary>
    /// <remarks>
    /// Matched against the job's own text rather than inferred from its name, because a job called
    /// something harmless can still hold a push step — and the name is the part a reviewer reads.
    /// </remarks>
    private static readonly (string Needle, string What)[] PublishingSteps =
    [
        ("dotnet nuget push", "pushes packages to NuGet"),
        ("npm publish", "publishes a package to npm"),
        ("push: true", "pushes a container image"),
    ];

    [Fact]
    public void EveryPublishingJobIsGatedOnTheTestSuite()
    {
        var path = Path.Combine(RepositoryRoot(), ".github", "workflows", "release.yml");
        Assert.True(File.Exists(path), $"Expected the release workflow at '{path}'.");

        var jobs = ReadJobs(File.ReadAllText(path));
        Assert.Contains(GateJob, jobs.Keys);

        var publishing = jobs
            .Select(j => (Name: j.Key, Job: j.Value,
                          Reasons: PublishingSteps.Where(s => j.Value.Body.Contains(s.Needle, StringComparison.Ordinal))
                                                  .Select(s => s.What).ToArray()))
            .Where(x => x.Reasons.Length > 0)
            .ToList();

        // If this trips, either the workflow stopped publishing or the needles stopped matching it.
        // Both mean this test is no longer checking anything, which is worse than a failure.
        Assert.True(publishing.Count >= 3,
            $"Expected at least three publishing jobs in release.yml, found {publishing.Count}. "
            + "Either the workflow changed shape or this test's step patterns have gone stale.");

        var ungated = publishing
            .Where(x => !DependsOn(jobs, x.Name, GateJob))
            .Select(x => $"  {x.Name} — {string.Join(", ", x.Reasons)} (needs: "
                         + (jobs[x.Name].Needs.Length == 0 ? "nothing" : string.Join(", ", jobs[x.Name].Needs)) + ")")
            .ToList();

        Assert.True(ungated.Count == 0,
            $"These release jobs publish without depending (even transitively) on the '{GateJob}' job, "
            + "so they can ship a build whose tests were never run:\n" + string.Join("\n", ungated));
    }

    /// <summary>The gate job must actually run the suite, not merely exist under that name.</summary>
    [Fact]
    public void TheGateJobRunsTheDotnetTestSuite()
    {
        var release = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "release.yml"));
        var jobs = ReadJobs(release);

        // The gate is allowed to be a reusable-workflow call; follow it to whatever runs the tests.
        var body = jobs[GateJob].Body;
        var called = Regex.Match(body, @"uses:\s*\./(?<path>\.github/workflows/[A-Za-z0-9._-]+)");
        if (called.Success)
        {
            var calledPath = Path.Combine(RepositoryRoot(), called.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(calledPath), $"release.yml's '{GateJob}' job calls '{calledPath}', which does not exist.");
            body = File.ReadAllText(calledPath);
        }

        Assert.Contains("dotnet test", body, StringComparison.Ordinal);
        Assert.Contains("Authagonal.Tests", body, StringComparison.Ordinal);
    }

    /// <summary>Depth-first reachability over the <c>needs:</c> graph.</summary>
    private static bool DependsOn(Dictionary<string, Job> jobs, string from, string target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([from]);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current) || !jobs.TryGetValue(current, out var job)) continue;

            foreach (var need in job.Needs)
            {
                if (string.Equals(need, target, StringComparison.Ordinal)) return true;
                stack.Push(need);
            }
        }

        return false;
    }

    /// <summary>
    /// Job name → its <c>needs:</c> list and its raw body.
    /// </summary>
    /// <remarks>
    /// A deliberately small parse rather than a YAML dependency: jobs are the two-space keys under
    /// <c>jobs:</c>, and a job's body runs until the next one. Enough for a reachability check, and it
    /// adds no package to a repository whose lock files are load-bearing.
    /// </remarks>
    private static Dictionary<string, Job> ReadJobs(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var jobsAt = Array.FindIndex(lines, l => l.StartsWith("jobs:", StringComparison.Ordinal));
        Assert.True(jobsAt >= 0, "release.yml has no top-level 'jobs:' key.");

        var jobHeader = new Regex(@"^  (?<name>[A-Za-z0-9_-]+):\s*$");
        var starts = new List<(string Name, int Line)>();
        for (var i = jobsAt + 1; i < lines.Length; i++)
        {
            // A non-indented, non-blank, non-comment line ends the jobs mapping.
            if (lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]) && !lines[i].StartsWith('#')) break;
            if (jobHeader.Match(lines[i]) is { Success: true } m)
                starts.Add((m.Groups["name"].Value, i));
        }

        var result = new Dictionary<string, Job>(StringComparer.Ordinal);
        for (var j = 0; j < starts.Count; j++)
        {
            var from = starts[j].Line;
            var to = j + 1 < starts.Count ? starts[j + 1].Line : lines.Length;
            var body = string.Join("\n", lines[from..to]);
            result[starts[j].Name] = new Job(body, ReadNeeds(body));
        }

        return result;
    }

    /// <summary>Both spellings: <c>needs: audit</c> and <c>needs: [audit, test]</c>.</summary>
    private static string[] ReadNeeds(string body)
    {
        var inline = Regex.Match(body, @"^\s{4}needs:\s*\[(?<list>[^\]]*)\]\s*$", RegexOptions.Multiline);
        if (inline.Success)
            return [.. inline.Groups["list"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        var single = Regex.Match(body, @"^\s{4}needs:\s*(?<name>[A-Za-z0-9_-]+)\s*$", RegexOptions.Multiline);
        if (single.Success)
            return [single.Groups["name"].Value];

        // Block form, one `- name` per line.
        var block = Regex.Match(body, @"^\s{4}needs:\s*$(?<items>(\n\s{6}-\s*[A-Za-z0-9_-]+)+)", RegexOptions.Multiline);
        if (block.Success)
            return [.. Regex.Matches(block.Groups["items"].Value, @"-\s*(?<name>[A-Za-z0-9_-]+)")
                .Select(m => m.Groups["name"].Value)];

        return [];
    }

    private sealed record Job(string Body, string[] Needs);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Authagonal.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
