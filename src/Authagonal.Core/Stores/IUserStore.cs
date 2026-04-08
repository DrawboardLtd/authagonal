using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IUserStore
{
    Task<AuthUser?> GetAsync(string userId, CancellationToken ct = default);
    Task<AuthUser?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task CreateAsync(AuthUser user, CancellationToken ct = default);
    Task UpdateAsync(AuthUser user, CancellationToken ct = default);
    Task DeleteAsync(string userId, CancellationToken ct = default);
    Task<bool> ExistsAsync(string userId, CancellationToken ct = default);

    Task<AuthUser?> FindByExternalIdAsync(string clientId, string externalId, CancellationToken ct = default);
    Task<(IReadOnlyList<AuthUser> Users, bool HasMore)> ListAsync(string? organizationId, int startIndex, int count, CancellationToken ct = default);
    Task<IReadOnlyList<AuthUser>> SearchAsync(string query, int maxResults = 20, CancellationToken ct = default);
    Task SetExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default);
    Task RemoveExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default);

    Task AddLoginAsync(ExternalLoginInfo login, CancellationToken ct = default);
    Task RemoveLoginAsync(string userId, string provider, string providerKey, CancellationToken ct = default);
    Task<ExternalLoginInfo?> FindLoginAsync(string provider, string providerKey, CancellationToken ct = default);
    Task<IReadOnlyList<ExternalLoginInfo>> GetLoginsAsync(string userId, CancellationToken ct = default);
}
