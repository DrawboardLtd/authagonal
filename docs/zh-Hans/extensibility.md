---
layout: default
title: 扩展性
locale: zh-Hans
---

# 扩展性

Authagonal 可以作为库托管在您自己的 ASP.NET Core 项目中，完全控制服务实现。

## 扩展方法

三个方法将 Authagonal 组合到任何 ASP.NET Core 应用中：

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

### 多租户托管

对于多租户部署，请改用 `AddAuthagonalCore()`。它注册端点、中间件和核心服务，但跳过存储和后台服务；您需要按租户提供这些。签名密钥管理默认使用 `Authagonal.Protocol` 的 `ProtocolKeyManager` 单例，若宿主在调用 `AddAuthagonalCore()` 之前注册了自己的 `IKeyManager`，则保留宿主的注册：

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` 和存储接口（`IClientStore`、`IScimTokenStore` 等）在请求时从 `HttpContext.RequestServices` 解析，因此作用域（scoped）注册可以正确地实现按租户隔离。

## 覆盖服务

在调用 `AddAuthagonal()` **之前**注册您的自定义实现。Authagonal 内部使用 `TryAdd`，因此您的注册具有优先权：

```csharp
// Custom implementations, registered first so they won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

`IAuthHook` 是个特例：它是一条多注册管道。您可以注册任意数量的钩子（任何生命周期，包括 `AddScoped`），它们全部按注册顺序运行。只有当 `AddAuthagonal()` / `AddAuthagonalCore()` 运行时还没有任何钩子被注册，才会添加空操作的 `NullAuthHook`，因此请始终先注册您的钩子。

### 扩展点

| 接口 | 默认实现 | 用途 |
|---|---|---|
| `IAuthHook` | `NullAuthHook`（空操作，仅在未注册任何钩子时添加） | 认证事件的生命周期钩子：审计日志、自定义验证、Webhook。可注册多个钩子；全部按顺序运行 |
| `IEmailService` | `NullEmailService`（空操作），或在配置了 `Email:ResendApiKey` 时使用内置的 Resend 发送器 | 用于验证、密码重置和"账户已存在"通知的邮件发送 |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator`（作用域） | 将用户预配到下游应用 |
| `ISecretProvider` | `PlaintextSecretProvider`，或在配置了 `SecretProvider:VaultUri` 时使用内置的 `KeyVaultSecretProvider` | 可逆的机密存储（Key Vault、AWS Secrets Manager、Vault Transit 等） |
| `ITenantContext` | `DefaultTenantContext`（从 `IConfiguration` 读取） | 多租户部署的租户解析 |
| `IKeyManager` | `ProtocolKeyManager`（单例，来自 `Authagonal.Protocol`） | 签名密钥管理；覆盖以实现按租户密钥隔离 |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider`（作用域） | 解析可用的预配应用；覆盖以实现动态或按租户的应用解析 |
| `IAuditLogger` | `NullAuditLogger`（空操作） | 配置变更和安全相关事件的审计跟踪 |

另有三个接缝位于**存储层**而非依赖注入中：`IFieldCipher`、`IIndexTokenizer` 和 `IChangeWriter`（均在 `Authagonal.Core.Services` 中）。存储提供者将它们作为可选的构造函数参数接受；参见下文各自的章节。

## IAuthHook

`IAuthHook` 接口提供认证生命周期的钩子。位于关键路径上的方法（认证、用户创建、令牌颁发）可以抛出异常来中止操作；较新的方法则是事后通知。可以注册多个 `IAuthHook` 实现，它们全部按注册顺序运行。

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

### 参数

| 方法 | 说明及 `method` / `via` 值 |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"passkey"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnUserUpdatedAsync` | `"admin"`, `"self"`（宿主可以传入自己的值，例如 SCIM 来源） |
| `OnUserDeletedAsync` | `"admin"`；仅为通知，记录可能已不可读 |
| `OnLoginFailedAsync` | `"user_not_found"`, `"invalid_password"` 等 |
| `OnTokenIssuedAsync` | 授权类型：`"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | 在密码验证之后调用；返回用户的有效 MFA 策略。默认：原样返回 `clientPolicy`。 |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |
| `OnMfaVerifyFailedAsync` | 与 `OnMfaVerifiedAsync` 相同的方法值。仅在第一因素凭据验证有效之后触发，因此连续爆发是强烈的 MFA 绕过尝试信号（区别于密码阶段的 `OnLoginFailedAsync`） |
| `OnEmailConfirmedAsync` | 用户通过验证链接确认了邮箱；已持久化 |
| `OnMfaEnrolledAsync` | `"totp"`, `"webauthn"`；凭据已生效 |
| `OnMfaCredentialRemovedAsync` | `"totp"`, `"webauthn"`, `"recoverycode"`；当移除后不再有任何主因素时，`mfaDisabled` 为 true |
| `OnRecoveryCodesRegeneratedAsync` | 之前的恢复码集合已作废 |
| `OnPasswordChangedAsync` | 例如 `"reset"`；变更已持久化，且现有会话已失效 |

### 示例：审计日志记录器

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

### 示例：域名限制

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

`ISecretProvider`（位于 `Authagonal.Core.Services`）是存储型机密（例如 SSO 客户端机密、SMTP 密码和 TOTP 种子）的可逆加密接缝。`ProtectAsync` 将明文转换为供存储持久化的引用；`ResolveAsync` 将引用还原回明文。默认的 `PlaintextSecretProvider` 按原样存储值（引用就是值本身）。

```csharp
public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
```

设置 `SecretProvider:VaultUri` 会自动接入内置的 `KeyVaultSecretProvider`（通过 `DefaultAzureCredential` 访问 Azure Key Vault）。若要使用其他方案，请在 `AddAuthagonal()` 之前注册您自己的实现。

