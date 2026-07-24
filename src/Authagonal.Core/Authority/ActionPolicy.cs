namespace Authagonal.Core.Authority;

/// <summary>
/// Per-action execution policy on an authority grant. Ordered by restrictiveness so an
/// intersection can take the max: <see cref="Auto"/> &lt; <see cref="Ask"/> &lt; <see cref="Deny"/>.
/// </summary>
public enum ActionPolicy
{
    /// <summary>The action executes without a human in the loop.</summary>
    Auto = 0,

    /// <summary>The action requires a just-in-time approval from the delegating user before it runs.</summary>
    Ask = 1,

    /// <summary>The action is blocked outright.</summary>
    Deny = 2,
}
