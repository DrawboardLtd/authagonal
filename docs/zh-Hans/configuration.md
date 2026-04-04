---
layout: default
title: 配置
locale: zh-Hans
---

# 配置

Authagonal 通过 `appsettings.json` 或环境变量进行配置。环境变量使用 `__` 作为节分隔符（例如 `Storage__ConnectionString`）。

## 必需设置

| 设置 | 环境变量 | 描述 |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Azure Table Storage 连接字符串 |
| `Issuer` | `Issuer` | 此服务器的公共基础 URL（例如 `https://auth.example.com`） |

## 认证

| 设置 | 默认值 | 描述 |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Cookie 会话生命周期（滑动过期） |
| `Auth:MaxFailedAttempts` | `5` | 账户锁定前允许的登录失败次数 |
| `Auth:LockoutDurationMinutes` | `10` | 达到最大失败次数后的账户锁定时长 |
| `Auth:MaxRegistrationsPerIp` | `5` | 时间窗口内每个 IP 地址的最大注册数 |
| `Auth:RegistrationWindowMinutes` | `60` | 注册速率限制时间窗口 |
| `Auth:EmailVerificationExpiryHours` | `24` | 邮箱验证链接有效期 |
| `Auth:PasswordResetExpiryMinutes` | `60` | 密码重置链接有效期 |
| `Auth:MfaChallengeExpiryMinutes` | `5` | MFA 验证令牌有效期 |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | MFA 设置令牌有效期（用于强制注册） |
| `Auth:Pbkdf2Iterations` | `100000` | 密码哈希的 PBKDF2 迭代次数 |
| `Auth:RefreshTokenReuseGraceSeconds` | `60` | 并发刷新令牌重用的宽限窗口 |
| `Auth:SigningKeyLifetimeDays` | `90` | RSA 签名密钥在自动轮换前的有效期 |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | 从存储重新加载签名密钥的频率 |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Cookie 安全标记检查间隔 |

## 缓存与超时

| 设置 | 默认值 | 描述 |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | CORS 允许来源的缓存时长 |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | OIDC 发现文档缓存时长 |
| `Cache:SamlMetadataCacheMinutes` | `60` | SAML IdP 元数据缓存时长 |
| `Cache:OidcStateLifetimeMinutes` | `10` | OIDC 授权 state 参数有效期 |
| `Cache:SamlReplayLifetimeMinutes` | `10` | SAML AuthnRequest ID 有效期（防重放） |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Table Storage 健康检查超时 |

## 后台服务

| 设置 | 默认值 | 描述 |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | 首次过期令牌清理前的初始延迟 |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | 过期令牌清理间隔 |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | 首次授权协调前的初始延迟 |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | 授权协调间隔 |

## 客户端

客户端在 `Clients` 数组中定义，并在启动时播种。每个客户端可以包含：

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "ClientName": "My Application",
      "ClientSecretHashes": ["sha256-hash-here"],
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email", "custom-scope"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "AlwaysIncludeUserClaimsInIdToken": false,
      "AccessTokenLifetimeSeconds": 1800,
      "IdentityTokenLifetimeSeconds": 300,
      "AuthorizationCodeLifetimeSeconds": 300,
      "AbsoluteRefreshTokenLifetimeSeconds": 2592000,
      "SlidingRefreshTokenLifetimeSeconds": 1296000,
      "RefreshTokenUsage": "OneTime",
      "MfaPolicy": "Enabled",
      "ProvisioningApps": ["my-backend"]
    }
  ]
}
```

### 授权类型

| 授权类型 | 使用场景 |
|---|---|
| `authorization_code` | 交互式用户登录（Web 应用、SPA、移动端） |
| `client_credentials` | 服务间通信 |
| `refresh_token` | 令牌续期（需要 `AllowOfflineAccess: true`） |

### 刷新令牌用法

| 值 | 行为 |
|---|---|
| `OneTime`（默认） | 每次刷新都会签发新的刷新令牌。旧令牌失效，但有 60 秒的宽限窗口以支持并发请求。宽限窗口过后的重放将撤销该用户+客户端的所有令牌。 |
| `ReUse` | 同一刷新令牌在过期前可重复使用。 |

### 预配应用

`ProvisioningApps` 数组引用在 `ProvisioningApps` 配置节中定义的应用 ID。当用户通过此客户端授权时，他们将通过 TCC 被预配到这些应用中。详情请参阅[预配](provisioning)。

## 预配应用

定义用户应被预配到的下游应用程序：

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-api-key"
    },
    "analytics": {
      "CallbackUrl": "https://analytics.example.com/provisioning",
      "ApiKey": "another-key"
    }
  }
}
```

