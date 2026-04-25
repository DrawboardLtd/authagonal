using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

public sealed class TccProvisioningOrchestrator(
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    IProvisioningAppProvider appProvider,
    ILogger<TccProvisioningOrchestrator> logger) : IProvisioningOrchestrator
{
    // Try can do real work (routing slips, org provisioning) so tolerate
    // seconds of latency. Confirm and Cancel must be cheap — hold them to a
    // fixed short budget so a slow downstream can't block the signup path.
    private const int DefaultTryTimeoutSeconds = 60;
    private const int ShortPhaseTimeoutSeconds = 10;

    private IUserProvisionStore GetProvisionStore() =>
        httpContextAccessor.HttpContext?.RequestServices.GetRequiredService<IUserProvisionStore>()
        ?? throw new InvalidOperationException("UserProvisionStore requires an active HTTP request");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task ProvisionAsync(AuthUser user, CancellationToken ct = default)
    {
        var provisioningApps = await appProvider.GetAppsAsync(ct);
        if (provisioningApps.Count == 0) return;

        var appIds = provisioningApps.Select(a => a.AppId).ToList();
        // Cache app configs for lookup during TCC phases
        _resolvedApps = provisioningApps.ToDictionary(a => a.AppId, a => new AppConfig(a.CallbackUrl, a.ApiKey, a.TryTimeoutSeconds), StringComparer.OrdinalIgnoreCase);

        await ProvisionAsync(user, appIds, ct);
        _resolvedApps = null;
    }

    // App configs resolved from the provider (set during ProvisionAsync(user) call)
    [ThreadStatic] private static Dictionary<string, AppConfig>? _resolvedApps;

    public async Task ProvisionAsync(AuthUser user, IReadOnlyList<string> requiredAppIds, CancellationToken ct = default)
    {
        if (requiredAppIds.Count == 0)
            return;

        // Determine which apps still need provisioning
        var existing = await GetProvisionStore().GetByUserAsync(user.Id, ct);
        var existingAppIds = existing.Select(p => p.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var appsToProvision = requiredAppIds.Where(id => !existingAppIds.Contains(id)).ToList();

        if (appsToProvision.Count == 0)
            return;

        // Resolve app configs up front
        var apps = new Dictionary<string, AppConfig>();
        foreach (var appId in appsToProvision)
        {
            var appConfig = GetAppConfig(appId)
                ?? throw new ProvisioningException(appId, $"Provisioning app '{appId}' is not configured");
            apps[appId] = appConfig;
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var succeededTries = new List<string>();

        // ── Phase 1: Try ──────────────────────────────────────────────
        foreach (var appId in appsToProvision)
        {
            try
            {
                var result = await TryAsync(apps[appId], transactionId, user, ct);
                if (!result.Approved)
                {
                    await CancelAllAsync(apps, succeededTries, transactionId);
                    throw new ProvisioningException(appId, result.Reason ?? "Provisioning rejected");
                }
                succeededTries.Add(appId);
            }
            catch (ProvisioningException) { throw; }
            catch (Exception ex)
            {
                await CancelAllAsync(apps, succeededTries, transactionId);
                throw new ProvisioningException(appId, "Try callback failed", ex);
            }
        }

        // ── Phase 2: Confirm ──────────────────────────────────────────
        var confirmedApps = new List<string>();
        foreach (var appId in appsToProvision)
        {
            try
            {
                await ConfirmAsync(apps[appId], transactionId, ct);
                confirmedApps.Add(appId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Confirm failed for app {AppId}, transaction {TransactionId}. " +
                    "Cancelling unconfirmed apps.", appId, transactionId);

                // Cancel apps still in try-only state (not yet confirmed, excluding current)
                var unconfirmed = appsToProvision
                    .Where(id => !confirmedApps.Contains(id) && id != appId)
                    .ToList();
                await CancelAllAsync(apps, unconfirmed, transactionId);

                // Persist records for apps that did confirm so they aren't retried
                await StoreProvisionRecordsAsync(user.Id, confirmedApps, ct);

                throw new ProvisioningException(appId, "Confirm callback failed", ex);
            }
        }

        // ── Phase 3: Persist provision records ────────────────────────
        await StoreProvisionRecordsAsync(user.Id, confirmedApps, ct);

        logger.LogInformation(
            "User {UserId} provisioned into apps [{Apps}], transaction {TransactionId}",
            user.Id, string.Join(", ", appsToProvision), transactionId);
    }

    public async Task DeprovisionAllAsync(string userId, CancellationToken ct = default)
    {
        var provisions = await GetProvisionStore().GetByUserAsync(userId, ct);

        foreach (var provision in provisions)
        {
            var appConfig = GetAppConfig(provision.AppId);
            if (appConfig is null)
            {
                logger.LogWarning(
                    "Cannot deprovision user {UserId} from app {AppId}: app not configured",
                    userId, provision.AppId);
            }
            else
            {
                try
                {
                    await DeprovisionAsync(appConfig, userId, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to deprovision user {UserId} from app {AppId}",
                        userId, provision.AppId);
                }
            }

            await GetProvisionStore().RemoveAsync(userId, provision.AppId, ct);
        }
    }

    // ── Internals ─────────────────────────────────────────────────────

    private async Task StoreProvisionRecordsAsync(
        string userId, List<string> appIds, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var appId in appIds)
        {
            await GetProvisionStore().StoreAsync(new UserProvision
            {
                UserId = userId,
                AppId = appId,
                ProvisionedAt = now
            }, ct);
        }
    }

    private async Task<TryResponse> TryAsync(
        AppConfig app, string transactionId, AuthUser user, CancellationToken ct)
    {
        var url = app.CallbackUrl.TrimEnd('/') + "/try";
        var payload = new TryRequest
        {
            TransactionId = transactionId,
            UserId = user.Id,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            OrganizationId = user.OrganizationId,
            CustomAttributes = user.CustomAttributes.Count > 0
                ? new Dictionary<string, string>(user.CustomAttributes)
                : null
        };

        var timeout = TimeSpan.FromSeconds(app.TryTimeoutSeconds ?? DefaultTryTimeoutSeconds);
        var response = await PostAsync<TryResponse>(app, url, payload, timeout, ct)
            ?? new TryResponse { Approved = true };

        if (response.Approved)
            MergeIntoUser(user, response);

        return response;
    }

    private static void MergeIntoUser(AuthUser user, TryResponse response)
    {
        // A downstream app can only assign OrganizationId if the user doesn't
        // have one yet. Later apps in the same transaction see the earlier
        // assignment and don't overwrite it.
        if (!string.IsNullOrWhiteSpace(response.OrganizationId) &&
            string.IsNullOrWhiteSpace(user.OrganizationId))
        {
            user.OrganizationId = response.OrganizationId;
        }

        if (response.CustomAttributes is { Count: > 0 })
        {
            foreach (var kvp in response.CustomAttributes)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key)) continue;
                user.CustomAttributes[kvp.Key] = kvp.Value;
            }
        }
    }

    private async Task ConfirmAsync(AppConfig app, string transactionId, CancellationToken ct)
    {
        var url = app.CallbackUrl.TrimEnd('/') + "/confirm";
        await PostAsync(app, url, new TransactionRequest { TransactionId = transactionId },
            TimeSpan.FromSeconds(ShortPhaseTimeoutSeconds), ct);
    }

    private async Task CancelAllAsync(
        Dictionary<string, AppConfig> apps,
        List<string> appIds,
        string transactionId)
    {
        foreach (var appId in appIds)
        {
            try
            {
                var url = apps[appId].CallbackUrl.TrimEnd('/') + "/cancel";
                await PostAsync(apps[appId], url,
                    new TransactionRequest { TransactionId = transactionId },
                    TimeSpan.FromSeconds(ShortPhaseTimeoutSeconds), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Cancel failed for app {AppId}, transaction {TransactionId} (will expire via TTL)",
                    appId, transactionId);
            }
        }
    }

    private async Task DeprovisionAsync(AppConfig app, string userId, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("Provisioning");
        var url = app.CallbackUrl.TrimEnd('/') + $"/users/{Uri.EscapeDataString(userId)}";

        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        if (!string.IsNullOrWhiteSpace(app.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", app.ApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(ShortPhaseTimeoutSeconds));
        using var response = await client.SendAsync(request, cts.Token);
        logger.LogInformation(
            "Deprovision user {UserId}: HTTP {StatusCode}", userId, (int)response.StatusCode);
    }

    private AppConfig? GetAppConfig(string appId)
    {
        // Check resolved apps from provider (set during ProvisionAsync(user) call)
        if (_resolvedApps?.TryGetValue(appId, out var resolved) == true)
            return resolved;

        // Fall back to async provider lookup (for legacy per-client app ID calls)
        var app = appProvider.GetAppsAsync().GetAwaiter().GetResult()
            .FirstOrDefault(a => string.Equals(a.AppId, appId, StringComparison.OrdinalIgnoreCase));

        return app is not null ? new AppConfig(app.CallbackUrl, app.ApiKey, app.TryTimeoutSeconds) : null;
    }

    // ── HTTP helpers ──────────────────────────────────────────────────

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Provisioning payloads are polymorphic external contracts")]
    private async Task<T?> PostAsync<T>(
        AppConfig app, string url, object payload, TimeSpan timeout, CancellationToken ct) where T : class
    {
        using var response = await SendPostAsync(app, url, payload, timeout, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Provisioning callback failed: HTTP {(int)response.StatusCode} — {body}");

        try { return JsonSerializer.Deserialize<T>(body, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private async Task PostAsync(
        AppConfig app, string url, object payload, TimeSpan timeout, CancellationToken ct)
    {
        using var response = await SendPostAsync(app, url, payload, timeout, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Provisioning callback failed: HTTP {(int)response.StatusCode} — {body}");
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Provisioning payloads are polymorphic external contracts")]
    private async Task<HttpResponseMessage> SendPostAsync(
        AppConfig app, string url, object payload, TimeSpan timeout, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("Provisioning");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        if (!string.IsNullOrWhiteSpace(app.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", app.ApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return await client.SendAsync(request, cts.Token);
    }

    // ── DTOs ──────────────────────────────────────────────────────────

    private sealed record AppConfig(string CallbackUrl, string? ApiKey, int? TryTimeoutSeconds);

    private sealed record TryRequest
    {
        public required string TransactionId { get; init; }
        public required string UserId { get; init; }
        public required string Email { get; init; }
        public string? FirstName { get; init; }
        public string? LastName { get; init; }
        public string? OrganizationId { get; init; }
        public Dictionary<string, string>? CustomAttributes { get; init; }
    }

    private sealed record TransactionRequest
    {
        public required string TransactionId { get; init; }
    }

    private sealed record TryResponse
    {
        public bool Approved { get; init; } = true;
        public string? Reason { get; init; }
        // When the downstream app provisions a new organization (or resolves an
        // existing one) for this user, it returns the org id here. The
        // orchestrator merges it onto AuthUser.OrganizationId, and it flows
        // onto tokens as the standard org_id claim.
        public string? OrganizationId { get; init; }
        // Downstream-assigned product-level attributes (e.g. org_role).
        // Merged into AuthUser.CustomAttributes; emitted on tokens via scope
        // UserClaims configuration.
        public Dictionary<string, string>? CustomAttributes { get; init; }
    }
}
