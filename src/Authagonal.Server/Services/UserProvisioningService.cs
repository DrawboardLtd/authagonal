using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Authagonal.Core.Services;

namespace Authagonal.Server.Services;

public sealed class UserProvisioningService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<UserProvisioningService> logger) : IUserProvisioningService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ProvisioningResult> ProvisionUserAsync(ProvisioningRequest request, CancellationToken ct = default)
    {
        var webhookUrl = configuration["Provisioning:WebhookUrl"];
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            // No webhook configured — auto-approve with a new ID
            logger.LogDebug("No provisioning webhook configured, auto-approving user {Email}", request.Email);
            return new ProvisioningResult
            {
                Approved = true,
                UserId = Guid.NewGuid().ToString("N")
            };
        }

        var client = httpClientFactory.CreateClient("Provisioning");
        var apiKey = configuration["Provisioning:ApiKey"];

        var url = webhookUrl.TrimEnd('/') + "/users/provision";
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(httpRequest, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Provisioning webhook failed for {Email} at {Url}", request.Email, url);
            return new ProvisioningResult
            {
                Approved = false,
                Reason = "Provisioning service unavailable. Please try again later."
            };
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Provisioning webhook rejected user {Email}: HTTP {StatusCode}, Body: {Body}",
                request.Email, (int)response.StatusCode, body);

            var errorReason = TryParseReason(body) ?? $"Provisioning rejected (HTTP {(int)response.StatusCode})";
            return new ProvisioningResult { Approved = false, Reason = errorReason };
        }

        try
        {
            var result = JsonSerializer.Deserialize<WebhookResponse>(body, JsonOptions);
            if (result is null)
            {
                return new ProvisioningResult
                {
                    Approved = true,
                    UserId = Guid.NewGuid().ToString("N")
                };
            }

            if (result.Approved == false)
            {
                logger.LogInformation("Provisioning webhook explicitly rejected user {Email}: {Reason}",
                    request.Email, result.Reason);
                return new ProvisioningResult
                {
                    Approved = false,
                    Reason = result.Reason ?? "Account creation not permitted."
                };
            }

            logger.LogInformation(
                "Provisioning webhook approved user {Email} with userId={UserId}",
                request.Email, result.UserId);

            return new ProvisioningResult
            {
                Approved = true,
                UserId = result.UserId ?? Guid.NewGuid().ToString("N"),
                OrganizationId = result.OrganizationId
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse provisioning webhook response for {Email}, treating as approved", request.Email);
            return new ProvisioningResult
            {
                Approved = true,
                UserId = Guid.NewGuid().ToString("N")
            };
        }
    }

    public async Task NotifyUserDeletedAsync(string userId, string email, CancellationToken ct = default)
    {
        var webhookUrl = configuration["Provisioning:WebhookUrl"];
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return;

        var client = httpClientFactory.CreateClient("Provisioning");
        var apiKey = configuration["Provisioning:ApiKey"];

        var url = webhookUrl.TrimEnd('/') + $"/users/{Uri.EscapeDataString(userId)}";
        var httpRequest = new HttpRequestMessage(HttpMethod.Delete, url);

        if (!string.IsNullOrWhiteSpace(apiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            var response = await client.SendAsync(httpRequest, ct);
            logger.LogInformation("Notified provisioning service of user deletion {UserId}: HTTP {StatusCode}",
                userId, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to notify provisioning service of user deletion {UserId}", userId);
        }
    }

    private static string? TryParseReason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("reason", out var reason))
                return reason.GetString();
            if (doc.RootElement.TryGetProperty("message", out var message))
                return message.GetString();
            if (doc.RootElement.TryGetProperty("error", out var error))
                return error.GetString();
        }
        catch { }
        return null;
    }

    private sealed class WebhookResponse
    {
        public bool? Approved { get; set; }
        public string? UserId { get; set; }
        public string? OrganizationId { get; set; }
        public string? Reason { get; set; }
    }
}
