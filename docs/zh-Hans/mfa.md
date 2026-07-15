---
layout: default
title: 多因素认证
locale: zh-Hans
---

# 多因素认证（MFA）

Authagonal 支持多因素认证。提供三种方式：TOTP（验证器应用）、WebAuthn/通行密钥（硬件密钥和生物识别）以及一次性恢复代码。通行密钥还可用于[无密码登录](#passwordless-passkey-login)。

联合登录（SAML/OIDC）同样在覆盖范围内：SAML 或 OIDC 断言证明的是第一因素，而非第二因素。已注册 MFA 的联合用户会经过与密码登录相同的本地 MFA 验证，`Required` 策略会在颁发任何会话之前强制注册。只有当 MFA 既未注册也未被要求时，联合认证才独立成立。

## 支持的方式

| 方式 | 描述 |
|---|---|
| **TOTP** | 基于时间的一次性密码（RFC 6238）：6 位数字、30 秒步长、SHA-1，验证时允许一个步长的时钟偏移窗口。适用于任何验证器应用（Google Authenticator、Authy、1Password 等）。已被接受的验证码在其有效窗口内无法重放。 |
| **WebAuthn / 通行密钥** | FIDO2 硬件安全密钥、平台生物识别（Touch ID、Windows Hello）以及同步通行密钥。用户可以注册多个通行密钥，且通行密钥可用于无密码登录。 |
| **恢复代码** | 10 个一次性备用代码（`XXXX-XXXX` 格式），用于在其他方式不可用时恢复账户。以哈希形式存储并静态加密。 |

## MFA 策略

MFA 强制执行通过 `appsettings.json` 中的 `MfaPolicy` 属性**按客户端**进行配置：

| 值 | 行为 |
|---|---|
| `Disabled`（默认） | 不强制注册；当所有客户端都是 `Disabled` 时，自助设置界面会隐藏 MFA |
| `Enabled` | 提供 MFA 注册；但不强制 |
| `Required` | 对未注册 MFA 的用户强制注册 |

已注册 MFA 的用户**在登录时总是会被验证，与客户端策略无关**。MFA 是用户及其会话的属性，而非发起请求的客户端的属性，因此经由 `Disabled` 客户端发起的请求无法绕过已注册用户的第二因素。

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

解析出的策略只决定注册环节（是提供注册还是强制注册）。它不会豁免已注册用户的验证；已注册的用户总是会被验证。

请参阅[扩展性](extensibility)以获取完整的钩子文档。

## 登录流程

带有 MFA 的登录流程如下：

1. 用户向 `POST /api/auth/login` 提交电子邮件和密码
2. 服务器验证密码，然后解析有效的 MFA 策略
3. 根据策略和用户的注册状态：

| 策略 | 用户已注册 MFA？ | 结果 |
|---|---|---|
| 任意 | 是 | 返回 `mfaRequired`：用户必须验证 |
| `Disabled` / `Enabled` | 否 | 设置 Cookie，登录完成 |
| `Required` | 否 | 返回 `mfaSetupRequired`：用户必须注册 |

### MFA 验证

当返回 `mfaRequired` 时，登录响应包含 `challengeId`、用户可用的 `methods`，以及（当用户拥有通行密钥时）`webAuthn` 断言选项。客户端重定向到 MFA 验证页面，用户通过 `POST /api/auth/mfa/verify` 使用其已注册的方式之一进行验证：

```json
{
  "challengeId": "...",
  "method": "totp",
  "code": "123456"
}
```

`method` 为 `totp`、`recovery` 或 `webauthn`（WebAuthn 发送 `assertion` 而非 `code`）。

验证挑战在 5 分钟后过期（可通过 `Auth:MfaChallengeExpiryMinutes` 配置），并在验证成功时被消耗。

#### 重试预算

输错验证码不会烧掉挑战。验证端点先校验验证码，仅在成功时才消耗挑战，因此输错一位 TOTP 数字后可以直接用同一个 `challengeId` 重试。失败的尝试会返回 `invalid_code`（WebAuthn 则为 `assertion_failed`）及 401，并在挑战上递增一个有上限的计数器；第 5 次错误尝试会消耗挑战并返回 `too_many_attempts`，用户须重新登录。这适用于全部三种方式，并将 TOTP 暴力破解限制在每个挑战 5 次猜测以内。

缺失、已过期或已被消耗的挑战返回 `invalid_challenge`。

### 联合登录

在 SAML 或 OIDC 断言成功后，服务器会解析相同的有效 MFA 策略。已注册 MFA 的用户会被重定向到托管的 MFA 验证页面（携带 `challengeId`），而不是直接获得会话；在 `Required` 策略下未注册 MFA 的用户会被重定向到 MFA 设置页面（携带 `setupToken`）。只有验证完成后，会话才会被标记为已通过 MFA 认证。

### 强制注册

当返回 `mfaSetupRequired` 时，响应包含 `setupToken`。此令牌通过 `X-MFA-Setup-Token` 标头对用户进行身份验证，以便他们在获得 Cookie 会话之前注册一种方式。设置令牌在 15 分钟后过期（可通过 `Auth:MfaSetupTokenExpiryMinutes` 配置）。

## 注册 MFA

用户通过自助服务设置端点注册 MFA。这些端点需要已认证的 Cookie 会话或设置令牌。

### TOTP 设置

1. 调用 `POST /api/auth/mfa/totp/setup`：返回 QR 码（`data:image/png;base64,...`）、`manualKey`（Base32 格式，用于手动输入）和设置令牌
2. 用户使用验证器应用扫描 QR 码
3. 用户输入 6 位验证码确认：`POST /api/auth/mfa/totp/confirm`

### WebAuthn / 通行密钥设置

1. 调用 `POST /api/auth/mfa/webauthn/setup`：返回 `setupToken` 和 `PublicKeyCredentialCreationOptions`
2. 客户端使用选项调用 `navigator.credentials.create()`
3. 将认证响应发送至 `POST /api/auth/mfa/webauthn/confirm`

注册通行密钥要求先确认一个 TOTP 凭据（`totp_required_first`）。通行密钥是叠加在可携带基础因素之上的按设备便利手段，因此每个账户都保留一个与设备无关的因素，`Required` 策略也无法仅凭通行密钥满足。

用户可以注册多个通行密钥（每台设备一个）。已注册给其他用户的凭据 ID 会被拒绝（`credential_already_registered`）；其邮箱域名经强制 SSO 路由到外部 IdP 的用户无法注册本地通行密钥（`sso_managed`），否则会绕过该 IdP 及其去配。

### 恢复代码

调用 `POST /api/auth/mfa/recovery/generate` 生成 10 个一次性代码。必须先注册至少一种主要方式（TOTP 或 WebAuthn）。

重新生成代码会替换所有现有的恢复代码。每个代码只能使用一次；已兑换的代码会被标记为已消耗，不再被接受。

代码从不以明文存储：每个代码都会被哈希，且哈希还会用租户的机密提供者进行静态加密，因此存储转储得到的是密文，而非可离线暴力破解的哈希。

## 无密码通行密钥登录 {#passwordless-passkey-login}

通行密钥不只是第二因素：拥有已注册通行密钥的用户可以不用密码登录。

1. `POST /api/auth/mfa/passwordless/begin` 返回 `challengeId` 和面向可发现凭据的断言 `options`，使验证器可以提供该站点的任何常驻通行密钥
2. 客户端使用选项调用 `navigator.credentials.get()`
3. 以 `{ challengeId, assertion }` 调用 `POST /api/auth/mfa/passwordless/complete`：服务器从通行密钥本身解析出用户并为其登录

托管登录页面通过条件中介（通行密钥自动填充）将其接入邮箱输入框：当浏览器支持时，可用的通行密钥会作为自动填充建议出现，无需额外 UI。

通行密钥是抗钓鱼的强认证，因此产生的会话带有 MFA 标记，不会被再次验证。如果用户的邮箱域名经强制 SSO 路由到外部 IdP，无密码登录会被拒绝并返回 409 `sso_required` 响应（其中包含 SSO 重定向 URL），因此本地通行密钥无法绕开该 IdP。

## 管理 MFA

### 用户自助服务

- `GET /api/auth/mfa/status`：查看已注册的方式（同时报告是否有任何客户端提供 MFA）
- `DELETE /api/auth/mfa/credentials/{id}`：删除特定凭据

删除凭据需要真实的已认证会话；设置令牌只授权添加第一个因素，在此处会得到 `session_required`，因此泄露的设置令牌无法降级用户的 MFA。

如果删除了最后一种主要方式，则为该用户禁用 MFA。

### 管理员 API

管理员可以通过[管理员 API](admin-api) 管理任何用户的 MFA：

- `GET /api/v1/profile/{userId}/mfa`：查看用户的 MFA 状态
- `DELETE /api/v1/profile/{userId}/mfa`：重置所有 MFA（适用于被锁定的用户）
- `DELETE /api/v1/profile/{userId}/mfa/{id}`：删除特定凭据

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

整个 MFA 生命周期都可挂接钩子：`OnMfaVerifyFailedAsync`（一次失败的验证尝试）、`OnMfaEnrolledAsync`（一种方式被确认）、`OnMfaCredentialRemovedAsync`（一个凭据被删除，附带一个标志指示这是否禁用了 MFA），以及 `OnRecoveryCodesRegeneratedAsync`。

## 自定义登录界面

如果您正在构建自定义登录界面，请处理来自 `POST /api/auth/login` 的以下响应：

1. **正常登录**：`{ userId, email, name }` 并设置 Cookie。重定向至 `returnUrl`。
2. **需要 MFA**：`{ mfaRequired: true, challengeId, methods, webAuthn? }`。显示 MFA 验证表单。
3. **需要 MFA 注册**：`{ mfaSetupRequired: true, setupToken }`。显示 MFA 注册流程。

处理 `POST /api/auth/mfa/verify` 的错误时：`invalid_code` 和 `assertion_failed` 可以用同一个 `challengeId` 重试（不超过尝试预算）；`too_many_attempts` 和 `invalid_challenge` 是终止性的，应将用户送回登录表单。

请参阅[认证 API](auth-api) 以获取完整的端点参考。
