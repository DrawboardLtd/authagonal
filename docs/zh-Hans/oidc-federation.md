---
layout: default
title: OIDC 联合
locale: zh-Hans
---

# OIDC 联合

Authagonal 可以将认证联合到外部 OIDC 身份提供者（Google、Apple、Azure AD 等）。这允许"使用 Google 登录"类型的流程，同时 Authagonal 仍然是中心认证服务器。

## 工作原理

进入联合有两条路径：

**基于域（交互式登录）：**

1. 用户在登录页面输入邮箱
2. SPA 调用 `/api/auth/sso-check` -- 如果邮箱域链接到 OIDC 提供者，则需要 SSO
3. 用户点击"通过 SSO 继续" -> 重定向到外部 IdP
4. 认证后，IdP 重定向回 `/oidc/callback`
5. Authagonal 验证 id_token，创建/关联用户，并设置会话 Cookie

**RP 提示（`idp_hint`）：**

下游依赖方可以直接路由到特定的上游 IdP，而无需经过邮箱/SSO 域这一步。在 `/connect/authorize` 上追加 `idp_hint={connectionId}`：

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

当请求未认证时，Authagonal 会重定向到 `/oidc/{connectionId}/login`，并把原始的 `/authorize` URL 作为 `returnUrl` 保留。联合完成后，用户带着会话 Cookie 回到 `/authorize`，流程照常继续。

## 设置

### 1. 创建 OIDC 提供者

**选项 A -- 配置（推荐用于静态设置）：**

添加到 `appsettings.json`：

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

提供者在启动时播种。可播种的字段正是所示的这些，但不含 `RedirectUrl`：`ConnectionId`、`ConnectionName`、`MetadataLocation`、`ClientId`、`ClientSecret`、`AllowedDomains`。`RedirectUrl` 仅为兼容而接受并被忽略——重定向 URI 按请求推导为 `{Issuer}/oidc/callback`，因为它必须位于浏览器所在的 origin 上；应向 IdP 注册的是该 URI。`ClientSecret` 通过 `ISecretProvider` 保护（配置 Key Vault 时使用 Key Vault，否则使用纯文本）。SSO 域映射从 `AllowedDomains` 自动注册。

连接模型还带有额外的可选行为 -- `PassthroughParams`（可通过管理 API 创建时设置），以及 `SessionExpClaim` 和 `DisableJitProvisioning`（存储级字段，由宿主代码通过 `IOidcProviderStore` 设置）-- 参见下文的[作用域与声明透传](#scope-and-claim-flow-through)和[会话生命周期上限](#session-lifetime-cap)。

**选项 B -- 管理 API（用于运行时管理）：**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"]
  }'
```

### 2. SSO 域路由

当指定了 `AllowedDomains`（在配置中或通过创建 API），SSO 域映射会自动注册。如果没有域路由，用户仍然可以通过 `/oidc/{connectionId}/login` 被引导到 OIDC 登录。

## 端点

| 端点 | 描述 |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...` | 发起 OIDC 登录。生成 PKCE + state + nonce，从 `returnUrl` 推导上游作用域和透传参数，重定向到 IdP 的授权端点。 |
| `GET /oidc/callback` | 处理 IdP 回调。用授权码交换令牌，验证 id_token，将每个非协议声明以 `federated:*` 形式捕获到 Cookie 上，创建/登录用户。 |

## 作用域与声明透传 {#scope-and-claim-flow-through}

下游 RP 在 `/connect/authorize` 请求的作用域集合会被转发给上游 IdP，但**过滤为标准 OIDC 集合** -- `openid`、`profile`、`email`、`address`、`phone`，且始终包含 `openid`。RP 请求的其他内容（自定义 API 作用域、`offline_access` 等）会在上游调用前被丢弃：像 Google 这样严格的 IdP 会对未知值返回 `invalid_scope`，而上游只需要识别用户 -- RP 自己的作用域在 Authagonal 颁发的令牌上生效，而非上游令牌上。上游 IdP 按作用域放到 id_token 上的声明会回到 Authagonal，以 `federated:<name>` 声明的形式存放在 Cookie 票据上，并在下一次经过 `/connect/authorize` 时进入 `OidcSubject.FederationClaims`。此后 `ProtocolTokenService` 会将它们重新签发到 Authagonal 颁发的令牌上，受与 `CustomAttributes` 相同的 `Scope.UserClaims` 白名单管控。键冲突时联合值胜出。

