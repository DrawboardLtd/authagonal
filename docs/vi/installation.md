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
  drawboardci/authagonal
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
- Node.js 24+

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
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

Gói nhà cung cấp lưu trữ có thể thay thế: `Authagonal.AzureProvider` cho Azure Table Storage (thiết lập `AddAuthagonal()` mặc định), `Authagonal.SqlProvider` cho PostgreSQL hoặc SQLite tự vận hành (xem [SQL backend](#sql-backend)), hoặc `Authagonal.AwsProvider` cho DynamoDB / S3 / Secrets Manager (xem [AWS backend](#aws-backend)).

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

Xem [Khả năng mở rộng](extensibility) để biết tất cả các điểm tùy chỉnh và [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server) để xem ví dụ hoàn chỉnh.

### Email

Trình gửi [Resend](https://resend.com) tích hợp sẵn sẽ tự động kích hoạt khi `Email:ResendApiKey` và `Email:SenderEmail` được cấu hình, không cần đăng ký dịch vụ. Nếu không có `IEmailService` nào, các email xác minh và đặt lại mật khẩu sẽ bị **âm thầm loại bỏ**, và vì đăng nhập mặc định yêu cầu email đã được xác nhận, người dùng tự đăng ký sẽ không bao giờ đăng nhập được (`UseAuthagonal` ghi một cảnh báo khi khởi động). Hãy hoặc đặt các khóa `Email:*`, đăng ký `IEmailService` của riêng bạn trước `AddAuthagonal()`, hoặc liệt kê các tên miền của bạn trong `Auth:AutoConfirmEmailDomains` để bỏ qua xác minh (chỉ dành cho dev/test). Xem [Cấu hình → Email](configuration#email).

## SQL backend

Để chạy trên cơ sở dữ liệu của riêng bạn thay vì một dịch vụ đám mây, hãy tham chiếu `Authagonal.SqlProvider` và đăng ký nó **trước** `AddAuthagonal()`: chính các đăng ký đó khiến `AddAuthagonal()` bỏ qua phần thiết lập Azure Table Storage của nó:

```csharp
using Authagonal.SqlProvider;

// PostgreSQL — the production self-hosted backend
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");

// or SQLite — one file, no server. Suits embedded hosts, CI and small single-node deployments
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");

builder.Services.AddAuthagonal(builder.Configuration);
```

Các bảng phản chiếu bố cục Azure và DynamoDB một-đối-một và được tạo khi khởi động nếu chưa có (mọi câu lệnh đều là `IF NOT EXISTS`, nên nhiều pod cùng tạo là an toàn và không làm gì với một lược đồ bạn đã tự cấp phát). Không cần cấu hình `Storage:*` nào. Vòng khóa Data Protection được lưu vào cùng cơ sở dữ liệu, nên cookie và token chống giả mạo (antiforgery) sống sót qua các lần khởi động lại và hoạt động giữa các pod mà không cần dịch vụ bổ sung nào.

SQLite tuần tự hóa các tiến trình ghi, nên nó là backend một node: lease trong tiến trình và bus sự kiện cụm được đăng ký mặc định chính là cặp phù hợp ở đó. Một triển khai PostgreSQL nhiều pod sẽ cần `clustering.UseSql(dataSource)` cho bầu chọn leader.

> **Collation (thứ tự đối chiếu).** Trên PostgreSQL, các cột khóa được ghim vào `COLLATE "C"`. Lược đồ khóa là thứ tự byte xuyên suốt (biên tiền tố, dải phân vùng theo môi trường, quét hết hạn của các grant, phân trang keyset), và một cơ sở dữ liệu được tạo với collation ngôn ngữ -- `en_US.UTF-8` và các locale ICU là mặc định phổ biến -- sẽ sắp xếp dấu câu và chữ hoa/thường khác đi và âm thầm trả về sai hàng. Việc ghim làm cho bố cục độc lập với cách cơ sở dữ liệu được tạo; bạn không cần tạo nó theo bất kỳ cách cụ thể nào.

Xem [README của gói](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider) để biết bố cục bảng, các nguyên thủy đồng thời đứng sau từng bảo đảm dùng-một-lần, và cách thêm một phương ngữ cho engine khác.

## AWS backend

Để chạy trên AWS thay vì Azure, hãy tham chiếu `Authagonal.AwsProvider` và đăng ký bộ AWS **trước** `AddAuthagonal()`: chính các đăng ký đó khiến `AddAuthagonal()` bỏ qua phần thiết lập Azure Table Storage của nó:

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB — required
    secretsManager,          // IAmazonSecretsManager — optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3 — optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

Các bảng DynamoDB phản chiếu bố cục Azure một-đối-một và được đảm bảo khi khởi động (idempotent, không làm gì khi chúng đã được cấp phát bởi Terraform). Thông tin xác thực được phân giải qua chuỗi AWS tiêu chuẩn (env / vai trò EC2 instance / IRSA), nên không có sự phân tách giữa chuỗi kết nối và managed identity: không cần cấu hình `Storage:*` nào.

> ⚠️ **Khóa S3 DataProtection.** Nếu không có client S3 + bucket, vòng khóa ASP.NET Core Data Protection được giữ trong bộ nhớ, ổn với một node đơn trong dev, nhưng cookie và token chống giả mạo (antiforgery) sẽ hỏng khi khởi động lại và giữa các node trong production. Hãy luôn truyền client S3 và bucket cho một triển khai AWS production.

## SPA đăng nhập (npm)

Giao diện đăng nhập được phát hành dưới dạng gói npm để tùy chỉnh:

```bash
npm install @authagonal/login
```

Gói đã bao gồm JS và CSS đã biên dịch: nhập trực tiếp các component và style vào ứng dụng React của bạn. Xem [Máy chủ tùy chỉnh](custom-server) để biết hướng dẫn đầy đủ.

## Danh sách kiểm tra bảo mật cho production

Trước khi đưa Authagonal ra lưu lượng thực, hãy xác nhận những điều sau. Mỗi mục được trình bày chi tiết trên trang [Cấu hình](configuration).

- **Chạy phía sau một proxy kết thúc TLS, và khai báo proxy đó.** Authagonal phải nằm sau một reverse proxy / ingress kết thúc TLS (hoặc tự kết thúc TLS). HSTS chỉ được phát trên HTTPS và `/connect/*` từ chối văn bản thuần, nên proxy phải chuyển tiếp `X-Forwarded-Proto: https` — và header đó sẽ bị bỏ qua chừng nào bạn chưa đặt `ForwardedHeaders:KnownNetworks` (hoặc `KnownProxies`) thành CIDR hoặc địa chỉ của proxy. Dùng `["0.0.0.0/0", "::/0"]` nếu proxy không có địa chỉ cố định và không gì khác có thể tiếp cận tiến trình. `ForwardedHeaders:ForwardLimit` mặc định là `1` (chỉ tin cậy hop cuối cùng).
- **Đặt `SecretProvider:VaultUri`.** Nhà cung cấp bí mật mặc định là **văn bản thuần**: nếu không có Key Vault, bí mật của client OIDC thượng nguồn và seed TOTP / MFA được lưu dưới dạng văn bản rõ trong Table Storage (và trong các bản sao lưu). Hãy cấu hình Key Vault cho bất kỳ triển khai production nào.
- **Khóa chặt API quản trị.** `AdminApi:Enabled` mặc định là **true**. Scope quản trị (`AdminApi:Scope`, mặc định `authagonal-admin`) cấp toàn quyền quản lý và giả mạo người dùng. Hãy giới hạn mạng cho các route quản trị `/api/v1/*` và kiểm soát chặt chẽ ai được cấp scope quản trị, hoặc đặt `AdminApi:Enabled = false` nếu không sử dụng.
- **Bảo vệ các endpoint nội bộ.** Đặt `Cluster:Secret` để endpoint nội bộ `/_internal/backchannel-logout` yêu cầu header `X-Cluster-Secret` (được so sánh trong thời gian không đổi). Khi không có bí mật, endpoint không cấp quyền cho **bất kỳ ai** và trả về 404: địa chỉ nguồn không phải là credential, và loopback là thứ mà reverse proxy trên cùng host xuất trình cho mọi yêu cầu nó chuyển tiếp. `Cluster:AllowLoopbackWithoutSecret` cho phép lại peer loopback trước khi chuyển tiếp, chỉ dành cho phát triển cục bộ. Không có gì trong sản phẩm được phát hành gọi endpoint này, nên việc fail-closed không làm hỏng luồng nào của bên thứ nhất: hãy đặt bí mật nếu bạn tự xây dựng cơ chế phát tán pod-to-pod trên đó.
- **Mã hóa các bản sao lưu.** Với nhà cung cấp bí mật văn bản thuần, các bản sao lưu chứa bí mật. Bảng `SigningKeys` bị loại trừ khỏi các bản sao lưu theo mặc định; nếu bạn tùy chọn bật qua `Backup:IncludeSigningKeys`, đích sao lưu phải được mã hóa khi lưu trữ. Xem [Sao lưu & Khôi phục](backup-restore).

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
