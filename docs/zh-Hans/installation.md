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

有关所有覆盖点，请参阅[扩展性](extensibility)；完整示例请参阅 [demos/custom-server/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/custom-server)。

## 登录 SPA（npm）

登录界面作为 npm 包发布，方便自定义：

```bash
npm install @drawboard/authagonal-login
```

通过 `branding.json` 进行自定义（参阅[品牌定制](branding)），并将 SPA 构建到服务器的 `wwwroot/` 目录：

```bash
cd node_modules/@drawboard/authagonal-login
npx vite build
cp -r dist/* /path/to/wwwroot/
```

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
