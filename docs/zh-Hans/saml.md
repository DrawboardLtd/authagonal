---
layout: default
title: SAML
locale: zh-Hans
---

# SAML 2.0 SP

Authagonal 包含一个自研的 SAML 2.0 服务提供者实现。不依赖第三方 SAML 库 -- 基于 `System.Security.Cryptography.Xml.SignedXml`（.NET 的一部分）构建。

## 范围

- **SP 发起的 SSO**（用户从 Authagonal 开始，重定向到 IdP）
- 用于 AuthnRequest 的 **HTTP-Redirect 绑定**
- 用于响应（ACS）的 **HTTP-POST 绑定**
- Azure AD 是主要目标，但任何兼容的 IdP 都可以使用

### 不支持

- SAML 注销（使用会话超时）
- 断言加密（不发布加密证书）
- Artifact 绑定

IdP 发起的 SSO 已支持 -- ACS 端点可以处理不含 `InResponseTo` 的响应（对非请求响应跳过重放验证）。

## Azure AD 设置

### 1. 创建 SAML 提供者

**选项 A -- 配置（推荐用于静态设置）：**

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

提供者在启动时播种。SSO 域映射从 `AllowedDomains` 自动注册。

**选项 B -- 管理 API（用于运行时管理）：**

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

### 2. 配置 Azure AD

1. 在 Azure AD 中 -> 企业应用程序 -> 新建应用程序 -> 创建你自己的
2. 设置单点登录 -> SAML
3. **标识符（实体 ID）：** `https://auth.example.com/saml/acme-azure`
4. **回复 URL（ACS）：** `https://auth.example.com/saml/acme-azure/acs`
5. **登录 URL：** `https://auth.example.com/saml/acme-azure/login`

### 3. SSO 域路由

当指定了 `AllowedDomains`（在配置中或通过创建 API），SSO 域映射会自动注册。当用户在登录页面输入 `user@acme.com` 时，SPA 会检测到需要 SSO 并显示"通过 SSO 继续"。

您也可以通过管理 API 在运行时管理域 -- 参阅[管理 API](admin-api)。

## 端点

| 端点 | 描述 |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...` | 发起 SP 发起的 SSO。构建 AuthnRequest 并重定向到 IdP。 |
| `POST /saml/{connectionId}/acs` | 断言消费者服务。接收 SAML 响应，验证它，创建/登录用户。 |
| `GET /saml/{connectionId}/metadata` | SP 元数据 XML，用于配置 IdP。 |

## Azure AD 兼容性

| Azure AD 行为 | 处理方式 |
|---|---|
| 仅签名断言（默认） | 验证 Assertion 元素上的签名 |
| 仅签名响应 | 验证 Response 元素上的签名 |
| 两者都签名 | 验证两个签名 |
| SHA-256（默认） | 支持 SHA-256 和 SHA-1 |
| NameID: emailAddress | 直接提取邮箱 |
| NameID: persistent（不透明） | 回退到属性中的邮箱声明 |
| NameID: transient, unspecified | 回退到属性中的邮箱声明 |

## 声明映射

Azure AD 声明（完整 URI 格式）映射为简单名称：

| Azure AD 声明 URI | 映射到 |
|---|---|
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress` | `email` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname` | `firstName` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname` | `lastName` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name` | `name`（UPN） |
| `http://schemas.microsoft.com/identity/claims/objectidentifier` | `oid` |
| `http://schemas.microsoft.com/identity/claims/tenantid` | `tenantId` |
| `http://schemas.microsoft.com/identity/claims/displayname` | `displayName` |

## 安全性

- **重放防护：** InResponseTo 根据存储的请求 ID 进行验证。每个 ID 只能使用一次。
- **时钟偏差：** NotBefore/NotOnOrAfter 有 5 分钟容差
- **包装攻击防护：** 签名验证使用正确的引用解析
- **开放重定向防护：** RelayState（returnUrl）必须是根相对路径（以 `/` 开头，不含协议或主机名）
