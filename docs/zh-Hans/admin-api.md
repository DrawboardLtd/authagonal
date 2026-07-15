---
layout: default
title: 管理 API
locale: zh-Hans
---

# 管理 API

管理端点需要包含 `authagonal-admin` 作用域的 JWT 访问令牌（可通过 `AdminApi:Scope` 配置）。

所有端点都在 `/api/v1/` 下。

## 引导第一个管理令牌

每个 `/api/v1/*` 端点都需要携带管理作用域的 bearer 令牌——但管理 API 本身（以及[动态客户端注册](client-registration)）**拒绝创建或更新任何持有该作用域的客户端**（`403 forbidden_scope`），因此运行时创建的客户端永远无法提权为管理员。铸造管理令牌的唯一途径是**配置播种的客户端**：`Clients:` 配置节中的条目会在启动时由 `ClientSeedService` 更新插入，而配置是受信任的——forbidden-scope 防护只作用于运行时 API。

在 `appsettings.json`（或等效的环境变量 / 机密存储）中播种一个持有管理作用域的 `client_credentials` 客户端：

```json
{
  "Clients": [
    {
      "Id": "admin-cli",
      "Name": "Admin CLI",
      "ClientSecret": "a-long-random-secret",
      "GrantTypes": ["client_credentials"],
      "Scopes": ["authagonal-admin"]
    }
  ]
}
```

（`ClientSecret` 在启动时被哈希；如果您希望配置中只保留预哈希的值，可改为提供 `SecretHashes`。`ClientId`/`ClientName`/`AllowedGrantTypes`/`AllowedScopes` 可作为 `Id`/`Name`/`GrantTypes`/`Scopes` 的别名使用。）

然后在标准令牌端点用凭据换取令牌：

```bash
curl -X POST https://auth.example.com/connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=admin-cli" \
  -d "client_secret=a-long-random-secret" \
  -d "scope=authagonal-admin"
```

```json
{ "access_token": "eyJhbGci...", "token_type": "Bearer", "expires_in": 1800, "scope": "authagonal-admin" }
```

`client_credentials` 授权会将请求的作用域与该客户端的 `AllowedScopes` 进行校验——由于播种的客户端持有 `authagonal-admin`，令牌得以签发。在每个管理调用上以 `Authorization: Bearer {access_token}` 使用它：

```bash
curl https://auth.example.com/api/v1/clients -H "Authorization: Bearer eyJhbGci..."
```

请将播种客户端的密钥保存在部署的机密存储中；轮换它是一次配置更改 + 重启。

## 用户

### 获取用户

```
GET /api/v1/profile/{userId}
```

返回用户详情，包括外部登录关联。

### 用户是否存在

```
GET /api/v1/profile/{userId}/exists
```

如果用户存在返回 `204`，否则返回 `404`（一个廉价的存在性探测——无响应体）。

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

创建用户并发送验证邮件。如果邮箱已被占用，返回 `409 user_exists`。

可选的仅管理员字段：`userId`（调用方提供的 id——冲突时返回 `409 user_id_in_use`）、`emailConfirmed`（创建时即已验证的用户，跳过验证邮件）、`companyName`、`organizationId`、`phone`、`locale`，以及 `customAttributes`（持久化到用户上并转发给预配目标的字符串映射）。

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

`userId` 为必填项；其他每个字段都是可选的——只有提供的字段会被更新。更改 `organizationId` 会触发：
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
PUT    /api/v1/saml/connections/{connectionId}     # 更新（部分——仅更改所提供的字段）
DELETE /api/v1/saml/connections/{connectionId}     # 删除
```

创建时需要 `connectionName`、`entityId`，以及 `metadataLocation`（元数据 URL）或 `metadataXml`（粘贴的 IdP 元数据，用于没有元数据 URL 的 IdP——保存时会经解析校验并压缩）**二者恰选其一**。可选项：`nameIdFormat`（省略则使用 emailAddress 默认值，`"none"` 表示省略 NameIDPolicy——推荐用于 ADFS，或填入某个 NameID 格式 URN）、`signAuthnRequests`、`iconUrl`、`allowedDomains`、`disableJitProvisioning`。每个连接都会获得一个服务器生成的 SP 密钥对；它绝不会被 API 返回。详情请参阅 [SAML](saml)。

### OIDC 提供者

```
POST   /api/v1/oidc/connections                    # 创建
GET    /api/v1/oidc/connections/{connectionId}     # 获取单个
DELETE /api/v1/oidc/connections/{connectionId}     # 删除
```

创建时需要 `connectionName`、`metadataLocation`、`clientId`、`clientSecret`、`redirectUrl`。可选项：`iconUrl`、`allowedDomains`、`passthroughParams`。客户端密钥在静态存储时受保护，且绝不返回。详情请参阅 [OIDC 联合](oidc-federation)。

### SSO 域

```
GET    /api/v1/sso/domains                 # 列出所有
```

## 客户端

在运行时管理 OAuth 客户端。所有路由都需要 `IdentityAdmin` 策略（管理作用域）。

```
GET    /api/v1/clients              # 列出所有客户端
GET    /api/v1/clients/{clientId}   # 获取单个客户端
POST   /api/v1/clients              # 创建客户端
PUT    /api/v1/clients/{clientId}   # 更新客户端
DELETE /api/v1/clients/{clientId}   # 删除客户端
```

### 创建 / 更新客户端

```
POST /api/v1/clients
Content-Type: application/json

