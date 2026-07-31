---
layout: default
title: Extensibility
---

# Extensibility

Authagonal can be hosted as a library in your own ASP.NET Core project, with full control over service implementations.

## Extension Methods

Three methods compose Authagonal into any ASP.NET Core app:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

### Multi-Tenant Hosting

For multi-tenant deployments, use `AddAuthagonalCore()` instead. It registers endpoints, middleware, and core services but skips storage and background services; you provide those per-tenant. Signing-key management defaults to `Authagonal.Protocol`'s `ProtocolKeyManager` singleton, and a host that registers its own `IKeyManager` before `AddAuthagonalCore()` keeps it:

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` and store interfaces (`IClientStore`, `IScimTokenStore`, etc.) are resolved from `HttpContext.RequestServices` at request time, so scoped registrations work correctly for per-tenant isolation.

### Embedding `Authagonal.Protocol` alone

A host that wants only the OIDC protocol surface — its own authentication, its own pipeline, drop-in `/connect/*` endpoints — calls `AddAuthagonalProtocol()` + `MapAuthagonalProtocolEndpoints()` without any of `Authagonal.Server`.

`/connect/authorize`, `/connect/token`, `/connect/userinfo` and `/connect/par` refuse plaintext http in that shape too, per RFC 6749 §3.1/§3.2. Because the package is mapped into a pipeline it does not own, the requirement rides on the endpoints as a filter rather than as middleware, so it holds however you compose your pipeline and whether you map the whole surface or one endpoint at a time. Two consequences worth knowing before you upgrade:

- **Behind a TLS-terminating proxy, call `UseForwardedHeaders`.** The filter reads the scheme after routing, so a forwarded `X-Forwarded-Proto: https` satisfies it. Without that middleware your host sees plaintext — which also means your cookies are not being marked `Secure` and your generated absolute URLs are wrong, so this is worth fixing rather than working around.
- **A host that genuinely serves the protocol surface over http sets the opt-in**, the same way the server does:

```csharp
builder.Services.AddAuthagonalProtocol(o =>
{
    o.AuthenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    o.AllowInsecureHttp = builder.Environment.IsDevelopment();   // never in production
});
```

Discovery and JWKS are deliberately not gated: they are public metadata, and a client that cannot read them cannot learn it needs https in the first place.

When you use `AddAuthagonal()` (the full server) you do not set this separately — `Auth:AllowInsecureHttp` is propagated into the protocol options for you, so one switch governs the whole surface.

## Overriding Services

Register your custom implementations **before** calling `AddAuthagonal()`. Authagonal uses `TryAdd` internally, so your registrations take precedence:

```csharp
// Custom implementations, registered first so they won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

`IAuthHook` is special: it is a multi-registration pipeline. Register as many hooks as you like (any lifetime, `AddScoped` included) and all of them run in registration order. The no-op `NullAuthHook` is added only when no hook has been registered by the time `AddAuthagonal()` / `AddAuthagonalCore()` runs, so always register your hooks first.

### Extensibility Points

| Interface | Default | Purpose |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (no-op, added only when no hook is registered) | Lifecycle hooks for auth events: audit logging, custom validation, webhooks. Multiple hooks can be registered; all run in order |
| `IEmailService` | `NullEmailService` (no-op), or the built-in Resend sender when `Email:ResendApiKey` is configured | Email delivery for verification, password reset, and account-exists notices |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` (scoped) | User provisioning into downstream apps |
| `ISecretProvider` | `PlaintextSecretProvider`, or the built-in `KeyVaultSecretProvider` when `SecretProvider:VaultUri` is configured | Reversible secret storage (Key Vault, AWS Secrets Manager, Vault Transit, etc.) |
| `ITenantContext` | `DefaultTenantContext` (reads from `IConfiguration`) | Tenant resolution for multi-tenant deployments |
| `IKeyManager` | `ProtocolKeyManager` (singleton, from `Authagonal.Protocol`) | Signing key management; override for per-tenant key isolation |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider` (scoped) | Resolves available provisioning apps; override for dynamic or per-tenant app resolution |
| `IAuditLogger` | `NullAuditLogger` (no-op) | Audit trail for configuration changes and security-relevant events |

Three further seams live at the **store level** rather than in DI: `IFieldCipher`, `IIndexTokenizer`, and `IChangeWriter` (all in `Authagonal.Core.Services`). The storage providers accept them as optional constructor parameters; see their sections below.

## IAuthHook

The `IAuthHook` interface provides hooks into the authentication lifecycle. Methods on the critical path (authentication, user creation, token issuance) can throw an exception to abort the operation; the newer methods are after-the-fact notifications. Multiple `IAuthHook` implementations can be registered and all run in registration order.

```csharp
public interface IAuthHook
{
    // Core lifecycle: implement these
    Task OnUserAuthenticatedAsync(string userId, string email, string method,
        string? clientId = null, CancellationToken ct = default);
    Task OnUserCreatedAsync(string userId, string email, string createdVia,
        CancellationToken ct = default);
    Task OnLoginFailedAsync(string email, string reason,
        CancellationToken ct = default);
    Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType,
        CancellationToken ct = default);
    Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email,
        MfaPolicy clientPolicy, string clientId, CancellationToken ct = default);
    Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default);
    Task OnUserUpdatedAsync(string userId, string email, string updatedVia,
        CancellationToken ct = default);
    Task OnUserDeletedAsync(string userId, string email, string deletedVia,
        CancellationToken ct = default);

    // Additive notifications: default no-op implementations, so existing
    // hooks keep compiling as the interface grows
    Task OnMfaVerifyFailedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnEmailConfirmedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaEnrolledAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaCredentialRemovedAsync(string userId, string email, string mfaMethod,
        bool mfaDisabled, CancellationToken ct = default) => Task.CompletedTask;
    Task OnRecoveryCodesRegeneratedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnPasswordChangedAsync(string userId, string email, string changedVia,
        CancellationToken ct = default) => Task.CompletedTask;
}
```

### Parameters

| Method | Notes and `method` / `via` values |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"passkey"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnUserUpdatedAsync` | `"admin"`, `"self"` (hosts may pass their own, e.g. a SCIM origin) |
| `OnUserDeletedAsync` | `"admin"`; notification only, the record may no longer be readable |
| `OnLoginFailedAsync` | `"user_not_found"`, `"invalid_password"`, etc. |
| `OnTokenIssuedAsync` | Grant types: `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Called after password verification; returns the effective MFA policy for the user. Default: return `clientPolicy` unchanged. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |
| `OnMfaVerifyFailedAsync` | Same methods as `OnMfaVerifiedAsync`. Fires only after valid first-factor credentials, so bursts are a strong MFA-bypass-attempt signal (distinct from `OnLoginFailedAsync`, the password stage) |
| `OnEmailConfirmedAsync` | User confirmed their email via the verification link; already persisted |
| `OnMfaEnrolledAsync` | `"totp"`, `"webauthn"`; the credential is already active |
| `OnMfaCredentialRemovedAsync` | `"totp"`, `"webauthn"`, `"recoverycode"`; `mfaDisabled` is true when the removal left no primary factor |
| `OnRecoveryCodesRegeneratedAsync` | The previous recovery-code set is invalidated |
| `OnPasswordChangedAsync` | e.g. `"reset"`; the change is persisted and existing sessions invalidated |

### Example: Audit Logger

```csharp
public sealed class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email,
        string createdVia, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] User created: {Email} via {Via}", email, createdVia);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct)
    {
        logger.LogWarning("[AUDIT] Login failed: {Email} ({Reason})", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Token issued: {ClientId} ({GrantType})",
            clientId, grantType);
        return Task.CompletedTask;
    }

    // ... remaining required methods return Task.CompletedTask
}
```

### Example: Domain Restriction

```csharp
public sealed class DomainRestrictionHook : IAuthHook
{
    private static readonly HashSet<string> BlockedDomains = ["competitor.com"];

    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        var domain = email.Split('@').Last();
        if (BlockedDomains.Contains(domain))
            throw new InvalidOperationException($"Domain {domain} is not allowed");

        return Task.CompletedTask;
    }

    // ... other methods return Task.CompletedTask
}
```

## ISecretProvider

`ISecretProvider` (in `Authagonal.Core.Services`) is the reversible-encryption seam for stored secrets such as SSO client secrets, SMTP passwords, and TOTP seeds. `ProtectAsync` turns a plaintext into a reference the store persists; `ResolveAsync` turns the reference back into the plaintext. The default `PlaintextSecretProvider` stores values as-is (the reference IS the value).

```csharp
public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
```

Setting `SecretProvider:VaultUri` auto-wires the built-in `KeyVaultSecretProvider` (Azure Key Vault via `DefaultAzureCredential`). For anything else, register your own implementation before `AddAuthagonal()`.

## PII Field Encryption: IFieldCipher

`IFieldCipher` encrypts individual user PII field values (phone, company, custom attributes, email and names on the profile row) at rest. It is a store-level seam: the storage providers take it as an optional constructor parameter (e.g. `TableUserStore`), and when absent the passthrough `NullFieldCipher` applies, so encryption is strictly opt-in and unconfigured hosts keep storing plaintext.

```csharp
public interface IFieldCipher
{
    Task<string> ProtectAsync(string plaintext, CancellationToken ct = default);
    Task<string> ResolveAsync(string stored, CancellationToken ct = default);

    // Batch variants have default loop implementations; override for backends
    // with a one-round-trip batch primitive (e.g. Vault Transit)
    Task<IReadOnlyList<string>> ProtectManyAsync(IReadOnlyList<string> plaintexts,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResolveManyAsync(IReadOnlyList<string> stored,
        CancellationToken ct = default);
}
```

Two contract points matter. `ProtectAsync` must return a self-describing ciphertext token (e.g. Vault Transit's `vault:v{n}:...`), and `ResolveAsync` must pass a value it does not recognize as its own ciphertext through unchanged. The passthrough rule is what lets encryption roll out lazily over existing rows: a read of an un-migrated row returns the legacy plaintext, and the next write re-protects it.

## Blind-Index Search: IIndexTokenizer

`IIndexTokenizer` keeps encrypted fields searchable. It turns a normalized plaintext value into a deterministic, table-key-safe blind-index token, typically a keyed HMAC where the key lives outside the database. Determinism means an equality lookup still works ("email = x" becomes "token = HMAC(x)"), while a database dump can neither recompute nor reverse a token. Prefix search is layered on top by tokenizing each prefix of a value separately, since a keyed HMAC destroys ordering and range scans.

> **What a dump still reveals.** "Neither recompute nor reverse" is true of a single token and not of
> the index as a whole. Three residues survive, and they are worth knowing before you rely on this:
>
>   *(Fixed.)* ~~**Structure.** The prefix index writes one row per prefix, so a record's row count
>   equals the length of the indexed field.~~ Every indexed value now writes a fixed number of rows,
>   padded with decoys that no query can produce and that a dump cannot tell from real prefixes.
> - **Equality and frequency.** Tokens are deterministic by construction, which is what makes lookup
>   work, so a dump shows which records share a value and how common each value is. The domain index
>   buckets your population by employer, which often identifies people without recovering an address.
> - **Chosen plaintext.** An attacker who can both read the store *and* cause values to be indexed
>   (register an account, be provisioned over SCIM) can submit a candidate and look for its token.
>   That recovers any guessable value — common domains, common first names — no matter where the key
>   lives, because the oracle is the write path rather than the cipher.
>
> Tokenization defends against the case it was built for: someone holding a dump and nothing else,
> trying to read addresses. The two residues that remain are exactly what a registration oracle gives
> away anyway. If they are unacceptable, leave the prefix and domain index tables unconfigured —
> exact-match lookup carries neither — rather than assuming the HMAC covers them.

```csharp
public interface IIndexTokenizer
{
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values,
        CancellationToken ct = default);
}
```

Like `IFieldCipher`, it is an optional store constructor parameter with a passthrough default (`NullIndexTokenizer`), so index rows stay keyed on plaintext until you opt in. Returned tokens must be safe as Azure Table PartitionKey/RowKey values (none of `/ \ # ?` or control characters).

## Change-Log Capture: IChangeWriter

`IChangeWriter` (renamed from `ITombstoneWriter` in 0.6.0) records the key of every changed row to a dedicated change-log table, so incremental backups can find what changed without scanning the unindexed `Timestamp` column of the live tables. Deletes are captured for every table (a live-row scan cannot see a row that is gone); upserts are captured for the tables the backup reads from the log instead of scanning. Built-in implementations: `TableChangeWriter` (Azure Table Storage), `DynamoChangeWriter` (DynamoDB), and `SqlChangeWriter` (PostgreSQL / SQLite).

```csharp
public interface IChangeWriter
{
    // Deletes
    Task WriteAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);

    // Upserts
    Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteUpsertBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);
}
```

Ordering contract for implementors and callers: write the delete tombstone BEFORE deleting the data row. A crash in the other order loses the delete from every future backup, since deletes are the one mutation class a re-scan cannot self-heal. The reverse crash is safe: a later write to the key re-stamps a newer timestamp, and merge/restore keep rows written after the tombstone.

## Custom Endpoints

Add your own endpoints alongside Authagonal's:

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## HashiCorp Vault Transit Integration

Authagonal can delegate JWT signing to HashiCorp Vault's Transit secrets engine. Private keys never leave Vault; only the signing operation is remote. Public keys are cached locally for verification.

```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure Vault Transit HTTP client
builder.Services.AddHttpClient("Vault", client =>
{
    client.BaseAddress = new Uri("https://vault.example.com");
    client.DefaultRequestHeaders.Add("X-Vault-Token", "hvs.xxx");
});

// Register Vault Transit services
builder.Services.AddSingleton<VaultTransitClient>();
builder.Services.AddSingleton<VaultTransitCryptoProvider>();

builder.Services.AddAuthagonal(builder.Configuration);
```

The `VaultTransitClient` provides these operations:

| Method | Description |
|---|---|
| `SignAsync(keyName, data)` | Sign data using a Vault Transit key |
| `VerifyAsync(keyName, data, signature)` | Verify a JWS-marshaled signature via the Transit verify endpoint |
| `EncryptAsync` / `DecryptAsync` (+ `EncryptBatchAsync` / `DecryptBatchAsync`) | Symmetric encryption under an `aes256-gcm96` key; returns `vault:v{n}:...` tokens to store verbatim |
| `HmacAsync` / `HmacBatchAsync` | Keyed HMAC under an `hmac` key (blind-index tokens) |
| `CreateKeyAsync(keyName, type)` | Create a new Transit key (default: `ecdsa-p256`) |
| `EnsureKeyTypeAsync(keyName, type)` | Idempotently ensure a key exists with the desired type (recreates on type mismatch; Transit keys cannot be retyped in place) |
| `RotateKeyAsync(keyName)` | Rotate a key to a new version |
| `DeleteKeyAsync(keyName)` | Delete a key (enables `deletion_allowed` first) |
| `ReadKeyAsync(keyName)` | Read key metadata, versions, and public keys |
| `KeyExistsAsync(keyName)` | Check if a key exists |

The `VaultTransitCryptoProvider` integrates with .NET's `JsonWebTokenHandler` so that JWT signing transparently uses Vault. The `VaultTransitSecurityKey` and `VaultTransitSignatureProvider` handle the low-level integration.

## Email

The built-in Resend sender activates automatically when `Email:ResendApiKey` is configured (set `Email:SenderEmail` too). Without any `IEmailService`, mail is discarded via `NullEmailService`, and because the confirmed-email login gate defaults to on, self-registered users could never log in; `UseAuthagonal()` logs a loud startup warning in that state.

To use another provider, register your own `IEmailService` before `AddAuthagonal()`:

```csharp
public sealed class SmtpEmailService(SmtpClient smtp) : IEmailService
{
    public async Task SendVerificationEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Verify your email", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Reset your password", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }
}
```

`IEmailService` also declares `SendAccountExistsEmailAsync` (sent when someone tries to register an already-registered email, keeping the registration response neutral against account enumeration). It has a default no-op implementation, so existing implementations keep compiling.

## See Also

- [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server): complete working example
- [demos/sample-app/](https://github.com/authagonal/authagonal/tree/master/demos/sample-app): client app example
