---
layout: default
title: 品牌定制
locale: zh-Hans
---

# 登录界面品牌定制

登录 SPA 通过 Web 根目录下的 `branding.json` 文件进行运行时配置。无需重新构建 -- 只需挂载您的配置和资源文件即可。

## 工作原理

启动时，SPA 会获取 `/branding.json`。如果文件不存在或无法访问，则使用默认值。（宿主服务器也可以将配置作为 `<script type="application/json" id="authagonal-boot">` 启动负载内联；当其存在时，SPA 会读取它而非发起获取。）配置控制以下内容：

- 应用名称（显示在页头和页面标题中）
- 徽标图片，可选按模式设置的背景"贴片"
- 主色调（按钮、链接、焦点环），可选深色模式变体
- 页面和卡片背景颜色，按模式设置
- 忘记密码和注册链接的可见性
- 深色模式默认值（浅色 / 跟随操作系统 / 深色）
- 语言选择器选项
- "Powered by Authagonal" 页脚
- 用于深度样式定制的自定义 CSS

## 配置

将 `branding.json` 文件放置在 `wwwroot/` 目录中（或挂载到 Docker 容器中）：

```json
{
  "appName": "Acme Corp",
  "logoUrl": "/branding/logo.svg",
  "primaryColor": "#1a56db",
  "darkPrimaryColor": "#3b82f6",
  "darkMode": "auto",
  "supportEmail": "help@acme.com",
  "showForgotPassword": true,
  "customCssUrl": "/branding/custom.css"
}
```

### 选项

| 属性 | 类型 | 默认值 | 描述 |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | 显示在页头和浏览器标签标题中 |
| `logoUrl` | `string \| null` | `null` | 徽标图片的 URL。设置后将替换文本页头。 |
| `primaryColor` | `string` | `"#2563eb"` | 按钮、链接和焦点指示器的十六进制颜色 |
| `supportEmail` | `string \| null` | `null` | 技术支持联系邮箱（保留供将来使用） |
| `showForgotPassword` | `boolean` | `true` | 在登录页面显示/隐藏"忘记密码？"链接 |
| `showRegistration` | `boolean` | `false` | 显示/隐藏自助注册链接 |
| `customCssUrl` | `string \| null` | `null` | 在默认样式之后加载的自定义 CSS 文件的 URL |
| `welcomeTitle` | `LocalizedString` | `null` | 可选的欢迎语，显示在认证页面页眉下方（纯字符串或 `{ "en": "...", "de": "..." }`）。未设置时不渲染任何内容。 |
| `welcomeSubtitle` | `LocalizedString` | `null` | `welcomeTitle` 下方的可选文字，格式相同。未设置时不渲染任何内容。 |
| `languages` | `array \| null` | `null` | 语言选择器选项（`[{ "code": "en", "label": "English" }, ...]`）。`null` 显示除趣味区域设置外的所有随附语言（参见 [本地化](localization)）。 |
| `poweredBy` | `boolean` | `true` | 在认证页面显示/隐藏 "Powered by Authagonal" 页脚 |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | 访客尚未选择时的默认主题：`"off"`（仅浅色）、`"auto"`（跟随操作系统偏好）、`"force"`（始终深色）。访客的主题切换仍然优先。 |
| `lightBg` | `string \| null` | `null` | 浅色模式下的页面背景颜色 |
| `lightCardBg` | `string \| null` | `null` | 浅色模式下的卡片/表单背景颜色 |
| `darkBg` | `string \| null` | `null` | 深色模式下的页面背景颜色 |
| `darkCardBg` | `string \| null` | `null` | 深色模式下的卡片/表单背景颜色 |
| `darkPrimaryColor` | `string \| null` | `null` | 在深色模式下覆盖 `primaryColor` |
| `lightLogoBg` | `string \| null` | `null` | 浅色模式下的徽标贴片背景（见下文） |
| `darkLogoBg` | `string \| null` | `null` | 深色模式下的徽标贴片背景（见下文） |

