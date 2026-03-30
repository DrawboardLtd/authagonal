using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IScimTokenStore
{
    Task<ScimToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default);
    Task<IReadOnlyList<ScimToken>> GetByClientAsync(string clientId, CancellationToken ct = default);
    Task StoreAsync(ScimToken token, CancellationToken ct = default);
    Task RevokeAsync(string tokenId, string clientId, CancellationToken ct = default);
    Task DeleteAsync(string tokenId, string clientId, CancellationToken ct = default);
}
