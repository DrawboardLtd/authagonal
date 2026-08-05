---
layout: default
title: SAML
locale: zh-Hans
---

# SAML 2.0 SP

Authagonal 包含一个自研的 SAML 2.0 服务提供者实现。不依赖第三方 SAML 库：基于 `System.Security.Cryptography.Xml.SignedXml`（.NET 的一部分）构建。

## 范围

- **SP 发起的 SSO**（用户从 Authagonal 开始，重定向到 IdP）
- 用于 AuthnRequest 的 **HTTP-Redirect 绑定**（可选签名，见下文）
- 用于响应（ACS）的 **HTTP-POST 绑定**
- **加密断言**（`EncryptedAssertion`），使用按连接的 SP 密钥对解密
- **单点登出**（SP 发起和 IdP 发起，Redirect 和 POST 绑定）
- Azure AD / Entra ID 是主要目标，但任何兼容的 IdP 都可以使用（Okta、OneLogin、Ping、Google Workspace、ADFS、Shibboleth 的属性名均可处理）

### 不支持

- Artifact 绑定
- AES-GCM 断言加密（.NET `EncryptedXml` 限制；请在 IdP 端配置 AES-CBC，见下文）

IdP 发起的 SSO **按连接支持，且默认关闭**：在连接上设置 `allowUnsolicitedResponses: true` 才会接受。否则 ACS 会拒绝不含 `InResponseTo` 的 Response，并带 `error=saml_unsolicited` 重定向。默认关闭的原因是：接受非请求响应会让任何在该 IdP 有账户的人从任意 user-agent 登入一个会话；而且只要同一断言可以在移除 `InResponseTo` 后重放，在 SP 发起路径上要求 request cookie 就毫无意义。启用后，对非请求响应会跳过请求 ID 检查，但仍强制断言 ID 的一次性使用（参见安全性）。

## Azure AD 设置

### 1. 创建 SAML 提供者

**选项 A：配置（推荐用于静态设置）**

添加到 `appsettings.json`：

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "acme-azure",
      "ConnectionName": "Acme Corp Azure AD",
      "EntityId": "https://auth.example.com/saml/acme-azure",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
      "AllowedDomains": ["acme.com"]
    }
  ]
}
```

提供者在启动时播种。SSO 域映射从 `AllowedDomains` 自动注册。通过配置播种的提供者需要一个 `MetadataLocation` URL，并且不会获得 SP 密钥对（因此没有签名的 AuthnRequest、加密断言或签名的登出消息）；这些功能请使用管理 API。

`EntityId` 是**您的 SP 实体 ID**（您在 IdP 端注册的标识符），而不是 IdP 的实体 ID。

**选项 B：管理 API（用于运行时管理）**

```bash
curl -X POST https://auth.example.com/api/v1/saml/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Acme Corp Azure AD",
    "entityId": "https://auth.example.com/saml/acme-azure",
    "metadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
    "allowedDomains": ["acme.com"]
  }'
```

该 API 会生成 `connectionId`（一个 GUID），并在 `Location` 头和响应正文中返回它。其他可选字段：`metadataXml`（粘贴的元数据，见下文）、`nameIdFormat`（见下文）、`signAuthnRequests`（强制签名 AuthnRequest）、`iconUrl`（登录按钮图标）、`disableJitProvisioning`（拒绝未知用户而不是自动创建它们）、`allowUnsolicitedResponses`（接受 IdP 发起的登录——默认关闭，见上文）。通过 API 创建的连接还会获得一个自动生成的 SP 密钥对（参见下文的 SP 密钥对）。

连接通过对 `/api/v1/saml/connections[/{connectionId}]` 的 `POST` / `GET` / `PUT` / `DELETE` 进行管理。`PUT` 是部分更新：只有在请求中提供的字段才会被修改。

### 2. 配置 Azure AD

1. 在 Azure AD 中 -> 企业应用程序 -> 新建应用程序 -> 创建你自己的
2. 设置单点登录 -> SAML
3. **标识符（实体 ID）：** `https://auth.example.com/saml/acme-azure`
4. **回复 URL（ACS）：** `https://auth.example.com/saml/acme-azure/acs`
5. **登录 URL：** `https://auth.example.com/saml/acme-azure/login`

