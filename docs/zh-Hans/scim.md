---
layout: default
title: SCIM 2.0 预配
locale: zh-Hans
---

# SCIM 2.0 预配

Authagonal 支持 SCIM 2.0（跨域身份管理系统），可从 Microsoft Entra ID、Okta 和 OneLogin 等企业身份提供商自动预配用户。

## 概述

SCIM 是一种入站预配协议：您的身份提供商将用户和组的变更推送到 Authagonal。这与现有的 TCC（Try-Confirm-Cancel）出站预配互为补充，后者将用户推送到下游应用。

**支持的操作：**
- 用户 CRUD（创建、读取、更新、通过软停用删除）
- 组 CRUD 及成员管理
- 过滤（RFC 7644 §3.4.2.2 的完整过滤语法）
- 分页：用户列表使用基于游标的分页（`cursor`/`nextCursor`），组使用 `startIndex` 和 `count`
- PATCH 部分更新（包括 `active=false` 停用）
- 在令牌签发时解析的组到角色映射

**不支持：** 批量操作、排序、ETag、通过 SCIM 管理密码。

所有资源都限定在预配它们的 SCIM 客户端范围内：由某个 SCIM 令牌的客户端创建的用户或组，对其他所有 SCIM 客户端都是不可见的（404）。

## 生成 SCIM 令牌

SCIM 端点使用静态 Bearer 令牌进行认证。通过管理 API 生成令牌：

```http
POST /api/v1/scim/tokens
Authorization: Bearer {admin-token}
Content-Type: application/json

{
  "clientId": "your-client-id",
  "description": "Entra ID SCIM token",
  "expiresInDays": 365
}
```

响应中的原始令牌**仅返回一次**。它以 SHA-256 哈希形式存储，之后无法找回，因此请妥善保管：

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "description": "Entra ID SCIM token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z"
}
```

省略 `expiresInDays`（或传入 `0`）可生成不过期的令牌。

### 列出令牌

```http
GET /api/v1/scim/tokens?clientId=your-client-id
Authorization: Bearer {admin-token}
```

### 吊销令牌

```http
DELETE /api/v1/scim/tokens/{tokenId}?clientId=your-client-id
Authorization: Bearer {admin-token}
```

## 配置您的身份提供商

### 租户 URL

```
https://your-authagonal-instance/scim/v2
```

### 认证

使用 **OAuth Bearer Token**，填入上面生成的令牌。

### Microsoft Entra ID

1. 在 Azure 门户中，转到 **Enterprise Applications** > 您的应用 > **Provisioning**
2. 将预配模式设为 **Automatic**
3. 输入租户 URL：`https://your-instance/scim/v2`
4. 输入机密令牌：生成步骤中得到的原始令牌
5. 点击 **Test Connection** 验证
6. 配置属性映射（见下文）

### Okta

1. 在 Okta 管理控制台中，转到 **Applications** > 您的应用 > **Provisioning**
2. 启用 **SCIM connector**
3. 设置 Base URL：`https://your-instance/scim/v2`
4. 将认证模式设为 **HTTP Header**
5. 输入 Bearer 令牌

### OneLogin

1. 在 OneLogin 管理界面中，转到 **Applications** > 您的应用 > **Provisioning**
2. 启用预配
3. 设置 SCIM Base URL：`https://your-instance/scim/v2`
4. 设置 SCIM Bearer 令牌

## SCIM 端点

| 方法 | 路径 | 描述 |
|--------|------|-------------|
| GET | `/scim/v2/Users` | 列出/过滤用户 |
| GET | `/scim/v2/Users/{id}` | 获取单个用户 |
| POST | `/scim/v2/Users` | 创建用户 |
| PUT | `/scim/v2/Users/{id}` | 替换用户 |
| PATCH | `/scim/v2/Users/{id}` | 部分更新 |
| DELETE | `/scim/v2/Users/{id}` | 墓碑（停用；之后的 GET 为 404） |
| GET | `/scim/v2/Groups` | 列出/过滤组 |
| GET | `/scim/v2/Groups/{id}` | 获取单个组 |
| POST | `/scim/v2/Groups` | 创建组 |
| PUT | `/scim/v2/Groups/{id}` | 替换组 |
| PATCH | `/scim/v2/Groups/{id}` | 添加/移除成员 |
| DELETE | `/scim/v2/Groups/{id}` | 删除组 |
| GET | `/scim/v2/ServiceProviderConfig` | 能力声明 |
| GET | `/scim/v2/Schemas` | 架构定义 |
| GET | `/scim/v2/ResourceTypes` | 资源类型 |

每个端点也映射了不带 `/v2` 段的形式（例如 `/scim/Users`），以适配会自行追加路径的身份提供商。发现端点（`ServiceProviderConfig`、`Schemas`、`ResourceTypes`，以及裸的 `/scim/` 和 `/scim/v2/` 基础 URL，后两者返回 ServiceProviderConfig）是匿名的；其余所有端点都需要 SCIM Bearer 令牌。

用户端点和群组端点按 SCIM 客户端限速为每分钟 200 个请求；超出的请求会收到状态为 `429` 的 SCIM 错误。

## 属性映射

### 用户属性

| SCIM 属性 | Authagonal 字段 |
|---------------|------------------|
| `userName` | `Email` |
| `name.givenName` | `FirstName` |
| `name.familyName` | `LastName` |
| `displayName` | `FirstName LastName` |
| `emails[type eq "work"].value` | `Email` |
| `active` | `IsActive` |
| `externalId` | `ExternalId` |
| `preferredLanguage`（回退到 `locale`） | `Locale` |

### 组属性

