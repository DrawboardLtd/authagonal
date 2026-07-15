---
layout: default
title: 认证 API
locale: zh-Hans
---

# 认证 API

这些端点为登录 SPA 提供支持。它们使用 Cookie 认证（`SameSite=Lax`、`HttpOnly`）。

如果您正在构建自定义登录界面，这些就是您需要对接的端点。

## 端点

### 登录

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**成功 (200)：** 设置认证 Cookie 并返回：

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

当客户端的 `MfaPolicy` 为 `Enabled` 但用户尚未注册时，`mfaAvailable` 为 `true`（界面可提供设置引导）；此时响应中还会包含 `clientId` 字段。

**需要 MFA (200)：** 如果用户已注册 MFA，则**始终**会对其发起质询 -- 无论发起请求的客户端的 `MfaPolicy` 如何（MFA 是用户 / 会话的属性，而非客户端的属性）：

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

客户端应重定向到 MFA 验证页面并调用 `POST /api/auth/mfa/verify`。

**需要 MFA 设置 (200)：** 如果 `MfaPolicy` 为 `Required` 且用户尚未注册 MFA：

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

客户端应重定向到 MFA 设置页面。设置令牌通过 `X-MFA-Setup-Token` 请求头对用户进行认证，以访问 MFA 设置端点。

**错误响应：**

| `error` | 状态码 | 描述 |
|---|---|---|
| `invalid_credentials` | 401 | 邮箱或密码错误。对于未知邮箱刻意返回相同结果（防枚举）。 |
| `locked_out` | 423 | 失败尝试次数过多。包含 `retryAfter`（秒）。 |
| `account_disabled` | 403 | 账户已被停用（仅在密码正确后才会显示） |
| `email_not_confirmed` | 403 | 邮箱尚未验证（仅在密码正确后才会显示） |
| `sso_required` | 409 | 该域需要 SSO。`redirectUrl` 指向 SSO 登录。 |
| `captcha_failed` | 400 | Turnstile 验证失败（仅在配置了 Turnstile 时；此时请求需要包含 `turnstileToken` 字段） |
| `email_required` | 400 | 邮箱字段为空 |
| `password_required` | 400 | 密码字段为空 |

### 注册

```
POST /api/auth/register
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

创建新用户账户并发送验证邮件。返回 `201 { "success": true, "userId": "..." }`。可选字段：`locale`（持久化到用户上的 BCP-47 标签）和 `customAttributes`（字符串映射）。

注册刻意做到**枚举中立**：如果邮箱已被注册，响应仍是相同的中立 `201`（附带一个一次性的 `userId`），而真正的所有者会改为收到一封登录 / 重置提示邮件。注册还按 IP 进行速率限制 -- 超出时返回 `429 rate_limited`（时间窗口和上限可通过 `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes` 配置）。

### 确认邮箱

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

使用验证邮件中的令牌确认用户的邮箱地址。`GET` 是邮件中可点击的链接 -- 它重定向到 `/login?email_confirmed=1`（当注册源自 OAuth 流程时，还会附加 `continue_client` 参数）。`POST` 是编程路径，返回 JSON（令牌也可以通过 JSON 请求体以 `{ "token": "..." }` 形式提供）；响应中包含一个可选的 `appLink`（“继续前往应用”的目标）。

### 提供者

```
GET /api/auth/providers
```

返回已配置的外部身份提供者列表（用于渲染 SSO 按钮）：

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "type": "oidc", "iconUrl": null, "loginUrl": "/oidc/google/login" }
  ],
  "turnstileSiteKey": null
}
```

配置了 `AllowedDomains` 的连接会被**排除** -- 那些连接通过 `/api/auth/sso-check` 以邮箱优先的方式访问，而不是通过按钮。当配置了 Cloudflare Turnstile 时会设置 `turnstileSiteKey`（此时登录界面必须在登录 / 注册 / 密码请求中发送 `turnstileToken`）。

### 注销

```
POST /api/auth/logout
```

清除认证 Cookie。返回 `200 { success: true }`。

### 忘记密码

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

始终返回 `200`（防枚举）。如果用户存在，则发送重置邮件。

### 重置密码

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | 描述 |
|---|---|
| `weak_password` | 不满足强度要求 |
| `invalid_token` | 令牌格式错误 |
| `token_expired` | 令牌已过期（默认 60 分钟有效期，可通过 `Auth:PasswordResetExpiryMinutes` 配置） |

### 会话

```
GET /api/auth/session
```

如果已认证，返回当前会话信息：

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

如果未认证，返回 `401`。

### 应用

```
GET /api/auth/apps
```

返回租户的应用链接，用于账户页面的“返回应用”启动器：已启用且具有主页 URI 的客户端（`initiateLoginUri` 优先于 `clientUri`）。每个条目为 `{ clientId, clientName, homeUri, logoUri, isDefault }`；恰好有一个应用被标记为默认（被标记的客户端，或唯一具有主页 URI 的客户端）。需要 Cookie 认证。