### 3. SSO 域路由

当指定了 `AllowedDomains`（在配置中或通过创建 API），SSO 域映射会自动注册。当用户在登录页面输入 `user@acme.com` 时，SPA 会检测到需要 SSO 并显示"通过 SSO 继续"。一个域只能映射到一个连接；API 会拒绝已被其他连接占用的域。

您也可以通过管理 API 在运行时管理域；参阅[管理 API](admin-api)。

## 粘贴的元数据 XML

某些 IdP 不发布元数据 URL（Google Workspace），或者其元数据端点从 SP 处无法访问（专用网络中的 ADFS）。对于这些情况，请改为粘贴元数据文档：在创建/更新时提供 `metadataXml`。必须且只能提供 `metadataLocation` 或 `metadataXml` 中的一个；在更新时提供其中一个会清除另一个。

粘贴的元数据会在保存时验证，并被**压缩**（`SamlMetadataParser.Condense`）为一个规范的最小 `EntityDescriptor`，只保留 SP 所消费的内容：entityID、签名证书、SSO 端点、存在时的 SLO 端点，以及 `WantAuthnRequestsSigned` 标志。厂商文档可能超过 100KB（ADFS 的 `FederationMetadata.xml`），超出 64KB 的 Azure Table 属性上限，而 SP 使用的部分只有几 KB。无法解析的粘贴内容会以 400 拒绝；文档必须包含一个带签名证书的 `IDPSSODescriptor` 和一个 `SingleSignOnService`。

## NameID 格式

`nameIdFormat` 字段控制 AuthnRequest 中请求的 `NameIDPolicy` Format：