完整的 TCC 协议规范请参阅[预配](provisioning)。

## MFA 策略

多因素认证通过客户端的 `MfaPolicy` 属性按客户端强制执行：

| 值 | 行为 |
|---|---|
| `Disabled`（默认） | 不进行 MFA 验证，即使用户已注册 MFA |
| `Enabled` | 对已注册 MFA 的用户进行验证；不强制注册 |
| `Required` | 对已注册用户进行验证；强制未注册 MFA 的用户进行注册 |

```json
{
  "Clients": [
    {
      "ClientId": "secure-app",
      "MfaPolicy": "Required"
    }
  ]
}
```

当 `MfaPolicy` 为 `Required` 且用户尚未注册 MFA 时，登录返回 `{ mfaSetupRequired: true, setupToken: "..." }`。设置令牌通过 `X-MFA-Setup-Token` 请求头对用户进行认证，以便在获得 Cookie 会话之前完成 MFA 注册。

联合登录（SAML/OIDC）跳过 MFA -- 由外部身份提供者处理。

### IAuthHook 覆盖

`IAuthHook.ResolveMfaPolicyAsync` 方法可以按用户覆盖客户端策略：

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // 无论客户端设置如何，强制管理员用户使用 MFA
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    return Task.FromResult(clientPolicy);
}
```

## 密码策略

自定义密码强度要求：

```json
{
  "PasswordPolicy": {
    "MinLength": 10,
    "MinUniqueChars": 3,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": false
  }
}
```

| 属性 | 默认值 | 描述 |
|---|---|---|
| `MinLength` | `8` | 最小密码长度 |
| `MinUniqueChars` | `2` | 最少不同字符数 |
| `RequireUppercase` | `true` | 要求至少一个大写字母 |
| `RequireLowercase` | `true` | 要求至少一个小写字母 |
| `RequireDigit` | `true` | 要求至少一个数字 |
| `RequireSpecialChar` | `true` | 要求至少一个非字母数字字符 |

该策略在密码重置和管理员用户注册时强制执行。登录界面从 `GET /api/auth/password-policy` 获取当前策略，以动态显示要求。

## SAML 提供者

在配置中定义 SAML 身份提供者。这些在启动时播种：

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com", "example.org"]
    }
  ]
}
```

| 属性 | 必需 | 描述 |
|---|---|---|
| `ConnectionId` | 是 | 稳定标识符（用于 `/saml/{connectionId}/login` 等 URL） |
| `ConnectionName` | 否 | 显示名称（默认为 ConnectionId） |
| `EntityId` | 是 | SAML 服务提供者实体 ID |
| `MetadataLocation` | 是 | IdP 的 SAML 元数据 XML 的 URL |
| `AllowedDomains` | 否 | 通过 SSO 路由到此提供者的电子邮件域 |

## OIDC 提供者

在配置中定义 OIDC 身份提供者。这些在启动时播种：

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

| 属性 | 必需 | 描述 |
|---|---|---|
| `ConnectionId` | 是 | 稳定标识符（用于 `/oidc/{connectionId}/login` 等 URL） |
| `ConnectionName` | 否 | 显示名称（默认为 ConnectionId） |
| `MetadataLocation` | 是 | IdP 的 OpenID Connect 发现文档的 URL |
| `ClientId` | 是 | 在 IdP 注册的 OAuth2 客户端 ID |
| `ClientSecret` | 是 | OAuth2 客户端密钥（启动时通过 `ISecretProvider` 保护） |
| `RedirectUrl` | 是 | 在 IdP 注册的 OAuth2 重定向 URI |
| `AllowedDomains` | 否 | 通过 SSO 路由到此提供者的电子邮件域 |

> **注意：** 提供者也可以通过[管理 API](admin-api) 在运行时管理。配置播种的提供者在每次启动时执行 upsert，因此配置更改在重启后生效。

## 密钥提供者

客户端密钥和 OIDC 提供者密钥可以选择存储在 Azure Key Vault 中：

