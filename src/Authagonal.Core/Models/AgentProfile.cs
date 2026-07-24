using Authagonal.Core.Authority;

namespace Authagonal.Core.Models;

/// <summary>Whose authority an agent uses at runtime.</summary>
public enum AgentMode
{
    /// <summary>Acts on behalf of a signed-in user via token exchange: composite identity
    /// (<c>sub</c> = user, <c>act</c> = agent), gated by that user's consent.</summary>
    Delegated = 0,

    /// <summary>Acts as its own principal via client_credentials: ceiling only, no user, no
    /// approvals (<c>ask</c> policies degrade to deny).</summary>
    Service = 1,

    /// <summary>Both modes enabled.</summary>
    Both = 2,
}

/// <summary>Canonical wire names for <see cref="AgentMode"/> ("delegated"/"service"/"both"),
/// shared by the storage providers and the admin API.</summary>
public static class AgentModes
{
    public static string Name(AgentMode mode) => mode switch
    {
        AgentMode.Service => "service",
        AgentMode.Both => "both",
        _ => "delegated",
    };

    public static AgentMode Parse(string? mode) => mode switch
    {
        "service" => AgentMode.Service,
        "both" => AgentMode.Both,
        _ => AgentMode.Delegated,
    };
}

/// <summary>
/// What makes a confidential client an agent. The <see cref="OAuthClient"/> record itself is
/// untouched — registering a profile against its client id is what enables the delegation
/// machinery, and deleting the profile reverts the client to plain OAuth behavior.
/// </summary>
public sealed class AgentProfile
{
    /// <summary>The confidential client this profile decorates.</summary>
    public required string ClientId { get; set; }

    public AgentMode Mode { get; set; } = AgentMode.Delegated;

    /// <summary>The admin ceiling: the widest authority any delegation through this agent can
    /// ever carry. Every mint intersects it with the live user consent and the task request —
    /// narrowing the ceiling takes effect on the next mint with no consent migration.</summary>
    public AuthoritySet Ceiling { get; set; } = AuthoritySet.Empty;

    /// <summary>How many further delegation hops are allowed beneath this agent.
    /// 0 = the agent may receive delegated authority but never re-delegate it.</summary>
    public int MaxDelegationDepth { get; set; }

    /// <summary>Hard cap on delegated-token lifetime, on top of the existing clamps
    /// (client lifetime, subject-token remainder, session cap).</summary>
    public int MaxTokenLifetimeSeconds { get; set; } = 300;

    /// <summary>Policy applied to actions the connector catalog marks high-risk when neither
    /// ceiling nor consent pins an explicit per-action policy.</summary>
    public ActionPolicy HighRiskDefault { get; set; } = ActionPolicy.Ask;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