| 值 | 行为 |
|---|---|
| 省略 / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress`（历史默认值） |
| `"none"` | 完全省略 `NameIDPolicy` 元素。ADFS 安全的设置：当 ADFS 的声明规则不发出所请求的格式时，它会使整个登录失败（MSIS7070）。 |
| 任何其他值 | 作为 Format URN 原样发送（必须以 `urn:` 开头） |

在更新时，`""` 会重置为 emailAddress 默认值。SP 元数据会公布连接所请求的格式（当设置为 `"none"` 时省略 `NameIDFormat`）。

## 端点

| 端点 | 描述 |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | 发起 SP 发起的 SSO。构建 AuthnRequest（适用时签名）并重定向到 IdP。对于支持的 IdP（Entra、Google），`loginHint` 会作为 `login_hint` 传递。 |
| `POST /saml/{connectionId}/acs` | 断言消费者服务。接收 SAML 响应，验证它，创建/登录用户。 |
| `GET /saml/{connectionId}/metadata` | SP 元数据 XML，用于配置 IdP。 |
| `GET /saml/{connectionId}/logout?returnUrl=...` | SP 发起的单点登出。先结束本地会话，然后在 IdP 支持 SLO 时向其发送 LogoutRequest。 |
| `GET/POST /saml/{connectionId}/slo` | 单点登出端点。接收 IdP 发起的 LogoutRequest（Redirect 或 POST 绑定）以及 SP 发起的 SLO 的 LogoutResponse 部分。 |

登录后的返回 URL 通过服务器端保存在存储的 AuthnRequest 上（以请求 ID 为键），而不是在 RelayState 中：SAML 规范将 RelayState 限制为 80 字节，且某些 IdP 会将其截断。RelayState 仅在 IdP 发起的流程中使用。

## SP 密钥对与加密断言

每个通过 API 创建的连接都会获得一个自动生成的 SP 密钥对：一张自签名的 2048 位 RSA 证书（10 年有效期），以 PKCS#12 存储，并由宿主的机密提供者在静态时保护。它仅供服务器使用，绝不会由 API 返回。该密钥对可实现：

- **签名的 AuthnRequest**（redirect 绑定的 `SigAlg`/`Signature` 查询签名）。当 IdP 的元数据声明了 `WantAuthnRequestsSigned` 时会自动开启签名，或当连接设置 `signAuthnRequests: true` 时始终开启。
- **加密断言解密。** 当 SP 元数据公布了加密证书时，ADFS 默认会开始加密断言；ACS 使用 SP 私钥对其解密，并让解密后的断言经过与明文断言相同的签名/条件流水线。支持：RSA-OAEP（SHA-1/SHA-256）密钥传输；AES-128/192/256-CBC 和 3DES 数据加密。**RSA-1.5 密钥传输会被拒绝**——PKCS#1 v1.5 解包是 Bleichenbacher/ROBOT 预言机——并且**不支持 AES-GCM**（.NET `EncryptedXml` 限制）。请将 IdP 配置为 RSA-OAEP 与 AES-CBC。两种失败都会返回同一条固定消息（"Could not decrypt the assertion."），这是有意为之：指明失败的算法或阶段正是构造预言机的关键，因此请从 IdP 的配置而不是错误消息入手诊断。
- **签名的登出消息**（redirect 绑定上的 LogoutRequest/LogoutResponse）。

SP 元数据会将该证书同时作为 `signing` 和 `encryption` 的 `KeyDescriptor` 发布，并在连接强制签名时设置 `AuthnRequestsSigned="true"`。

## 单点登出

ACS 会在认证 Cookie 上记录 SAML 会话（`saml_connection`、`saml_name_id`、`saml_name_id_format`、`saml_session_index` 声明），以便登出可以关联回 IdP 会话。

- **SP 发起：** `GET /saml/{connectionId}/logout` 总是先结束本地 Cookie 会话（用户要求登出；IdP SLO 是尽力而为）。如果浏览器的会话来自此连接，且 IdP 元数据公布了 `SingleLogoutService`，则会通过 redirect 绑定发送一个 LogoutRequest（NameID + SessionIndex，当 SP 拥有密钥时签名）；IdP 的 LogoutResponse 会返回到 `/slo`，将用户带到存储的 `returnUrl`。没有 SLO 端点的 IdP（Google）只会得到本地登出。
- **IdP 发起：** IdP 向 `/saml/{connectionId}/slo` 发送一个 LogoutRequest（Redirect GET 或 POST 绑定）。签名的请求会根据 IdP 元数据中的证书进行验证。**未签名或无法验证的 LogoutRequest 会在查询任何会话之前直接以 400 拒绝。** 不存在按会话放宽的回退路径：第三方页面把*受害者*的浏览器导航到这里时，携带的是受害者的会话而不是攻击者的会话，因此限定为当前会话并不能限制谁会被登出。Profiles §4.4.3.1 本就要求 IdP 对 Redirect 或 POST 绑定上的 LogoutRequest 进行签名，而连接的元数据已经提供了证书，所以拒绝未签名请求对合规的 IdP 没有任何代价。当 IdP 拥有 SLO 端点时会返回一个签名的 LogoutResponse。仅前端通道：消息到达用户的浏览器，因此结束该 Cookie 会话恰好登出该浏览器。

## 元数据缓存与证书轮换

- 从 `MetadataLocation` 获取的 IdP 元数据会在内存中缓存 60 分钟（可通过 `Cache:SamlMetadataCacheMinutes` 配置），以元数据 URL（而非连接 ID）为键，因此不可能发生跨租户缓存混淆。
- 粘贴的元数据按内容寻址缓存（XML 的哈希），并且绝不会重新获取。
- **签名失败后重新获取：** IdP 证书轮换后立即出现的签名验证失败意味着缓存的元数据已过时。在恰好这种失败时，缓存条目会被逐出并将元数据重新获取一次，然后重试验证，每个元数据位置有 5 分钟的冷却期，因此垃圾断言无法被用来猛烈冲击 IdP 的元数据端点。若没有这一机制，证书轮换将导致登录失败，直到缓存 TTL 过期。（仅限从 URL 获取的元数据；粘贴的元数据没有可重新获取的内容。）

## Azure AD 兼容性

| Azure AD 行为 | 处理方式 |
|---|---|
| 仅签名断言（默认） | 验证 Assertion 元素上的签名 |
| 仅签名响应 | 验证 Response 元素上的签名 |
| 两者都签名 | 验证两个签名 |
| SHA-256（默认） | 支持 SHA-256 和 SHA-1 |
| NameID: emailAddress | 直接提取邮箱 |
| NameID: persistent（不透明） | 回退到属性中的邮箱声明 |
| NameID: unspecified | 回退到属性中的邮箱声明 |
| NameID: transient | 每次登录都会轮换，因此绝不会用作联合键。改用 IdP 的稳定 object-id 属性；如果未断言任何此类属性，则以可操作的错误拒绝登录（请配置 persistent 或 emailAddress NameID，或断言一个 object-id 属性）。 |

## 属性映射

属性会在其 `Name` 和 `FriendlyName` 下不区分大小写地建立索引（Okta 和 Shibboleth 会发出带有人类可读 FriendlyName 的 OID Name；能匹配其中任一种，正是使厂商映射生效的关键）。每个字段会按顺序尝试一个别名列表；第一个别名是 Microsoft 声明 URI，因此 Entra/ADFS 的行为保持不变，其余则覆盖 Okta、OneLogin、Ping、Google 和 Shibboleth 默认发出的友好名称和 OID 名称：

| 字段 | 接受的属性名 |
|---|---|
| email | `.../claims/emailaddress`、`email`、`mail`、`emailaddress`、`urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`、`givenName`、`given_name`、`firstName`、`first_name`、`urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`、`sn`、`surname`、`lastName`、`last_name`、`familyName`、`family_name`、`urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`、`displayName`、`urn:oid:2.16.840.1.113730.3.1.241`、`cn`、`urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`、`objectGUID`、`user.objectid` |
| groups | `.../claims/groups`、`groups`、`memberOf`、`.../claims/role`、`urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

