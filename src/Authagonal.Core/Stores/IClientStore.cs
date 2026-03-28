using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IClientStore
{
    Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct = default);
    Task<IReadOnlyList<OAuthClient>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(OAuthClient client, CancellationToken ct = default);
    Task DeleteAsync(string clientId, CancellationToken ct = default);
}
