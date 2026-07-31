---
layout: default
title: Khả năng mở rộng
locale: vi
---

# Khả năng mở rộng

Authagonal có thể được tích hợp dưới dạng thư viện trong dự án ASP.NET Core của bạn, với toàn quyền kiểm soát các triển khai dịch vụ.

## Phương thức mở rộng

Ba phương thức tích hợp Authagonal vào bất kỳ ứng dụng ASP.NET Core nào:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

### Lưu trữ đa tenant

Đối với triển khai đa tenant, sử dụng `AddAuthagonalCore()` thay thế. Nó đăng ký endpoint, middleware và các dịch vụ cốt lõi nhưng bỏ qua storage và các dịch vụ nền; bạn cung cấp chúng theo từng tenant. Việc quản lý khóa ký mặc định dùng singleton `ProtocolKeyManager` của `Authagonal.Protocol`, và một host tự đăng ký `IKeyManager` của riêng mình trước khi gọi `AddAuthagonalCore()` sẽ giữ lại nó:

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` và các giao diện lưu trữ (`IClientStore`, `IScimTokenStore`, v.v.) được giải quyết từ `HttpContext.RequestServices` tại thời điểm yêu cầu, nên các đăng ký scoped hoạt động chính xác cho cách ly theo tenant.

## Ghi đè dịch vụ

Đăng ký các triển khai tùy chỉnh **trước** khi gọi `AddAuthagonal()`. Authagonal sử dụng `TryAdd` nội bộ, nên các đăng ký của bạn được ưu tiên:

```csharp
// Custom implementations, registered first so they won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

`IAuthHook` là trường hợp đặc biệt: nó là một pipeline đa đăng ký. Đăng ký bao nhiêu hook tùy thích (mọi lifetime, kể cả `AddScoped`) và tất cả đều chạy theo thứ tự đăng ký. `NullAuthHook` (không làm gì) chỉ được thêm vào khi chưa có hook nào được đăng ký tại thời điểm `AddAuthagonal()` / `AddAuthagonalCore()` chạy, nên hãy luôn đăng ký các hook của bạn trước.

### Các điểm mở rộng

| Giao diện | Mặc định | Mục đích |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (không làm gì, chỉ thêm khi không có hook nào được đăng ký) | Hook vòng đời cho các sự kiện xác thực: ghi nhật ký kiểm tra, xác thực tùy chỉnh, webhooks. Có thể đăng ký nhiều hook; tất cả chạy theo thứ tự |
| `IEmailService` | `NullEmailService` (không làm gì), hoặc bộ gửi Resend tích hợp sẵn khi `Email:ResendApiKey` được cấu hình | Gửi email cho xác minh, đặt lại mật khẩu và thông báo tài khoản đã tồn tại |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` (scoped) | Cấp phát người dùng vào các ứng dụng phía sau |
| `ISecretProvider` | `PlaintextSecretProvider`, hoặc `KeyVaultSecretProvider` tích hợp sẵn khi `SecretProvider:VaultUri` được cấu hình | Lưu trữ bí mật có thể đảo ngược (Key Vault, AWS Secrets Manager, Vault Transit, v.v.) |
| `ITenantContext` | `DefaultTenantContext` (đọc từ `IConfiguration`) | Giải quyết tenant cho triển khai đa tenant |
| `IKeyManager` | `ProtocolKeyManager` (singleton, từ `Authagonal.Protocol`) | Quản lý khóa ký; ghi đè cho cách ly khóa theo tenant |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider` (scoped) | Giải quyết các ứng dụng cấp phát khả dụng; ghi đè cho giải quyết ứng dụng động hoặc theo tenant |
| `IAuditLogger` | `NullAuditLogger` (không làm gì) | Nhật ký kiểm tra cho các thay đổi cấu hình và sự kiện liên quan đến bảo mật |

Ba phương thức mở rộng khác nằm ở **cấp store** thay vì trong DI: `IFieldCipher`, `IIndexTokenizer` và `IChangeWriter` (tất cả đều trong `Authagonal.Core.Services`). Các nhà cung cấp lưu trữ nhận chúng như các tham số constructor tùy chọn; xem các mục tương ứng bên dưới.

## IAuthHook

