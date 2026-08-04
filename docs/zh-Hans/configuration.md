---
layout: default
title: 配置
locale: zh-Hans
---

# 配置

Authagonal 通过 `appsettings.json` 或环境变量进行配置。环境变量使用 `__` 作为节分隔符（例如 `Storage__ConnectionString`）。

## 必需设置

存储可以通过两种方式之一配置——提供 **`Storage:ConnectionString`** **或** **`Storage:TableServiceUri`**（托管标识路径，生产环境首选）。

| 设置 | 环境变量 | 描述 |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | 带有账户密钥的 Azure Table Storage 连接字符串。适用于开发 / Azurite。 |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | 托管标识的 Table Storage 终结点，例如 `https://{account}.table.core.windows.net/`。作为 `Storage:ConnectionString` 的替代方案，且**在生产环境中首选**——通过 `DefaultAzureCredential` 进行认证，因此不会有任何访问密钥落入机密中。宿主必须授予工作负载标识 **Storage Table Data Contributor** 角色。 |
| `Issuer` | `Issuer` | 此服务器的公共基础 URL（例如 `https://auth.example.com`） |

## 存储

| 设置 | 环境变量 | 默认值 | 描述 |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *（无）* | 带有账户密钥的连接字符串（参见“必需设置”）。 |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *（无）* | 托管标识的 Table Storage URI（参见“必需设置”）。当两者都设置时，优先于 `Storage:ConnectionString`。 |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | 是否维护支撑管理员姓名前缀搜索的 `UserFirstNames` / `UserLastNames` 前缀搜索索引表。在不向外暴露管理员姓名搜索的宿主上设为 `false` 以跳过这些写入。**扩展注意事项：** 这些索引使用单个热分区，在规模化时将吞吐量限制在大约 2,000 ops/秒——如果不需要姓名搜索，请禁用它们。 |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | `/connect/authorize` 端点重定向到登录 SPA（登录、升级验证和同意界面）的基础 URL。当登录 UI 由与服务器不同的源提供时，请设置此项；默认为内置 SPA 提供的相对路径 `/login`。 |

## 认证