颜色值必须是十六进制颜色（`#rgb`、`#rrggbb`、`#rrggbbaa`）或 `rgb()`/`rgba()`/`hsl()`/`hsla()` 表达式；其他任何内容都会被忽略。按模式的颜色会作为 `<style id="branding-theme-vars">` 规则注入在打包样式之后（浅色值位于 `:root`，深色值位于 `.dark`），因此深色值可以与其浅色对应值不同。

### 徽标背景贴片

如果您的徽标采用白色或透明的图案，它可能会在浅色卡片上消失。设置 `lightLogoBg` 和/或 `darkLogoBg`，即可将徽标渲染在一个带内边距的圆角"贴片"中，并采用该背景颜色：

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

该贴片（一个由 `--auth-logo-bg` CSS 变量驱动的 `data-auth="logo-chip"` 包装器）仅在配置了徽标背景时才会获得内边距和背景，因此未设置的租户看到的徽标会像以前一样平贴在卡片上。这两个字段相互独立：仅设置 `lightLogoBg` 即可在浅色模式下为徽标添加贴片，而在深色模式下保持其无贴片。

## Docker 示例

将您的品牌文件挂载到容器中：

```bash
docker run -p 8080:8080 \
  -v ./my-branding/branding.json:/app/wwwroot/branding.json \
  -v ./my-branding/logo.svg:/app/wwwroot/branding/logo.svg \
  -v ./my-branding/custom.css:/app/wwwroot/branding/custom.css \
  -e Storage__ConnectionString="..." \
  -e Issuer="https://auth.example.com" \
  authagonal
```

或使用 docker-compose：

```yaml
services:
  authagonal:
    build: .
    ports:
      - "8080:8080"
    volumes:
      - ./my-branding/branding.json:/app/wwwroot/branding.json
      - ./my-branding/assets:/app/wwwroot/branding
    environment:
      - Storage__ConnectionString=...
      - Issuer=https://auth.example.com
```

## 自定义 CSS

`customCssUrl` 选项会在默认样式之后加载一个额外的样式表，因此您的规则具有更高优先级。适用于更改字体、调整间距或重新设计特定元素的样式。该 URL 必须是同源的（像 `/branding/custom.css` 这样的相对 URL 是可以的）；跨源样式表会被静默跳过。

### CSS 自定义属性

登录界面暴露了若干 CSS 自定义属性以进行细粒度控制：

| 属性 | 默认值 | 描述 |
|---|---|---|
| `--brand-primary` | `#2563eb` | 按钮、链接、焦点环的主色调 |
| `--auth-bg` | `#f3f4f6` | 页面背景颜色 |
| `--auth-card-bg` | `#ffffff` | 卡片/表单背景颜色 |
| `--auth-logo-bg` | `transparent` | 徽标贴片背景（仅在配置了徽标背景时才会出现贴片内边距） |
| `--auth-radius` | `0.5rem` | 认证卡片的边框圆角 |
| `--auth-font` | *（继承；系统字体栈）* | 认证卡片的字体系列 |
| `--auth-heading` | `#111827` | 标题文本颜色 |

这里的颜色变量直接映射到配置字段（`primaryColor`、`lightBg`/`darkBg`、`lightCardBg`/`darkCardBg`、`lightLogoBg`/`darkLogoBg`），因此对于简单的颜色更改优先使用配置，将自定义 CSS 保留用于其他所有情况。

在自定义 CSS 中覆盖它们：

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

登录界面使用 Tailwind CSS。自定义 CSS 可以定位标准 HTML 元素和 Tailwind 实用类。导出的 UI 组件（`Button`、`Input`、`Card`、`Alert` 等）内部使用 Tailwind。

## 深色模式

登录 SPA 随附浅色、深色和**系统**主题。主题切换器始终显示在布局中。用户的选择会以 `auth-theme` 键持久化到 `localStorage`。

