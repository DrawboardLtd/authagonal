---
layout: default
title: Cấu hình
locale: vi
---

# Cấu hình

Authagonal được cấu hình qua `appsettings.json` hoặc biến môi trường. Biến môi trường sử dụng `__` làm dấu phân cách phần (ví dụ: `Storage__ConnectionString`).

## Cài đặt bắt buộc

| Cài đặt | Biến môi trường | Mô tả |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Chuỗi kết nối Azure Table Storage |
| `Issuer` | `Issuer` | URL công khai gốc của máy chủ này (ví dụ: `https://auth.example.com`) |

## Xác thực

| Cài đặt | Mặc định | Mô tả |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Thời gian sống phiên cookie (trượt) |
| `Auth:MaxFailedAttempts` | `5` | Số lần đăng nhập thất bại trước khi khóa tài khoản |
| `Auth:LockoutDurationMinutes` | `10` | Thời gian khóa tài khoản sau khi vượt quá số lần thất bại tối đa |
| `Auth:MaxRegistrationsPerIp` | `5` | Số lượt đăng ký tối đa mỗi địa chỉ IP trong cửa sổ thời gian |
| `Auth:RegistrationWindowMinutes` | `60` | Cửa sổ giới hạn tốc độ đăng ký |
| `Auth:EmailVerificationExpiryHours` | `24` | Thời gian hiệu lực liên kết xác minh email |
| `Auth:PasswordResetExpiryMinutes` | `60` | Thời gian hiệu lực liên kết đặt lại mật khẩu |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Thời gian hiệu lực token xác thực MFA |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Thời gian hiệu lực token thiết lập MFA (cho đăng ký bắt buộc) |
| `Auth:Pbkdf2Iterations` | `100000` | Số lần lặp PBKDF2 cho băm mật khẩu |
| `Auth:RefreshTokenReuseGraceSeconds` | `60` | Cửa sổ cho phép sử dụng lại refresh token đồng thời |
| `Auth:SigningKeyLifetimeDays` | `90` | Thời gian hiệu lực khóa ký RSA trước khi tự động xoay vòng |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Tần suất tải lại khóa ký từ bộ lưu trữ |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Khoảng cách giữa các lần kiểm tra dấu bảo mật cookie |

## Bộ nhớ đệm và thời gian chờ

| Cài đặt | Mặc định | Mô tả |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | Thời gian lưu bộ nhớ đệm các origin CORS được phép |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | Thời gian lưu bộ nhớ đệm tài liệu khám phá OIDC |
| `Cache:SamlMetadataCacheMinutes` | `60` | Thời gian lưu bộ nhớ đệm metadata SAML IdP |
| `Cache:OidcStateLifetimeMinutes` | `10` | Thời gian hiệu lực tham số state ủy quyền OIDC |
| `Cache:SamlReplayLifetimeMinutes` | `10` | Thời gian hiệu lực ID AuthnRequest SAML (chống phát lại) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Thời gian chờ kiểm tra sức khỏe Table Storage |

## Dịch vụ nền

| Cài đặt | Mặc định | Mô tả |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | Độ trễ ban đầu trước lần dọn dẹp token hết hạn đầu tiên |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | Khoảng cách dọn dẹp token hết hạn |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | Độ trễ ban đầu trước lần đối chiếu cấp quyền đầu tiên |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | Khoảng cách đối chiếu cấp quyền |

## Client

