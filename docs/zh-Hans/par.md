---
layout: default
title: 推送式授权请求
locale: zh-Hans
---

# 推送式授权请求（PAR）

[RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) 允许客户端使用标准的客户端认证，将其 authorize 请求参数直接 POST 给服务器，并收到一个短生命周期的不透明 `request_uri` 交给浏览器。浏览器随后访问 `/connect/authorize?request_uri=...&client_id=...`，而不必在 URL 上携带每一个参数。

为什么使用它：

- authorize 参数绝不会出现在浏览器历史、服务器日志或 `Referer` 头中。
- 服务器在推送时对客户端进行认证，因此参数在任何重定向发生之前就已完成完整性校验。
- 长参数集（大型 `claims` 请求、多资源流程）不会撑破 URL 长度限制。

## 端点

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

认证方式与 `/connect/token` 相同：使用 `client_id`/`client_secret` 的 HTTP Basic，或表单编码的凭据。机密客户端必须认证；公共客户端不带密钥提交。客户端认证失败返回 `401`（依据 RFC 9126——与令牌端点不同，在令牌端点只有 `invalid_client` 才是 401）。

表单请求体携带的参数与通常会放到 `/connect/authorize` 上的相同（`response_type`、`redirect_uri`、`scope`、`state`、`code_challenge`、`code_challenge_method`、`nonce`、`resource` 等）。`request_uri` 本身会被拒绝——规范 §2.1 禁止串联 PAR。如果请求体携带了 `client_id`，它必须与已认证的客户端匹配。

### 响应

```
HTTP/1.1 201 Created
```
```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

`request_uri` 是一次性的。一旦匹配的 `/connect/authorize` 请求消费了它（或 90 秒窗口过期，以较早者为准），它就会从存储中移除。

### 授权步骤

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

当 `request_uri` 存在时，所有其他参数都从推送的载荷中拉取——URL 上的其他任何内容都会被忽略。此请求上的 `client_id` 必须与推送该载荷的客户端匹配。

## 按客户端要求 PAR

在客户端上设置 `RequirePushedAuthorizationRequests = true`，以拒绝来自它的普通 `/connect/authorize` 请求。任何非 PAR 的 authorize 尝试都会返回 `invalid_request`，描述为 "This client requires requests to be pushed via /connect/par"。

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

对于处理敏感作用域的客户端，这是推荐的姿态——与 PKCE 结合，它把 URL 地址栏从攻击面上移除。

## 生命周期与存储

`request_uri` 的生命周期由服务器设定为 90 秒，与典型的参考 IdP 值一致。推送的载荷通过与授权码和刷新令牌相同的 `IGrantStore` 存储，因此它们会自动继承宿主的持久化和复制策略。

## 发现

PAR 端点在 `.well-known/openid-configuration` 中如下公布自身：

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```