### 工作原理

- **默认** -- 在访客选择主题之前，`darkMode` 品牌定制选项设定默认值：`"off"`（浅色）、`"auto"`（系统，默认值）或 `"force"`（深色）。一旦访客使用切换器，其选择始终优先。
- **检测** -- 当主题为"系统"时，SPA 会观察 `window.matchMedia('(prefers-color-scheme: dark)')`，并随操作系统偏好的变化自动重新应用主题。
- **应用** -- SPA 会在 `<html>` 上切换 `.dark` 类。Tailwind 的深色变体（`&:where(.dark, .dark *)`）会激活编译进每个组件的深色样式。
- **持久化** -- 显式的"浅色" / "深色" / "系统"选择会存储在 `localStorage` 中。

### CSS 变量

浅色值在 `:root` 声明；深色模式覆盖限定在 `.dark` 作用域内，因此当提供时，`customCssUrl` 中的租户品牌定制始终优先。

| 变量 | 浅色 | 深色 |
|---|---|---|
| `--auth-bg` | `#f3f4f6`（或 `lightBg`） | `#030712`（或 `darkBg`） |
| `--auth-card-bg` | `#ffffff`（或 `lightCardBg`） | `#111827`（或 `darkCardBg`） |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--auth-logo-bg` | `transparent`（或 `lightLogoBg`） | `transparent`（或 `darkLogoBg`） |
| `--brand-primary` | `#2563eb`（或 `primaryColor`） | 浅色值（或 `darkPrimaryColor`） |

### 禁用或覆盖

租户品牌定制始终优先。要强制使用单一主题，请在 `customCssUrl` 中设置您自己的值：

```css
/* Force dark palette regardless of user choice */
:root {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
.dark {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

要完全移除主题切换器，请使用 npm 包路径 -- 导入 `AuthLayout` 并在渲染时不带切换器，或者 fork 该 SPA。

### 数据属性

所有登录表单元素都带有 `data-auth` 属性，用于 CSS 定位和测试自动化：

| 属性 | 元素 |
|---|---|
| `data-auth="page"` | 主页面包装器 |
| `data-auth="header"` | 页头部分 |
| `data-auth="logo-chip"` | 徽标图片周围的包装器（仅在设置了徽标背景时才带内边距） |
| `data-auth="logo"` | 徽标图片 |
| `data-auth="app-name"` | 应用名称标题 |
| `data-auth="content"` | 主内容区域 |
| `data-auth="languages"` | 语言选择器 |
| `data-auth="language-trigger"` | 语言选择器触发按钮 |
| `data-auth="theme-toggle"` | 浅色/系统/深色主题切换器 |
| `data-auth="powered-by"` | "Powered by Authagonal" 页脚 |

在自定义 CSS 中定位这些元素：

```css
[data-auth="header"] {
  background: linear-gradient(135deg, #667eea, #764ba2);
}
```

### 示例：自定义背景和字体

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## 定制层级

| 层级 | 操作内容 | 更新路径 |
|---|---|---|
| **仅配置** | 挂载 `branding.json` + 徽标 | 无缝 -- 更新 Docker 镜像，保留您的挂载 |
| **配置 + CSS** | 添加 `customCssUrl` 进行样式覆盖 | 相同 -- CSS 类是稳定的 |
| **npm 包** | `npm install @authagonal/login`，自定义 `branding.json`，构建到 `wwwroot/` | 可更新 -- `npm update` 拉取新版本 |
| **Fork SPA** | 克隆 `login-app/`，修改源代码，构建您自己的版本 | 您拥有界面 -- 服务器更新是独立的 |
| **自行编写** | 针对认证 API 构建完全自定义的前端 | 完全控制 -- 参阅 [Auth API](auth-api) 了解接口规范 |

参阅 `demos/custom-server/` 获取带有自定义品牌的完整示例（绿色主题，"Acme Corp"）。
