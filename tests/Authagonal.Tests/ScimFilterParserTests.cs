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
