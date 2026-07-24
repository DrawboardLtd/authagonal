using Authagonal.Core.Models;
using Authagonal.Migration;

namespace Authagonal.Tests;

/// <summary>Pure mapping helpers for the Duende migration — claim folding, secret tagging, id validation.</summary>
public class DuendeMappingsTests
{
    private static AuthUser NewUser() => new()
    {
        Id = "u1",
        Email = "a@b.com",
        NormalizedEmail = "A@B.COM",
    };

    [Fact]
    public void ApplyClaims_FoldsFirstClassFields()
    {
        var user = NewUser();
        DuendeMappings.ApplyClaims(user, new Dictionary<string, string>
        {
            ["given_name"] = "Ada",
            ["family_name"] = "Lovelace",
            ["company"] = "Analytical Engines",
            ["org_id"] = "org-42",
        }, overwrite: true);

        Assert.Equal("Ada", user.FirstName);
        Assert.Equal("Lovelace", user.LastName);
        Assert.Equal("Analytical Engines", user.CompanyName);
        Assert.Equal("org-42", user.OrganizationId);
        Assert.Empty(user.CustomAttributes);
    }

    [Fact]
    public void ApplyClaims_FoldsXmlsoapNameVariants()
    {
        var user = NewUser();
        DuendeMappings.ApplyClaims(user, new Dictionary<string, string>
        {
            ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname"] = "Grace",
            ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname"] = "Hopper",
        }, overwrite: true);

        Assert.Equal("Grace", user.FirstName);
        Assert.Equal("Hopper", user.LastName);
    }

    [Fact]
    public void ApplyClaims_ExcludesEmailClaims()
    {
        var user = NewUser();
        DuendeMappings.ApplyClaims(user, new Dictionary<string, string>
        {
            ["email"] = "a@b.com",
            ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"] = "a@b.com",
        }, overwrite: true);

        Assert.Empty(user.CustomAttributes);
    }

    [Fact]
    public void ApplyClaims_PutsUnknownClaimsInCustomAttributes()
    {
        var user = NewUser();
        DuendeMappings.ApplyClaims(user, new Dictionary<string, string>
        {
            ["department"] = "R&D",
        }, overwrite: true);

        Assert.Equal("R&D", user.CustomAttributes["department"]);
    }

    [Fact]
    public void ApplyClaims_SplitsLoneNameWhenNoExplicitNames()
    {
        var user = NewUser();
        DuendeMappings.ApplyClaims(user, new Dictionary<string, string> { ["name"] = "John Von Neumann" }, overwrite: true);
        Assert.Equal("John", user.FirstName);
        Assert.Equal("Von Neumann", user.LastName);
    }

    [Fact]
    public void ApplyClaims_NoOverwrite_KeepsExistingValues()
    {
        var user = NewUser();
        user.FirstName = "Existing";
        DuendeMappings.ApplyClaims(user, new Dictionary<string, string> { ["given_name"] = "New" }, overwrite: false);
        Assert.Equal("Existing", user.FirstName);
    }

    [Theory]
    [InlineData(44, "SHA256$")]
    [InlineData(88, "SHA512$")]
    public void TagClientSecret_TagsByDigestLength(int length, string expectedPrefix)
    {
        var body = new string('A', length);
        var tagged = DuendeMappings.TagClientSecret(body);
        Assert.Equal(expectedPrefix + body, tagged);
    }

    [Fact]
    public void TagClientSecret_TrimsBeforeMeasuring()
    {
        var body = new string('A', 44);
        Assert.Equal("SHA256$" + body, DuendeMappings.TagClientSecret("  " + body + "\n"));
    }

    [Theory]
    [InlineData(43)]
    [InlineData(45)]
    [InlineData(64)]
    [InlineData(0)]
    public void TagClientSecret_ReturnsNullForUnrecognizedLength(int length)
    {
        Assert.Null(DuendeMappings.TagClientSecret(new string('A', length)));
    }

    [Theory]
    [InlineData("d1f8e0a0-1111-2222-3333-444455556666")]        // GUID
    [InlineData("samlp|tmna-saml|user@toyota.com")]              // pipe-prefixed external id
    [InlineData("waad|abc123")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")] // 64-char hex
    public void IsValidUserId_AcceptsRealDuendeIdShapes(string id)
    {
        Assert.True(DuendeMappings.IsValidUserId(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has/slash")]
    [InlineData("has\\backslash")]
    [InlineData("has#hash")]
    [InlineData("has?question")]
    public void IsValidUserId_RejectsIllegalOrEmpty(string? id)
    {
        Assert.False(DuendeMappings.IsValidUserId(id));
    }

    [Fact]
    public void IsValidUserId_RejectsOver64Chars()
    {
        Assert.False(DuendeMappings.IsValidUserId(new string('a', 65)));
    }
}