Các client được định nghĩa trong mảng `Clients` và được khởi tạo khi khởi động. Mỗi client có thể có:

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "ClientName": "My Application",
      "ClientSecretHashes": ["sha256-hash-here"],
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email", "custom-scope"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "AlwaysIncludeUserClaimsInIdToken": false,
      "AccessTokenLifetimeSeconds": 1800,
      "IdentityTokenLifetimeSeconds": 300,
      "AuthorizationCodeLifetimeSeconds": 300,
      "AbsoluteRefreshTokenLifetimeSeconds": 2592000,
      "SlidingRefreshTokenLifetimeSeconds": 1296000,
      "RefreshTokenUsage": "OneTime",
      "MfaPolicy": "Enabled",
      "ProvisioningApps": ["my-backend"]
    }
  ]
}
```

### Các loại cấp quyền

| Loại cấp quyền | Trường hợp sử dụng |
|---|---|
| `authorization_code` | Đăng nhập tương tác người dùng (ứng dụng web, SPA, di động) |
| `client_credentials` | Giao tiếp giữa các dịch vụ |
| `refresh_token` | Gia hạn token (yêu cầu `AllowOfflineAccess: true`) |

### Sử dụng Refresh Token

| Giá trị | Hành vi |
|---|---|
| `OneTime` (mặc định) | Mỗi lần làm mới sẽ cấp một refresh token mới. Token cũ bị vô hiệu hóa với cửa sổ cho phép 60 giây cho các yêu cầu đồng thời. Sử dụng lại sau cửa sổ cho phép sẽ thu hồi tất cả token của người dùng+client đó. |
| `ReUse` | Cùng một refresh token được sử dụng lại cho đến khi hết hạn. |

### Ứng dụng cấp phát

Mảng `ProvisioningApps` tham chiếu các ID ứng dụng được định nghĩa trong phần cấu hình `ProvisioningApps`. Khi người dùng ủy quyền qua client này, họ sẽ được cấp phát vào các ứng dụng đó qua TCC. Xem [Cấp phát](provisioning) để biết chi tiết.

## Ứng dụng cấp phát

Định nghĩa các ứng dụng phía sau mà người dùng cần được cấp phát vào:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-api-key"
    },
    "analytics": {
      "CallbackUrl": "https://analytics.example.com/provisioning",
      "ApiKey": "another-key"
    }
  }
}
```

Xem [Cấp phát](provisioning) để biết đặc tả đầy đủ giao thức TCC.

## Chính sách MFA

Xác thực đa yếu tố được áp dụng theo từng client thông qua thuộc tính `MfaPolicy`:

| Giá trị | Hành vi |
|---|---|
| `Disabled` (mặc định) | Không yêu cầu xác thực MFA, ngay cả khi người dùng đã đăng ký MFA |
| `Enabled` | Yêu cầu xác thực MFA cho người dùng đã đăng ký; không bắt buộc đăng ký |
| `Required` | Yêu cầu xác thực cho người dùng đã đăng ký; bắt buộc đăng ký cho người dùng chưa có MFA |

```json
{
  "Clients": [
    {
      "ClientId": "secure-app",
      "MfaPolicy": "Required"
    }
  ]
}
```

Khi `MfaPolicy` là `Required` và người dùng chưa đăng ký MFA, đăng nhập trả về `{ mfaSetupRequired: true, setupToken: "..." }`. Token thiết lập xác thực người dùng đến các endpoint thiết lập MFA (qua header `X-MFA-Setup-Token`) để họ có thể đăng ký trước khi nhận phiên cookie.

Đăng nhập liên kết (SAML/OIDC) bỏ qua MFA — nhà cung cấp danh tính bên ngoài xử lý việc này.

### Ghi đè IAuthHook

Phương thức `IAuthHook.ResolveMfaPolicyAsync` có thể ghi đè chính sách client cho từng người dùng:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Bắt buộc MFA cho người dùng quản trị bất kể cài đặt client
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    return Task.FromResult(clientPolicy);
}
```

## Chính sách mật khẩu

Tùy chỉnh yêu cầu độ mạnh mật khẩu:

```json
{
  "PasswordPolicy": {
    "MinLength": 10,
    "MinUniqueChars": 3,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": false
  }
}
```

| Thuộc tính | Mặc định | Mô tả |
|---|---|---|
| `MinLength` | `8` | Độ dài tối thiểu của mật khẩu |
| `MinUniqueChars` | `2` | Số lượng ký tự khác nhau tối thiểu |
| `RequireUppercase` | `true` | Yêu cầu ít nhất một chữ cái viết hoa |
| `RequireLowercase` | `true` | Yêu cầu ít nhất một chữ cái viết thường |
| `RequireDigit` | `true` | Yêu cầu ít nhất một chữ số |
| `RequireSpecialChar` | `true` | Yêu cầu ít nhất một ký tự không phải chữ và số |

Chính sách được áp dụng khi đặt lại mật khẩu và đăng ký người dùng qua quản trị. Giao diện đăng nhập lấy chính sách hiện hành từ `GET /api/auth/password-policy` để hiển thị yêu cầu một cách động.

## Nhà cung cấp SAML

Định nghĩa các nhà cung cấp danh tính SAML trong cấu hình. Chúng được khởi tạo khi khởi động:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com", "example.org"]
    }
  ]
}
```

