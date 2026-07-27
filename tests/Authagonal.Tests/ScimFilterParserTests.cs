using Authagonal.Server.Services;

namespace Authagonal.Tests;

public sealed class ScimFilterParserTests
{
    [Fact]
    public void Parse_EqFilter_ReturnsFilter()
    {
        var result = ScimFilterParser.Parse("userName eq \"john@example.com\"");
        Assert.NotNull(result);
        Assert.Equal("userName", result.Attribute);
        Assert.Equal("eq", result.Operator);
        Assert.Equal("john@example.com", result.Value);
    }

    [Fact]
    public void Parse_CoFilter_ReturnsFilter()
    {
        var result = ScimFilterParser.Parse("displayName co \"John\"");
        Assert.NotNull(result);
        Assert.Equal("displayName", result.Attribute);
        Assert.Equal("co", result.Operator);
        Assert.Equal("John", result.Value);
    }

    [Fact]
    public void Parse_Null_ReturnsNull()
    {
        Assert.Null(ScimFilterParser.Parse(null));
        Assert.Null(ScimFilterParser.Parse(""));
        Assert.Null(ScimFilterParser.Parse("  "));
    }

    [Fact]
    public void Parse_UnsupportedOperator_ReturnsNull()
    {
        Assert.Null(ScimFilterParser.Parse("userName gt \"test\""));
    }

    [Fact]
    public void Parse_InvalidFormat_ReturnsNull()
    {
        Assert.Null(ScimFilterParser.Parse("userName"));
        Assert.Null(ScimFilterParser.Parse("justOneWord"));
    }

    // "No filter" and "a filter I cannot read" are opposite answers: the first means list everything,
    // the second must refuse. The lenient Parse collapses both to null, which is why the endpoints use
    // TryParse — an unsupported filter that silently lists the whole population is a wrong answer, not
    // a permissive one.
    [Fact]
    public void TryParse_Absent_IsAbsentNotUnsupported()
    {
        foreach (var absent in new string?[] { null, "", "   " })
        {
            var result = ScimFilterParser.TryParse(absent);
            Assert.Equal(ScimFilterParser.ScimFilterStatus.Absent, result.Status);
            Assert.Null(result.Filter);
        }
    }

    [Theory]
    [InlineData("userName gt \"test\"")]                       // operator we don't implement
    [InlineData("userName sw \"jo\"")]
    [InlineData("userName pr")]                                // presence, no value
    [InlineData("userName eq \"a\" and externalId eq \"b\"")]  // compound
    [InlineData("userName eq \"a\" or userName eq \"b\"")]
    [InlineData("justOneWord")]                                // malformed
    public void TryParse_PresentButUnreadable_IsUnsupported(string filter)
    {
        var result = ScimFilterParser.TryParse(filter);
        Assert.Equal(ScimFilterParser.ScimFilterStatus.Unsupported, result.Status);
        Assert.Null(result.Filter);
    }

    [Fact]
    public void TryParse_SupportedTerm_IsParsed()
    {
        var result = ScimFilterParser.TryParse("userName eq \"john@example.com\"");
        Assert.Equal(ScimFilterParser.ScimFilterStatus.Parsed, result.Status);
        Assert.Equal("userName", result.Filter!.Attribute);
        Assert.Equal("john@example.com", result.Filter.Value);
    }

    [Fact]
    public void Matches_EqOnUserName_CaseInsensitive()
    {
        var filter = new ScimFilterParser.ScimFilter("userName", "eq", "John@Example.com");
        Assert.True(ScimFilterParser.Matches(filter, "john@example.com", null, null));
        Assert.False(ScimFilterParser.Matches(filter, "jane@example.com", null, null));
    }

    [Fact]
    public void Matches_CoOnDisplayName()
    {
        var filter = new ScimFilterParser.ScimFilter("displayName", "co", "John");
        Assert.True(ScimFilterParser.Matches(filter, null, null, "John Doe"));
        Assert.False(ScimFilterParser.Matches(filter, null, null, "Jane Doe"));
    }

    [Fact]
    public void Matches_EqOnExternalId()
    {
        var filter = new ScimFilterParser.ScimFilter("externalId", "eq", "ext-123");
        Assert.True(ScimFilterParser.Matches(filter, null, "ext-123", null));
        Assert.False(ScimFilterParser.Matches(filter, null, "ext-456", null));
    }

    [Fact]
    public void MatchesGroup_EqOnDisplayName()
    {
        var filter = new ScimFilterParser.ScimFilter("displayName", "eq", "Engineering");
        Assert.True(ScimFilterParser.MatchesGroup(filter, "Engineering", null));
        Assert.False(ScimFilterParser.MatchesGroup(filter, "Marketing", null));
    }

    [Fact]
    public void MatchesGroup_EqOnExternalId()
    {
        var filter = new ScimFilterParser.ScimFilter("externalId", "eq", "grp-123");
        Assert.True(ScimFilterParser.MatchesGroup(filter, null, "grp-123"));
        Assert.False(ScimFilterParser.MatchesGroup(filter, null, "grp-456"));
    }
}