Giao diện `IAuthHook` cung cấp các hook vào vòng đời xác thực. Các phương thức trên đường dẫn quan trọng (xác thực, tạo người dùng, phát hành token) có thể ném ngoại lệ để hủy bỏ thao tác; các phương thức mới hơn là các thông báo sau sự việc. Có thể đăng ký nhiều triển khai `IAuthHook` và tất cả đều chạy theo thứ tự đăng ký.

```csharp
public interface IAuthHook
{
    // Core lifecycle: implement these
    Task OnUserAuthenticatedAsync(string userId, string email, string method,
        string? clientId = null, CancellationToken ct = default);
    Task OnUserCreatedAsync(string userId, string email, string createdVia,
        CancellationToken ct = default);
    Task OnLoginFailedAsync(string email, string reason,
        CancellationToken ct = default);
    Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType,
        CancellationToken ct = default);
    Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email,
        MfaPolicy clientPolicy, string clientId, CancellationToken ct = default);
    Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default);
    Task OnUserUpdatedAsync(string userId, string email, string updatedVia,
        CancellationToken ct = default);
    Task OnUserDeletedAsync(string userId, string email, string deletedVia,
        CancellationToken ct = default);

    // Additive notifications: default no-op implementations, so existing
    // hooks keep compiling as the interface grows
    Task OnMfaVerifyFailedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnEmailConfirmedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaEnrolledAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaCredentialRemovedAsync(string userId, string email, string mfaMethod,
        bool mfaDisabled, CancellationToken ct = default) => Task.CompletedTask;
    Task OnRecoveryCodesRegeneratedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnPasswordChangedAsync(string userId, string email, string changedVia,
        CancellationToken ct = default) => Task.CompletedTask;
}
```

### Tham số

| Phương thức | Ghi chú và các giá trị `method` / `via` |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"passkey"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnUserUpdatedAsync` | `"admin"`, `"self"` (các host có thể truyền giá trị riêng, ví dụ một nguồn SCIM) |
| `OnUserDeletedAsync` | `"admin"`; chỉ là thông báo, bản ghi có thể không còn đọc được |
| `OnLoginFailedAsync` | `"user_not_found"`, `"invalid_password"`, v.v. |
| `OnTokenIssuedAsync` | Các loại cấp quyền: `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Được gọi sau khi xác minh mật khẩu; trả về chính sách MFA hiệu lực cho người dùng. Mặc định: trả về `clientPolicy` không thay đổi. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |
| `OnMfaVerifyFailedAsync` | Cùng các phương thức như `OnMfaVerifiedAsync`. Chỉ kích hoạt sau khi thông tin xác thực yếu tố thứ nhất hợp lệ, nên các đợt dồn dập là tín hiệu mạnh về nỗ lực vượt qua MFA (khác với `OnLoginFailedAsync`, ở giai đoạn mật khẩu) |
| `OnEmailConfirmedAsync` | Người dùng đã xác nhận email qua liên kết xác minh; đã được lưu |
| `OnMfaEnrolledAsync` | `"totp"`, `"webauthn"`; thông tin xác thực đã hoạt động |
| `OnMfaCredentialRemovedAsync` | `"totp"`, `"webauthn"`, `"recoverycode"`; `mfaDisabled` là true khi việc gỡ bỏ không còn để lại yếu tố chính nào |
| `OnRecoveryCodesRegeneratedAsync` | Bộ mã khôi phục trước đó bị vô hiệu hóa |
| `OnPasswordChangedAsync` | ví dụ `"reset"`; thay đổi đã được lưu và các phiên hiện có bị vô hiệu hóa |

### Ví dụ: Ghi nhật ký kiểm tra

```csharp
public sealed class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email,
        string createdVia, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] User created: {Email} via {Via}", email, createdVia);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct)
    {
        logger.LogWarning("[AUDIT] Login failed: {Email} ({Reason})", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Token issued: {ClientId} ({GrantType})",
            clientId, grantType);
        return Task.CompletedTask;
    }

    // ... remaining required methods return Task.CompletedTask
}
```

### Ví dụ: Hạn chế tên miền