| SCIM 属性 | Authagonal 字段 |
|---------------|------------------|
| `displayName` | `DisplayName` |
| `externalId` | `ExternalId` |
| `members` | `MemberUserIds` |

## 行为细节

### 用户创建
- SCIM 预配的用户在创建时带有 `EmailConfirmed = true`（仅 SSO，无密码）。
- `ScimProvisionedByClientId` 字段记录是哪个 SCIM 客户端创建了该用户。
- 如果客户端配置了 `ProvisioningApps`，会自动触发 TCC 预配。如果预配拒绝了该用户，SCIM 创建会被回滚，响应是符合 SCIM 格式的 `400`，带 `scimType: invalidValue` 和一条固定消息（下游应用自己的文本被有意不回传给 SCIM 客户端）。
- 创建的用户如果其 `userName` 或 `externalId` 已存在，会返回 SCIM `409` 冲突。通过 PUT 或 PATCH 修改邮箱时会以同样的方式做冲突检查。

### 用户停用
- `DELETE /scim/v2/Users/{id}` 会写入**墓碑（tombstone）**：停用该用户、保留本地记录，并标记 `ScimDeletedAt`。之后的 `GET /scim/v2/Users/{id}` 返回 **404**，符合 RFC 7644 §3.6 的要求（"服务提供方对已删除资源的所有操作都必须返回 404"）。因此不要通过重新读取资源并期望 `active: false` 来确认取消预配——读取结果就是 404，而这正是成功。
- 保留记录而不是彻底删除，是为了让再次入职的人可以被重新创建：墓碑会释放新资源所需的 `userName`/`externalId`，同时本地账户、其审计历史以及群组成员关系都会保留。
- 带 `active = false` 的 `PATCH` 同样会停用该用户。
- 被停用的用户无法通过密码、SAML 或 OIDC 登录。
- 停用时会吊销所有授权（刷新令牌、会话）。
- 只有 `DELETE` 会触发下游应用的取消预配；`PATCH` 停用会吊销授权，但不会触及下游应用。

### 过滤
支持的过滤表达式：
- `userName eq "user@example.com"`
- `externalId eq "12345"`
- `displayName co "John"`

仅支持单属性过滤。不支持复杂布尔表达式（`and`、`or`）。

`userName` 和 `externalId` 上的 `eq` 过滤（Entra 和 Okta 在每次创建或更新之前发出的查询）通过带索引的点查询解析，而不是列表扫描，因此在任何用户规模下都能保持快速。其他过滤（`co`，或对 `displayName` 的过滤）则在分页遍历该客户端的用户时应用。

### 分页
用户列表使用**游标分页**。`GET /scim/v2/Users` 的每一页都会在列表响应中返回一个 `nextCursor` 属性；将其作为 `?cursor=` 传回即可获取下一页。当 `nextCursor` 不存在时，列表即已取完。页面大小由 `count` 控制（默认 100，最大 200）。

在 Users 端点请求大于 1 的 `startIndex` 会返回 `400` 错误并引导您使用游标分页；不提供越过第一页的偏移量分页。只要 `nextCursor` 存在，`totalResults` 就会被**省略**，只有在最后一页才携带精确总数——它有意不报告本次返回的页大小，因为把两者混淆的客户端会静默地读不全整个目录。请用 `nextCursor` 驱动循环，而不是 `totalResults`；并把缺失的 `totalResults` 视为"尚未知道"，而不是 0。

组列表仍使用 `startIndex`/`count` 偏移量分页。

### 通过 PATCH 管理组成员
`PATCH /scim/v2/Groups/{id}` 接受主流身份提供商实际发送的各种成员格式：

- **添加成员：** `op: "add"`，带 `path: "members"` 和由 `{ "value": "user-id" }` 对象组成的 value 数组。重复项会被忽略。
- **替换成员：** `op: "replace"`，带 `path: "members"`，用提供的数组替换全部成员。
- **移除特定成员（value 数组）：** `op: "remove"`，带 `path: "members"` 和要移除的成员 id 的 value 数组（Entra ID 发送的格式）。
- **移除特定成员（路径过滤器）：** `op: "remove"`，带 `path: 'members[value eq "user-id"]'`，id 携带在路径过滤器中且不带 value（Okta 在取消预配时发送的格式）。
- **移除所有成员：** `op: "remove"`，带 `path: "members"` 且不带 value，会清空该组。

### 组到角色映射
SCIM 组的成员身份可以授予应用角色。映射按（组、角色）对每对一行存储，一个组可以授予多个角色。它们在**令牌签发**时解析：用户的有效角色是其直接分配的角色，加上其所属的每个已映射组的角色，因此添加或移除组成员会在下一个令牌上生效，而无需触及用户记录。空的映射存储是无操作。

映射通过 `IScimGroupRoleMappingStore` 持久化（由 Azure 和 AWS 存储提供程序实现；否则注册一个内存中的默认实现），并由宿主应用的管理界面管理，而不是通过 SCIM API 本身。

另外，启用了 `IncludeGroupsInTokens` 的客户端还会在签发的令牌中收到用户的 SCIM 组显示名称作为 `groups` 声明。

## 已知限制

- **无批量操作：** 用户和组必须逐个预配。
- **无排序：** 游标分页下用户列表按存储顺序返回；组列表按创建日期排序。
- **无密码管理：** SCIM 预配的用户仅通过 SSO 认证。
- **墓碑而非删除：** `DELETE` 会停用并写入墓碑（之后的 `GET` 为 404，符合 RFC 7644 §3.6），而不是永久移除本地用户记录。需要彻底删除请使用管理 API。