净效果：无需按连接维护要保留的声明白名单。上游放到 id_token 上的每个非协议声明都会被捕获；其中哪些能到达下游令牌由下游作用域的 `UserClaims` 控制 -- 在那里声明该 claim，其值就会流转过去。

`FederationClaims` 在刷新轮换中独立于 `CustomAttributes` 存续，因此按会话的联合上下文（例如原始授权时捕获的分享链接令牌）保持完好，而按用户的属性仍会从用户存储中重新读取。

## 透传查询参数

`OidcProviderConfig.PassthroughParams` 是一个按连接的查询键白名单，这些键会从原始 `/authorize` 请求流转到上游 IdP 的授权 URL 上。标准集合（`scope`、`state`、`nonce`、PKCE）总是被转发；此白名单用于额外的、由 RP 指定的值，例如上游认证所需的一次性凭据（如分享链接 IdP 的 `link_token`）。

当某个键在白名单中时，Authagonal 会从原始 `/authorize` 查询（经 `returnUrl` 携带）中取出其值并追加到上游 URL 上。不在白名单中的内容会被静默丢弃。

## 会话生命周期上限 {#session-lifetime-cap}

`OidcProviderConfig.SessionExpClaim` 是一个可选的 id_token 声明名称（Unix 秒），其值会为本地会话生命周期设置上限。存在时，上游的值会以 `session_max_exp` 的形式随 Cookie 票据流转并进入颁发的授权码；访问 / id / 刷新令牌都会被钳制，使任何令牌（包括轮换铸造的令牌）都不会比上游会话活得更久。当上游 IdP 强制的会话边界比 Authagonal 默认的更短时很有用。

## 安全特性

- **PKCE** -- 每个授权请求都使用 S256 的 code_challenge
- **Nonce 验证** -- nonce 与 state 一起存储，必须存在于 id_token 中并且匹配
- **State 验证** -- 一次性使用（通过 `IOidcStateStore` 原子性消费，持久化并设有过期时间），**且绑定到浏览器**：登录时会设置一个作用域为 `/oidc` 的 `SameSite=Lax` Cookie，并且必须与回调上的 `state` 匹配，因此攻击者无法完成自己发起的联合流程再把回调 URL 交给受害者（登录 CSRF）
- **id_token 签名验证** -- 从 IdP 的 JWKS 端点获取密钥；验证颁发者、受众和有效期
- **Userinfo 回退** -- 如果 id_token 不包含邮箱，则尝试 userinfo 端点。userinfo 的 `sub` 必须与 id_token 的 `sub` 匹配（OIDC Core 5.3.2），否则将忽略该响应
- **稳定身份关联** -- 回访用户通过提供者 + `sub` 解析，绝不仅凭邮箱。将联合身份附加到**已存在的**本地账户（按邮箱）要求该连接的 `AllowedDomains` 覆盖该邮箱的域 -- 即管理员明确担保该 IdP 拥有该域。上游断言的 `email_verified` *不足以*夺取一个已存在的账户
- **域强制** -- 设置了 `AllowedDomains` 时，该连接只能断言这些域内的身份（否则返回 `access_denied`）
- **JIT 退出** -- `DisableJitProvisioning` 会拒绝未知用户，而不是自动创建它们
- **开放重定向防护** -- `returnUrl` 必须是同站相对路径；协议相对（`//`）和反斜杠形式会被拒绝
- **本地 MFA 仍然适用** -- 联合仅证明第一重因素。已注册 MFA（或其客户端策略要求 MFA）的用户会在回调后被引导通过本地 MFA 质询/设置页面，而不是直接登入；只有在此之后会话才会带上 MFA 标记

## Azure AD 特殊说明

Azure AD 有时会将邮箱作为 JSON 数组在 `emails` 声明中返回（特别是 B2C）。Authagonal 通过同时检查 `email` 声明和 `emails` 数组来处理此情况。

## 支持的提供者

任何支持以下功能的 OIDC 兼容提供者：
- 授权码流程
- PKCE (S256)
- 发现文档 (`.well-known/openid-configuration`)

已测试：
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C
