using Authagonal.Server.Services.Saml;

namespace Authagonal.Tests;

public class SamlClaimMapperTests
{
    [Fact]
    public void MapClaims_ExplicitEmailClaim_UsesIt()
    {
        var attrs = new Dictionary<string, string>
        {
            ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"] = "user@example.com"
        };

        var result = SamlClaimMapper.MapClaims("nameid123", null, attrs);
        Assert.Equal("user@example.com", result.Email);
    }

    [Fact]
    public void MapClaims_NoEmailClaim_NameIdFormatEmail_UsesNameId()
    {
        var attrs = new Dictionary<string, string>();

        var result = SamlClaimMapper.MapClaims("user@example.com",
            "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress", attrs);
        Assert.Equal("user@example.com", result.Email);
    }

    [Fact]
    public void MapClaims_NoEmailClaim_NameContainsAt_UsesName()
    {
        var attrs = new Dictionary<string, string>
        {
            ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"] = "user@example.com"
        };

        var result = SamlClaimMapper.MapClaims("some-id", null, attrs);
        Assert.Equal("user@example.com", result.Email);
    }

    [Fact]
    public void MapClaims_NoEmail_ReturnsNull()
    {
        var attrs = new Dictionary<string, string>
        {
            ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"] = "John Doe"
        };

        var result = SamlClaimMapper.MapClaims("some-id", null, attrs);
        Assert.Null(result.Email);
    }

    [Fact]
    public void MapClaims_MapsAllFields()
    {
        var attrs = new Dictionary<string, string>
        {
            ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"] = "user@example.com",
            ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname"] = "John",
            ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname"] = "Doe",
            ["http://schemas.microsoft.com/identity/claims/displayname"] = "John Doe",
            ["http://schemas.microsoft.com/identity/claims/objectidentifier"] = "obj-123",
        };

        var result = SamlClaimMapper.MapClaims("nameid", null, attrs);

        Assert.Equal("user@example.com", result.Email);
        Assert.Equal("John", result.FirstName);
        Assert.Equal("Doe", result.LastName);
        Assert.Equal("John Doe", result.DisplayName);
        Assert.Equal("obj-123", result.ObjectId);
        Assert.Equal("nameid", result.NameId);
    }

    [Fact]
    public void MapClaims_ExplicitEmailTakesPriorityOverNameId()
    {
        var attrs = new Dictionary<string, string>
        {
            ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"] = "explicit@example.com"
        };

        var result = SamlClaimMapper.MapClaims("nameid@example.com",
            "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress", attrs);
        Assert.Equal("explicit@example.com", result.Email);
    }

    [Fact]
    public void MapClaims_EmptyAttributes_ReturnsNameIdOnly()
    {
        var result = SamlClaimMapper.MapClaims("my-name-id", null, new Dictionary<string, string>());

        Assert.Null(result.Email);
        Assert.Null(result.FirstName);
        Assert.Null(result.LastName);
        Assert.Equal("my-name-id", result.NameId);
    }
}
