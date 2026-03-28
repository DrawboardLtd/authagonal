---
layout: default
title: 首页
locale: zh-Hans
---

# Authagonal

基于 Azure Table Storage 的 OAuth 2.0 / OpenID Connect / SAML 2.0 认证服务器。

Authagonal 以单一、自包含的部署方式取代了 Duende IdentityServer + Sustainsys.Saml2。服务器和登录界面作为一个 Docker 镜像发布 -- SPA 与 API 从同一来源提供服务，因此 Cookie 认证、重定向和 CSP 均无需处理跨域复杂性。

## 核心功能

- **OIDC 提供者** -- authorization_code + PKCE、client_credentials、带一次性轮换的 refresh_token 授权类型
- **SAML 2.0 SP** -- 自研实现，全面支持 Azure AD（签名响应、断言或两者兼有）
- **动态 OIDC 联合** -- 连接 Google、Apple、Azure AD 或任何符合 OIDC 标准的 IdP
- **TCC 预配** -- 在授权时通过 Try-Confirm-Cancel 模式将用户预配到下游应用
- **可定制登录界面** -- 通过 JSON 文件进行运行时配置 -- 徽标、颜色、自定义 CSS -- 无需重新构建
- **认证钩子** -- `IAuthHook` 扩展性，支持审计日志、自定义验证、Webhook
- **可组合库** -- `AddAuthagonal()` / `UseAuthagonal()` 可在您自己的项目中托管，并支持自定义服务覆盖
- **Azure Table Storage** -- 低成本、无服务器友好的存储后端
- **管理 API** -- 用户 CRUD、SAML/OIDC 提供者管理、SSO 域路由、令牌模拟

## 架构

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    │                             │                                    │
    ├─ GET /connect/authorize ──► │                                    │
    │                             ├─ 302 → /login (SPA)                │
    │                             │   ├─ SSO check                     │
    │                             │   └─ SAML/OIDC redirect ─────────► │
    │                             │                                    │
    │                             │ ◄── SAML Response / OIDC callback ─┤
    │                             │   └─ Create user + cookie          │
    │                             │                                    │
    │                             ├─ TCC provisioning (try/confirm)    │
    │                             ├─ Issue authorization code          │
    │ ◄─ 302 ?code=...&state=... ┤                                    │
    │                             │                                    │
    ├─ POST /connect/token ─────► │                                    │
    │ ◄─ { access_token, ... } ──┤                                    │
```

通过[安装](installation)指南开始使用，或直接跳转到[快速入门](quickstart)。如需在您自己的项目中托管 Authagonal，请参阅[扩展性](extensibility)。
