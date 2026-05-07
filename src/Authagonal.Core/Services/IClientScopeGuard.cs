using System.Security.Claims;

namespace Authagonal.Core.Services;

/// <summary>
/// Privilege gate for OAuth client mutation. When a caller creates or updates a client
/// and assigns scopes to it, this guard decides whether the caller is allowed to grant
/// each requested scope. Used by the admin clients API to prevent privilege escalation
/// (e.g. a developer-role caller minting a client that holds owner-level admin scopes).
/// </summary>
public interface IClientScopeGuard
{
    /// <summary>
    /// Returns the first scope in <paramref name="requestedScopes"/> the caller is not
    /// authorized to grant, or null if all are grantable.
    /// </summary>
    string? FindUngrantableScope(ClaimsPrincipal user, IEnumerable<string>? requestedScopes);
}

/// <summary>
/// Default single-role implementation: any authenticated admin caller can grant any scope.
/// Hosts with a richer role hierarchy (e.g. Authagonal Cloud) register their own.
/// </summary>
public sealed class AllowAllClientScopeGuard : IClientScopeGuard
{
    public string? FindUngrantableScope(ClaimsPrincipal user, IEnumerable<string>? requestedScopes) => null;
}
