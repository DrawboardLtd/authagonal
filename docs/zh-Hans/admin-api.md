---
layout: default
title: 管理 API
locale: zh-Hans
---

# 管理 API

管理端点需要包含 `authagonal-admin` 作用域的 JWT 访问令牌（可通过 `AdminApi:Scope` 配置）。

所有端点都在 `/api/v1/` 下。

## 用户

### 获取用户

```
GET /api/v1/profile/{userId}
```

返回用户详情，包括外部登录关联。

### 注册用户

```
POST /api/v1/profile/
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

创建用户并发送验证邮件。如果邮箱已被占用，返回 `409`。

### 更新用户

```
PUT /api/v1/profile/
Content-Type: application/json

{
  "userId": "user-id",
  "firstName": "Jane",
  "lastName": "Smith",
  "organizationId": "new-org-id"
}
```

所有字段都是可选的 -- 只有提供的字段会被更新。更改 `organizationId` 会触发：
- SecurityStamp 轮换（在 30 分钟内使所有 Cookie 会话失效）
- 撤销所有刷新令牌

### 删除用户

```
DELETE /api/v1/profile/{userId}
```

删除用户、撤销所有授权，并从所有下游应用取消预配（尽力而为）。

### 确认邮箱

```
POST /api/v1/profile/confirm-email?token={token}
```

### 发送验证邮件

```
POST /api/v1/profile/{userId}/send-verification-email
```

### 关联外部身份

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### 取消关联外部身份

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

## MFA 管理

### 获取 MFA 状态

```
GET /api/v1/profile/{userId}/mfa
```

返回用户的 MFA 状态和已注册的方法。

### 重置所有 MFA

```
DELETE /api/v1/profile/{userId}/mfa
```

删除所有 MFA 凭据并设置 `MfaEnabled=false`。如果需要，用户将需要重新注册。

### 删除特定 MFA 凭据

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

删除特定的 MFA 凭据（例如丢失的认证器）。如果最后一个主要方法被删除，则 MFA 将被禁用。

## SSO 提供者

### SAML 提供者

```
GET    /api/v1/sso/saml                    # 列出所有
GET    /api/v1/sso/saml/{connectionId}     # 获取单个
POST   /api/v1/sso/saml                    # 创建
PUT    /api/v1/sso/saml/{connectionId}     # 更新
DELETE /api/v1/sso/saml/{connectionId}     # 删除
```

### OIDC 提供者

```
GET    /api/v1/sso/oidc                    # 列出所有
GET    /api/v1/sso/oidc/{connectionId}     # 获取单个
POST   /api/v1/sso/oidc                    # 创建
PUT    /api/v1/sso/oidc/{connectionId}     # 更新
DELETE /api/v1/sso/oidc/{connectionId}     # 删除
```

### SSO 域

```
GET    /api/v1/sso/domains                 # 列出所有
GET    /api/v1/sso/domains/{domain}        # 获取单个
POST   /api/v1/sso/domains                 # 创建
DELETE /api/v1/sso/domains/{domain}        # 删除
```

## 令牌

### 模拟用户

```
POST /api/v1/tokens/impersonate
Content-Type: application/json

{
  "userId": "user-id",
  "clientId": "client-id",
  "scopes": ["openid", "profile"]
}
```

代表用户签发令牌，无需其凭据。适用于测试和技术支持。
