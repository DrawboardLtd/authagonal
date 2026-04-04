---
layout: default
title: OIDC 联合
locale: zh-Hans
---

# OIDC 联合

Authagonal 可以将认证联合到外部 OIDC 身份提供者（Google、Apple、Azure AD 等）。这允许"使用 Google 登录"类型的流程，同时 Authagonal 仍然是中心认证服务器。

## 工作原理

1. 用户在登录页面输入邮箱
2. SPA 调用 `/api/auth/sso-check` -- 如果邮箱域链接到 OIDC 提供者，则需要 SSO
3. 用户点击"通过 SSO 继续" -> 重定向到外部 IdP
4. 认证后，IdP 重定向回 `/oidc/callback`
5. Authagonal 验证 id_token，创建/关联用户，并设置会话 Cookie

## 设置

### 1. 创建 OIDC 提供者

**选项 A -- 配置（推荐用于静态设置）：**

添加到 `appsettings.json`：

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

提供者在启动时播种。`ClientSecret` 通过 `ISecretProvider` 保护（配置 Key Vault 时使用 Key Vault，否则使用纯文本）。SSO 域映射从 `AllowedDomains` 自动注册。

**选项 B -- 管理 API（用于运行时管理）：**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"]
  }'
```

### 2. SSO 域路由

当指定了 `AllowedDomains`（在配置中或通过创建 API），SSO 域映射会自动注册。如果没有域路由，用户仍然可以通过 `/oidc/{connectionId}/login` 被引导到 OIDC 登录。

## 端点

| 端点 | 描述 |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...` | 发起 OIDC 登录。生成 PKCE + state + nonce，重定向到 IdP 的授权端点。 |
| `GET /oidc/callback` | 处理 IdP 回调。用授权码交换令牌，验证 id_token，创建/登录用户。 |

## 安全特性

- **PKCE** -- 每个授权请求都使用 S256 的 code_challenge
- **Nonce 验证** -- nonce 存储在 state 中，在 id_token 中验证
- **State 验证** -- 一次性使用，存储在 Azure Table Storage 中并设有过期时间
- **id_token 签名验证** -- 从 IdP 的 JWKS 端点获取密钥
- **Userinfo 回退** -- 如果 id_token 不包含邮箱，则尝试 userinfo 端点

## Azure AD 特殊说明

Azure AD 有时会将邮箱作为 JSON 数组在 `emails` 声明中返回（特别是 B2C）。Authagonal 通过同时检查 `email` 声明和 `emails` 数组来处理此情况。

## 支持的提供者

任何支持以下功能的 OIDC 兼容提供者：
- 授权码流程
- PKCE (S256)
- 发现文档 (`.well-known/openid-configuration`)

已测试：
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C
