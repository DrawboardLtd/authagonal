using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;

namespace Authagonal.Server.Endpoints.Admin;

public static class ProvisioningEndpoints
{
    public static IEndpointRouteBuilder MapProvisioningAdminEndpoints(this IEndpointRouteBuilder app, string policy = "IdentityAdmin")
    {
        var group = app.MapGroup("/api/v1/provisioning/apps")
            .RequireAuthorization(policy)
            .WithTags("Admin - Provisioning");

        group.MapGet("/", ListApps);
        group.MapPost("/", CreateApp);
        group.MapPut("/{appId}", UpdateApp);
        group.MapDelete("/{appId}", DeleteApp);
        group.MapPost("/{appId}/test", TestApp);

        return app;
    }

    private static async Task<IResult> ListApps(
        IProvisioningAppStore store,
        IProvisioningAppQuota quota,
        CancellationToken ct)
    {
        var apps = await store.GetAllAsync(ct);
        var response = new ProvisioningAppListResponse
        {
            Apps = apps.Select(ToView).ToList(),
            Limit = await quota.GetMaxAsync(ct),
        };
        return TypedResults.Json(response, AuthagonalJsonContext.Default.ProvisioningAppListResponse);
    }

    private static async Task<IResult> CreateApp(
        ProvisioningAppRequest request,
        IProvisioningAppStore store,
        IProvisioningAppQuota quota,
        IAuditLogger audit,
        HttpContext http,
        CancellationToken ct)
    {
        if (ValidateRequest(request) is { } error)
            return error;

        var max = await quota.GetMaxAsync(ct);
        if (max is not null)
        {
            var existing = await store.GetAllAsync(ct);
            if (existing.Count >= max.Value)
                return TypedResults.Json(
                    new ErrorInfoResponse
                    {
                        Error = "provisioning_app_limit",
                        ErrorDescription = $"Maximum of {max.Value} provisioning apps allowed.",
                    },
                    AuthagonalJsonContext.Default.ErrorInfoResponse,
                    statusCode: 400);
        }

        var appId = Guid.NewGuid().ToString("N")[..12];
        var app = new ProvisioningAppConfig
        {
            AppId = appId,
            Name = request.Name!.Trim(),
            CallbackUrl = request.CallbackUrl!.Trim(),
            ApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim(),
            TryTimeoutSeconds = NormalizeTimeout(request.TryTimeoutSeconds),
        };

        await store.UpsertAsync(app, ct);
        await audit.LogAsync(Actor(http), "provisioning_app.created", "provisioning_app", appId, app.Name, ct);
        return TypedResults.Json(ToView(app), AuthagonalJsonContext.Default.ProvisioningAppView);
    }

