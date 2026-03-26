using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IUserStore
{
    Task<AuthUser?> FindByIdAsync(string userId, CancellationToken ct = default);
    Task<AuthUser?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task CreateAsync(AuthUser user, CancellationToken ct = default);
    Task UpdateAsync(AuthUser user, CancellationToken ct = default);
    Task DeleteAsync(string userId, CancellationToken ct = default);
    Task<bool> ExistsAsync(string userId, CancellationToken ct = default);

    Task AddLoginAsync(ExternalLoginInfo login, CancellationToken ct = default);
    Task RemoveLoginAsync(string userId, string provider, string providerKey, CancellationToken ct = default);
    Task<ExternalLoginInfo?> FindLoginAsync(string provider, string providerKey, CancellationToken ct = default);
    Task<IReadOnlyList<ExternalLoginInfo>> GetLoginsAsync(string userId, CancellationToken ct = default);
}
