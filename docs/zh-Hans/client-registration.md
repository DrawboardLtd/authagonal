---
layout: default
title: 动态客户端注册
locale: zh-Hans
---

# 动态客户端注册

Authagonal 实现了 **OAuth 2.0 动态客户端注册**（[RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)），允许客户端应用在运行时自行注册，无需管理员参与。

## 启用端点

动态注册**默认禁用**。通过配置选择启用：

```json
{
  "Auth": {
    "DynamicClientRegistrationEnabled": true
  }
}
```

或将 `Auth__DynamicClientRegistrationEnabled=true` 设为环境变量。

启用后，发现文档会公布该端点：

```
GET /.well-known/openid-configuration
```
```json
{
  "registration_endpoint": "https://auth.example.com/connect/register"
}
```

## 注册客户端

```
POST /connect/register
Content-Type: application/json

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access",
  "audiences": ["https://api.myapp.example.com"],
  "allowed_cors_origins": ["https://myapp.example.com"],
  "backchannel_logout_uri": "https://myapp.example.com/oidc/backchannel",
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

### 响应

```
HTTP/1.1 201 Created
Content-Type: application/json

{
  "client_id": "a1b2c3d4e5f6...",
  "client_secret": "xkCd2_base64url...",
  "client_id_issued_at": 1745000000,
  "client_secret_expires_at": 0,
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email offline_access",
  "token_endpoint_auth_method": "client_secret_basic"
}
```

`client_secret` **只返回一次**，之后无法再取回。请妥善存储。

## 请求参数

| 参数 | 必需 | 说明 |
|---|---|---|
| `client_name` | 否 | 省略时默认为生成的 `client_id` |
| `redirect_uris` | 有条件 | 当 `grant_types` 包含 `authorization_code` 时必填。必须是绝对 URI；`javascript:`/`data:`/`vbscript:`/`file:` 方案会被拒绝（用于移动端深度链接的原生自定义方案则可以）。 |
| `post_logout_redirect_uris` | 否 | 注销后的有效重定向目标 |
| `grant_types` | 否 | 默认为 `["authorization_code"]`。**只有 `authorization_code` 和 `refresh_token` 可注册**——`client_credentials`、`implicit`、设备码及任何其他授权类型都会以 `invalid_client_metadata` 被拒绝，因此开放注册永远无法铸造出机器对机器的客户端。若请求了 `offline_access`，则会自动添加 `refresh_token`。 |
| `token_endpoint_auth_method` | 否 | `client_secret_basic`（默认）、`client_secret_post`，或用于公共客户端的 `none` |
| `scope` | 否 | 空格分隔的作用域——必须全部为内置或此前已注册的（参见[作用域](scopes)）。管理作用域（`AdminApi:Scope`，默认 `authagonal-admin`）永远不能被注册。 |
| `audiences` | 否 | 添加到访问令牌的 JWT `aud` 值 |
| `allowed_cors_origins` | 否 | 允许从浏览器调用令牌端点的来源 |
| `backchannel_logout_uri` | 否 | 启用[后通道注销](index#features) |
| `frontchannel_logout_uri` | 否 | 启用[前通道注销](front-channel-logout) |
| `frontchannel_logout_session_required` | 否 | 默认为 `true`；为 `true` 时，注销 URL 会携带 `iss` 和 `sid` 参数 |

## 默认值与不变量

- **要求 PKCE**——对于动态注册的客户端，`RequirePkce` 始终为 `true`。
- **公共客户端**——`token_endpoint_auth_method: "none"` 会生成一个没有密钥的客户端。仍然要求 PKCE。
- **离线访问**——请求作用域 `offline_access` 会隐式地向 `grant_types` 添加 `refresh_token`。

## 错误响应

| HTTP | `error` | 原因 |
|---|---|---|
| `400` | `invalid_redirect_uri` | `redirect_uris` 中有一个不是有效的绝对 URI，或使用了 script/data/file 伪方案 |
| `400` | `invalid_client_metadata` | 请求了不可注册的授权类型，或对某个需要 `redirect_uris` 的授权类型缺失了它 |
| `400` | `invalid_scope` | 请求的某个作用域既非内置也未注册 |
| `403` | `invalid_scope` | 请求了管理作用域——它永远不能通过注册被授予 |
| `403` | `not_supported` | 未启用动态客户端注册 |
| `429` | `rate_limited` | 来自此 IP 的注册过多（每小时 10 次） |

## 安全考量

注册端点是**无需认证的**，但在设计上受到约束：

- **速率限制**——每个 IP 每滚动小时 10 次注册（`429 rate_limited`），因此客户端存储无法被灌满。
- **授权类型受限**——只有 `authorization_code` + `refresh_token`；已注册的客户端始终需要一个由用户介入的流程，永远不能充当机器对机器的客户端。
- **管理作用域为保留项**——`authagonal-admin` 作用域（或 `AdminApi:Scope` 所设的任何值）会被拒绝，因此注册永远无法产生一个能触及[管理 API](admin-api) 的客户端。
- 已注册客户端**始终要求 PKCE**。

若需更强的门控（初始访问令牌、mTLS、软件声明），请在该端点前面加上您自己的中间件或 `IAuthHook`。在不要求自助注册的环境中，请考虑彻底禁用动态注册，改为通过管理 API 管理客户端。