    private static async Task<IResult> UpdateApp(
        string appId,
        ProvisioningAppRequest request,
        IProvisioningAppStore store,
        IAuditLogger audit,
        HttpContext http,
        CancellationToken ct)
    {
        if (ValidateRequest(request) is { } error)
            return error;

        var existing = await store.GetAsync(appId, ct);
        if (existing is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "app_not_found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        existing.Name = request.Name!.Trim();
        existing.CallbackUrl = request.CallbackUrl!.Trim();
        // null = leave unchanged, empty = clear, otherwise = replace.
        if (request.ApiKey is not null)
            existing.ApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim();
        existing.TryTimeoutSeconds = NormalizeTimeout(request.TryTimeoutSeconds);

        await store.UpsertAsync(existing, ct);
        await audit.LogAsync(Actor(http), "provisioning_app.updated", "provisioning_app", appId, existing.Name, ct);
        return TypedResults.Json(ToView(existing), AuthagonalJsonContext.Default.ProvisioningAppView);
    }

    private static async Task<IResult> DeleteApp(
        string appId,
        IProvisioningAppStore store,
        IAuditLogger audit,
        HttpContext http,
        CancellationToken ct)
    {
        var existing = await store.GetAsync(appId, ct);
        if (existing is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "app_not_found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        await store.DeleteAsync(appId, ct);
        await audit.LogAsync(Actor(http), "provisioning_app.deleted", "provisioning_app", appId, null, ct);
        return TypedResults.Json(new ProvisioningAppDeleteResponse { Removed = true }, AuthagonalJsonContext.Default.ProvisioningAppDeleteResponse);
    }

    private static async Task<IResult> TestApp(
        string appId,
        IProvisioningAppStore store,
        IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        var app = await store.GetAsync(appId, ct);
        if (app is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "app_not_found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        if (!OutboundUrlValidator.IsSafe(app.CallbackUrl))
            return TypedResults.Json(new ProvisioningTestResult { Success = false, StatusCode = 0, Body = "Callback URL is not an allowed external host." }, AuthagonalJsonContext.Default.ProvisioningTestResult);

        var http = httpClientFactory.CreateClient("Provisioning");
        http.Timeout = TimeSpan.FromSeconds(10);

        var url = app.CallbackUrl.TrimEnd('/') + "/try";
        var payload = JsonSerializer.Serialize(new ProvisioningTestPayload
        {
            TransactionId = $"test-{Guid.NewGuid():N}",
            UserId = "test-user",
            Email = "test@example.com",
            FirstName = "Test",
            LastName = "User",
        }, AuthagonalJsonContext.Default.ProvisioningTestPayload);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(app.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", app.ApiKey);

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return TypedResults.Json(new ProvisioningTestResult
            {
                Success = response.IsSuccessStatusCode,
                StatusCode = (int)response.StatusCode,
                Body = body.Length > 1000 ? body[..1000] : body,
            }, AuthagonalJsonContext.Default.ProvisioningTestResult);
        }
        catch (TaskCanceledException)
        {
            return TypedResults.Json(new ProvisioningTestResult { Success = false, StatusCode = 0, Body = "Connection timed out" }, AuthagonalJsonContext.Default.ProvisioningTestResult);
        }
        catch (HttpRequestException ex)
        {
            return TypedResults.Json(new ProvisioningTestResult { Success = false, StatusCode = 0, Body = ex.Message }, AuthagonalJsonContext.Default.ProvisioningTestResult);
        }
    }

    private static IResult? ValidateRequest(ProvisioningAppRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "name is required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        if (string.IsNullOrWhiteSpace(request.CallbackUrl))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "callbackUrl is required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        if (!Uri.TryCreate(request.CallbackUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "callbackUrl must be an absolute http(s) URL" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        if (!OutboundUrlValidator.IsSafe(request.CallbackUrl))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "callbackUrl must be an external host (internal/loopback addresses are not allowed)" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        return null;
    }

    private static ProvisioningAppView ToView(ProvisioningAppConfig app) => new()
    {
        AppId = app.AppId,
        Name = app.Name,
        CallbackUrl = app.CallbackUrl,
        HasApiKey = !string.IsNullOrEmpty(app.ApiKey),
        TryTimeoutSeconds = app.TryTimeoutSeconds,
    };

    // Clamp to the orchestrator's useful range. 5s floor matches the short-phase budget;
    // 300s ceiling is the outer edge of a realistic /try workload.
    private static int? NormalizeTimeout(int? value) => value switch
    {
        null => null,
        < 5 => 5,
        > 300 => 300,
        _ => value,
    };

    private static string Actor(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.Email)
        ?? http.User.FindFirstValue("email")
        ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? http.User.FindFirstValue("sub")
        ?? http.User.FindFirstValue("client_id")
        ?? "unknown";
}

public sealed class ProvisioningAppRequest
{
    public string? Name { get; set; }
    public string? CallbackUrl { get; set; }
    public string? ApiKey { get; set; }
    public int? TryTimeoutSeconds { get; set; }
}

public sealed class ProvisioningAppView
{
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public string CallbackUrl { get; set; } = "";
    public bool HasApiKey { get; set; }
    public int? TryTimeoutSeconds { get; set; }
}

public sealed class ProvisioningTestPayload
{
    public string TransactionId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Email { get; set; } = "";
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}

public sealed class ProvisioningTestResult
{
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string Body { get; set; } = "";
}

public sealed class ProvisioningAppListResponse
{
    public List<ProvisioningAppView> Apps { get; set; } = [];
    public int? Limit { get; set; }
}

public sealed class ProvisioningAppDeleteResponse
{
    public bool Removed { get; set; }
}
