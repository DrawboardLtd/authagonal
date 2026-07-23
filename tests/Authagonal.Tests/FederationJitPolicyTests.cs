using Authagonal.Server.Services;

namespace Authagonal.Tests;

/// <summary>M6: the shared OIDC/SAML JIT gate decision table — including the AllowUninvitedJit escape
/// hatch, which used to exist only on the SAML path.</summary>
public class FederationJitPolicyTests
{
    [Fact]
    public void JitDisabled_rejects()
        => Assert.Equal(FederationJitPolicy.Decision.RejectJitDisabled,
            FederationJitPolicy.Evaluate(jitProvisioningEnabled: false, provisioningAttributeParamCount: 1, capturedAttributeCount: 1, allowUninvitedJit: false));

    [Fact]
    public void InviteOnly_noContext_rejects()
        => Assert.Equal(FederationJitPolicy.Decision.RejectInviteRequired,
            FederationJitPolicy.Evaluate(jitProvisioningEnabled: true, provisioningAttributeParamCount: 2, capturedAttributeCount: 0, allowUninvitedJit: false));

    [Fact]
    public void InviteOnly_withContext_provisions()
        => Assert.Equal(FederationJitPolicy.Decision.Provision,
            FederationJitPolicy.Evaluate(jitProvisioningEnabled: true, provisioningAttributeParamCount: 2, capturedAttributeCount: 1, allowUninvitedJit: false));

    [Fact]
    public void InviteOnly_noContext_butAllowUninvited_provisions()
        => Assert.Equal(FederationJitPolicy.Decision.Provision,
            FederationJitPolicy.Evaluate(jitProvisioningEnabled: true, provisioningAttributeParamCount: 2, capturedAttributeCount: 0, allowUninvitedJit: true));

    [Fact]
    public void NoInviteParams_provisions()
        => Assert.Equal(FederationJitPolicy.Decision.Provision,
            FederationJitPolicy.Evaluate(jitProvisioningEnabled: true, provisioningAttributeParamCount: 0, capturedAttributeCount: 0, allowUninvitedJit: false));
}