### 个人资料（自助服务）

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

已认证用户读取 / 更新自己的非敏感个人资料字段：`firstName`、`lastName`、`companyName`、`phone`、`locale`。为 null 的字段保持不变；邮箱、密码、角色、激活状态和组织在此**不可**编辑。两者都返回个人资料 `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }`。

### SSO 检查

```
GET /api/auth/sso-check?email=user@acme.com
```

检查邮箱域是否需要 SSO：

```json
{
  "ssoRequired": true,
  "providerType": "saml",
  "connectionId": "acme-azure",
  "redirectUrl": "/saml/acme-azure/login"
}
```

如果不需要 SSO：

```json
{
  "ssoRequired": false
}
```

### 密码策略

```
GET /api/auth/password-policy
```

返回服务器的密码要求（通过设置中的 `PasswordPolicy` 配置）：

```json
{
  "rules": [
    { "rule": "minLength", "value": 8, "label": "At least 8 characters" },
    { "rule": "uppercase", "value": null, "label": "Uppercase letter" },
    { "rule": "lowercase", "value": null, "label": "Lowercase letter" },
    { "rule": "digit", "value": null, "label": "Number" },
    { "rule": "specialChar", "value": null, "label": "Special character" }
  ]
}
```

默认登录界面在重置密码页面获取此端点以动态显示要求。

## 默认密码要求

使用默认配置，密码必须满足以下所有条件：

- 至少 8 个字符
- 至少一个大写字母
- 至少一个小写字母
- 至少一个数字
- 至少一个非字母数字字符
- 至少 2 个不同字符

这些可以通过 `PasswordPolicy` 配置节进行自定义 -- 参阅[配置](configuration)。

## MFA 端点

### MFA 验证

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

验证 MFA 质询。成功后设置认证 Cookie 并返回用户信息。

**验证方法：**

| `method` | 必需字段 | 描述 |
|---|---|---|
| `totp` | `code`（6 位数字） | 来自认证器应用的基于时间的一次性密码 |
| `webauthn` | `assertion`（JSON 字符串） | 来自 `navigator.credentials.get()` 的 WebAuthn 断言响应 |
| `recovery` | `code`（`XXXX-XXXX`） | 一次性恢复码（使用后即失效） |

**重试语义：** 错误的验证码**不会**烧毁质询 -- 系统先验证验证码，仅在成功时才消费质询，因此用户在输错一位数字后（`401 invalid_code` / `assertion_failed`）可以用相同的 `challengeId` 重试。每个质询最多容忍 **5 次失败尝试**；第 5 次失败会消费该质询并返回 `401 too_many_attempts`，强制重新登录（这将 TOTP 暴力破解限制在每个质询 5 次猜测以内）。质询也会过期（默认 5 分钟，`Auth:MfaChallengeExpiryMinutes`）；已过期、未知或已被消费的 `challengeId` 返回 `invalid_challenge`。TOTP 验证码还有重放保护 -- 来自已使用时间步长的验证码会被拒绝。

### MFA 状态

```
GET /api/auth/mfa/status
```

返回用户已注册的 MFA 方法。需要 Cookie 认证或 `X-MFA-Setup-Token` 请求头。

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

当每个客户端的 `MfaPolicy` 都为 `Disabled` 时，`offered` 为 `false` -- 即该租户已关闭 MFA，因此设置界面可以自行隐藏。恢复码条目还会附带 `isConsumed`。

### TOTP 设置

```
POST /api/auth/mfa/totp/setup
-> { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
-> { "success": true }
```

### WebAuthn / 通行密钥设置

```
POST /api/auth/mfa/webauthn/setup
-> { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
-> { "success": true, "credentialId": "..." }
```

注册通行密钥需要**先有一个已确认的 TOTP 凭据**（`400 totp_required_first`）-- 通行密钥是在可移植的基础要素之上叠加的按设备便利手段，因此账户永远不会变成仅有通行密钥而被锁定在单一设备上。邮箱域被 SSO 路由的用户无法注册本地通行密钥（`400 sso_managed`）-- 那会绕过租户的 IdP。已注册到其他用户的凭据 ID 会以 `409 credential_already_registered` 被拒绝。

### 恢复码

