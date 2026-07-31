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

    /// <summary>Optional — a host outside a request scope simply skips the merge persist.</summary>
    private IUserStore? TryGetUserStore() =>
        httpContextAccessor.HttpContext?.RequestServices.GetService<IUserStore>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task ProvisionAsync(AuthUser user, CancellationToken ct = default)
        => ProvisionAllAsync(user, forceReprovision: false, ct);

    public Task ReprovisionAsync(AuthUser user, CancellationToken ct = default)
        => ProvisionAllAsync(user, forceReprovision: true, ct);

    private async Task ProvisionAllAsync(AuthUser user, bool forceReprovision, CancellationToken ct)
    {
        var provisioningApps = await appProvider.GetAppsAsync(ct);
        if (provisioningApps.Count == 0) return;

        var appIds = provisioningApps.Select(a => a.AppId).ToList();
        // Resolved app configs are threaded through the call chain — never stored thread-static,
        // which bled across requests and was lost across awaits.
        var resolved = provisioningApps.ToDictionary(
            a => a.AppId,
            a => new AppConfig(a.CallbackUrl, a.ApiKey, a.TryTimeoutSeconds),
            StringComparer.OrdinalIgnoreCase);

        await ProvisionInternalAsync(user, appIds, resolved, ct, forceReprovision);
    }

    public Task ProvisionAsync(AuthUser user, IReadOnlyList<string> requiredAppIds, CancellationToken ct = default)
        => ProvisionInternalAsync(user, requiredAppIds, resolvedApps: null, ct);

    private async Task ProvisionInternalAsync(
        AuthUser user, IReadOnlyList<string> requiredAppIds,
        IReadOnlyDictionary<string, AppConfig>? resolvedApps, CancellationToken ct,
        bool forceReprovision = false)
    {
        if (requiredAppIds.Count == 0)
            return;

        // Determine which apps still need provisioning. A forced reprovision re-runs every app even
        // when already provisioned — the downstream relationship changed (e.g. guest → standard-user
        // upgrade) and the app must react again.
        var appsToProvision = requiredAppIds.ToList();
        if (!forceReprovision)
        {
            var existing = await GetProvisionStore().GetByUserAsync(user.Id, ct);
            var existingAppIds = existing.Select(p => p.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            appsToProvision = requiredAppIds.Where(id => !existingAppIds.Contains(id)).ToList();
        }

        if (appsToProvision.Count == 0)
            return;

        // Resolve app configs up front
        var apps = new Dictionary<string, AppConfig>();
        foreach (var appId in appsToProvision)
        {
            var appConfig = await GetAppConfigAsync(appId, resolvedApps, ct)
                ?? throw new ProvisioningException(appId, $"Provisioning app '{appId}' is not configured");
            apps[appId] = appConfig;
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var succeededTries = new List<string>();
        var merged = false;

        // ── Phase 1: Try ──────────────────────────────────────────────
        foreach (var appId in appsToProvision)
        {
            try
            {
                var result = await TryAsync(apps[appId], transactionId, user, ct);
                // TryAsync merges the response into `user` in place; record that so phase 3 knows
                // there is something to persist.
                merged = true;
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

                // Compensate the apps that DID confirm, here rather than leaving it to the caller.
                //
                // This used to persist provision records for them and rethrow. Every caller answers a
                // ProvisioningException by deleting the local user and nothing else — no deprovision,
                // no provision-row cleanup (IUserProvisionStore.RemoveAllByUserAsync exists in all
                // three providers and was called from nowhere). So a partial failure ended with a
                // downstream app holding a live, confirmed account for a subject the IdP no longer
                // has, provision rows pointing at a deleted user id, and nothing anywhere recording
                // that a compensation was owed. Cancel is a no-op for an app past confirm, which is
                // why the confirmed set needs an explicit deprovision.
                //
                // The records are still written first, because DeprovisionAllAsync walks the store to
                // find what to undo — and it removes each row as it goes, so nothing is left behind.
                await StoreProvisionRecordsAsync(user.Id, confirmedApps, ct);

                if (confirmedApps.Count > 0)
                {
                    logger.LogWarning(
                        "Compensating {Count} app(s) that already confirmed in transaction {TransactionId}",
                        confirmedApps.Count, transactionId);

                    try
                    {
                        await DeprovisionAllAsync(user.Id, ct);
                    }
                    catch (Exception compensationEx)
                    {
                        // The original failure is what the caller needs to see. A failed compensation
                        // is louder in the log than it can be in the exception, and swallowing the
                        // cause to report the cleanup would hide why any of this happened.
                        logger.LogError(compensationEx,
                            "Compensation failed for transaction {TransactionId}; app accounts for user " +
                            "{UserId} may survive the rollback and need manual removal",
                            transactionId, user.Id);
                    }
                }

                throw new ProvisioningException(appId, "Confirm callback failed", ex);
            }
        }

        // ── Phase 3: Persist provision records ────────────────────────
        await StoreProvisionRecordsAsync(user.Id, confirmedApps, ct);

        // …and persist the merge, which is the whole point of the /try response.
        //
        // MergeIntoUser mutates the in-memory AuthUser only: it sets OrganizationId, unions
        // CustomAttributes and can set EmailConfirmed (the documented "downstream vouches it verified
        // this address" contract). Exactly ONE caller saved afterwards — self-service registration.
        // Admin create, SAML JIT, OIDC JIT and SCIM create all call CreateAsync BEFORE provisioning
        // and never update after, and /authorize re-reads the user through the subject resolver
        // immediately afterwards, so the merge was thrown away there too. And because the provision
        // records above mark the app as provisioned, /try is never repeated — the values were lost
        // permanently, not for one request. For the admin path that included EmailConfirmed, which is
        // what made an admin-created user unable to log in.
        if (merged)
            await PersistMergeAsync(user, ct);

        logger.LogInformation(
            "User {UserId} provisioned into apps [{Apps}], transaction {TransactionId}",
            user.Id, string.Join(", ", appsToProvision), transactionId);
    }

    public async Task DeprovisionAllAsync(string userId, CancellationToken ct = default)
    {
        var provisions = await GetProvisionStore().GetByUserAsync(userId, ct);

        foreach (var provision in provisions)
        {
            var appConfig = await GetAppConfigAsync(provision.AppId, null, ct);
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

    /// <summary>
    /// Writes the merged in-memory user back to the store, so the values a downstream app supplied at
    /// /try survive the request that fetched them.
    /// </summary>
    /// <remarks>
    /// Best effort: provisioning has already succeeded at this point, and failing the whole operation
    /// because a profile write did not land would undo real downstream work to fix a metadata
    /// problem. A failure is logged with the fields at stake.
    /// </remarks>
    private async Task PersistMergeAsync(AuthUser user, CancellationToken ct)
    {
        var userStore = TryGetUserStore();
        if (userStore is null) return;

        try
        {
            // Re-read and re-apply rather than writing the in-hand instance: it was loaded before the
            // provisioning round-trips, and overwriting the row wholesale would clobber anything
            // written in between (a login stamp, a lockout reset).
            var current = await userStore.GetAsync(user.Id, ct);
            if (current is null) return;

            var changed = false;

            if (!string.IsNullOrWhiteSpace(user.OrganizationId)
                && !string.Equals(current.OrganizationId, user.OrganizationId, StringComparison.Ordinal))
            {
                current.OrganizationId = user.OrganizationId;
                changed = true;
            }

            // Only the vouch direction: a downstream app may confirm an address, never un-confirm one.
            if (user.EmailConfirmed && !current.EmailConfirmed)
            {
                current.EmailConfirmed = true;
                changed = true;
            }

            foreach (var (key, value) in user.CustomAttributes)
            {
                if (current.CustomAttributes.TryGetValue(key, out var existing)
                    && string.Equals(existing, value, StringComparison.Ordinal))
                    continue;
                current.CustomAttributes[key] = value;
                changed = true;
            }

            if (!changed) return;

            current.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(current, ct);

            // Keep the caller's instance consistent with what was stored — several callers go on to
            // build a subject or a response from it. That includes the revision: this write moved the
            // row on, and callers like ConfirmEmailAsync update their own instance right afterwards,
            // which the store now refuses if it is still holding the pre-merge revision.
            user.OrganizationId = current.OrganizationId;
            user.EmailConfirmed = current.EmailConfirmed;
            user.CustomAttributes = current.CustomAttributes;
            user.ConcurrencyToken = current.ConcurrencyToken;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Could not persist the provisioning merge for user {UserId}; organization id, custom " +
                "attributes and any email-verified vouch from /try will not survive this request",
                user.Id);
        }
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

        if (response.EmailVerified == true)
            user.EmailConfirmed = true;
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

    private async Task<AppConfig?> GetAppConfigAsync(
        string appId, IReadOnlyDictionary<string, AppConfig>? resolvedApps, CancellationToken ct)
    {
        // Prefer the configs resolved for this operation.
        if (resolvedApps?.TryGetValue(appId, out var resolved) == true)
            return resolved;

        // Fall back to a provider lookup (per-client app-id calls / deprovision).
        var apps = await appProvider.GetAppsAsync(ct);
        var app = apps.FirstOrDefault(a => string.Equals(a.AppId, appId, StringComparison.OrdinalIgnoreCase));
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

        // The downstream app vouches that it VERIFIED the registrant's email address as part of
        // approving this Try — e.g. an invite redemption where the app enforced that the
        // authenticated email equals the emailed invite's recipient. The orchestrator marks the
        // account EmailConfirmed, and registration skips the verification email entirely:
        // possession of the emailed invite IS the verification.
        public bool? EmailVerified { get; init; }
    }
}