```csharp
public sealed class DomainRestrictionHook : IAuthHook
{
    private static readonly HashSet<string> BlockedDomains = ["competitor.com"];

    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        var domain = email.Split('@').Last();
        if (BlockedDomains.Contains(domain))
            throw new InvalidOperationException($"Domain {domain} is not allowed");

        return Task.CompletedTask;
    }

    // ... other methods return Task.CompletedTask
}
```

## ISecretProvider

`ISecretProvider` (trong `Authagonal.Core.Services`) là phương thức mã hóa có thể đảo ngược cho các bí mật được lưu trữ như client secret SSO, mật khẩu SMTP và hạt giống TOTP. `ProtectAsync` biến một plaintext thành một tham chiếu mà store lưu lại; `ResolveAsync` biến tham chiếu đó trở lại thành plaintext. `PlaintextSecretProvider` mặc định lưu các giá trị nguyên trạng (tham chiếu CHÍNH LÀ giá trị).

```csharp
public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
```

Đặt `SecretProvider:VaultUri` sẽ tự động nối `KeyVaultSecretProvider` tích hợp sẵn (Azure Key Vault qua `DefaultAzureCredential`). Với bất kỳ trường hợp nào khác, hãy đăng ký triển khai của riêng bạn trước khi gọi `AddAuthagonal()`.

## Mã hóa trường PII: IFieldCipher

`IFieldCipher` mã hóa từng giá trị trường PII của người dùng (số điện thoại, công ty, thuộc tính tùy chỉnh, email và tên trên hàng profile) khi lưu trữ. Đây là một phương thức ở cấp store: các nhà cung cấp lưu trữ nhận nó như một tham số constructor tùy chọn (ví dụ `TableUserStore`), và khi vắng mặt thì `NullFieldCipher` (chuyển tiếp thẳng) được áp dụng, nên mã hóa hoàn toàn là tùy chọn và các host chưa cấu hình vẫn tiếp tục lưu plaintext.

```csharp
public interface IFieldCipher
{
    Task<string> ProtectAsync(string plaintext, CancellationToken ct = default);
    Task<string> ResolveAsync(string stored, CancellationToken ct = default);

    // Batch variants have default loop implementations; override for backends
    // with a one-round-trip batch primitive (e.g. Vault Transit)
    Task<IReadOnlyList<string>> ProtectManyAsync(IReadOnlyList<string> plaintexts,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResolveManyAsync(IReadOnlyList<string> stored,
        CancellationToken ct = default);
}
```

Hai điểm hợp đồng quan trọng. `ProtectAsync` phải trả về một token ciphertext tự mô tả (ví dụ `vault:v{n}:...` của Vault Transit), và `ResolveAsync` phải chuyển qua nguyên vẹn một giá trị mà nó không nhận ra là ciphertext của chính mình. Quy tắc chuyển tiếp thẳng chính là điều cho phép mã hóa được triển khai lười biếng trên các hàng hiện có: một lần đọc hàng chưa di trú trả về plaintext kế thừa, và lần ghi tiếp theo sẽ bảo vệ lại nó.

## Tìm kiếm chỉ mục mù: IIndexTokenizer

`IIndexTokenizer` giữ cho các trường đã mã hóa vẫn có thể tìm kiếm được. Nó biến một giá trị plaintext đã chuẩn hóa thành một token chỉ mục mù có tính xác định, an toàn làm khóa bảng, thường là một HMAC có khóa với khóa nằm ngoài cơ sở dữ liệu. Tính xác định nghĩa là tra cứu bằng đẳng thức vẫn hoạt động ("email = x" trở thành "token = HMAC(x)"), trong khi một bản dump cơ sở dữ liệu không thể tính lại lẫn đảo ngược một token. Tìm kiếm theo tiền tố được xếp chồng lên trên bằng cách token hóa riêng từng tiền tố của một giá trị, vì một HMAC có khóa phá vỡ thứ tự và quét dải.

