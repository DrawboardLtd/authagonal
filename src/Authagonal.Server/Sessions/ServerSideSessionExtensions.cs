using Authagonal.Core.Services;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Authagonal.Server;

/// <summary>
/// Opt-in server-side SSO sessions for a self-hosted (single-tenant) Authagonal host.
/// </summary>
public static class ServerSideSessionExtensions
{
    private const string SessionsTableName = "Sessions";
    private const string SessionsByUserTableName = "SessionsByUser";

    /// <summary>
    /// Enables server-side SSO sessions backed by Azure Table Storage. Registers an
    /// <see cref="ITicketStore"/> — <c>AddAuthagonal</c>'s cookie PostConfigure picks it up automatically, so
    /// the auth cookie carries only an opaque session id and the ticket lives server-side (instant
    /// per-session revocation) — plus an <see cref="IUserSessionRegistry"/> that lights up the login SPA's
    /// "active sessions" section and the <c>/api/auth/sessions</c> self-service endpoints.
    /// <para>
    /// Reads the same <c>Storage:ConnectionString</c> / <c>Storage:TableServiceUri</c> configuration as
    /// <c>AddAuthagonal</c>, so no extra config is required. Call it <b>after</b> <c>AddAuthagonal</c>.
    /// Uses <see cref="EnvPartitioner.Live"/> — the single-env partitioning a self-hosted host runs on.
    /// </para>
    /// </summary>
    public static IServiceCollection AddAuthagonalServerSideSessions(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["Storage:ConnectionString"];
        var tableServiceUri = configuration["Storage:TableServiceUri"];

        TableServiceClient serviceClient;
        if (!string.IsNullOrWhiteSpace(tableServiceUri))
            serviceClient = new TableServiceClient(new Uri(tableServiceUri), new DefaultAzureCredential());
        else if (!string.IsNullOrWhiteSpace(connectionString))
            serviceClient = new TableServiceClient(connectionString);
        else
            throw new InvalidOperationException(
                "Server-side sessions need storage: set Storage:TableServiceUri (managed identity) or Storage:ConnectionString.");

        var sessions = serviceClient.GetTableClient(SessionsTableName);
        var sessionsByUser = serviceClient.GetTableClient(SessionsByUserTableName);
        sessions.CreateIfNotExists();
        sessionsByUser.CreateIfNotExists();

        services.AddHttpContextAccessor();
        // One instance behind both interfaces so "this session" flagging is shared.
        services.TryAddSingleton(sp => new TableTicketStore(
            sessions, sessionsByUser, EnvPartitioner.Live, sp.GetRequiredService<IHttpContextAccessor>(),
            // Resolved lazily: the upstream-token store is optional, and RemoveAsync runs from the
            // expiry sweep where there is no request scope to resolve it from.
            sp));
        services.TryAddSingleton<ITicketStore>(sp => sp.GetRequiredService<TableTicketStore>());
        services.TryAddSingleton<IUserSessionRegistry>(sp => sp.GetRequiredService<TableTicketStore>());
        return services;
    }
}