{
  "clientId": "my-app",
  "clientName": "My Application",
  "allowedGrantTypes": ["authorization_code"],
  "redirectUris": ["https://app.example.com/callback"],
  "allowedScopes": ["openid", "profile", "email"]
}
```

如果客户端已存在，`POST` 返回 `409`。`PUT` 更新现有客户端（未找到则返回 `404`）；更新时，仅对新增的作用域进行提权检查。

注意事项：

- **密钥哈希永远不会被返回。** `clientSecretHashes` 会从每个响应中剥离（列表、获取、创建、更新）。更新时，省略 `clientSecretHashes` 会保留已存储的密钥；提供新的哈希则会轮换它。
- **管理作用域不能授予给客户端。** 在 `allowedScopes` 中请求 `AdminApi:Scope`（默认 `authagonal-admin`）会返回 `403 forbidden_scope`——任何客户端都不得持有管理作用域，否则 `client_credentials` 客户端将能无限期地铸造管理令牌。
- 添加调用方无权授予的作用域会返回 `403`。

## 作用域

在运行时管理自定义 OAuth 作用域。完整的作用域模型请参阅 [OAuth 作用域](scopes)。

```
GET    /api/v1/scopes           # 列出所有作用域
GET    /api/v1/scopes/{name}    # 获取单个作用域
POST   /api/v1/scopes           # 创建作用域
PUT    /api/v1/scopes/{name}    # 更新作用域（仅更改所提供的字段）
DELETE /api/v1/scopes/{name}    # 删除作用域
```

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing — read-only",
  "description": "View invoices and payment history",
  "userClaims": ["billing_plan"]
}
```

创建时返回 `201`（如果作用域已存在则返回 `409`），获取/更新时返回作用域 JSON，删除时返回 `204`。

## 预配应用

在运行时管理下游预配目标。所有路由都需要 `IdentityAdmin` 策略。

```
GET    /api/v1/provisioning/apps               # 列出应用（同时返回已配置的上限）
POST   /api/v1/provisioning/apps               # 创建应用
PUT    /api/v1/provisioning/apps/{appId}       # 更新应用
DELETE /api/v1/provisioning/apps/{appId}       # 删除应用
POST   /api/v1/provisioning/apps/{appId}/test  # 向应用回调发送测试 /try 调用
```

### 创建 / 更新预配应用

```
POST /api/v1/provisioning/apps
Content-Type: application/json

{
  "name": "Backend",
  "callbackUrl": "https://api.example.com/provisioning",
  "apiKey": "secret-api-key",
  "tryTimeoutSeconds": 30
}
```

- `name` 和 `callbackUrl` 为必填项；`callbackUrl` 必须是绝对的 `http(s)` URL。
- `tryTimeoutSeconds` 被限制在 5–300 范围内。
- **API 密钥永远不会被返回。** 响应会暴露 `hasApiKey`（布尔值），而非密钥本身。更新时，省略 `apiKey` 会保持其不变，空字符串会清除它，提供值则会替换它。
- 创建受可配置的每部署配额（`IProvisioningAppQuota`）约束；超出则返回 `400 provisioning_app_limit`。列表响应包含当前的 `limit`。

### 测试预配应用

```
POST /api/v1/provisioning/apps/{appId}/test
```

发送一个带有示例载荷的合成 `POST {callbackUrl}/try`（如果设置了 API 密钥，则以其作为 bearer 令牌），并返回 `{ success, statusCode, body }`，以便您可以从管理 UI 验证连通性。

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
  "roleName": "admin"
}
```

按**角色名称**分配，而非角色 id。返回用户更新后的角色列表。

### 取消用户角色分配

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
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
  "clientId": "client-id",
  "description": "Entra provisioning",
  "expiresInDays": 365
}
```

`description` 和 `expiresInDays` 为可选项（省略 `expiresInDays` 表示不过期的令牌）。返回原始令牌一次。请安全存储——之后无法再次获取。

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
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

代表用户签发令牌（访问令牌、刷新令牌，以及当请求 `openid` 时的 id 令牌），无需其凭据。适用于测试和技术支持。参数通过查询字符串传递。

| 查询参数 | 必需 | 描述 |
|---|---|---|
| `clientId` | 是 | 令牌为之签发的客户端。令牌有效期来自此客户端的配置。 |
| `userId` | 是 | 要模拟的用户。 |
| `scopes` | 否 | **空格分隔**的作用域列表（请对空格进行 URL 编码）。省略时默认为该客户端的 `AllowedScopes`。 |

限制：

- 作用域受限于该客户端的 `AllowedScopes`——请求任何该客户端自身无法请求的作用域会返回 `400 invalid_scope`。
- 管理作用域（`AdminApi:Scope`，默认 `authagonal-admin`）**不能**通过此端点签发；请求它会返回 `403 forbidden_scope`。这可以防止（可能有时限的）管理令牌铸造出长期有效的管理访问/刷新令牌。

响应是标准的令牌响应，包含 `access_token`、`refresh_token`、可选的 `id_token`、`expires_in`，以及已授予的 `scope`（空格分隔）。