> **Những gì một bản dump vẫn để lộ.** "Không thể tính lại lẫn đảo ngược" đúng với một token đơn lẻ,
> chứ không đúng với toàn bộ chỉ mục. Ba phần dư còn lại, và bạn nên biết chúng trước khi dựa vào cơ
> chế này:
>
>   *(Đã khắc phục.)* ~~**Cấu trúc.** Chỉ mục tiền tố ghi một hàng cho mỗi tiền tố, nên số hàng của
>   một bản ghi bằng đúng độ dài của trường được lập chỉ mục.~~ Mỗi giá trị được lập chỉ mục nay ghi
>   một số hàng cố định, được độn thêm các mồi nhử mà không truy vấn nào tạo ra được và một bản dump
>   không thể phân biệt với tiền tố thật.
> - **Đẳng thức và tần suất.** Token mang tính xác định theo thiết kế -- chính điều đó khiến việc tra
>   cứu hoạt động -- nên một bản dump cho thấy những bản ghi nào dùng chung một giá trị và mỗi giá
>   trị phổ biến đến đâu. Chỉ mục tên miền phân nhóm tập người dùng của bạn theo nơi làm việc, điều
>   này thường đủ để nhận diện con người mà không cần khôi phục địa chỉ.
> - **Plaintext được chọn.** Kẻ vừa đọc được kho lưu trữ *vừa* có thể khiến các giá trị được lập chỉ
>   mục (đăng ký một tài khoản, được cấp phát qua SCIM) có thể gửi lên một ứng viên rồi tìm token của
>   nó. Việc đó khôi phục được bất kỳ giá trị nào đoán được -- các tên miền phổ biến, các tên riêng
>   phổ biến -- bất kể khóa nằm ở đâu, vì oracle chính là đường ghi chứ không phải thuật toán mã hóa.
>
> Token hóa phòng vệ đúng trường hợp mà nó được xây dựng cho: ai đó chỉ có một bản dump và không có
> gì khác, đang cố đọc các địa chỉ. Hai phần dư còn lại đúng bằng những gì một oracle đăng ký vốn đã
> để lộ. Nếu chúng là không chấp nhận được, hãy để các bảng chỉ mục tiền tố và tên miền không được
> cấu hình -- tra cứu khớp chính xác không mang theo cả hai -- thay vì giả định rằng HMAC đã che phủ
> chúng.

```csharp
public interface IIndexTokenizer
{
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values,
        CancellationToken ct = default);
}
```

Giống như `IFieldCipher`, đây là một tham số constructor tùy chọn của store với mặc định chuyển tiếp thẳng (`NullIndexTokenizer`), nên các hàng chỉ mục vẫn được khóa theo plaintext cho đến khi bạn chọn dùng. Các token trả về phải an toàn khi làm giá trị PartitionKey/RowKey của Azure Table (không chứa `/ \ # ?` hoặc ký tự điều khiển).

## Ghi nhật ký thay đổi: IChangeWriter

`IChangeWriter` (đổi tên từ `ITombstoneWriter` trong 0.6.0) ghi lại khóa của mọi hàng đã thay đổi vào một bảng nhật ký thay đổi chuyên dụng, để các bản sao lưu tăng dần có thể tìm ra những gì đã thay đổi mà không cần quét cột `Timestamp` không được lập chỉ mục của các bảng đang hoạt động. Các thao tác xóa được ghi lại cho mọi bảng (một lần quét hàng đang hoạt động không thể thấy một hàng đã biến mất); các thao tác upsert được ghi lại cho những bảng mà bản sao lưu đọc từ nhật ký thay vì quét. Các triển khai tích hợp sẵn: `TableChangeWriter` (Azure Table Storage), `DynamoChangeWriter` (DynamoDB) và `SqlChangeWriter` (PostgreSQL / SQLite).

```csharp
public interface IChangeWriter
{
    // Deletes
    Task WriteAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);

    // Upserts
    Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteUpsertBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);
}
```

Hợp đồng về thứ tự cho người triển khai và người gọi: ghi tombstone xóa TRƯỚC khi xóa hàng dữ liệu. Một sự cố theo thứ tự ngược lại sẽ làm mất thao tác xóa khỏi mọi bản sao lưu tương lai, vì xóa là loại thay đổi duy nhất mà một lần quét lại không thể tự chữa lành. Sự cố theo chiều ngược lại thì an toàn: một lần ghi sau đó vào khóa sẽ đóng dấu thời gian mới hơn, và merge/restore vẫn giữ các hàng được ghi sau tombstone.

## Endpoint tùy chỉnh

