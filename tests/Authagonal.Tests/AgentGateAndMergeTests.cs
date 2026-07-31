using Authagonal.Core.Authority;

namespace Authagonal.Tests;

/// <summary>
/// F65 / F80 — what an approval is bound to, and what the pre-mint gate is shown.
/// </summary>
public sealed class AgentGateAndMergeTests
{
    // -----------------------------------------------------------------------
    // F65 — approvals bind to the exchange's context
    // -----------------------------------------------------------------------
    //
    // The hash is computed inside ProtocolTokenService, so these exercise the recorded shape rather
    // than the private method: an approval carries the context it was parked with, and that context
    // round-trips through the store, which is what the redeem-time comparison and the approval screen
    // both read.

    [Fact]
    public void ApprovalContext_RoundTripsThroughStorage()
    {
        // The approval UI shows the client, the pending type:action pairs and the authority slice. A
        // context-bound exchange scopes the resulting token to a tenant/project/workspace entirely
        // through these parameters, and none of it reached the person being asked to approve — so
        // "approve read:payments" gave no way to ask "whose payments?".
        var data = new ApprovalData
        {
            Id = "a1",
            ClientId = "agent-1",
            SubjectId = "user-1",
            Slice = AuthoritySet.Empty,
            RequestHash = "HASH",
            Context = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workspace_id"] = "ws-42",
                ["project_id"] = "proj-7",
            },
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var parsed = Approval.Parse(Approval.Serialize(data));

        Assert.NotNull(parsed);
        Assert.Equal("ws-42", parsed!.Context["workspace_id"]);
        Assert.Equal("proj-7", parsed.Context["project_id"]);
    }

    [Fact]
    public void ApprovalWithoutContext_StillParses()
    {
        // Approvals written before the field existed must keep resolving.
        var data = new ApprovalData
        {
            Id = "a1",
            ClientId = "agent-1",
            SubjectId = "user-1",
            Slice = AuthoritySet.Empty,
            RequestHash = "HASH",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var parsed = Approval.Parse(Approval.Serialize(data));

        Assert.NotNull(parsed);
        Assert.Empty(parsed!.Context);
    }

    // -----------------------------------------------------------------------
    // F80 — the pre-mint gate sees what is being granted
    // -----------------------------------------------------------------------

    [Fact]
    public void TokenIssuanceContext_CarriesTheEffectiveAuthoritySeparately()
    {
        // RequestedAuthorityJson used to mean different things by grant type: the effective set on
        // client_credentials, but the raw request parameter on the delegated exchange — where it is
        // null whenever the agent omits authorization_details, which means "everything grantable". So
        // on the one path that mints delegated USER authority, a hook could not see what was being
        // granted, and could be blinded entirely by leaving the parameter out.
        var ctx = new Authagonal.Core.Services.TokenIssuanceContext(
            "agent-1", "user-1", "urn:ietf:params:oauth:grant-type:token-exchange", ["openid"],
            RequestedAuthorityJson: null)
        {
            EffectiveAuthorityJson = """[{"type":"payments","actions":["read"]}]""",
        };

        Assert.Null(ctx.RequestedAuthorityJson);
        Assert.Contains("payments", ctx.EffectiveAuthorityJson);
    }
}
