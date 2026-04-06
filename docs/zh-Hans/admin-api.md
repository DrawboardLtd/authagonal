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
POST   /api/v1/saml/connections                    # 创建
GET    /api/v1/saml/connections/{connectionId}     # 获取单个
PUT    /api/v1/saml/connections/{connectionId}     # 更新
DELETE /api/v1/saml/connections/{connectionId}     # 删除
```

### OIDC 提供者

```
POST   /api/v1/oidc/connections                    # 创建
GET    /api/v1/oidc/connections/{connectionId}     # 获取单个
DELETE /api/v1/oidc/connections/{connectionId}     # 删除
```

### SSO 域

```
GET    /api/v1/sso/domains                 # 列出所有
```

## 角色

### 列出角色

```
GET /api/v1/roles
```

### 获取角色

```
GET /api/v1/roles/{roleId}
```

### 创建角色

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### 更新角色

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### 删除角色

```
DELETE /api/v1/roles/{roleId}
```

### 为用户分配角色

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleId": "role-id"
}
```

### 取消用户角色分配

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleId": "role-id"
}
```

### 获取用户的角色

```
GET /api/v1/roles/user/{userId}
```

## SCIM 令牌

### 生成令牌

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id"
}
```

返回原始令牌一次。请安全存储 -- 之后无法再次获取。

### 列出令牌

```
GET /api/v1/scim/tokens?clientId=client-id
```

返回令牌元数据（ID、创建日期），不包含原始令牌值。

### 撤销令牌

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## 令牌

### 模拟用户

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid,profile
```

代表用户签发令牌，无需其凭据。适用于测试和技术支持。参数通过查询字符串传递。可选的 `refreshTokenLifetime` 参数控制刷新令牌的有效期。
