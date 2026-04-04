---
layout: default
title: 快速入门
locale: zh-Hans
---

# 快速入门

5 分钟内让 Authagonal 在本地运行。

## 1. 启动服务器

```bash
docker compose up
```

这会在 `http://localhost:8080` 上启动 Authagonal，并使用 Azurite 作为存储。

## 2. 验证运行状态

```bash
# Health check
curl http://localhost:8080/health

# OIDC discovery
curl http://localhost:8080/.well-known/openid-configuration

# Login page (returns the SPA)
curl http://localhost:8080/login
```

## 3. 注册客户端

将客户端添加到您的 `appsettings.json`（或通过环境变量传入）：

```json
{
  "Clients": [
    {
      "ClientId": "my-web-app",
      "ClientName": "My Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["http://localhost:3000/callback"],
      "PostLogoutRedirectUris": ["http://localhost:3000"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["http://localhost:3000"],
      "RequirePkce": true,
      "RequireClientSecret": false
    }
  ]
}
```

客户端在启动时播种 -- 每次部署运行都是安全的。

## 4. 发起登录

将用户重定向到：

```
http://localhost:8080/connect/authorize
  ?client_id=my-web-app
  &redirect_uri=http://localhost:3000/callback
  &response_type=code
  &scope=openid profile email
  &state=random-state
  &code_challenge=...
  &code_challenge_method=S256
```

用户将看到登录页面，完成认证后被重定向回来，携带授权码。

## 5. 兑换授权码

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

响应：

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800
}
```

## 示例演示

`demos/sample-app/` 目录包含一个完整的 React SPA + API，实现了上述完整的 OIDC 流程。请参阅 [demos README](https://github.com/DrawboardLtd/authagonal/tree/master/demos) 了解使用说明。

## 后续步骤

- [配置](configuration) -- 所有设置的完整参考
- [扩展性](extensibility) -- 作为库托管，添加自定义钩子
- [品牌定制](branding) -- 自定义登录界面
- [SAML](saml) -- 添加 SAML SSO 提供者
- [预配](provisioning) -- 将用户预配到下游应用
