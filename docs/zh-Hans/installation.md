---
layout: default
title: 安装
locale: zh-Hans
---

# 安装

## Docker（推荐）

拉取并运行预构建镜像：

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  authagonal
```

## Docker Compose

使用 Azurite（Azure Storage 模拟器）进行本地开发：

```yaml
services:
  azurite:
    image: mcr.microsoft.com/azure-storage/azurite
    ports:
      - "10000:10000"
      - "10001:10001"
      - "10002:10002"

  authagonal:
    build: .
    ports:
      - "8080:8080"
    environment:
      - Storage__ConnectionString=DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://azurite:10002/devstoreaccount1;
      - Issuer=http://localhost:8080
    depends_on:
      - azurite
```

```bash
docker compose up
```

## 从源代码构建

### 前提条件

- .NET 10 SDK
- Node.js 22+

### 构建

```bash
# Build everything
dotnet build

# Build the login SPA
cd login-app
npm ci
npm run build

# Run the server
dotnet run --project src/Authagonal.Server
```

### Docker 构建

```bash
# Server image (multi-stage: builds SPA + .NET in one image)
docker build -t authagonal .

# Migration tool
docker build -f Dockerfile.migration -t authagonal-migration .
```

## 作为库使用（NuGet）

在您自己的 ASP.NET Core 项目中引用 Authagonal 包：

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.Storage" Version="x.y.z" />
```

然后在您的 `Program.cs` 中进行组合：

```csharp
builder.Services.AddSingleton<IAuthHook, MyAuditHook>();   // Custom hook
builder.Services.AddSingleton<IEmailService, MyEmailService>(); // Custom email
builder.Services.AddAuthagonal(builder.Configuration);

var app = builder.Build();
app.UseAuthagonal();
app.MapAuthagonalEndpoints();
app.MapFallbackToFile("index.html");
app.Run();
```

有关所有覆盖点，请参阅[扩展性](extensibility)；完整示例请参阅 [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server)。

## 登录 SPA（npm）

登录界面作为 npm 包发布，方便自定义：

```bash
npm install @authagonal/login
```

该包提供编译后的 JS 和 CSS -- 可在您自己的 React 应用中直接导入组件和样式。完整演练请参阅[自定义服务器](custom-server)。

## 生产环境安全检查清单

在将 Authagonal 暴露给真实流量之前，请确认以下各项。每一项都在[配置](configuration)页面中详细说明。

- **运行在 TLS 终止代理后面。** Authagonal 必须位于终止 TLS 的反向代理 / 入口（ingress）后面。会话 cookie 使用 `SecurePolicy = SameAsRequest`，且 HSTS 仅在 HTTPS 上发出，因此代理必须转发 `X-Forwarded-Proto: https`。将 `ForwardedHeaders:KnownNetworks`（或 `KnownProxies`）设为您的入口 / Pod CIDR，使客户端 IP 和协议无法被伪造；`ForwardedHeaders:ForwardLimit` 默认为 `1`（仅信任最后一跳）。
- **设置 `SecretProvider:VaultUri`。** 默认密钥提供者为**纯文本**——若无 Key Vault，上游 OIDC 客户端密钥和 TOTP / MFA 种子会以明文存储在 Table Storage（以及备份）中。对于任何生产部署，请配置 Key Vault。
- **锁定管理 API。** `AdminApi:Enabled` 默认为 **true**。管理作用域（`AdminApi:Scope`，默认 `authagonal-admin`）授予完整的管理权限和用户模拟能力。请对 `/api/v1/*` 管理路由进行网络限制，并严格控制谁能被签发管理作用域；如果未使用，请设置 `AdminApi:Enabled = false`。
- **保护内部端点。** 设置 `Cluster:Secret`，使 `/_internal/cluster/gossip` 和 `/_internal/backchannel-logout` 要求 `X-Cluster-Secret` 请求头——尤其是当 gossip 通过 `Cluster:InternalUrl` 经由负载均衡器路由时。
- **加密备份。** 使用纯文本密钥提供者时，备份包含密钥。`SigningKeys` 表默认从备份中排除；如果您通过 `Backup:IncludeSigningKeys` 选择启用，备份目标必须静态加密。参见[备份与恢复](backup-restore)。

## 迁移工具

用于从 Duende IdentityServer + SQL Server 迁移：

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

详情请参阅[迁移](migration)。
