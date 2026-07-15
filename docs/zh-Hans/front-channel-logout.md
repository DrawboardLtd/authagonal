---
layout: default
title: 前通道注销
locale: zh-Hans
---

# 前通道注销

Authagonal 实现了 **OpenID Connect Front-Channel Logout 1.0**，这是一种由浏览器驱动的注销机制，与[后通道注销](index#features)互为补充。后通道注销是服务器到服务器的 POST，而前通道注销会在一个隐藏的 iframe 中渲染每个信赖方的注销 URL，从而在用户的浏览器内部清理每个应用的浏览器会话（Cookie、本地存储）。

## 何时使用哪一种

| 关注点 | 后通道 | 前通道 |
|---|---|---|
| 服务器端会话 | ✅ | ❌ |
| 浏览器 Cookie / 本地存储 | ❌ | ✅ |
| 用户浏览器离线时也能工作 | ✅ | ❌ |
| 能挺过网络错误（重试） | ✅ | ❌（单次尽力而为的尝试） |

大多数应用同时配置**两者**会获益。后通道保证服务器被告知；前通道清理浏览器。

## 客户端配置

在 `OAuthClient` 记录中添加一个前通道注销 URI：

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| 字段 | 描述 |
|---|---|
| `FrontChannelLogoutUri` | 客户端的浏览器可见注销端点 |
| `FrontChannelLogoutSessionRequired` | 若为 `true`（默认），则会带上 `iss` 和 `sid` 查询参数调用该 URL，以便客户端将注销与特定会话关联 |

## 工作原理

当浏览器访问 `/connect/endsession` 时：

1. 服务器找出该用户当前持有授权的所有客户端。
2. 对于每个带有 `FrontChannelLogoutUri` 的客户端，服务器构建一个 URL——若 `FrontChannelLogoutSessionRequired` 为 `true`，则追加 `iss=<issuer>`（以及 `sid=<session_id>`，当会话有一个时）。
3. 服务器将用户从授权服务器 Cookie 中注销，在后台触发后通道注销通知，并返回一个 HTML 页面，其中为每个客户端注销 URL 包含一个隐藏的 `<iframe>`：
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
4. 经过 2 秒的宽限期后，浏览器被重定向到 `post_logout_redirect_uri`——仅当请求同时携带用于标识客户端的 `id_token_hint`，且该 URI 在该客户端已注册的 `PostLogoutRedirectUris` 中时才会被采纳（若提供了 `state` 参数，会附加到重定向上）。否则会显示一个“已注销”的确认页面。

## 客户端侧注销处理器

每个信赖方都应实现 `FrontChannelLogoutUri` 所引用的 URL。一个最小化的处理器：

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. 验证 `iss` 与预期的授权服务器匹配。
2. 如果提供了 `sid`，确认它与会话 Cookie 的会话 ID 匹配。
3. 清除本地会话（Cookie、服务器端会话、SPA 存储）。
4. 以 `200 OK` 和空响应体（或一个极小的页面）作答——该响应对用户永远不可见。

```csharp
app.MapGet("/oidc/frontchannel", (HttpContext ctx) =>
{
    var iss = ctx.Request.Query["iss"].ToString();
    var sid = ctx.Request.Query["sid"].ToString();
    // Validate iss/sid, then clear local session
    ctx.SignOutAsync();
    return Results.Ok();
});
```

## 发现文档

前通道注销在 `/.well-known/openid-configuration` 中公布：

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## 动态客户端注册

通过[动态客户端注册](client-registration)注册的客户端可以包含：

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

## 局限性

- **尽力而为**——iframe 只加载一次。如果网络错误或浏览器扩展阻止了它们，则不会重试。请与后通道注销搭配以获得可靠性。
- **第三方 Cookie**——某些浏览器默认会阻止跨站点 iframe 中的 Cookie。如果您的 RP 依赖第一方 Cookie，请确认注销处理器不依赖于 Cookie 被发送。
- **超时**——页面在重定向 / 确认之前会等待约 2 秒。繁重的 RP 注销处理器可能无法在此时间内完成。

## 相关

- [动态客户端注册](client-registration)——注册请求中的前通道参数
- [OAuth 作用域](scopes)——作用域感知的同意与注销流程相辅相成
