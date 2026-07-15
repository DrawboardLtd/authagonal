---
layout: default
title: OAuth 作用域
locale: zh-Hans
---

# OAuth 作用域

Authagonal 同时支持**内置**的 OAuth/OIDC 作用域和在运行时管理的**自定义**作用域。自定义作用域会被持久化、通过发现文档公布，并与内置作用域一起显示在同意界面上。

## 内置作用域

这些作用域始终可用，无需注册：

| 作用域 | 用途 |
|---|---|
| `openid` | 发起 OIDC 流程所必需。签发一个 ID 令牌。 |
| `profile` | 标准的个人资料声明（name、family_name、given_name 等） |
| `email` | 电子邮件地址和 `email_verified` 声明 |
| `offline_access` | 在访问令牌之外再签发一个刷新令牌 |

## 自定义作用域

自定义作用域通过管理 API 在 `/api/v1/scopes` 处管理。它们需要一个带有 `authagonal-admin` 作用域的 JWT 访问令牌（可通过 `AdminApi:Scope` 配置）。

### 作用域模型

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| 字段 | 描述 |
|---|---|
| `Name` | 在令牌请求中发送的作用域标识符（例如 `billing.read`） |
| `DisplayName` | 显示在同意界面上的人类可读名称 |
| `Description` | 显示在同意界面上的较长描述 |
| `Emphasize` | 若为 `true`，同意界面会将此作用域突出显示为敏感 |
| `Required` | 若为 `true`，用户在同意时无法取消勾选此作用域 |
| `ShowInDiscoveryDocument` | 若为 `true`，此作用域会出现在 `/.well-known/openid-configuration` 的 `scopes_supported` 下 |
| `UserClaims` | 授予此作用域时添加到访问令牌的声明 |

## 管理端点

### 列出作用域

```
GET /api/v1/scopes
```

返回 `{ "scopes": [ ... ] }`。

### 获取作用域

```
GET /api/v1/scopes/{name}
```

返回该作用域，若未找到则返回 `404`。

### 创建作用域

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing — read-only",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

返回 `201 Created` 及该作用域。如果已存在同名作用域，则返回 `409`。

### 更新作用域

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing — read",
  "description": "View invoices",
  "emphasize": true
}
```

只有提供的字段会被更新；省略的字段保留其当前值。

### 删除作用域

```
DELETE /api/v1/scopes/{name}
```

返回 `204 No Content`（若作用域不存在则返回 `404`）。已签发且包含此作用域的令牌在过期前仍然有效——如有需要，请通过 `/connect/revocation` 显式撤销它们。

## 发现文档

`ShowInDiscoveryDocument = true` 的作用域会出现在 `/.well-known/openid-configuration` 的 `scopes_supported` 下。内置作用域始终会被公布。

```json
{
  "scopes_supported": ["openid", "profile", "email", "offline_access", "billing.read"]
}
```

## 同意界面

当客户端请求一个不在其“跳过同意”列表中的作用域时，同意页面会按 `DisplayName`（回退到 `Name`）列出每个所请求的作用域，并在其下方附上 `Description`。`Emphasize = true` 的作用域会获得独特的视觉呈现。`Required` 作用域无法被取消勾选。

面向用户的流程请参阅 [OAuth 同意界面](index#features)。

## 动态客户端注册

通过[动态客户端注册](client-registration)注册的客户端只能请求内置的、或此前通过管理 API 创建的作用域。未知的作用域会以 `invalid_scope` 被拒绝。
