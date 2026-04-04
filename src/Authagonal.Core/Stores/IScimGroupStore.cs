using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IScimGroupStore
{
    Task<ScimGroup?> GetAsync(string groupId, CancellationToken ct = default);
    Task<ScimGroup?> FindByExternalIdAsync(string organizationId, string externalId, CancellationToken ct = default);
    Task<(IReadOnlyList<ScimGroup> Groups, int TotalCount)> ListAsync(string? organizationId, int startIndex, int count, CancellationToken ct = default);
    Task<IReadOnlyList<ScimGroup>> GetGroupsByUserIdAsync(string userId, CancellationToken ct = default);
    Task CreateAsync(ScimGroup group, CancellationToken ct = default);
    Task UpdateAsync(ScimGroup group, CancellationToken ct = default);
    Task DeleteAsync(string groupId, CancellationToken ct = default);
}
