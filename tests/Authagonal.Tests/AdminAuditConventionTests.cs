using System.Text.RegularExpressions;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// #205 — every admin write leaves an attributable record.
//
// An admin credential is a bearer token. "This account's MFA was reset" is not the fact an incident
// responder needs; "and this subject reset it, at this time" is. Four groups produced no audit row at
// all — MFA reset and credential removal (the first thing an attacker with an admin token does), the
// endpoint that mints a token AS another user (the strongest impersonation primitive in the product),
// and nine writes on UserEndpoints including set-password, delete and confirm-email.
//
// Some of those logged a Warning, which is not the same thing: the shipped log configuration decides
// whether it survives, nothing indexes it by subject, and there is nowhere to query.
//
// This covers the class rather than the four instances, because the next admin write added is the one
// nobody will think about.
// -------------------------------------------------------------------------------------------------
public sealed class AdminAuditConventionTests
{
    /// <summary>
    /// Handlers mapped to a write verb that legitimately record nothing, each with the reason.
    /// </summary>
    /// <remarks>
    /// Only reads belong here. A POST that reads is a POST because the query would not fit in a URL, and it
    /// changes nothing to attribute — but that has to be stated per handler rather than inferred from the
    /// verb, because "it is only a read" is exactly what a future write will also claim.
    /// </remarks>
    private static readonly Dictionary<string, string> ReadsOnly = new(StringComparer.Ordinal)
    {
        ["UsersExist"] = "A batch existence check. POST because the email list would not fit in a query "
            + "string; it writes nothing.",
        ["GetMfaStatus"] = "A batch MFA-status read, POST for the same reason. Writes nothing.",
    };

    private static readonly Regex MappedWrite = new(
        @"Map(?:Post|Put|Patch|Delete)\s*\(\s*""[^""]*""\s*,\s*(?<handler>[A-Za-z_][A-Za-z0-9_]*)\s*\)",
        RegexOptions.Compiled);

    [Fact]
    public void EveryAdminWriteEndpointRecordsAnAuditRow()
    {
        var adminDir = Path.Combine(RepositoryRoot(), "src", "Authagonal.Server", "Endpoints", "Admin");
        Assert.True(Directory.Exists(adminDir), $"Expected the admin endpoints at '{adminDir}'.");

        var offenders = new List<string>();
        var checked_ = 0;

        foreach (var file in Directory.EnumerateFiles(adminDir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (Match m in MappedWrite.Matches(text))
            {
                var handler = m.Groups["handler"].Value;
                if (ReadsOnly.ContainsKey(handler)) continue;

                checked_++;

                // The handler's own body, from its declaration to the next method at the same indent.
                var declaration = new Regex($@"^\s*private static (?:async )?Task<IResult> {Regex.Escape(handler)}\s*\(",
                    RegexOptions.Multiline).Match(text);
                Assert.True(declaration.Success,
                    $"{name}: '{handler}' is mapped to a write verb but no handler method of that name was found.");

                var body = HandlerBody(text, declaration.Index);

                if (!body.Contains("audit.LogAsync", StringComparison.Ordinal))
                    offenders.Add($"{name}: {handler}");
            }
        }

        Assert.True(checked_ > 0, "The scan matched no admin write endpoints, so it proved nothing.");

        Assert.True(offenders.Count == 0,
            "These admin write endpoints record no audit row. An admin credential is a bearer token, so a "
            + "state change on someone else's account without an attributable row is one an incident cannot "
            + "reconstruct — a Warning in the log is not a trail. Add an audit.LogAsync call, or list the "
            + "handler in ReadsOnly with the reason it writes nothing. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The read-only exemptions have to stay reads.
    /// </summary>
    /// <remarks>
    /// An exemption is a standing licence, so it has to be revalidated: a handler that grows a write while
    /// listed here would be silently pre-authorised to leave no trace.
    /// </remarks>
    [Fact]
    public void TheReadOnlyExemptionsDoNotWrite()
    {
        var adminDir = Path.Combine(RepositoryRoot(), "src", "Authagonal.Server", "Endpoints", "Admin");
        var writers = new List<string>();

        foreach (var file in Directory.EnumerateFiles(adminDir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (var (handler, _) in ReadsOnly)
            {
                var declaration = new Regex($@"^\s*private static (?:async )?Task<IResult> {Regex.Escape(handler)}\s*\(",
                    RegexOptions.Multiline).Match(text);
                if (!declaration.Success) continue;

                var body = HandlerBody(text, declaration.Index);

                if (Regex.IsMatch(body, @"\.(?:UpsertAsync|UpdateAsync|CreateAsync|DeleteAsync|StoreAsync)\s*\("))
                    writers.Add($"{Path.GetFileName(file)}: {handler}");
            }
        }

        Assert.True(writers.Count == 0,
            "These handlers are exempt from the audit rule as reads, and they now write to a store. Remove "
            + "the exemption and audit them:" + Environment.NewLine + string.Join(Environment.NewLine, writers));
    }

    /// <summary>
    /// One handler's text, from its declaration to the next method declaration.
    /// </summary>
    /// <remarks>
    /// The search starts PAST the declaration itself. Searching from index 0 finds the handler's own
    /// declaration — the match includes the newline before it — so the boundary was always zero and the
    /// "body" was the whole remainder of the file. That made this lint read the NEXT handler's writes as this
    /// one's, which is how it reported a plain read as a store write. A lint whose boundaries are wrong is
    /// worse than no lint: it teaches people to add exemptions.
    /// </remarks>
    private static string HandlerBody(string text, int declarationIndex)
    {
        var rest = text[declarationIndex..];
        var next = rest.IndexOf("\n    private static", 1, StringComparison.Ordinal);
        return next > 0 ? rest[..next] : rest;
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
