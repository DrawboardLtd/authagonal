using Authagonal.Core.Services;
using Authagonal.Server;
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

app.MapFallbackToFile("index.html");

app.Run();
