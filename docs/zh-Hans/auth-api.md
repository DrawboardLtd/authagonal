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
  "name": "Jane Doe"
}
```

**需要 MFA (200)：** 如果用户已注册 MFA 且客户端的 `MfaPolicy` 为 `Enabled` 或 `Required`：

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
| `invalid_credentials` | 401 | 邮箱或密码错误 |
| `locked_out` | 423 | 失败尝试次数过多。包含 `retryAfter`（秒）。 |
| `email_not_confirmed` | 403 | 邮箱尚未验证 |
| `sso_required` | 409 | 该域需要 SSO。`redirectUrl` 指向 SSO 登录。 |
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

创建新用户账户并发送验证邮件。如果邮箱已被注册，返回 `409`。

### 确认邮箱

```
POST /api/auth/confirm-email?token={token}
```

使用验证邮件中的令牌确认用户的邮箱地址。

### 提供者

```
GET /api/auth/providers
```

返回已配置的外部身份提供者列表（用于渲染 SSO 按钮）：

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "loginUrl": "/oidc/google/login" }
  ]
}
```

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

### MFA 状态

```
GET /api/auth/mfa/status
```

返回用户已注册的 MFA 方法。需要 Cookie 认证或 `X-MFA-Setup-Token` 请求头。

```json
{
  "enabled": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

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

删除特定的 MFA 凭据。如果最后一个主要方法被删除，则该用户的 MFA 将被禁用。

## 构建自定义登录界面

默认 SPA（`login-app/`）是此 API 的一种实现。要构建您自己的：

1. 在路径 `/login`、`/forgot-password`、`/reset-password` 上提供您的界面
2. 授权端点将未认证用户重定向到 `/login?returnUrl={encoded-authorize-url}`
3. 登录成功（Cookie 已设置）后，将用户重定向到 `returnUrl`
4. 密码重置链接使用 `{Issuer}/reset-password?p={token}`

您的界面必须从与 API **相同的来源**提供服务，因为：
- Cookie 认证使用 `SameSite=Lax` + `HttpOnly`
- 授权端点重定向到 `/login`（相对路径）
- 重置链接使用 `{Issuer}/reset-password`
