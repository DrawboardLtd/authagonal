using Authagonal.Core.Stores;

namespace Authagonal.Migration;

/// <summary>
/// The store abstractions the migration engine writes through. Bundled so both the in-host DI
/// singletons and the CLI's <c>StoreFactory</c> can supply the same engine — the engine never
/// touches a concrete Table*Store or a <c>TableServiceClient</c> directly.
/// </summary>
public sealed record DuendeMigrationStores
{
    public required IUserStore Users { get; init; }
    public required IRoleStore Roles { get; init; }
    public required IScopeStore Scopes { get; init; }
    public required IClientStore Clients { get; init; }
    public required IMfaStore Mfa { get; init; }
    public required ISamlProviderStore SamlProviders { get; init; }
    public required IOidcProviderStore OidcProviders { get; init; }
    public required ISsoDomainStore SsoDomains { get; init; }
    public required IGrantStore Grants { get; init; }
}
