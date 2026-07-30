using System.Text.Json.Nodes;
using Authagonal.Server.Services.Scim;

namespace Authagonal.Tests;

/// <summary>
/// The RFC 7644 §3.4.2.2 filter grammar. SCIM's ServiceProviderConfig cannot advertise a partial filter
/// capability, so <c>filter.supported = true</c> is a claim to all of this; these tests are what makes
/// the claim true.
/// </summary>
public sealed class ScimFilterGrammarTests
{
    // A user resource in the shape the API actually returns.
    private static JsonNode User(
        string userName = "alice@example.com",
        string? externalId = "ext-1",
        string? givenName = "Alice",
        string? familyName = "Smith",
        string? displayName = "Alice Smith",
        bool active = true,
        string? title = null,
        string workEmail = "alice@acme.com",
        string homeEmail = "alice@personal.example",
        string lastModified = "2026-06-15T10:00:00Z") =>
        new JsonObject
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:User"),
            ["id"] = "abc123",
            ["externalId"] = externalId,
            ["userName"] = userName,
            ["name"] = new JsonObject { ["givenName"] = givenName, ["familyName"] = familyName },
            ["displayName"] = displayName,
            ["title"] = title,
            ["active"] = active,
            ["emails"] = new JsonArray(
                new JsonObject { ["value"] = workEmail, ["type"] = "work", ["primary"] = true },
                new JsonObject { ["value"] = homeEmail, ["type"] = "home", ["primary"] = false }),
            ["meta"] = new JsonObject { ["lastModified"] = lastModified, ["resourceType"] = "User" },
        };

    private static bool Eval(string filter, JsonNode? resource)
    {
        Assert.True(ScimFilterParser.TryParse(filter, out var expression, out var error), error);
        Assert.NotNull(expression);
        return ScimFilterEvaluator.Matches(expression!, resource);
    }

    // ── Comparison operators ─────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("userName eq \"alice@example.com\"", true)]
    [InlineData("userName eq \"ALICE@EXAMPLE.COM\"", true)]   // caseExact=false
    [InlineData("userName eq \"bob@example.com\"", false)]
    [InlineData("userName ne \"bob@example.com\"", true)]
    [InlineData("userName ne \"alice@example.com\"", false)]
    [InlineData("userName co \"example\"", true)]
    [InlineData("userName co \"zzz\"", false)]
    [InlineData("userName sw \"alice\"", true)]
    [InlineData("userName sw \"bob\"", false)]
    [InlineData("userName ew \".com\"", true)]
    [InlineData("userName ew \".org\"", false)]
    public void StringOperators(string filter, bool expected) => Assert.Equal(expected, Eval(filter, User()));

    [Theory]
    [InlineData("active eq true", true)]
    [InlineData("active eq false", false)]
    [InlineData("active ne false", true)]
    public void BooleanOperators(string filter, bool expected) => Assert.Equal(expected, Eval(filter, User()));

    [Theory]
    [InlineData("meta.lastModified gt \"2026-01-01T00:00:00Z\"", true)]
    [InlineData("meta.lastModified gt \"2026-12-01T00:00:00Z\"", false)]
    [InlineData("meta.lastModified ge \"2026-06-15T10:00:00Z\"", true)]
    [InlineData("meta.lastModified lt \"2026-12-01T00:00:00Z\"", true)]
    [InlineData("meta.lastModified le \"2026-06-15T10:00:00Z\"", true)]
    public void OrderingOperatorsOnTimestamps(string filter, bool expected) =>
        Assert.Equal(expected, Eval(filter, User()));

    // ── Presence ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Present_TrueWhenSet_FalseWhenNullOrEmpty()
    {
        Assert.True(Eval("userName pr", User()));
        Assert.False(Eval("title pr", User(title: null)));
        Assert.True(Eval("title pr", User(title: "Manager")));
        Assert.False(Eval("displayName pr", User(displayName: "")));
    }

    // ── Absent attributes ────────────────────────────────────────────────────────────────────────
    [Fact]
    public void AbsentAttribute_ComparisonsAreFalse_ExceptNe()
    {
        var noTitle = User(title: null);
        Assert.False(Eval("title eq \"Manager\"", noTitle));
        Assert.False(Eval("title co \"Man\"", noTitle));
        Assert.False(Eval("title gt \"A\"", noTitle));
        // A user with no title genuinely is not titled "Manager"; false here would put `ne` and
        // `not (... eq ...)` in disagreement.
        Assert.True(Eval("title ne \"Manager\"", noTitle));
        // An attribute the schema doesn't have at all behaves the same way.
        Assert.False(Eval("nosuchattribute eq \"x\"", noTitle));
    }

    // ── Sub-attributes ───────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("name.givenName eq \"Alice\"", true)]
    [InlineData("name.familyName sw \"Smi\"", true)]
    [InlineData("name.givenName eq \"Bob\"", false)]
    public void SubAttributes(string filter, bool expected) => Assert.Equal(expected, Eval(filter, User()));

    // ── Multi-valued attributes ──────────────────────────────────────────────────────────────────
    [Fact]
    public void MultiValued_MatchesWhenAnyElementMatches()
    {
        Assert.True(Eval("emails.value co \"@acme\"", User()));
        Assert.True(Eval("emails.value co \"@personal\"", User()));
        Assert.False(Eval("emails.value co \"@nowhere\"", User()));
    }

    // ── Value paths ──────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void ValuePath_SelectsTheMatchingElement()
    {
        // The work address contains @acme; the home one does not. Without the value path this would
        // match on either, which is the whole point of the construct.
        Assert.True(Eval("emails[type eq \"work\"].value co \"@acme\"", User()));
        Assert.False(Eval("emails[type eq \"home\"].value co \"@acme\"", User()));
        Assert.True(Eval("emails[type eq \"home\"].value co \"@personal\"", User()));
    }

    [Fact]
    public void ValuePath_BareIsAnExistenceTest()
    {
        Assert.True(Eval("emails[type eq \"work\"]", User()));
        Assert.False(Eval("emails[type eq \"other\"]", User()));
    }

    [Fact]
    public void ValuePath_WithCompoundInnerFilter()
    {
        Assert.True(Eval("emails[type eq \"work\" and primary eq true]", User()));
        Assert.False(Eval("emails[type eq \"home\" and primary eq true]", User()));
    }

    // ── Logical operators and grouping ───────────────────────────────────────────────────────────
    [Theory]
    [InlineData("userName eq \"alice@example.com\" and active eq true", true)]
    [InlineData("userName eq \"alice@example.com\" and active eq false", false)]
    [InlineData("userName eq \"nobody\" or active eq true", true)]
    [InlineData("userName eq \"nobody\" or active eq false", false)]
    [InlineData("not (userName eq \"alice@example.com\")", false)]
    [InlineData("not (userName eq \"bob\")", true)]
    public void LogicalOperators(string filter, bool expected) => Assert.Equal(expected, Eval(filter, User()));

    [Fact]
    public void Precedence_AndBindsTighterThanOr()
    {
        // Parsed as (false and false) or true -> true. Left-to-right without precedence would give
        // false and (false or true) -> false.
        Assert.True(Eval("userName eq \"nobody\" and active eq false or name.givenName eq \"Alice\"", User()));
    }

    [Fact]
    public void Grouping_OverridesPrecedence()
    {
        Assert.False(Eval("(userName eq \"nobody\" or name.givenName eq \"Alice\") and active eq false", User()));
        Assert.True(Eval("(userName eq \"nobody\" or name.givenName eq \"Alice\") and active eq true", User()));
    }

    [Fact]
    public void UrnPrefixedAttributePath()
    {
        Assert.True(Eval("urn:ietf:params:scim:schemas:core:2.0:User:userName eq \"alice@example.com\"", User()));
    }

    // ── Malformed input ──────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("userName")]                              // no operator
    [InlineData("userName xx \"a\"")]                     // not an operator
    [InlineData("userName eq")]                           // no value
    [InlineData("userName eq \"unterminated")]
    [InlineData("(userName eq \"a\"")]                    // unbalanced
    [InlineData("not userName eq \"a\"")]                 // not requires parentheses
    [InlineData("userName eq \"a\" and")]
    [InlineData("emails[type eq \"work\"")]               // unbalanced bracket
    public void MalformedFilters_AreRejectedWithAMessage(string filter)
    {
        Assert.False(ScimFilterParser.TryParse(filter, out var expression, out var error));
        Assert.Null(expression);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void AbsentFilter_IsNotAnError()
    {
        foreach (var absent in new string?[] { null, "", "   " })
        {
            Assert.True(ScimFilterParser.TryParse(absent, out var expression, out var error));
            Assert.Null(expression);
            Assert.Null(error);
        }
    }

    // ── The regression that started this ─────────────────────────────────────────────────────────
    [Fact]
    public void CompoundFilter_IsHonoured_NotSilentlyMisparsed()
    {
        // The old parser saw the leading and trailing quote of the whole tail and read this as
        // userName eq `alice@example.com" and active eq "false`, which matched nobody and returned an
        // empty list — indistinguishable from "no such user", which is how duplicates got created.
        Assert.True(Eval("userName eq \"alice@example.com\" and active eq true", User()));
        Assert.False(Eval("userName eq \"alice@example.com\" and active eq false", User()));
    }

    // -----------------------------------------------------------------------
    // Resource bounds (#29)
    // -----------------------------------------------------------------------

    /// <summary>
    /// ParseExpression → ParseAnd → ParseNot → ParsePrimary is mutually recursive and descended on every
    /// '(' with no bound, so nested parentheses overflowed the stack. A StackOverflowException cannot be
    /// caught in .NET — it terminates the PROCESS, so one request killed the worker and every tenant it
    /// served. It must come back as a catchable parse error instead.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(500)]
    [InlineData(5000)]
    public void Deeply_nested_parentheses_are_refused_not_fatal(int depth)
    {
        var filter = new string('(', depth) + "userName eq \"a\"" + new string(')', depth);

        Assert.False(ScimFilterParser.TryParse(filter, out var expression, out var error));
        Assert.Null(expression);
        Assert.NotNull(error);
        // Depth 60 is only ~136 characters, so this is the DEPTH budget rejecting it, not the length cap.
        if (depth == 60)
            Assert.Contains("depth", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The value-path bracket is the second recursive descent and nests just as deeply.</summary>
    [Fact]
    public void Deeply_nested_value_paths_are_refused_not_fatal()
    {
        // emails[emails[emails[... type eq "work" ...]]]
        const int depth = 200;
        var filter = string.Concat(Enumerable.Repeat("emails[", depth))
            + "type eq \"work\""
            + new string(']', depth);

        Assert.False(ScimFilterParser.TryParse(filter, out _, out var error));
        Assert.NotNull(error);
    }

    /// <summary>'not(' descends too.</summary>
    [Fact]
    public void Deeply_nested_not_groups_are_refused_not_fatal()
    {
        const int depth = 300;
        var filter = string.Concat(Enumerable.Repeat("not(", depth))
            + "userName eq \"a\""
            + new string(')', depth);

        Assert.False(ScimFilterParser.TryParse(filter, out _, out var error));
        Assert.NotNull(error);
    }

    /// <summary>An over-long filter is refused before tokenizing.</summary>
    [Fact]
    public void Over_long_filters_are_refused()
    {
        var filter = "userName eq \"" + new string('a', 4000) + "\"";
        Assert.False(ScimFilterParser.TryParse(filter, out _, out var error));
        Assert.Contains("length", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Realistic nesting still parses — the bound must not break legitimate filters. Guards against
    /// setting the depth budget so low it becomes a functional regression.
    /// </summary>
    [Theory]
    [InlineData("(userName eq \"a\" or userName eq \"b\") and active eq true")]
    [InlineData("not(active eq false) and (emails[type eq \"work\"] or emails[type eq \"home\"])")]
    [InlineData("((((active eq true))))")]
    [InlineData("emails[type eq \"work\" and value co \"@acme.com\"]")]
    public void Realistic_nesting_still_parses(string filter)
    {
        Assert.True(ScimFilterParser.TryParse(filter, out var expression, out var error), error);
        Assert.NotNull(expression);
    }
}
