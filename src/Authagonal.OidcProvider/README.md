# Authagonal.OidcProvider

A lightweight OIDC provider built on [OpenIddict](https://documentation.openiddict.com/). Expose
`authorization_code` + PKCE (with optional `refresh_token`) endpoints while keeping your existing
user identity and cookie auth untouched.

The only thing you implement is `IOidcSubjectResolver`:

```csharp
public sealed class MySubjectResolver : IOidcSubjectResolver
{
    public Task<OidcSubject?> ResolveAsync(
        ClaimsPrincipal principal,
        OidcSubjectResolutionContext ctx,
        CancellationToken ct) => Task.FromResult<OidcSubject?>(new OidcSubject
        {
            SubjectId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!,
            Email = principal.FindFirstValue(ClaimTypes.Email),
            Name = principal.FindFirstValue(ClaimTypes.Name),
        });
}
```

## Getting started

```csharp
builder.Services
    .AddAuthagonalOidcProvider(o =>
    {
        o.Issuer = "https://auth.example.com";
        o.AuthenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.Clients.Add(new OidcClientDescriptor
        {
            ClientId = "spa",
            RedirectUris = { "https://app.example.com/callback" },
            AllowedScopes = { "offline_access" },
        });
    })
    .AddCore(core =>
    {
        core.UseEntityFrameworkCore().UseDbContext<AppDbContext>();
    })
    .AddValidation();

builder.Services.AddScoped<IOidcSubjectResolver, MySubjectResolver>();

var app = builder.Build();
app.MapAuthagonalOidcEndpoints();
app.Run();
```

You pick the OpenIddict storage (EF Core, MongoDB, in-memory, etc.) — the provider library
stays out of your persistence story.