| Thuộc tính | Bắt buộc | Mô tả |
|---|---|---|
| `ConnectionId` | Có | Mã định danh ổn định (dùng trong URL như `/saml/{connectionId}/login`) |
| `ConnectionName` | Không | Tên hiển thị (mặc định là ConnectionId) |
| `EntityId` | Có | Entity ID của SAML Service Provider |
| `MetadataLocation` | Có | URL đến tệp XML metadata SAML của IdP |
| `AllowedDomains` | Không | Các tên miền email được định tuyến đến nhà cung cấp này qua SSO |

## Nhà cung cấp OIDC

Định nghĩa các nhà cung cấp danh tính OIDC trong cấu hình. Chúng được khởi tạo khi khởi động:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

| Thuộc tính | Bắt buộc | Mô tả |
|---|---|---|
| `ConnectionId` | Có | Mã định danh ổn định (dùng trong URL như `/oidc/{connectionId}/login`) |
| `ConnectionName` | Không | Tên hiển thị (mặc định là ConnectionId) |
| `MetadataLocation` | Có | URL đến tài liệu khám phá OpenID Connect của IdP |
| `ClientId` | Có | OAuth2 client ID đã đăng ký với IdP |
| `ClientSecret` | Có | OAuth2 client secret (được bảo vệ qua `ISecretProvider` khi khởi động) |
| `RedirectUrl` | Có | OAuth2 redirect URI đã đăng ký với IdP |
| `AllowedDomains` | Không | Các tên miền email được định tuyến đến nhà cung cấp này qua SSO |

> **Lưu ý:** Các nhà cung cấp cũng có thể được quản lý tại thời điểm chạy qua [API Quản trị](admin-api). Các nhà cung cấp được khởi tạo từ cấu hình sẽ được upsert mỗi lần khởi động, nên thay đổi cấu hình có hiệu lực khi khởi động lại.

## Nhà cung cấp bí mật

Bí mật của client và nhà cung cấp OIDC có thể được lưu trữ tùy chọn trong Azure Key Vault:

| Cài đặt | Mô tả |
|---|---|
| `SecretProvider:VaultUri` | URI Key Vault (ví dụ: `https://my-vault.vault.azure.net/`). Nếu không đặt, bí mật được xử lý dạng văn bản thuần. |

Khi được cấu hình, các giá trị bí mật trông giống tham chiếu Key Vault sẽ được giải quyết tại thời điểm chạy. Sử dụng `DefaultAzureCredential` để xác thực.

## Email

Mặc định, Authagonal sử dụng dịch vụ email no-op bỏ qua tất cả email. Để bật gửi email, đăng ký triển khai `IEmailService` trước khi gọi `AddAuthagonal()`. Dịch vụ tích hợp `EmailService` sử dụng SendGrid.

| Cài đặt | Mô tả |
|---|---|
| `Email:SendGridApiKey` | Khóa API SendGrid để gửi email |
| `Email:FromAddress` | Địa chỉ email người gửi |
| `Email:FromName` | Tên hiển thị người gửi |
| `Email:VerificationTemplateId` | ID mẫu động SendGrid cho xác minh email |
| `Email:PasswordResetTemplateId` | ID mẫu động SendGrid cho đặt lại mật khẩu |

