using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IScopeStore
{
    Task<Scope?> GetAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Scope>> ListAsync(CancellationToken ct = default);
    Task CreateAsync(Scope scope, CancellationToken ct = default);
    Task UpdateAsync(Scope scope, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);
}
