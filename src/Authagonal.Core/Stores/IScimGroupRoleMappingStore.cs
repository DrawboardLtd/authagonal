using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

/// <summary>
/// Per-tenant store of SCIM-group → role grants. Read at token issuance to compute a
/// user's effective roles; managed by admins (platform) / tenant owners (portal).
/// An empty store is a no-op — effective roles equal directly-assigned roles.
/// </summary>
public interface IScimGroupRoleMappingStore
{
    Task<IReadOnlyList<ScimGroupRoleMapping>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Upsert a (group, role) grant.</summary>
    Task SetAsync(ScimGroupRoleMapping mapping, CancellationToken ct = default);

    /// <summary>Remove a single (group, role) grant.</summary>
    Task DeleteAsync(string groupId, string role, CancellationToken ct = default);
}
