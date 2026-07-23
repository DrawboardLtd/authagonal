using Authagonal.Core.Models;

namespace Authagonal.Tests;

/// <summary>S1: an OIDC connection defaults to first-party (trusted); marking it external neutralises the
/// first-party-only flags (upstream-chooses-user-id, auto-link-by-email) even if they are set.</summary>
public class OidcProviderConfigTrustTests
{
    [Fact]
    public void Default_is_first_party()
        => Assert.False(new OidcProviderConfig().IsExternalConnection);

    [Fact]
    public void FirstParty_honours_the_flags()
    {
        var c = new OidcProviderConfig { UseUpstreamSubjectAsUserId = true, AutoLinkExistingByEmail = true };
        Assert.True(c.EffectiveUseUpstreamSubjectAsUserId);
        Assert.True(c.EffectiveAutoLinkExistingByEmail);
    }

    [Fact]
    public void External_neutralises_the_flags()
    {
        var c = new OidcProviderConfig { UseUpstreamSubjectAsUserId = true, AutoLinkExistingByEmail = true, IsExternalConnection = true };
        Assert.False(c.EffectiveUseUpstreamSubjectAsUserId);
        Assert.False(c.EffectiveAutoLinkExistingByEmail);
    }
}
