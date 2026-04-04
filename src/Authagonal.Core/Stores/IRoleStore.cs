using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IRoleStore
{
    Task<Role?> GetAsync(string roleId, CancellationToken ct = default);
    Task<Role?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Role>> ListAsync(CancellationToken ct = default);
    Task CreateAsync(Role role, CancellationToken ct = default);
    Task UpdateAsync(Role role, CancellationToken ct = default);
    Task DeleteAsync(string roleId, CancellationToken ct = default);
}