| 设置 | 默认值 | 描述 |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Cookie 会话生命周期（滑动过期） |
| `Authentication:AllowInsecureCookie` | `false` | Let the session cookie be sent over plain http (`SameAsRequest` instead of `Always`). **Development only** — see the English documentation. |
| `Authentication:CookieDomain` | *(unset)* | Scope the session cookie to a parent domain. **Costs the `__Host-` prefix and its origin binding** — see the English documentation. |
| `Auth:AllowInsecureHttp` | `false` | 允许 OAuth 端点（`/connect/*`）响应明文 http 请求。**仅限开发环境。** RFC 6749 §3.1/§3.2 要求授权端点和令牌端点使用 TLS，因此默认情况下对其中任一端点的非 https 请求都会以 `invalid_request` 被拒绝。协议方案是在转发头处理*之后*才判定的，所以终止 TLS 并转发 `X-Forwarded-Proto: https` 的代理即使不开启此项也能通过该关卡——前提是该代理已在 `ForwardedHeaders:KnownNetworks` / `KnownProxies` 中声明；没有这项声明，该头会被忽略。只有真正以明文运行的部署（随附的 `docker-compose.yml`、custom-server 演示）才需要它，而且只要它处于开启状态，服务器就会在启动时记录一条警告。该值会传播到 `AuthagonalProtocolOptions.AllowInsecureHttp`，因此也同样管辖由 `Authagonal.Protocol` 拥有的那些端点（参见[扩展性](extensibility#embedding-authagonalprotocol-alone)）。 |
| `Auth:MaxFailedAttempts` | `5` | 账户锁定前允许的登录失败次数 |
| `Auth:LockoutDurationMinutes` | `10` | 达到最大失败次数后的账户锁定时长 |
| `Auth:MaxRegistrationsPerIp` | `5` | 时间窗口内每个 IP 地址的最大注册数 |
| `Auth:RegistrationWindowMinutes` | `60` | 注册速率限制时间窗口 |
| `Auth:MaxPasswordResetsPerEmail` | `3` | 时间窗口内每个目标地址的最大密码重置邮件数（以邮箱为键，而非调用方 IP，因此单个地址不会被邮件轰炸） |
| `Auth:PasswordResetWindowMinutes` | `60` | 密码重置速率限制时间窗口 |
| `Auth:AutoConfirmEmailDomains` | *（空）* | 其自助注册会被自动确认的邮箱域名（字符串数组）——它们会跳过验证邮件。为空（默认）表示每个注册都必须完成验证。仅用于开发 / 测试；切勿列入能接收真实邮件的域名。 |
| `Auth:EmailVerificationExpiryHours` | `24` | 邮箱验证链接有效期 |
| `Auth:PasswordResetExpiryMinutes` | `60` | 密码重置链接有效期 |
| `Auth:MfaChallengeExpiryMinutes` | `5` | MFA 验证令牌有效期 |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | MFA 设置令牌有效期（用于强制注册） |
| `Auth:Pbkdf2Iterations` | `100000` | 密码哈希的 PBKDF2 迭代次数 |
| `Auth:FailedLoginMinimumMilliseconds` | `250` | 失败登录在返回 `invalid_credentials` 之前被保持的挂钟时间下限，从请求开始处计时。用于关闭用户枚举的时间旁道：不存在的账户会针对原生 PBKDF2 格式的哑元哈希进行校验，但真实账户可能仍持有成本不同的、导入的 bcrypt 或 ASP.NET Identity V3 哈希——因此让计算量相等是不可能的，被强制相等的是耗时。请把它调到高于该部署所持有的最慢哈希，例如您导入了成本高于 11 的 bcrypt，或把 `Pbkdf2Iterations` 提到远超默认值——首次有失败登录超出该下限时会记录一条一次性警告。设为 `0` 将禁用填充并重新打开该旁道。 |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | 并发刷新令牌重用的可选宽限窗口（秒）。`0`（默认）保持严格姿态：对已消费刷新令牌的任何重用都会撤销该用户+客户端的所有令牌。设为 `> 0` 则将窗口内的重用视为幂等重试（重新下发后继令牌）——对于网络连接不稳定的移动客户端很有用。 |
| `Auth:DynamicClientRegistrationEnabled` | `false` | 启用 `POST /connect/register` 动态客户端注册端点（RFC 7591）。默认关闭，因为在多租户部署中开放注册可能被滥用。参见[动态客户端注册](client-registration)。 |
| `Auth:SigningKeyLifetimeDays` | `90` | RSA 签名密钥在自动轮换前的有效期 |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | 从存储重新加载签名密钥的频率 |
| `Auth:KeyRotationEnabled` | `false` | 启用签名密钥自动轮换 |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | 检查活动密钥是否需要轮换的频率 |
| `Auth:KeyRotationLeadTimeDays` | `14` | 当活动密钥在此天数内过期时进行轮换 |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Cookie 安全标记检查间隔 |

## 数据保护

ASP.NET Core Data Protection 密钥（用于加密会话 Cookie）必须在各实例之间共享——参见[扩展](scaling#cookie-encryption-data-protection)。持久化选项按优先级顺序如下：

| 设置 | 默认值 | 描述 |
|---|---|---|
| `DataProtection:BlobUri` | *（无）* | 密钥环的显式 Azure Blob URI（例如 `https://{account}.blob.core.windows.net/dataprotection/keys.xml`）。通过 `DefaultAzureCredential` 认证——与 `Storage:TableServiceUri` 并列的生产环境首选路径。 |
| *（回退）* | — | 当 `DataProtection:BlobUri` 未设置且 `Storage:ConnectionString` 指向真实的存储账户（而非 Azurite）时，密钥会自动持久化到该账户中的 `dataprotection` 容器。使用 Azurite 时，密钥回退到默认的基于文件的存储。 |

在 AWS 后端上，向 `AddAuthagonalAwsStorage` 传入 S3 客户端 + 存储桶，即可将密钥环持久化到 S3——参见[安装 → AWS 后端](installation#aws-backend)。

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

## 角色

角色在 `Roles` 数组中定义，并与客户端、scope 和提供者一同在启动时播种。当某个 scope 用
[`AllowedRoles`](scopes#role-gated-scopes) 加以限制时，播种最为要紧：一个被限制到无人创建的角色上的
scope，对所有人都是被限制的——包括配置它的运维人员本人——而且它会静默失败：该 scope 根本不会被授予。

```json
{
  "Roles": [
    {
      "Name": "staff-admin",
      "Description": "Internal staff console",
      "Members": [ "ada@example.com", "grace@example.com" ]
    }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `Name` | 角色名称，用于 `Scope.AllowedRoles` 以及令牌的 `roles` claim |
| `Description` | 供人阅读；当播种数据给出该字段时，会在后续启动时更新 |
| `Members` | 每次启动时被放入该角色的邮箱地址。尚无对应用户的地址会被跳过并记录警告，下次启动时重试——启动过程绝不依赖于某个尚未被创建的账户 |

播种是**增量且幂等**的。它绝不会删除角色或撤销成员资格：配置并不是「谁拥有什么」的权威来源，因此通过管理
API 授予的角色能够在下次重启后继续存在。

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
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
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
| `urn:ietf:params:oauth:grant-type:device_code` | 用于输入受限设备的设备授权许可（RFC 8628） |

### 刷新令牌用法

| 值 | 行为 |
|---|---|
| `OneTime`（默认） | 每次刷新都会签发新的刷新令牌，并使旧令牌失效。默认情况下（`Auth:RefreshTokenReuseGraceSeconds = 0`），对已消费令牌的任何重用都会立即撤销该用户+客户端的所有令牌——默认**不**启用宽限窗口。将 `Auth:RefreshTokenReuseGraceSeconds` 设为正值以启用重试容忍窗口。 |
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

联合登录（SAML/OIDC）同样遵守 MFA 策略：已注册 MFA 的用户在外部 IdP 完成认证后，会被引导通过 MFA 验证挑战；而 `Required` 会对未启用 MFA 的联合用户强制要求注册。

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
| `EntityId` | 是 | **本服务器的** SP 实体 ID——即您在 IdP 处注册的标识符，而非 IdP 自身的实体 ID |
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

上游 OIDC 客户端密钥和 TOTP / MFA 种子可以存储在 Azure Key Vault 中，而非以纯文本形式保存：

| 设置 | 描述 |
|---|---|
| `SecretProvider:VaultUri` | Key Vault URI（例如 `https://my-vault.vault.azure.net/`）。如未设置，将使用**纯文本**提供者，密钥会原样存储在 Table Storage 中。 |

| `SecretProvider:RequireVaultReferences` | 默认为 `false`。设为 `true` 时，已存储的、不带 vault 前缀（Key Vault 用 `kv:`，AWS Secrets Manager 用 `sm:`）的引用会被视为**错误**，而不是当作纯文本值接受。迁移进 vault 完成之后即可开启。 |

配置后，看起来像 Key Vault 引用的密钥值会在运行时解析。使用 `DefaultAzureCredential` 进行认证。

### 迁移进 vault，以及事后把门关上

两种基于 vault 的提供者都会原样返回不带前缀的引用，把它当作在该部署还没有 vault 之前写入的纯文本值。正是这一点让一个正在运行的系统可以逐个密钥地迁移，而不必一次性全部迁移——但若一直敞着，它就是一条永久的降级通道：任何能写入一列配置的东西（一次做了一半的迁移、一条把原始值写进本该是引用的位置的管理路径、一个能访问存储却访问不到 vault 的攻击者）都可以把受 vault 保护的密钥替换成自己挑选的值，而且它能完美通过校验，因为对于不带前缀的引用来说，引用*就是*值本身。

迁移完成后请设置 `SecretProvider:RequireVaultReferences`。此后解析不带前缀的引用会抛出异常，而不是悄悄返回明文。如果解析出来的提供者是纯文本提供者却又设置了该项，启动时会被拒绝，因为这个组合没有任何可用状态——纯文本提供者写出的每一个引用都是不带前缀的。

此外，只要非 Development 主机最终使用的是纯文本提供者，服务器就会在启动时记录一条警告。

> ⚠️ **生产环境：请设置 `SecretProvider:VaultUri`。** 默认密钥提供者为**纯文本**。当 `SecretProvider:VaultUri` 未设置时，上游 OIDC 客户端密钥和 TOTP / MFA 种子会以明文写入 Azure Table Storage——因此也会以明文出现在任何[备份](backup-restore)中。对于任何生产部署，请配置 `SecretProvider:VaultUri`，以便这些密钥存储在 Key Vault 中。

## 管理 API

| 设置 | 默认值 | 描述 |
|---|---|---|
| `AdminApi:Enabled` | `true` | **默认启用。** 设为 `false` 以禁用所有管理端点（它们将不会被注册）。 |
| `AdminApi:Scope` | `authagonal-admin` | 访问管理端点所需的 JWT 作用域。将其更改为与您现有的作用域名称匹配（例如，对于 IdentityServer 迁移使用 `projects-identity-admin`）。 |

> ⚠️ **管理 API 默认启用且具有高度特权。** 管理作用域授予完整的管理权限和用户模拟能力——任何持有带 `AdminApi:Scope` 令牌的人都可以为任意用户铸造令牌、管理客户端，以及读写所有配置。请对管理端点（`/api/v1/*` 管理路由）进行网络限制，并严格控制谁能被签发管理作用域。作为纵深防御措施，该作用域是*保留的*：它永远不能授予给某个 OAuth 客户端（参见[管理 API](admin-api)），也不能通过模拟端点签发。如果不使用管理 API，请直接设置 `AdminApi:Enabled = false`。

## 同意

可以通过 `RequireConsent` 属性启用按客户端的同意：

| 值 | 行为 |
|---|---|
| `false`（默认） | 认证后立即继续授权 |
| `true` | 向用户显示列出所请求作用域的同意界面。同意会持久化 5 年，仅在请求新作用域时才重新提示。 |

用户可以在 `GET /consent/grants` 查看其同意授权，并在 `DELETE /consent/grants/{clientId}` 撤销它们。

## 后通道注销

在客户端上注册 `BackChannelLogoutUri` 以接收 OIDC Back-Channel Logout 1.0 通知。当用户注销时，Authagonal 会向每个客户端注册的 URI 发送一个签名的注销令牌（JWT）。

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback"
    }
  ]
}
```

## 电子邮件

内置的邮件发送器使用 [Resend](https://resend.com)，并在配置了 `Email:ResendApiKey` 时**自动启用**——无需注册任何服务。若要使用其他提供者，请在调用 `AddAuthagonal()` 之前注册您自己的 `IEmailService` 实现（无论 `Email:*` 键如何设置，它都会优先生效）。

| 设置 | 描述 |
|---|---|
| `Email:ResendApiKey` | Resend API 密钥。设置后即使用内置的 Resend 发送器。 |
| `Email:SenderEmail` | 发件人电子邮件地址 |
| `Email:SenderName` | 发件人显示名称（默认为 `"Authagonal"`） |

> ⚠️ **在没有任何邮件发送器时，自助注册将无法正常工作。** 当 `Email:ResendApiKey` 未设置且未注册自定义 `IEmailService` 时，一个空操作服务会静默丢弃所有邮件——验证邮件和密码重置邮件永远不会送达，而由于默认情况下登录要求邮箱已确认，自助注册的用户将永远无法登录。在此状态下，`UseAuthagonal` 会在启动时记录一条警告。开发 / 测试的应急出口：`Auth:AutoConfirmEmailDomains` 会自动确认所列域名的注册。

发送到 `@example.com` 地址的邮件会被静默跳过（便于测试）。

## 集群

集群层提供**领导者选举**（使得诸如签名密钥轮换之类的领导者门控作业只在恰好一个节点上运行）和一个**跨节点事件总线**，二者均位于可插拔的后端之后。默认采用进程内实现：单个节点始终是它自己的领导者——这正是单节点和本地开发的合适设置，且无需任何配置。

| 设置 | 环境变量 | 默认值 | 描述 |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | 主开关。设为 `false` 时节点独立运行（始终为领导者，使用进程内事件总线）。 |
| `Cluster:Secret` | `Cluster__Secret` | *（无）* | 内部专用端点 `/_internal/backchannel-logout` 所需的共享密钥。设置后，调用方必须在 `X-Cluster-Secret` 请求头中提供它（以恒定时间比较）。**未设置**时，该端点仅可从环回 / 私有（RFC 1918 / 链路本地 / ULA）源 IP 访问——携带公网 IP 的外部请求将被拒绝。 |
| `Cluster:LeaseTtlSeconds` | `Cluster__LeaseTtlSeconds` | `30` | 领导权租约时长。大约每隔其一半的间隔续约一次。 |
| `Cluster:PollIntervalSeconds` | `Cluster__PollIntervalSeconds` | `3` | 事件总线后端轮询其他节点所发布消息的频率。 |

**多节点部署**通过 `AddAuthagonal` / `AddAuthagonalCore` 上的 `configureClustering` 回调换入真实的后端：

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS equivalent (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// Self-hosted PostgreSQL (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` 仅注册事件总线，保留进程内租约——适用于必须接收集群事件、但绝不能争夺领导权的节点。

详情请参阅[扩展](scaling)了解领导权和事件总线在各实例间的行为。

## 转发头（受信任代理）

Authagonal 以客户端 IP 作为速率限制和账户锁定的键，并且仅在 HTTPS 请求上发出 HSTS。在反向代理 / 入口（ingress）后面，真实的客户端 IP 和协议（scheme）通过 `X-Forwarded-For` / `X-Forwarded-Proto` 头到达。这些设置控制**哪些代理跳点受信任**来设置这些值，从而防止调用方伪造 `X-Forwarded-For` 来冒充客户端 IP。

| 设置 | 环境变量 | 默认值 | 描述 |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | 从 `X-Forwarded-For` 链右侧起信任的代理跳点数量。默认值 `1` 仅信任您的入口追加的那一跳，忽略链中更靠左的任何内容。 |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0`（数组） | *（空）* | 允许设置转发头的 CIDR 范围（字符串数组，例如 `"10.0.0.0/8"`）。将其设为您的代理 / 入口 / Pod CIDR。正是这项声明才使 `X-Forwarded-Proto` 得以被采纳——参见下文。 |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0`（数组） | *（空）* | 允许设置转发头的单个代理 IP 地址（字符串数组）。可与 `KnownNetworks` 一起使用或替代之。 |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

### 这两个头并不按同样的条件被信任

`X-Forwarded-For` 用于修正**客户端 IP**——限流、账户锁定以及 `/_internal` 守卫都以它为键。若什么都没有声明，Authagonal 仍会接受来自回环地址和 RFC1918 各段的该头，并记录一条警告。这是一个尽力而为的默认值，它优于框架在信任集合为空时的行为：那种情况下，*任何*调用方发来的该头都会被采纳。

`X-Forwarded-Proto` 会改变**协议方案**，而协议方案决定了 `/connect/*` 是否会作出响应（RFC 6749 §3.1/§3.2）、cookie 是否被标记为 `Secure`，以及生成的绝对 URL 是否为 https。它**只**会从您在 `KnownNetworks` / `KnownProxies` 中声明过的代理处被采纳。私有地址并不构成声明：Authagonal 以库的形式分发，看不到自己被部署到什么网络上，因此"对端持有私有地址"只是对网络拓扑的一种猜测。在扁平局域网、共享 VPC 或共享容器网桥中，每一个相邻工作负载都落在这些网段内，都可以为一个实际以明文到达的请求声称 `https`。

**如果您的代理没有固定地址**——Kubernetes 入口、轮换的负载均衡器、不会告诉您该跳 CIDR 的平台——那就把每个对端都声明为代理：

```json
{
  "ForwardedHeaders": {
    "KnownNetworks": ["0.0.0.0/0", "::/0"]
  }
}
```

这恰恰在"除代理之外没有任何东西能触达该进程"时是安全的，而这正是此类部署本就依赖的前提。把它写下来，就把这个前提放到了可供审阅之处，而不是留给库去推断。如果其他工作负载**确实**能直接触达 Kestrel，那么在此设置下它们就能伪造协议方案和客户端 IP——这时请改为固定真实的 CIDR。

> ⚠️ **需要 TLS 终止代理，并且必须声明它。** Authagonal 必须运行在 TLS 终止反向代理后面（或自行终止 TLS）。HSTS（`Strict-Transport-Security`）仅在 HTTPS 请求上发出，且除非开启 `Auth:AllowInsecureHttp`，OAuth 端点会直接拒绝明文请求——因此代理必须转发 `X-Forwarded-Proto: https`，**并且**要在 `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` 中被指名，HSTS 才会发送，`/connect/*` 才会作出响应。什么都不声明是最常见的升级故障：头确实到达了，却没有任何东西有权采纳它，于是在一个确实跑在 TLS 上的部署里，每个 `/connect/*` 请求都返回 400。启动日志会这么说，拒绝响应的正文也会。

## 速率限制

内置的速率限制保护那些易被滥用的端点：

| 端点 | 限制 | 时间窗口 | 键 |
|---|---|---|---|
| `POST /api/auth/register` | 5（`Auth:MaxRegistrationsPerIp`） | 1 小时（`Auth:RegistrationWindowMinutes`） | 客户端 IP |
| `POST /api/auth/forgot-password` | 3（`Auth:MaxPasswordResetsPerEmail`） | 1 小时（`Auth:PasswordResetWindowMinutes`） | 目标邮箱 |
| `POST /connect/register`（启用时） | 10 | 1 小时 | 客户端 IP |
| SCIM 端点 | 200 | 1 分钟 | SCIM 客户端 |

这些限制在**每个节点进程内**执行（位于 `IRateLimiter` 接缝之后），因此在 N 个实例下，有效上限是所配置值的 N 倍。请将它们视为一道兜底防线，并在边缘（WAF / 入口 / CDN）执行权威的全局限制。参见[扩展](scaling#rate-limiting)。

## CORS

CORS 动态配置。所有已注册客户端的 `AllowedCorsOrigins` 中的来源自动被允许，缓存 60 分钟。

## HashiCorp Vault Transit

Authagonal 可以使用 HashiCorp Vault 的 Transit 机密引擎签名 JWT。私钥永远不会离开 Vault——只有签名操作被远程委托。公钥在本地缓存以供验证。

这是在作为库托管时以编程方式配置的。详情请参阅[扩展性](extensibility)。

## 完整示例

```json
{
  "Storage": {
    "TableServiceUri": "https://myaccount.table.core.windows.net/",
    "NameIndexesEnabled": true
  },
  "Issuer": "https://auth.example.com",
  "LoginAppUrl": "/login",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "RefreshTokenReuseGraceSeconds": 0,
    "DynamicClientRegistrationEnabled": false,
    "SigningKeyLifetimeDays": 90
  },
  "SecretProvider": {
    "VaultUri": "https://my-vault.vault.azure.net/"
  },
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"]
  },
  "Cluster": {
    "Enabled": true,
    "Secret": "shared-secret-here"
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
    "ResendApiKey": "re_xxx",
    "SenderEmail": "noreply@example.com",
    "SenderName": "Example Auth"
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
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