Thêm các endpoint riêng của bạn bên cạnh các endpoint của Authagonal:

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Tích hợp HashiCorp Vault Transit

Authagonal có thể ủy thác việc ký JWT cho Transit secrets engine của HashiCorp Vault. Khóa riêng tư không bao giờ rời khỏi Vault; chỉ có thao tác ký là từ xa. Khóa công khai được lưu bộ nhớ đệm cục bộ để xác minh.

```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure Vault Transit HTTP client
builder.Services.AddHttpClient("Vault", client =>
{
    client.BaseAddress = new Uri("https://vault.example.com");
    client.DefaultRequestHeaders.Add("X-Vault-Token", "hvs.xxx");
});

// Register Vault Transit services
builder.Services.AddSingleton<VaultTransitClient>();
builder.Services.AddSingleton<VaultTransitCryptoProvider>();

builder.Services.AddAuthagonal(builder.Configuration);
```

`VaultTransitClient` cung cấp các thao tác sau:

| Phương thức | Mô tả |
|---|---|
| `SignAsync(keyName, data)` | Ký dữ liệu bằng một khóa Vault Transit |
| `VerifyAsync(keyName, data, signature)` | Xác minh một chữ ký được marshal theo JWS qua endpoint verify của Transit |
| `EncryptAsync` / `DecryptAsync` (+ `EncryptBatchAsync` / `DecryptBatchAsync`) | Mã hóa đối xứng dưới một khóa `aes256-gcm96`; trả về các token `vault:v{n}:...` để lưu nguyên văn |
| `HmacAsync` / `HmacBatchAsync` | HMAC có khóa dưới một khóa `hmac` (các token chỉ mục mù) |
| `CreateKeyAsync(keyName, type)` | Tạo một khóa Transit mới (mặc định: `ecdsa-p256`) |
| `EnsureKeyTypeAsync(keyName, type)` | Đảm bảo một cách idempotent rằng một khóa tồn tại với loại mong muốn (tạo lại khi loại không khớp; khóa Transit không thể đổi loại tại chỗ) |
| `RotateKeyAsync(keyName)` | Xoay vòng một khóa sang phiên bản mới |
| `DeleteKeyAsync(keyName)` | Xóa một khóa (bật `deletion_allowed` trước) |
| `ReadKeyAsync(keyName)` | Đọc metadata, các phiên bản và khóa công khai của khóa |
| `KeyExistsAsync(keyName)` | Kiểm tra xem một khóa có tồn tại hay không |

`VaultTransitCryptoProvider` tích hợp với `JsonWebTokenHandler` của .NET để việc ký JWT dùng Vault một cách trong suốt. `VaultTransitSecurityKey` và `VaultTransitSignatureProvider` xử lý phần tích hợp ở mức thấp.

## Email

Bộ gửi Resend tích hợp sẵn tự động kích hoạt khi `Email:ResendApiKey` được cấu hình (hãy đặt cả `Email:SenderEmail`). Nếu không có bất kỳ `IEmailService` nào, email bị loại bỏ qua `NullEmailService`, và vì cổng đăng nhập yêu-cầu-email-đã-xác-nhận mặc định bật, những người dùng tự đăng ký sẽ không bao giờ đăng nhập được; `UseAuthagonal()` ghi một cảnh báo khởi động rõ ràng trong trạng thái đó.

Để dùng một nhà cung cấp khác, hãy đăng ký `IEmailService` của riêng bạn trước khi gọi `AddAuthagonal()`:

```csharp
public sealed class SmtpEmailService(SmtpClient smtp) : IEmailService
{
    public async Task SendVerificationEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Verify your email", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Reset your password", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }
}
```

`IEmailService` cũng khai báo `SendAccountExistsEmailAsync` (được gửi khi ai đó cố đăng ký một email đã đăng ký, giữ cho phản hồi đăng ký trung lập trước việc dò tìm tài khoản). Nó có một triển khai mặc định không làm gì, nên các triển khai hiện có vẫn biên dịch được.

## Xem thêm

- [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server): ví dụ hoạt động hoàn chỉnh
- [demos/sample-app/](https://github.com/authagonal/authagonal/tree/master/demos/sample-app): ví dụ ứng dụng client
