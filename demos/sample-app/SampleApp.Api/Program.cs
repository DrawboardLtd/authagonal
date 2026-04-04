using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------------------------------------------------------------------------
// Authentication — validate JWTs issued by Authagonal
// ---------------------------------------------------------------------------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = config["Auth:Authority"]
            ?? throw new InvalidOperationException("Auth:Authority is not configured");

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidAudience = config["Auth:Audience"] ?? "sample-app",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(60)
        };

        // For development with HTTP (not recommended in production)
        if (config.GetValue("Auth:AllowHttp", false))
        {
            options.RequireHttpsMetadata = false;
        }
    });

builder.Services.AddAuthorization();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = config.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:3000"];
        policy.WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// ---------------------------------------------------------------------------
// Public endpoint — no auth required
// ---------------------------------------------------------------------------
app.MapGet("/api/public", () => new
{
    message = "This endpoint is public — no authentication required.",
    timestamp = DateTimeOffset.UtcNow
});

// ---------------------------------------------------------------------------
// Protected endpoint — requires a valid JWT from Authagonal
// ---------------------------------------------------------------------------
app.MapGet("/api/protected", (HttpContext ctx) =>
{
    var sub = ctx.User.FindFirst("sub")?.Value;
    var email = ctx.User.FindFirst("email")?.Value;
    var scope = ctx.User.FindFirst("scope")?.Value;
    var clientId = ctx.User.FindFirst("client_id")?.Value;

    return new
    {
        message = "You are authenticated!",
        userId = sub,
        email,
        scope,
        clientId,
        timestamp = DateTimeOffset.UtcNow
    };
}).RequireAuthorization();

// ---------------------------------------------------------------------------
// Example: protected resource that returns user-specific data
// ---------------------------------------------------------------------------
app.MapGet("/api/todos", (HttpContext ctx) =>
{
    var sub = ctx.User.FindFirst("sub")?.Value;

    // In a real app, you'd query a database filtered by sub
    return new[]
    {
        new { id = 1, title = "Set up Authagonal", done = true, ownerId = sub },
        new { id = 2, title = "Integrate OIDC into my app", done = true, ownerId = sub },
        new { id = 3, title = "Ship to production", done = false, ownerId = sub }
    };
}).RequireAuthorization();

app.Run();
