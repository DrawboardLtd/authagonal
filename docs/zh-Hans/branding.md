---
layout: default
title: 品牌定制
locale: zh-Hans
---

# 登录界面品牌定制

登录 SPA 通过 Web 根目录下的 `branding.json` 文件进行运行时配置。无需重新构建 -- 只需挂载您的配置和资源文件即可。

## 工作原理

启动时，SPA 会获取 `/branding.json`。如果文件不存在或无法访问，则使用默认值。配置控制以下内容：

- 应用名称（显示在页头和页面标题中）
- 徽标图片
- 主色调（按钮、链接、焦点环）
- 忘记密码链接的可见性
- 用于深度样式定制的自定义 CSS

## 配置

将 `branding.json` 文件放置在 `wwwroot/` 目录中（或挂载到 Docker 容器中）：

```json
{
  "appName": "Acme Corp",
  "logoUrl": "/branding/logo.svg",
  "primaryColor": "#1a56db",
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
| `welcomeTitle` | `LocalizedString` | `null` | 覆盖登录页面标题（纯字符串或 `{ "en": "...", "de": "..." }`） |
| `welcomeSubtitle` | `LocalizedString` | `null` | 覆盖登录页面副标题 |
| `languages` | `array \| null` | `null` | 语言选择器选项（`[{ "code": "en", "label": "English" }, ...]`） |

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

`customCssUrl` 选项会在默认样式之后加载一个额外的样式表，因此您的规则具有更高优先级。适用于更改字体、调整间距或重新设计特定元素的样式。

### CSS 自定义属性

主色调通过 `--brand-primary` CSS 自定义属性设置（馈入 Tailwind 主题）。在自定义 CSS 中覆盖它，而不使用 `branding.json`：

```css
:root {
  --brand-primary: #059669;
}
```

登录界面使用 Tailwind CSS。自定义 CSS 可以定位标准 HTML 元素和 Tailwind 实用类。导出的 UI 组件（`Button`、`Input`、`Card`、`Alert` 等）内部使用 Tailwind。

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
| **npm 包** | `npm install @drawboard/authagonal-login`，自定义 `branding.json`，构建到 `wwwroot/` | 可更新 -- `npm update` 拉取新版本 |
| **Fork SPA** | 克隆 `login-app/`，修改源代码，构建您自己的版本 | 您拥有界面 -- 服务器更新是独立的 |
| **自行编写** | 针对认证 API 构建完全自定义的前端 | 完全控制 -- 参阅 [Auth API](auth-api) 了解接口规范 |

参阅 `demos/custom-server/` 获取带有自定义品牌的完整示例（绿色主题，"Acme Corp"）。
