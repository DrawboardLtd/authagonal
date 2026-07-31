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
  drawboardci/authagonal
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
- Node.js 24+

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
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

存储提供者包是可插拔的：`Authagonal.AzureProvider` 用于 Azure Table Storage（默认的 `AddAuthagonal()` 接线），`Authagonal.SqlProvider` 用于自托管的 PostgreSQL 或 SQLite（参见 [SQL 后端](#sql-backend)），或 `Authagonal.AwsProvider` 用于 DynamoDB / S3 / Secrets Manager（参见 [AWS 后端](#aws-backend)）。

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

### 邮件

内置的 [Resend](https://resend.com) 发送器会在配置了 `Email:ResendApiKey` 和 `Email:SenderEmail` 时自动激活——无需注册服务。如果没有任何 `IEmailService`，验证邮件和密码重置邮件会被**静默丢弃**，而由于登录默认要求已确认的邮箱，自助注册的用户将永远无法登录（`UseAuthagonal` 会在启动时记录一条警告）。请设置 `Email:*` 键、在 `AddAuthagonal()` 之前注册您自己的 `IEmailService`，或在 `Auth:AutoConfirmEmailDomains` 中列出您的域名以跳过验证（仅限开发/测试）。参见 [配置 → 邮件](configuration#email)。

## SQL 后端 {#sql-backend}

要在您自己的数据库而非云服务上运行，请引用 `Authagonal.SqlProvider` 并在 `AddAuthagonal()` **之前**注册它——正是这些注册让 `AddAuthagonal()` 跳过其 Azure Table Storage 接线：

```csharp
using Authagonal.SqlProvider;

// PostgreSQL — the production self-hosted backend
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");

// or SQLite — one file, no server. Suits embedded hosts, CI and small single-node deployments
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");

builder.Services.AddAuthagonal(builder.Configuration);
```

这些表与 Azure 和 DynamoDB 的布局一一对应，并在启动时按需创建（每条语句都是 `IF NOT EXISTS`，因此多个 Pod 并发创建是安全的，而对您自行预配的架构则是空操作）。无需任何 `Storage:*` 配置。Data Protection 密钥环持久化到同一个数据库，因此 cookie 和防伪令牌可在重启后保留，并可跨 Pod 工作，无需额外服务。

SQLite 会将写入串行化，因此它是单节点后端——默认注册的进程内租约和集群事件总线正是那里的正确搭配。多 Pod 的 PostgreSQL 部署则需要 `clustering.UseSql(dataSource)` 来进行领导者选举。

> **排序规则（Collation）。** 在 PostgreSQL 上，键列被固定为 `COLLATE "C"`。键方案自始至终按字节序（前缀边界、环境分区范围、授权过期清扫、keyset 分页），而使用语言排序规则创建的数据库——`en_US.UTF-8` 和 ICU 区域设置是常见默认值——对标点和大小写的排序方式不同，会静默返回错误的行。这一固定使布局与数据库的创建方式无关；您无需以任何特定方式创建它。

表布局、每项一次性保证背后的并发原语，以及如何为其他引擎添加方言，参见 [包 README](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider)。

## AWS 后端 {#aws-backend}

要在 AWS 而非 Azure 上运行，请引用 `Authagonal.AwsProvider` 并在 `AddAuthagonal()` **之前**注册 AWS 套件——正是这些注册让 `AddAuthagonal()` 跳过其 Azure Table Storage 接线：

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB — required
    secretsManager,          // IAmazonSecretsManager — optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3 — optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

DynamoDB 表与 Azure 布局一一对应，并在启动时确保存在（幂等——当它们已由 Terraform 预配时则为空操作）。凭据通过标准的 AWS 链解析（环境变量 / EC2 实例角色 / IRSA），因此不存在连接字符串与托管标识之分——无需任何 `Storage:*` 配置。

> ⚠️ **S3 DataProtection 密钥。** 若没有 S3 客户端 + 存储桶，ASP.NET Core Data Protection 密钥环会保存在内存中——在开发环境的单个节点上没问题，但在生产环境中，cookie 和防伪令牌会在重启时以及跨节点时失效。对于生产环境的 AWS 部署，请始终传入 S3 客户端和存储桶。

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
- **保护内部端点。** 设置 `Cluster:Secret`，使内部的 `/_internal/backchannel-logout` 端点要求 `X-Cluster-Secret` 请求头（以恒定时间比较）。未设置时，它只接受回环 / 私有（RFC 1918 / 链路本地 / ULA）源 IP——请确保已配置您的 forwarded-headers 信任，使外部调用者无法伪装成内部调用者。
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
