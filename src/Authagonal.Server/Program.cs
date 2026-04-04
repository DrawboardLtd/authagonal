using Authagonal.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthagonal(builder.Configuration);

var app = builder.Build();

app.UseAuthagonal();
app.MapAuthagonalEndpoints();
app.MapFallbackToFile("index.html");

app.Run();