| 设置 | 描述 |
|---|---|
| `SecretProvider:VaultUri` | Key Vault URI（例如 `https://my-vault.vault.azure.net/`）。如未设置，密钥将被视为纯文本。 |

配置后，看起来像 Key Vault 引用的密钥值会在运行时解析。使用 `DefaultAzureCredential` 进行认证。

## 电子邮件

默认情况下，Authagonal 使用空操作邮件服务，静默丢弃所有邮件。要启用邮件发送，请在调用 `AddAuthagonal()` 之前注册 `IEmailService` 实现。内置的 `EmailService` 使用 SendGrid。

| 设置 | 描述 |
|---|---|
| `Email:SendGridApiKey` | 用于发送邮件的 SendGrid API 密钥 |
| `Email:FromAddress` | 发件人电子邮件地址 |
| `Email:FromName` | 发件人显示名称 |
| `Email:VerificationTemplateId` | 用于邮箱验证的 SendGrid 动态模板 ID |
| `Email:PasswordResetTemplateId` | 用于密码重置的 SendGrid 动态模板 ID |

发送到 `@example.com` 地址的邮件会被静默跳过（便于测试）。

## 集群

Authagonal 实例自动组成集群以共享速率限制状态。集群功能默认启用，无需任何配置。

| 设置 | 环境变量 | 默认值 | 描述 |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | 集群主开关。设为 `false` 则仅使用本地速率限制。 |
| `Cluster:MulticastGroup` | `Cluster__MulticastGroup` | `239.42.42.42` | 用于对等发现的 UDP 多播组 |
| `Cluster:MulticastPort` | `Cluster__MulticastPort` | `19847` | 用于对等发现的 UDP 多播端口 |
| `Cluster:InternalUrl` | `Cluster__InternalUrl` | *（无）* | 多播不可用时用于 gossip 的负载均衡回退 URL |
| `Cluster:Secret` | `Cluster__Secret` | *（无）* | gossip 端点认证的共享密钥（设置 `InternalUrl` 时建议配置） |
| `Cluster:GossipIntervalSeconds` | `Cluster__GossipIntervalSeconds` | `5` | 实例交换速率限制状态的频率（秒） |
| `Cluster:DiscoveryIntervalSeconds` | `Cluster__DiscoveryIntervalSeconds` | `10` | 实例通过多播宣告自身的频率（秒） |
| `Cluster:PeerStaleAfterSeconds` | `Cluster__PeerStaleAfterSeconds` | `30` | 超过此秒数未响应的对等节点将被移除 |

**零配置（默认）：** 实例通过 UDP 多播互相发现。适用于 Kubernetes、Docker Compose 或任何共享网络。

**多播不可用时（例如某些云 VPC）：**

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

**完全禁用集群：**

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

详情请参阅[扩展](scaling)了解分布式速率限制的工作原理。

## 速率限制

内置的按 IP 速率限制通过集群 gossip 协议在所有实例间同步执行：

| 端点 | 限制 | 时间窗口 |
|---|---|---|
| `POST /api/auth/register` | 5 次注册 | 1 小时 |

启用集群时，这些限制在所有实例间合并统计。禁用集群时，每个实例独立执行各自的限制。

## CORS

CORS 动态配置。所有已注册客户端的 `AllowedCorsOrigins` 中的来源自动被允许，缓存 60 分钟。

## 完整示例

```json
{
  "Storage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..."
  },
  "Issuer": "https://auth.example.com",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "SigningKeyLifetimeDays": 90
  },
  "Cluster": {
    "Enabled": true
  },
  "AdminApi": {
    "Enabled": true,
    "Scope": "authagonal-admin"
  },
  "Authentication": {
    "CookieLifetimeHours": 48
  },
  "PasswordPolicy": {
    "MinLength": 8,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": true
  },
  "Email": {
    "SendGridApiKey": "SG.xxx",
    "FromAddress": "noreply@example.com",
    "FromName": "Example Auth",
    "VerificationTemplateId": "d-xxx",
    "PasswordResetTemplateId": "d-yyy"
  },
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com"]
    }
  ],
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "...",
      "ClientSecret": "...",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["gmail.com"]
    }
  ],
  "ProvisioningApps": {
    "backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret"
    }
  },
  "Clients": [
    {
      "ClientId": "web",
      "ClientName": "Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "MfaPolicy": "Enabled",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
