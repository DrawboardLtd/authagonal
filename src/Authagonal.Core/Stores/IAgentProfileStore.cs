using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IAgentProfileStore
{
    Task<AgentProfile?> GetAsync(string clientId, CancellationToken ct = default);
    Task<IReadOnlyList<AgentProfile>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(AgentProfile profile, CancellationToken ct = default);
    Task DeleteAsync(string clientId, CancellationToken ct = default);
}

/// <summary>
/// Default for hosts that haven't wired agent storage: no client has an agent profile, so
/// every token flow behaves exactly as it did before the agentic layer existed. Provider
/// packages (Table Storage / DynamoDB) replace this with a real store.
/// </summary>
public sealed class NullAgentProfileStore : IAgentProfileStore
{
    public Task<AgentProfile?> GetAsync(string clientId, CancellationToken ct = default) =>
        Task.FromResult<AgentProfile?>(null);

    public Task<IReadOnlyList<AgentProfile>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AgentProfile>>([]);

    public Task UpsertAsync(AgentProfile profile, CancellationToken ct = default) =>
        throw new NotSupportedException("No agent profile store is configured");

    public Task DeleteAsync(string clientId, CancellationToken ct = default) =>
        throw new NotSupportedException("No agent profile store is configured");
}
