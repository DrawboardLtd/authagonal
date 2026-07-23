namespace Authagonal.Server.Services;

/// <summary>
/// The just-in-time provisioning gate shared by the OIDC and SAML federation callbacks: given a
/// connection's config and whether any provisioning context actually arrived on the request, decides
/// whether an unknown federated user may be provisioned. Centralised so the two protocols can't drift —
/// the <c>AllowUninvitedJit</c> escape hatch once existed only on the SAML path, which this prevents
/// recurring. Each caller maps the returned decision to its own redirect + log (they differ by protocol).
/// </summary>
public static class FederationJitPolicy
{
    public enum Decision
    {
        /// <summary>Provision the unknown user.</summary>
        Provision,
        /// <summary>JIT provisioning is disabled for the connection — reject.</summary>
        RejectJitDisabled,
        /// <summary>The connection is invite-only (declares ProvisioningAttributeParams) and no provisioning
        /// context arrived, and it hasn't opted into uninvited auto-provisioning — reject.</summary>
        RejectInviteRequired,
    }

    public static Decision Evaluate(
        bool jitProvisioningEnabled,
        int provisioningAttributeParamCount,
        int capturedAttributeCount,
        bool allowUninvitedJit)
    {
        if (!jitProvisioningEnabled)
            return Decision.RejectJitDisabled;
        if (provisioningAttributeParamCount > 0 && capturedAttributeCount == 0 && !allowUninvitedJit)
            return Decision.RejectInviteRequired;
        return Decision.Provision;
    }
}