```
POST /api/auth/mfa/recovery/generate
-> { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

生成 10 个一次性恢复码。需要至少注册一个主要方法（TOTP 或 WebAuthn）。重新生成将替换所有现有恢复码。

### 删除 MFA 凭据

```
DELETE /api/auth/mfa/credentials/{credentialId}
-> { "success": true }
```

删除特定的 MFA 凭据。如果最后一个主要方法被删除，则该用户的 MFA 将被禁用。需要真实的 Cookie 会话 -- 设置令牌会以 `403 session_required` 被拒绝（设置令牌只用于添加第一个要素，绝不能用于降级 MFA）。

### 无密码通行密钥登录

```
POST /api/auth/mfa/passwordless/begin
→ { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
→ { "userId": "...", "email": "...", "name": "..." }
```

在没有先前用户上下文的情况下进行可发现凭据（常驻通行密钥）登录：`begin` 签发一个 `allowCredentials` 列表为空的断言质询，`complete` **从**所选通行密钥解析出用户、验证断言并为其登录（会话携带 MFA 标记 -- 通行密钥是抗钓鱼的强认证）。如果解析出的用户的邮箱域被 SSO 路由，则登录会以 `409 sso_required` + `redirectUrl` 被拒绝，以防本地通行密钥绕过被强制的 IdP。

## 设备授权（RFC 8628）

### 请求设备码

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

返回设备码、用户码和验证 URI：

```json
{
  "device_code": "abc123...",
  "user_code": "ABCD-EFGH",
  "verification_uri": "https://auth.example.com/device",
  "verification_uri_complete": "https://auth.example.com/device?user_code=ABCD-EFGH",
  "expires_in": 300,
  "interval": 5
}
```

`expires_in` 来自客户端的 `DeviceCodeLifetimeSeconds`（默认 300）。设备向用户显示 `verification_uri` 和 `user_code`，然后携带 `device_code` 轮询令牌端点 -- 两次轮询间隔不得短于 `interval` 秒，否则令牌端点会返回 `slow_down`（RFC 8628 §3.5）。在用户尚未批准前，令牌端点返回 `authorization_pending`。用户访问验证 URI，登录，并输入用户码以批准。

### 批准设备

```
POST /api/auth/device/approve
Content-Type: application/json

{
  "userCode": "ABCD-EFGH"
}
```

需要 Cookie 认证。为当前用户批准该设备码。之后设备即可通过令牌端点、使用授权类型 `urn:ietf:params:oauth:grant-type:device_code` 用设备码兑换令牌。

## 令牌自省（RFC 7662）

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

token=eyJhbGci...
```

或使用表单编码的凭据：

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJhbGci...&client_id=my-app&client_secret=secret
```

返回令牌元数据：

```json
{
  "active": true,
  "sub": "user-id",
  "client_id": "my-app",
  "scope": "openid profile",
  "iss": "https://auth.example.com",
  "exp": 1234567890,
  "iat": 1234567890,
  "token_type": "Bearer"
}
```

未激活或无效的令牌返回 `{ "active": false }`。同时支持 JWT 访问令牌和不透明的刷新令牌。

## 同意端点

### 同意信息

```
GET /consent/info?client_id=my-app&scope=openid%20profile%20email
```

返回同意页面所需的客户端详情和请求的作用域（省略时 `scope` 默认为 `openid`）：

```json
{
  "clientId": "my-app",
  "clientName": "My Application",
  "description": null,
  "clientUri": null,
  "logoUri": null,
  "scopes": ["openid", "profile", "email"]
}
```

对于未知的客户端返回 `404 client_not_found`。

### 提交同意

```
POST /consent
Content-Type: application/json

{
  "clientId": "my-app",
  "decision": "allow",
  "scopes": ["openid", "profile", "email"],
  "returnUrl": "/connect/authorize?..."
}
```

记录用户的同意决定（需要 Cookie 认证），并返回 `{ "redirect": "..." }` 供 SPA 导航。批准时，被授予的作用域会被持久化（过滤到客户端的 `AllowedScopes` -- 被篡改的请求体无法记录客户端本无法请求的作用域），重定向指回授权流程。当 `"decision": "deny"` 时，重定向指向客户端的 `redirect_uri` 并带有 `access_denied` 错误。

### 列出授权

```
GET /consent/grants
```

返回用户已授权的所有应用：

```json
[
  {
    "clientId": "my-app",
    "clientName": "My Application",
    "scopes": ["openid", "profile", "email"],
    "consentedAt": "2026-04-09T12:00:00Z"
  }
]
```

### 撤销授权

```
DELETE /consent/grants/{clientId}
```

撤销对特定应用的同意。用户下次登录时将被提示重新同意。

## 构建自定义登录界面

默认 SPA（`login-app/`）是此 API 的一种实现。要构建您自己的：

1. 在路径 `/login`、`/forgot-password`、`/reset-password` 上提供您的界面
2. 授权端点将未认证用户重定向到 `/login?returnUrl={encoded-authorize-url}`
3. 登录成功（Cookie 已设置）后，将用户重定向到 `returnUrl`
4. 密码重置链接使用 `{Issuer}/login/reset-password?p={token}`（登录 SPA 挂载在 `/login` 下）

您的界面必须从与 API **相同的来源**提供服务，因为：
- Cookie 认证使用 `SameSite=Lax` + `HttpOnly`
- 授权端点重定向到 `/login`（相对路径）
- 重置链接使用 `{Issuer}/login/reset-password`
