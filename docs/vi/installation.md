---
layout: default
title: Cài đặt
locale: vi
---

# Cài đặt

## Docker (khuyến nghị)

Tải và chạy image đã được build sẵn:

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  authagonal
```

## Docker Compose

Để phát triển local với Azurite (trình giả lập Azure Storage):

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

## Build từ mã nguồn

### Yêu cầu

- .NET 10 SDK
- Node.js 22+

### Build

```bash
# Build toàn bộ
dotnet build

# Build SPA đăng nhập
cd login-app
npm ci
npm run build

# Chạy máy chủ
dotnet run --project src/Authagonal.Server
```

### Build Docker

```bash
# Image máy chủ (multi-stage: build SPA + .NET trong một image)
docker build -t authagonal .

# Công cụ di chuyển
docker build -f Dockerfile.migration -t authagonal-migration .
```

## Dưới dạng thư viện (NuGet)

Tham chiếu các gói Authagonal trong dự án ASP.NET Core của bạn:

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.Storage" Version="x.y.z" />
```

Sau đó tích hợp vào `Program.cs` của bạn:

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

Xem [Khả năng mở rộng](extensibility) để biết tất cả các điểm tùy chỉnh và [demos/custom-server/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/custom-server) để xem ví dụ hoàn chỉnh.

## SPA đăng nhập (npm)

Giao diện đăng nhập được phát hành dưới dạng gói npm để tùy chỉnh:

```bash
npm install @drawboard/authagonal-login
```

Tùy chỉnh qua `branding.json` (xem [Tùy chỉnh giao diện](branding)) và build SPA vào thư mục `wwwroot/` của máy chủ:

```bash
cd node_modules/@drawboard/authagonal-login
npx vite build
cp -r dist/* /path/to/wwwroot/
```

## Công cụ di chuyển

Để di chuyển từ Duende IdentityServer + SQL Server:

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Xem [Di chuyển](migration) để biết chi tiết.
