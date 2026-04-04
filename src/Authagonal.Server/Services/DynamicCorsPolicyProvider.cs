using Authagonal.Core.Stores;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

public sealed class DynamicCorsPolicyProvider(
    IClientStore clientStore,
    IConfiguration configuration,
    IOptions<CacheOptions> cacheOptions,
    ILogger<DynamicCorsPolicyProvider> logger) : ICorsPolicyProvider
{

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private string[]? _cachedOrigins;
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;

    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var origins = await GetAllowedOriginsAsync();

        if (origins.Length == 0)
            return null;

        var requestOrigin = context.Request.Headers.Origin.ToString();

        if (string.IsNullOrEmpty(requestOrigin) ||
            !origins.Contains(requestOrigin, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var policyBuilder = new CorsPolicyBuilder();

        policyBuilder
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();

        return policyBuilder.Build();
    }

    private async Task<string[]> GetAllowedOriginsAsync()
    {
        if (_cachedOrigins is not null && DateTimeOffset.UtcNow < _cacheExpiry)
            return _cachedOrigins;

        await _semaphore.WaitAsync();
        try
        {
            // Double-check after acquiring the lock.
            if (_cachedOrigins is not null && DateTimeOffset.UtcNow < _cacheExpiry)
                return _cachedOrigins;

            var staticOrigins = configuration.GetSection("AllowedCorsOrigins").Get<string[]>() ?? [];

            var clientOrigins = new List<string>();
            try
            {
                var clients = await clientStore.GetAllAsync();
                foreach (var client in clients)
                {
                    clientOrigins.AddRange(client.AllowedCorsOrigins);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load client CORS origins; using static origins only");
            }

            _cachedOrigins = staticOrigins
                .Concat(clientOrigins)
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _cacheExpiry = DateTimeOffset.UtcNow.AddMinutes(cacheOptions.Value.CorsCacheMinutes);

            logger.LogDebug("CORS origins cache refreshed with {Count} origins", _cachedOrigins.Length);

            return _cachedOrigins;
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