（`.../claims/...` 是完整的 `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` 或 `http://schemas.microsoft.com/ws/2008/06/identity/claims/...` URI 的缩写。）

邮箱解析优先级：显式的 email 属性（任意别名）→ 格式为 emailAddress 的 NameID → 包含 `@` 的 `name` 声明 → 拒绝（必须提供邮箱）。

**组是多值的：** 会捕获每个 `AttributeValue` 元素（每个组成员身份对应一个），而不仅仅是第一个。

## JIT 预配

未知用户会在首次登录时自动创建（邮箱、来自断言的名字/姓氏，邮箱标记为已确认），并通过其稳定的联合身份（`saml:{connectionId}` + NameID，或对于 transient NameID 使用 object-id）关联到该连接。设置 `disableJitProvisioning: true` 可拒绝未知用户。回访用户首先通过联合关联进行匹配，绝不仅凭邮箱；只有当连接的 `AllowedDomains` 覆盖该邮箱的域时（管理员明确声明此 IdP 拥有该域），才会按邮箱附加到已存在的本地账户，从而防止通过恶意 IdP 进行账户接管。

## 安全性

- **重放防护：** 对于 SP 发起的流程，`InResponseTo` 会根据存储的请求 ID 进行验证（一次性使用）。此外，每个被接受的断言的 ID 都会被存储并强制一次性使用，这也涵盖 IdP 发起的响应以及 `InResponseTo` 被剥离的响应（断言 ID 位于签名的断言内部，因此在不破坏签名的情况下无法被更改）。
- **时钟偏差：** NotBefore/NotOnOrAfter 有 5 分钟容差
- **包装攻击防护：** 签名的 Reference URI 必须与签名元素的 ID 匹配
- **开放重定向防护：** 登录后的返回 URL 必须是根相对路径（以 `/` 开头，不含 `//`，不含反斜杠，因为浏览器会将 `\` 视为 `/`）
- **域担保：** 配置了 `AllowedDomains` 时，针对这些域之外邮箱的断言会被拒绝，因此一个连接无法断言另一个连接的域或本地用户的邮箱
- **MFA：** 联合仅证明第一重因素。如果用户的有效策略要求 MFA，登录会经由本地 MFA 质询/设置，而不是签发一个完全认证的会话。