## PII 字段加密：IFieldCipher

`IFieldCipher` 对单个用户 PII 字段值（电话、公司、自定义属性，以及资料行上的邮箱和姓名）进行静态加密。它是存储层的接缝：存储提供者将其作为可选的构造函数参数接受（例如 `TableUserStore`），缺省时应用直通的 `NullFieldCipher`，因此加密是严格可选的，未配置的宿主继续存储明文。

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

有两个契约要点。`ProtectAsync` 必须返回可自我描述的密文令牌（例如 Vault Transit 的 `vault:v{n}:...`），而 `ResolveAsync` 必须将它无法识别为自身密文的值原样透传。正是这条直通规则让加密能够在现有行上惰性推进：读取尚未迁移的行会返回遗留的明文，而下一次写入会重新加密保护它。

## 盲索引搜索：IIndexTokenizer

`IIndexTokenizer` 让加密后的字段仍可搜索。它将规范化后的明文值转换为确定性的、可安全用作表键的盲索引令牌，通常是密钥保存在数据库之外的带密钥 HMAC。确定性意味着等值查询仍然有效（"email = x" 变为 "token = HMAC(x)"），而数据库转储既无法重新计算也无法逆向还原令牌。前缀搜索则通过对值的每个前缀分别令牌化来叠加实现，因为带密钥的 HMAC 会破坏排序和范围扫描。

```csharp
public interface IIndexTokenizer
{
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values,
        CancellationToken ct = default);
}
```

与 `IFieldCipher` 一样，它是带直通默认实现（`NullIndexTokenizer`）的可选存储构造函数参数，因此在您选择启用之前，索引行仍以明文为键。返回的令牌必须可以安全地用作 Azure Table 的 PartitionKey/RowKey 值（不含 `/ \ # ?` 或控制字符）。

## 变更日志捕获：IChangeWriter

`IChangeWriter`（在 0.6.0 中由 `ITombstoneWriter` 更名而来）将每个变更行的键记录到专用的变更日志表，这样增量备份无需扫描实时表中未建索引的 `Timestamp` 列即可找到变更内容。删除操作对每个表都会捕获（实时行扫描无法看到已消失的行）；更新插入（upsert）则只对备份从日志读取而非扫描的那些表捕获。内置实现：`TableChangeWriter`（Azure Table Storage）和 `DynamoChangeWriter`（DynamoDB）。

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

对实现者和调用者的顺序契约：先写入删除墓碑，**再**删除数据行。若以相反顺序发生崩溃，这次删除将从之后的每一次备份中丢失，因为删除是重新扫描无法自愈的唯一一类变更。相反方向的崩溃是安全的：对该键的后续写入会盖上更新的时间戳，而合并/恢复会保留在墓碑之后写入的行。

## 自定义端点

在 Authagonal 端点旁添加您自己的端点：

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## HashiCorp Vault Transit 集成

Authagonal 可以将 JWT 签名委托给 HashiCorp Vault 的 Transit 机密引擎。私钥从不离开 Vault；只有签名操作是远程的。公钥在本地缓存用于验证。

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

`VaultTransitClient` 提供以下操作：

| 方法 | 描述 |
|---|---|
| `SignAsync(keyName, data)` | 使用 Vault Transit 密钥对数据签名 |
| `VerifyAsync(keyName, data, signature)` | 通过 Transit verify 端点验证 JWS 编排格式的签名 |
| `EncryptAsync` / `DecryptAsync`（+ `EncryptBatchAsync` / `DecryptBatchAsync`） | 在 `aes256-gcm96` 密钥下进行对称加密；返回应原样存储的 `vault:v{n}:...` 令牌 |
| `HmacAsync` / `HmacBatchAsync` | 在 `hmac` 密钥下计算带密钥的 HMAC（盲索引令牌） |
| `CreateKeyAsync(keyName, type)` | 创建新的 Transit 密钥（默认：`ecdsa-p256`） |
| `EnsureKeyTypeAsync(keyName, type)` | 幂等地确保密钥以期望的类型存在（类型不匹配时重建；Transit 密钥无法就地更改类型） |
| `RotateKeyAsync(keyName)` | 将密钥轮换到新版本 |
| `DeleteKeyAsync(keyName)` | 删除密钥（先启用 `deletion_allowed`） |
| `ReadKeyAsync(keyName)` | 读取密钥元数据、版本和公钥 |
| `KeyExistsAsync(keyName)` | 检查密钥是否存在 |

`VaultTransitCryptoProvider` 与 .NET 的 `JsonWebTokenHandler` 集成，使 JWT 签名透明地使用 Vault。`VaultTransitSecurityKey` 和 `VaultTransitSignatureProvider` 负责底层集成。

## 邮件

配置了 `Email:ResendApiKey` 后，内置的 Resend 发送器会自动启用（同时请设置 `Email:SenderEmail`）。在没有任何 `IEmailService` 时，邮件会经由 `NullEmailService` 被丢弃，而由于"邮箱须先确认"的登录门槛默认开启，自助注册的用户将永远无法登录；`UseAuthagonal()` 在这种状态下会在启动时记录一条醒目的警告。

若要使用其他提供商，请在 `AddAuthagonal()` 之前注册您自己的 `IEmailService`：

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

`IEmailService` 还声明了 `SendAccountExistsEmailAsync` 方法（当有人尝试注册一个已注册的邮箱时发送，使注册响应对账户枚举保持中立）。它带有默认的空操作实现，因此现有实现可以继续编译。

## 另请参阅

- [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server)：完整的可运行示例
- [demos/sample-app/](https://github.com/authagonal/authagonal/tree/master/demos/sample-app)：客户端应用示例
