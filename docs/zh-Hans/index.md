---
layout: default
title: 首页
locale: zh-Hans
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

面向 .NET 的 OAuth 2.0 / OpenID Connect / SAML 2.0 认证服务器，采用可插拔的存储后端：您自己的 PostgreSQL 或 SQLite、Azure Table Storage，或 AWS（DynamoDB / S3 / Secrets Manager）。

单一、自包含的部署。服务器和登录界面作为一个 Docker 镜像发布 -- SPA 与 API 从同一来源提供服务，因此 Cookie 认证、重定向和 CSP 均无需处理跨域复杂性。

> **更想要托管服务？** [Authagonal Cloud](https://authagonal.io) 为你运行这一切 -- 多租户，每个套餐都包含全部功能，且不按连接收取 SSO 费用。→ [authagonal.io](https://authagonal.io)

## 核心功能

- **OIDC 提供者** -- authorization_code + PKCE、client_credentials、带一次性轮换的 refresh_token 授权类型
- **SAML 2.0 SP** -- 自研实现，全面支持 Azure AD（签名响应、断言或两者兼有）、用于签名 AuthnRequest 的按连接 SP 密钥对 + `EncryptedAssertion` 解密，以及单点登出（SP 发起和 IdP 发起）
- **动态 OIDC 联合** -- 连接 Google、Apple、Azure AD 或任何符合 OIDC 标准的 IdP
- **多因素认证** -- TOTP、WebAuthn/通行密钥、恢复码；按客户端的策略（`Disabled` / `Enabled` / `Required`），可通过 `IAuthHook` 进行按用户覆盖，联合登录同样强制执行
- **SCIM 2.0 预配** -- 从 Entra ID、Okta、OneLogin 入站预配用户 / 组；游标分页列表和基于盲索引的 `eq` 过滤
- **OAuth 同意界面** -- 按客户端的同意，支持随作用域变化的再次征询以及授权管理
- **设备授权许可** -- 面向输入受限设备（智能电视、CLI、IoT）的 RFC 8628 流程
- **令牌自省** -- RFC 7662，供资源服务器校验令牌有效性
- **令牌签名** -- 仅 ES256。访问令牌带有 RFC 9068 的 `typ: at+jwt`，以便资源服务器能把它们与
  id_token 和登出令牌区分开，但**并不声称符合 RFC 9068**：§2.1 要求在受支持算法中包含 RS256，而本服务器
  既不签发也不接受它。只用一种算法是有意为之的立场：每多接受一种算法，就多一条把校验方诱导到错误算法上的
  路径。
- **后通道登出** -- 向依赖方发送 OIDC Back-Channel Logout 1.0 通知
- **GDPR 自助服务** -- 从托管的账户页面导出数据并计划删除账户
- **TCC 预配** -- 在授权时通过 Try-Confirm-Cancel 模式将用户预配到下游应用
- **可定制登录界面** -- 通过 JSON 文件进行运行时配置 -- 徽标、颜色、自定义 CSS -- 无需重新构建；已本地化为 10 种语言
- **认证钩子** -- `IAuthHook` 扩展性，支持审计日志、自定义验证、Webhook
- **PII 加密接缝** -- `IFieldCipher` / `IIndexTokenizer` 扩展点，用于静态字段级加密并支持带密钥的盲索引（HMAC）搜索；恢复码通过 `ISecretProvider` 加密
- **HashiCorp Vault Transit** -- 远程签发 JWT，无需在本地接触私钥
- **可组合库** -- `AddAuthagonal()` / `UseAuthagonal()` 可在您自己的项目中托管，并支持自定义服务覆盖
- **Native AOT 就绪** -- IL 裁剪与源生成的 JSON 序列化，启动更快
- **可插拔存储** -- 自托管的 PostgreSQL 或 SQLite（无需云账户），或 Azure Table Storage / AWS（DynamoDB / S3 / Secrets Manager）作为低成本、无服务器友好的后端
- **备份与恢复** -- 增量备份（由变更日志驱动，并带全量扫描兜底）、完整性校验、基于墓碑的删除跟踪
- **管理 API** -- 用户 CRUD、SAML/OIDC 提供者管理、SSO 域路由、令牌模拟

## 常见集成

面向团队最常构建的那些流程的任务式指南。这些页面目前仅有英文版：

- **[升级用户](../user-upgrade)** -- 通过无密码账户认领，把访客 / SSO / 邀请账户转为拥有自有凭据的账户，并在确认时执行访客到正式成员的升级。
- **[自助 SSO](../self-service-sso)** -- 面向企业连接的 JIT 预配：仅限邀请与自助两种上线方式的取舍、如何避免外部 IdP 变成隐患，以及联合前的过渡页。
- **[联合会话](../federated-sessions)** -- 当上游 IdP 撤销会话时同步撤销本地会话（`RevalidateOnRefresh`）。
- **[WebSocket 认证](../websocket-auth)** -- 通过 BFF 对浏览器 WebSocket 进行认证，而不暴露令牌。
- **[代理式认证](../agentic-auth)** -- 把用户的权限委托给 AI 代理：已注册的代理、细粒度的 RFC 9396 权限、复合委托令牌（RFC 8693 `act`）、常驻同意、即时审批、能力票据。

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
