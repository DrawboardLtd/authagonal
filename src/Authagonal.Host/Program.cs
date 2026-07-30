using Authagonal.Server;
using Authagonal.SqlProvider;

var builder = WebApplication.CreateBuilder(args);

// Storage:Provider = postgres | sqlite registers the self-hosted SQL stores; anything else (including
// the default) is a no-op and AddAuthagonal falls through to its Azure Table wiring. This lives in the
// host, not in AddAuthagonal, so that referencing Authagonal.Server as a library never drags in the
// PostgreSQL and SQLite drivers.
builder.Services.AddAuthagonalSqlStorageFromConfiguration(builder.Configuration);

builder.Services.AddAuthagonal(builder.Configuration);

var app = builder.Build();

app.UseAuthagonal();
app.MapAuthagonalEndpoints();
app.MapFallbackToFile("index.html");

app.Run();
