using System.Security.Cryptography;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server;
using Authagonal.Server.Services;
using CustomAuthServer.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Register custom implementations BEFORE AddAuthagonal — TryAdd won't
// overwrite these, so your implementations take precedence.
// ---------------------------------------------------------------------------

// Custom auth hook: logs every authentication event to the console (and could
// write to a database, send webhooks, emit metrics, etc.)
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();

// Custom email service: writes emails to the console instead of SendGrid.
// Useful for development; swap for SMTP, Mailgun, SES, etc. in production.
builder.Services.AddSingleton<IEmailService, ConsoleEmailService>();

// ---------------------------------------------------------------------------
// Standard Authagonal setup — registers storage, auth, endpoints, etc.
// ---------------------------------------------------------------------------
builder.Services.AddAuthagonal(builder.Configuration);

// Demo-only: background purge of stale users (older than 24 hours)
builder.Services.AddHostedService<DemoPurgeService>();

var app = builder.Build();

app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// ---------------------------------------------------------------------------
// Custom endpoints — add your own alongside the standard Authagonal ones
// ---------------------------------------------------------------------------
app.MapGet("/custom/health", () => Results.Ok(new
{
    service = "custom-auth-server",
    status = "healthy",
    timestamp = DateTimeOffset.UtcNow
}));

// ---------------------------------------------------------------------------
// Demo: public self-service registration (no admin JWT required)
// ---------------------------------------------------------------------------
app.MapPost("/api/auth/register", async (
    RegisterRequest request,
    IUserStore userStore,
    PasswordHasher passwordHasher,
    IAuthHook authHook,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        return Results.Json(new { error = "email_and_password_required" }, statusCode: 400);

    var email = request.Email.Trim();
    if (request.Password.Length < 8)
        return Results.Json(new { error = "password_too_short", message = "Password must be at least 8 characters" }, statusCode: 400);

    var existing = await userStore.FindByEmailAsync(email, ct);
    if (existing is not null)
        return Results.Json(new { error = "email_already_registered" }, statusCode: 409);

    var user = new AuthUser
    {
        Id = Guid.NewGuid().ToString(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        PasswordHash = passwordHasher.HashPassword(request.Password),
        EmailConfirmed = true, // Demo: skip email verification
        FirstName = request.FirstName?.Trim(),
        LastName = request.LastName?.Trim(),
        LockoutEnabled = true,
        SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    await userStore.CreateAsync(user, ct);
    await authHook.OnUserCreatedAsync(user.Id, user.Email, "self-registration", ct);

    return Results.Json(new
    {
        userId = user.Id,
        email = user.Email,
        firstName = user.FirstName,
        lastName = user.LastName,
        createdAt = user.CreatedAt,
    }, statusCode: 201);
}).DisableAntiforgery();

app.MapFallbackToFile("index.html");

app.Run();

// ---------------------------------------------------------------------------
// Request DTOs
// ---------------------------------------------------------------------------
public sealed record RegisterRequest(string? Email, string? Password, string? FirstName, string? LastName);

// ---------------------------------------------------------------------------
// Background service: purge demo users older than 24 hours
// ---------------------------------------------------------------------------
public sealed class DemoPurgeService(IServiceProvider services, ILogger<DemoPurgeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);

            try
            {
                using var scope = services.CreateScope();
                var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
                var mfaStore = scope.ServiceProvider.GetRequiredService<IMfaStore>();
                var cutoff = DateTimeOffset.UtcNow.AddHours(-24);

                // Table Storage doesn't support server-side date filtering on custom properties,
                // so we use the admin list approach: query all users and filter in memory.
                // For a demo with a small number of users this is fine.
                var users = await GetAllUsersAsync(scope.ServiceProvider, stoppingToken);
                var stale = users.Where(u => u.CreatedAt < cutoff).ToList();

                foreach (var user in stale)
                {
                    await mfaStore.DeleteAllCredentialsAsync(user.Id, stoppingToken);
                    await userStore.DeleteAsync(user.Id, stoppingToken);
                    logger.LogInformation("[PURGE] Deleted stale user {Email} (created {CreatedAt})", user.Email, user.CreatedAt);
                }

                if (stale.Count > 0)
                    logger.LogInformation("[PURGE] Removed {Count} stale user(s)", stale.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "[PURGE] Error during purge cycle");
            }
        }
    }

    private static async Task<List<AuthUser>> GetAllUsersAsync(IServiceProvider sp, CancellationToken ct)
    {
        // IUserStore doesn't expose ListAll, so query Table Storage directly.
        var config = sp.GetRequiredService<IConfiguration>();
        var connStr = config["Storage:ConnectionString"];
        if (string.IsNullOrEmpty(connStr)) return [];

        var table = new Azure.Data.Tables.TableServiceClient(connStr).GetTableClient("Users");
        var users = new List<AuthUser>();

        await foreach (var entity in table.QueryAsync<Azure.Data.Tables.TableEntity>(cancellationToken: ct))
        {
            users.Add(new AuthUser
            {
                Id = entity.PartitionKey,
                Email = entity.GetString("Email") ?? "",
                NormalizedEmail = entity.GetString("NormalizedEmail") ?? "",
                CreatedAt = entity.GetDateTimeOffset("CreatedAt") ?? entity.Timestamp ?? DateTimeOffset.MinValue,
            });
        }

        return users;
    }
}
