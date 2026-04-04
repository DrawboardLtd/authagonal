---
layout: default
title: 多因素认证
locale: zh-Hans
---

# 多因素认证（MFA）

Authagonal 支持基于密码的登录的多因素认证。提供三种方式：TOTP（验证器应用）、WebAuthn/通行密钥（硬件密钥和生物识别）以及一次性恢复代码。

联合登录（SAML/OIDC）跳过 MFA — 由外部身份提供商处理第二因素认证。

## 支持的方式

| 方式 | 描述 |
|---|---|
| **TOTP** | 基于时间的一次性密码（RFC 6238）。适用于任何验证器应用 — Google Authenticator、Authy、1Password 等。 |
| **WebAuthn / 通行密钥** | FIDO2 硬件安全密钥、平台生物识别（Touch ID、Windows Hello）以及同步通行密钥。 |
| **恢复代码** | 10 个一次性备用代码（`XXXX-XXXX` 格式），用于在其他方式不可用时恢复账户。 |

## MFA 策略

MFA 强制执行通过 `appsettings.json` 中的 `MfaPolicy` 属性**按客户端**进行配置：

| 值 | 行为 |
|---|---|
| `Disabled`（默认） | 不进行 MFA 验证，即使用户已注册 MFA |
| `Enabled` | 对已注册 MFA 的用户进行验证；不强制注册 |
| `Required` | 对已注册用户进行验证；对未注册 MFA 的用户强制注册 |

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "MfaPolicy": "Enabled"
    },
    {
      "ClientId": "admin-portal",
      "MfaPolicy": "Required"
    }
  ]
}
```

默认值为 `Disabled`，因此现有客户端在选择加入之前不受影响。

### 按用户覆盖

实现 `IAuthHook.ResolveMfaPolicyAsync` 以覆盖特定用户的客户端策略：

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    // Exempt service accounts
    if (email.EndsWith("@service.internal"))
        return Task.FromResult(MfaPolicy.Disabled);

    return Task.FromResult(clientPolicy);
}
```

请参阅[扩展性](extensibility)以获取完整的钩子文档。

## 登录流程

带有 MFA 的登录流程如下：

1. 用户向 `POST /api/auth/login` 提交电子邮件和密码
2. 服务器验证密码，然后解析有效的 MFA 策略
3. 根据策略和用户的注册状态：

| 策略 | 用户已注册 MFA？ | 结果 |
|---|---|---|
| `Disabled` | — | 设置 Cookie，登录完成 |
| `Enabled` | 否 | 设置 Cookie，登录完成 |
| `Enabled` | 是 | 返回 `mfaRequired` — 用户必须验证 |
| `Required` | 否 | 返回 `mfaSetupRequired` — 用户必须注册 |
| `Required` | 是 | 返回 `mfaRequired` — 用户必须验证 |

### MFA 验证

当返回 `mfaRequired` 时，登录响应包含 `challengeId` 和用户可用的方式。客户端重定向到 MFA 验证页面，用户通过 `POST /api/auth/mfa/verify` 使用其已注册的方式之一进行验证。

验证在 5 分钟后过期，且只能使用一次。

### 强制注册

当返回 `mfaSetupRequired` 时，响应包含 `setupToken`。此令牌通过 `X-MFA-Setup-Token` 标头对用户进行身份验证，以便他们在获得 Cookie 会话之前注册一种方式。

## 注册 MFA

用户通过自助服务设置端点注册 MFA。这些端点需要已认证的 Cookie 会话或设置令牌。

### TOTP 设置

1. 调用 `POST /api/auth/mfa/totp/setup` — 返回 QR 码（`data:image/svg+xml;base64,...`）和设置令牌
2. 用户使用验证器应用扫描 QR 码
3. 用户输入 6 位验证码确认：`POST /api/auth/mfa/totp/confirm`

### WebAuthn / 通行密钥设置

1. 调用 `POST /api/auth/mfa/webauthn/setup` — 返回 `PublicKeyCredentialCreationOptions`
2. 客户端使用选项调用 `navigator.credentials.create()`
3. 将认证响应发送至 `POST /api/auth/mfa/webauthn/confirm`

### 恢复代码

调用 `POST /api/auth/mfa/recovery/generate` 生成 10 个一次性代码。必须先注册至少一种主要方式（TOTP 或 WebAuthn）。

重新生成代码会替换所有现有的恢复代码。每个代码只能使用一次。

## 管理 MFA

### 用户自助服务

- `GET /api/auth/mfa/status` — 查看已注册的方式
- `DELETE /api/auth/mfa/credentials/{id}` — 删除特定凭据

如果删除了最后一种主要方式，则为该用户禁用 MFA。

### 管理员 API

管理员可以通过[管理员 API](admin-api) 管理任何用户的 MFA：

- `GET /api/v1/profile/{userId}/mfa` — 查看用户的 MFA 状态
- `DELETE /api/v1/profile/{userId}/mfa` — 重置所有 MFA（适用于被锁定的用户）
- `DELETE /api/v1/profile/{userId}/mfa/{id}` — 删除特定凭据

### 审计钩子

实现 `IAuthHook.OnMfaVerifiedAsync` 以记录 MFA 事件：

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

## 自定义登录界面

如果您正在构建自定义登录界面，请处理来自 `POST /api/auth/login` 的以下响应：

1. **正常登录** — `{ userId, email, name }` 并设置 Cookie。重定向至 `returnUrl`。
2. **需要 MFA** — `{ mfaRequired: true, challengeId, methods, webAuthn? }`。显示 MFA 验证表单。
3. **需要 MFA 注册** — `{ mfaSetupRequired: true, setupToken }`。显示 MFA 注册流程。

请参阅[认证 API](auth-api) 以获取完整的端点参考。