Email gửi đến địa chỉ `@example.com` sẽ được bỏ qua im lặng (hữu ích cho kiểm thử).

## Cluster

Các instance Authagonal tự động hình thành cụm để chia sẻ trạng thái giới hạn tốc độ. Clustering được bật mặc định mà không cần cấu hình.

| Cài đặt | Biến môi trường | Mặc định | Mô tả |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Công tắc chính cho clustering. Đặt thành `false` để chỉ giới hạn tốc độ cục bộ. |
| `Cluster:MulticastGroup` | `Cluster__MulticastGroup` | `239.42.42.42` | Nhóm UDP multicast để khám phá peer |
| `Cluster:MulticastPort` | `Cluster__MulticastPort` | `19847` | Cổng UDP multicast để khám phá peer |
| `Cluster:InternalUrl` | `Cluster__InternalUrl` | *(không có)* | URL cân bằng tải dự phòng cho gossip khi multicast không khả dụng |
| `Cluster:Secret` | `Cluster__Secret` | *(không có)* | Bí mật dùng chung để xác thực endpoint gossip (khuyến nghị khi `InternalUrl` được đặt) |
| `Cluster:GossipIntervalSeconds` | `Cluster__GossipIntervalSeconds` | `5` | Tần suất các instance trao đổi trạng thái giới hạn tốc độ |
| `Cluster:DiscoveryIntervalSeconds` | `Cluster__DiscoveryIntervalSeconds` | `10` | Tần suất các instance thông báo qua multicast |
| `Cluster:PeerStaleAfterSeconds` | `Cluster__PeerStaleAfterSeconds` | `30` | Loại bỏ peer không phản hồi sau số giây này |

**Không cần cấu hình (mặc định):** Các instance khám phá lẫn nhau qua UDP multicast. Hoạt động trong Kubernetes, Docker Compose, hoặc bất kỳ mạng chia sẻ nào.

**Multicast bị vô hiệu hóa (ví dụ: một số cloud VPC):**

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

**Tắt hoàn toàn clustering:**

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

Xem [Mở rộng](scaling) để biết thêm chi tiết về cách giới hạn tốc độ phân tán hoạt động.

## Giới hạn tốc độ

Giới hạn tốc độ theo IP tích hợp sẵn được áp dụng trên tất cả các instance thông qua giao thức gossip của cụm:

| Endpoint | Giới hạn | Cửa sổ |
|---|---|---|
| `POST /api/auth/register` | 5 lượt đăng ký | 1 giờ |

Khi clustering được bật, các giới hạn này được tổng hợp trên tất cả các instance. Khi tắt, mỗi instance áp dụng giới hạn riêng một cách độc lập.

## CORS

CORS được cấu hình động. Các origin từ `AllowedCorsOrigins` của tất cả client đã đăng ký được tự động cho phép, với bộ nhớ đệm 60 phút.

## Ví dụ đầy đủ

```json
{
  "Storage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..."
  },
  "Issuer": "https://auth.example.com",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "SigningKeyLifetimeDays": 90
  },
  "Cluster": {
    "Enabled": true
  },
  "AdminApi": {
    "Enabled": true,
    "Scope": "authagonal-admin"
  },
  "Authentication": {
    "CookieLifetimeHours": 48
  },
  "PasswordPolicy": {
    "MinLength": 8,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": true
  },
  "Email": {
    "SendGridApiKey": "SG.xxx",
    "FromAddress": "noreply@example.com",
    "FromName": "Example Auth",
    "VerificationTemplateId": "d-xxx",
    "PasswordResetTemplateId": "d-yyy"
  },
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com"]
    }
  ],
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "...",
      "ClientSecret": "...",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["gmail.com"]
    }
  ],
  "ProvisioningApps": {
    "backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret"
    }
  },
  "Clients": [
    {
      "ClientId": "web",
      "ClientName": "Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "MfaPolicy": "Enabled",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
