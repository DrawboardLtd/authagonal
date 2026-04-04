using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IUserProvisionStore
{
    Task<IReadOnlyList<UserProvision>> GetByUserAsync(string userId, CancellationToken ct = default);
    Task StoreAsync(UserProvision provision, CancellationToken ct = default);
    Task RemoveAsync(string userId, string appId, CancellationToken ct = default);
    Task RemoveAllByUserAsync(string userId, CancellationToken ct = default);
}
