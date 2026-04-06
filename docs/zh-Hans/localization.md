---
layout: default
title: 本地化
locale: zh-Hans
---

# 本地化

Authagonal 开箱即支持八种语言：英语、简体中文 (`zh-Hans`)、德语 (`de`)、法语 (`fr`)、西班牙语 (`es`)、越南语 (`vi`)、葡萄牙语 (`pt`) 和克林贡语 (`tlh`)。本地化涵盖服务器 API 响应、登录界面以及本文档站点。

## 支持的语言

| 代码 | 语言 |
|---|---|
| `en` | 英语（默认） |
| `zh-Hans` | 简体中文 |
| `de` | 德语 |
| `fr` | 法语 |
| `es` | 西班牙语 |
| `vi` | 越南语 |
| `pt` | 葡萄牙语 |

## 服务器（API 响应）

服务器使用 ASP.NET Core 内置的本地化功能，通过 `IStringLocalizer<T>` 和 `.resx` 资源文件实现。语言根据 `Accept-Language` HTTP 头进行选择。

### 已本地化的内容

- 密码验证错误消息
- 密码策略标签 (`GET /api/auth/password-policy`)
- 密码重置流程消息（令牌错误、过期、成功）
- 异常处理中间件的通用错误描述
- 管理员用户管理消息（邮箱确认、验证等）
- 结束会话确认消息

### 未本地化的内容

- 机器可读的 `error` 代码（`"email_required"`、`"invalid_credentials"` 等）——这些是 API 契约，保持不变
- OAuth/OIDC 错误代码以及令牌、授权和撤销端点上面向开发者的错误描述
- 内部日志消息和异常消息

### 测试服务器本地化

向任何已本地化的端点发送 `Accept-Language` 头：

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### 资源文件

所有服务器翻译字符串位于 `src/Authagonal.Server/Resources/` 下的 `.resx` 文件中：

```
Resources/
  SharedMessages.cs          # Marker class
  SharedMessages.resx        # English (default)
  SharedMessages.zh-Hans.resx
  SharedMessages.de.resx
  SharedMessages.fr.resx
  SharedMessages.es.resx
  SharedMessages.vi.resx
  SharedMessages.pt.resx
```

## 登录界面

登录单页应用使用 [react-i18next](https://react.i18next.com/) 进行客户端本地化。语言根据浏览器的 `navigator.language` 设置自动检测。

### 语言检测

检测顺序如下：

1. **localStorage** — 来自上次访问的持久化偏好
2. **查询参数** — `?lng=de` 覆盖浏览器检测
3. **浏览器语言** — `navigator.language`（自动）
4. **回退** — 英语 (`en`)

### 翻译文件

翻译 JSON 文件与应用一起打包，位于 `login-app/src/i18n/`：

```
i18n/
  index.ts        # i18n initialization
  en.json         # English
  zh-Hans.json    # Simplified Chinese
  de.json         # German
  fr.json         # French
  es.json         # Spanish
  vi.json         # Vietnamese
  pt.json         # Portuguese
  tlh.json        # Klingon
```

### 密码策略标签

登录界面根据 `GET /api/auth/password-policy` 返回的 `rule` 键在客户端翻译密码要求标签，而不是使用服务器提供的 `label` 字段。这确保密码要求始终以用户浏览器的语言显示，即使服务器的 `Accept-Language` 头不同。

### npm 包使用者

如果你通过 `@drawboard/authagonal-login` 使用登录应用，i18n 实例已导出：

```typescript
import { i18n } from '@drawboard/authagonal-login';

// Change language programmatically
i18n.changeLanguage('de');
```

## 文档

文档站点采用基于目录的方式。英语页面位于根目录，翻译版本位于语言子目录中（`/zh-Hans/`、`/de/`、`/fr/`、`/es/`）。侧边栏中的语言切换下拉菜单允许在不同语言之间切换。

## 添加新语言

要添加新语言支持（例如日语 `ja`）：

### 1. 服务器

复制英语资源文件并翻译其中的值，创建新的 `.resx` 文件：

```
src/Authagonal.Server/Resources/SharedMessages.ja.resx
```

在 `AuthagonalExtensions.cs` 中将 `"ja"` 添加到支持的文化数组中：

```csharp
var supportedCultures = new[] { "en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja" };
```

### 2. 登录界面

复制 `en.json` 并翻译其中的值，创建新的翻译 JSON 文件：

```
login-app/src/i18n/ja.json
```

在 `login-app/src/i18n/index.ts` 中注册：

```typescript
import ja from './ja.json';

// In the resources object:
ja: { translation: ja },
```

### 3. 文档

创建一个新目录，包含翻译后的 markdown 文件：

```
docs/ja/
  index.md
  installation.md
  quickstart.md
  ...
```

在 `docs/_config.yml` 中添加语言默认值：

```yaml
defaults:
  - scope:
      path: "ja"
    values:
      locale: "ja"
```

在 `docs/_layouts/default.html` 中将该语言选项添加到语言切换器。

## 添加新字符串

### 服务器

1. 将键和英语值添加到 `SharedMessages.resx`
2. 将翻译值添加到每个语言的 `.resx` 文件中
3. 使用 `IStringLocalizer<SharedMessages>` 访问字符串：

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### 登录界面

1. 将键和英语值添加到 `en.json`
2. 将翻译值添加到每个语言的 JSON 文件中
3. 在组件中使用 `t()` 函数：

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```
