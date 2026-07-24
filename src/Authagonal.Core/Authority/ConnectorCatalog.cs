namespace Authagonal.Core.Authority;

/// <summary>One action a connector exposes, with the metadata consent and admin UIs render.</summary>
public sealed record ConnectorAction(string Name, string? Description = null, bool HighRisk = false);

/// <summary>
/// Catalog metadata for one connector — a downstream surface (API, MCP server) whose
/// <see cref="Type"/> is the RFC 9396 <c>type</c> value authority grants are keyed by.
/// Purely descriptive: the algebra never consults the catalog; it exists so consent screens,
/// admin UIs and discovery can render authority in plain language.
/// </summary>
public sealed record ConnectorDescriptor(
    string Type,
    string? DisplayName = null,
    string? Description = null,
    IReadOnlyList<ConnectorAction>? Actions = null);

/// <summary>Source of connector metadata. Register before <c>AddAuthagonal</c> to supply the
/// host's catalog; the default is empty (authority still works — it just renders raw).</summary>
public interface IConnectorCatalog
{
    Task<IReadOnlyList<ConnectorDescriptor>> GetAllAsync(CancellationToken ct = default);
    Task<ConnectorDescriptor?> GetAsync(string type, CancellationToken ct = default);
}

/// <summary>Config-seeded catalog: the same descriptor list on every host, loaded at boot.</summary>
public sealed class ConfigConnectorCatalog(IEnumerable<ConnectorDescriptor> connectors) : IConnectorCatalog
{
    private readonly IReadOnlyList<ConnectorDescriptor> _connectors = [.. connectors];

    public Task<IReadOnlyList<ConnectorDescriptor>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult(_connectors);

    public Task<ConnectorDescriptor?> GetAsync(string type, CancellationToken ct = default) =>
        Task.FromResult(_connectors.FirstOrDefault(c =>
            string.Equals(c.Type, type, StringComparison.Ordinal)));
}
