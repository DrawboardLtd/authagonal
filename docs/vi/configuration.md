---
layout: default
title: Cấu hình
locale: vi
---

# Cấu hình

Authagonal được cấu hình qua `appsettings.json` hoặc biến môi trường. Biến môi trường sử dụng `__` làm dấu phân cách phần (ví dụ: `Storage__ConnectionString`).

## Cài đặt bắt buộc

Bộ lưu trữ có thể được cấu hình theo một trong hai cách: cung cấp **một trong hai** `Storage:ConnectionString` **hoặc** `Storage:TableServiceUri` (đường dẫn managed-identity, được ưu tiên trong production).

| Cài đặt | Biến môi trường | Mô tả |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Chuỗi kết nối Azure Table Storage với account key. Phù hợp cho dev / Azurite. |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | Endpoint Table Storage dùng managed-identity, ví dụ `https://{account}.table.core.windows.net/`. Thay thế cho `Storage:ConnectionString` và **được ưu tiên trong production**: xác thực qua `DefaultAzureCredential` nên không có access key nào lọt vào một bí mật. Host phải cấp cho workload identity vai trò **Storage Table Data Contributor**. |
| `Issuer` | `Issuer` | URL công khai gốc của máy chủ này (ví dụ: `https://auth.example.com`) |

## Bộ lưu trữ

| Cài đặt | Biến môi trường | Mặc định | Mô tả |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(không có)* | Chuỗi kết nối với account key (xem Cài đặt bắt buộc). |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(không có)* | URI Table Storage dùng managed-identity (xem Cài đặt bắt buộc). Được ưu tiên hơn `Storage:ConnectionString` khi cả hai cùng được đặt. |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | Có duy trì các bảng chỉ mục tìm kiếm theo tiền tố `UserFirstNames` / `UserLastNames` (hỗ trợ tìm kiếm theo tiền tố tên trong trang quản trị) hay không. Đặt `false` trên các host không cung cấp tìm kiếm theo tên trong trang quản trị để bỏ qua các lượt ghi đó. **Lưu ý về mở rộng:** các chỉ mục này dùng một phân vùng nóng duy nhất và giới hạn thông lượng ở khoảng 2.000 thao tác/giây ở quy mô lớn: hãy vô hiệu hóa chúng nếu bạn không cần tìm kiếm theo tên. |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | URL gốc mà endpoint `/connect/authorize` chuyển hướng đến cho SPA đăng nhập (màn hình đăng nhập, step-up và đồng ý). Đặt giá trị này khi giao diện đăng nhập được phục vụ từ một origin khác với máy chủ; mặc định là đường dẫn tương đối `/login` do SPA tích hợp phục vụ. |

## Xác thực

| Cài đặt | Mặc định | Mô tả |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Thời gian sống phiên cookie (trượt) |
| `Authentication:AllowInsecureCookie` | `false` | Let the session cookie be sent over plain http (`SameAsRequest` instead of `Always`). **Development only** — see the English documentation. |
| `Authentication:CookieDomain` | *(unset)* | Scope the session cookie to a parent domain. **Costs the `__Host-` prefix and its origin binding** — see the English documentation. |
| `Auth:AllowInsecureHttp` | `false` | Cho phép các endpoint OAuth (`/connect/*`) trả lời các request http thuần. **Chỉ dùng cho phát triển.** RFC 6749 §3.1/§3.2 yêu cầu TLS tại endpoint authorization và token, nên theo mặc định một request không phải https tới bất kỳ endpoint nào trong số đó đều bị từ chối với `invalid_request`. Scheme được đánh giá *sau* khi xử lý các forwarded header, nên một proxy kết thúc TLS và chuyển tiếp `X-Forwarded-Proto: https` vẫn qua được cổng này dù để tắt tùy chọn — với điều kiện proxy đó đã được khai báo trong `ForwardedHeaders:KnownNetworks` / `KnownProxies`; không có khai báo đó thì header bị bỏ qua. Chỉ một triển khai thực sự chạy văn bản thuần (tệp `docker-compose.yml` đi kèm, bản demo custom-server) mới cần đến nó, và máy chủ ghi một cảnh báo khi khởi động bất cứ khi nào nó đang bật. Được truyền sang `AuthagonalProtocolOptions.AllowInsecureHttp`, nên nó cũng chi phối các endpoint do `Authagonal.Protocol` sở hữu (xem [Khả năng mở rộng](extensibility#embedding-authagonalprotocol-alone)). |
| `Auth:MaxFailedAttempts` | `5` | Số lần đăng nhập thất bại trước khi khóa tài khoản |
| `Auth:LockoutDurationMinutes` | `10` | Thời gian khóa tài khoản sau khi vượt quá số lần thất bại tối đa |
| `Auth:MaxRegistrationsPerIp` | `5` | Số lượt đăng ký tối đa mỗi địa chỉ IP trong cửa sổ thời gian |
| `Auth:RegistrationWindowMinutes` | `60` | Cửa sổ giới hạn tốc độ đăng ký |
| `Auth:MaxPasswordResetsPerEmail` | `3` | Số email đặt lại mật khẩu tối đa cho mỗi địa chỉ đích trong cửa sổ thời gian (lập khóa theo email chứ không theo IP người gọi, nên một địa chỉ không thể bị dội bom email) |
| `Auth:PasswordResetWindowMinutes` | `60` | Cửa sổ giới hạn tốc độ đặt lại mật khẩu |
| `Auth:AutoConfirmEmailDomains` | *(rỗng)* | Các tên miền email (mảng chuỗi) mà các lượt đăng ký tự phục vụ được tự động xác nhận, chúng bỏ qua email xác minh. Giá trị rỗng (mặc định) nghĩa là mọi lượt đăng ký đều phải xác minh. Chỉ dành cho dev/test; không bao giờ liệt kê một tên miền có thể nhận email thật. |
| `Auth:EmailVerificationExpiryHours` | `24` | Thời gian hiệu lực liên kết xác minh email |
| `Auth:PasswordResetExpiryMinutes` | `60` | Thời gian hiệu lực liên kết đặt lại mật khẩu |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Thời gian hiệu lực token xác thực MFA |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Thời gian hiệu lực token thiết lập MFA (cho đăng ký bắt buộc) |
| `Auth:Pbkdf2Iterations` | `100000` | Số lần lặp PBKDF2 cho băm mật khẩu |
| `Auth:FailedLoginMinimumMilliseconds` | `250` | Sàn thời gian thực mà một lần đăng nhập thất bại bị giữ lại trước khi trả về `invalid_credentials`, tính từ lúc bắt đầu request. Đóng lại oracle định thời cho phép liệt kê người dùng: một tài khoản không tồn tại được kiểm tra với một hash giả ở định dạng PBKDF2 gốc, nhưng một tài khoản thật vẫn có thể mang hash bcrypt hoặc ASP.NET Identity V3 đã nhập với chi phí khác, nên không thể làm cho khối lượng công việc bằng nhau, thứ được cưỡng chế là thời gian trôi qua bằng nhau. Hãy nâng nó lên trên hash chậm nhất mà triển khai đang giữ, ví dụ nếu bạn đã nhập bcrypt với cost trên 11 hoặc nâng `Pbkdf2Iterations` cao hơn nhiều so với mặc định: một cảnh báo duy nhất được ghi lần đầu tiên một lần đăng nhập thất bại vượt quá sàn. `0` tắt phần đệm và mở lại oracle. |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Cửa sổ ân hạn (giây) tùy chọn cho việc sử dụng lại refresh token đồng thời. `0` (mặc định) giữ thế phòng thủ nghiêm ngặt: bất kỳ lần sử dụng lại nào của một refresh token đã tiêu thụ đều thu hồi tất cả token của người dùng+client đó. Đặt giá trị `> 0` để coi một lần sử dụng lại trong cửa sổ như một lần thử lại idempotent (cấp lại các token kế thừa), hữu ích cho các client di động có kết nối chập chờn. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Bật endpoint đăng ký client động `POST /connect/register` (RFC 7591). Tắt theo mặc định vì đăng ký mở có thể bị lạm dụng trong các triển khai đa tenant. Xem [Đăng ký Client động](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | Thời gian hiệu lực khóa ký RSA trước khi tự động xoay vòng |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Tần suất tải lại khóa ký từ bộ lưu trữ |
| `Auth:KeyRotationEnabled` | `false` | Bật tự động xoay vòng khóa ký |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | Tần suất kiểm tra xem khóa đang hoạt động có cần xoay vòng hay không |
| `Auth:KeyRotationLeadTimeDays` | `14` | Xoay vòng khi khóa đang hoạt động hết hạn trong vòng số ngày này |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Khoảng cách giữa các lần kiểm tra dấu bảo mật cookie |

## Data Protection

Các khóa ASP.NET Core Data Protection (dùng để mã hóa cookie phiên) phải được chia sẻ giữa các instance, xem [Mở rộng](scaling#cookie-encryption-data-protection). Các tùy chọn lưu trữ bền vững, theo thứ tự ưu tiên:

| Cài đặt | Mặc định | Mô tả |
|---|---|---|
| `DataProtection:BlobUri` | *(không có)* | Azure Blob URI rõ ràng cho key ring (ví dụ `https://{account}.blob.core.windows.net/dataprotection/keys.xml`). Xác thực qua `DefaultAzureCredential`, là đường dẫn production được ưu tiên đi kèm với `Storage:TableServiceUri`. |
| *(dự phòng)* | — | Khi `DataProtection:BlobUri` không được đặt và `Storage:ConnectionString` trỏ đến một storage account thật (không phải Azurite), các khóa được tự động lưu vào một container `dataprotection` trong account đó. Với Azurite, các khóa quay về bộ lưu trữ dựa trên tệp mặc định. |

Trên backend AWS, hãy truyền một S3 client + bucket vào `AddAuthagonalAwsStorage` để lưu key ring vào S3, xem [Cài đặt → AWS backend](installation#aws-backend).

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

## Vai trò

Các vai trò được định nghĩa trong mảng `Roles` và được khởi tạo khi khởi động, cùng với client, scope
và provider. Việc khởi tạo chúng quan trọng nhất khi một scope bị chặn bằng
[`AllowedRoles`](scopes#role-gated-scopes): một scope bị chặn theo một vai trò mà không có gì tạo ra
thì bị chặn với tất cả mọi người, kể cả người vận hành đã cấu hình nó, và nó thất bại trong im lặng
-- scope đơn giản là không bao giờ được cấp.

```json
{
  "Roles": [
    {
      "Name": "staff-admin",
      "Description": "Internal staff console",
      "Members": [ "ada@example.com", "grace@example.com" ]
    }
  ]
}
```

| Trường | Mô tả |
|---|---|
| `Name` | Tên vai trò, như được dùng trong `Scope.AllowedRoles` và trong claim `roles` của token |
| `Description` | Dạng người đọc được; được cập nhật ở các lần khởi động sau khi phần khởi tạo có nêu |
| `Members` | Các email được đưa vào vai trò ở mỗi lần khởi động. Một địa chỉ chưa có người dùng sẽ bị bỏ qua kèm cảnh báo và thử lại ở lần khởi động sau -- việc khởi động không bao giờ phụ thuộc vào một tài khoản chưa ai tạo |

Việc khởi tạo là **cộng dồn và idempotent**. Nó không bao giờ xóa một vai trò hay thu hồi một tư cách
thành viên: cấu hình không phải là nguồn sự thật về ai nắm giữ gì, nên một vai trò được cấp qua admin
API vẫn tồn tại sau lần khởi động lại kế tiếp.

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
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
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
| `urn:ietf:params:oauth:grant-type:device_code` | Device authorization grant (RFC 8628) cho các thiết bị hạn chế đầu vào |

### Sử dụng Refresh Token

| Giá trị | Hành vi |
|---|---|
| `OneTime` (mặc định) | Mỗi lần làm mới sẽ cấp một refresh token mới và vô hiệu hóa token cũ. Theo mặc định (`Auth:RefreshTokenReuseGraceSeconds = 0`) bất kỳ lần sử dụng lại nào của một token đã tiêu thụ sẽ ngay lập tức thu hồi tất cả token của người dùng+client đó: **không có** cửa sổ ân hạn nào được bật theo mặc định. Đặt `Auth:RefreshTokenReuseGraceSeconds` thành một giá trị dương để tùy chọn bật cửa sổ dung sai cho việc thử lại. |
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

Đăng nhập liên kết (SAML/OIDC) cũng tuân theo chính sách MFA: một người dùng đã đăng ký MFA sẽ được dẫn qua bước xác thực MFA sau khi IdP bên ngoài xác thực họ, và `Required` bắt buộc đăng ký cho những người dùng liên kết chưa có MFA.

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
| `EntityId` | Có | Entity ID của SP của **máy chủ này**, mã định danh bạn đăng ký tại IdP, không phải entity ID của chính IdP |
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

Bí mật của client OIDC thượng nguồn và seed TOTP / MFA có thể được lưu trữ trong Azure Key Vault thay vì ở dạng văn bản thuần:

| Cài đặt | Mô tả |
|---|---|
| `SecretProvider:VaultUri` | URI Key Vault (ví dụ: `https://my-vault.vault.azure.net/`). Nếu không đặt, nhà cung cấp **văn bản thuần** sẽ được sử dụng và các bí mật được lưu nguyên trạng trong Table Storage. |

| `SecretProvider:RequireVaultReferences` | Mặc định là `false`. Khi đặt `true`, một tham chiếu đã lưu mà không có tiền tố vault (`kv:` cho Key Vault, `sm:` cho AWS Secrets Manager) sẽ là **lỗi** thay vì được chấp nhận như một giá trị văn bản thuần. Hãy bật nó sau khi đã hoàn tất việc chuyển vào vault. |

Khi được cấu hình, các giá trị bí mật trông giống tham chiếu Key Vault sẽ được giải quyết tại thời điểm chạy. Sử dụng `DefaultAzureCredential` để xác thực.

### Chuyển vào vault, và đóng cửa lại sau đó

Cả hai nhà cung cấp dựa trên vault đều trả về nguyên trạng một tham chiếu không có tiền tố, coi nó như một giá trị văn bản thuần đã được ghi trước khi triển khai có vault. Chính điều đó cho phép chuyển đổi một hệ thống đang chạy theo từng bí mật một thay vì tất cả cùng lúc -- nhưng nếu để ngỏ, nó là một lối hạ cấp vĩnh viễn: bất cứ thứ gì ghi được một cột cấu hình (một lần chuyển đổi làm dở dang, một đường dẫn quản trị lưu giá trị thô vào chỗ lẽ ra phải là tham chiếu, một kẻ tấn công có quyền truy cập kho lưu trữ nhưng không có quyền vào vault) đều thay thế một bí mật được vault bảo vệ bằng một giá trị do chính nó chọn, và nó xác minh hoàn hảo, bởi vì với một tham chiếu không có tiền tố thì tham chiếu *chính là* giá trị.

Hãy đặt `SecretProvider:RequireVaultReferences` khi việc chuyển đổi đã xong. Khi đó, việc giải quyết một tham chiếu không có tiền tố sẽ ném lỗi thay vì lặng lẽ trả về văn bản rõ. Việc đặt nó trong khi nhà cung cấp được phân giải là loại văn bản thuần sẽ bị từ chối lúc khởi động, vì tổ hợp đó không có trạng thái hoạt động nào -- mọi tham chiếu mà nhà cung cấp văn bản thuần ghi ra đều không có tiền tố.

Máy chủ cũng ghi một cảnh báo khi khởi động mỗi khi một host không phải Development rốt cuộc lại dùng nhà cung cấp văn bản thuần.

> ⚠️ **Production: hãy đặt `SecretProvider:VaultUri`.** Nhà cung cấp bí mật mặc định là **văn bản thuần**. Khi `SecretProvider:VaultUri` không được đặt, bí mật của client OIDC thượng nguồn và seed TOTP / MFA được ghi vào Azure Table Storage dưới dạng văn bản rõ, và do đó xuất hiện dưới dạng văn bản rõ trong bất kỳ [bản sao lưu](backup-restore) nào. Đối với bất kỳ triển khai production nào, hãy cấu hình `SecretProvider:VaultUri` để các bí mật này được lưu trong Key Vault.

## API Quản trị

| Cài đặt | Mặc định | Mô tả |
|---|---|---|
| `AdminApi:Enabled` | `true` | **Được bật theo mặc định.** Đặt thành `false` để vô hiệu hóa tất cả endpoint quản trị (chúng sẽ không được đăng ký). |
| `AdminApi:Scope` | `authagonal-admin` | Scope JWT cần thiết để truy cập các endpoint quản trị. Thay đổi giá trị này để khớp với tên scope hiện có của bạn (ví dụ: `projects-identity-admin` cho việc di chuyển từ IdentityServer). |

> ⚠️ **API quản trị được bật theo mặc định và có đặc quyền rất cao.** Scope quản trị cấp toàn quyền quản lý và giả mạo người dùng: bất kỳ ai nắm giữ token có `AdminApi:Scope` đều có thể cấp token cho bất kỳ người dùng nào, quản lý client, và đọc/ghi toàn bộ cấu hình. Hãy giới hạn mạng cho các endpoint quản trị (các route quản trị `/api/v1/*`), và kiểm soát chặt chẽ ai có thể được cấp scope quản trị. Như một biện pháp phòng thủ theo chiều sâu, scope này được *dành riêng*: nó không bao giờ có thể được cấp cho một OAuth client (xem [API Quản trị](admin-api)) và không thể được cấp qua endpoint giả mạo. Hãy đặt `AdminApi:Enabled = false` hoàn toàn nếu không sử dụng API quản trị.

## Đồng ý

Đồng ý theo từng client có thể được bật với thuộc tính `RequireConsent`:

| Giá trị | Hành vi |
|---|---|
| `false` (mặc định) | Việc ủy quyền tiến hành ngay sau khi xác thực |
| `true` | Người dùng được hiển thị màn hình đồng ý liệt kê các scope được yêu cầu. Đồng ý được lưu trong 5 năm và chỉ hỏi lại khi có scope mới được yêu cầu. |

Người dùng có thể xem và thu hồi các cấp quyền đồng ý của họ tại `GET /consent/grants` và `DELETE /consent/grants/{clientId}`.

## Back-Channel Logout

Đăng ký một `BackChannelLogoutUri` trên một client để nhận thông báo OIDC Back-Channel Logout 1.0. Khi một người dùng đăng xuất, Authagonal gửi một logout token đã ký (JWT) đến URI đã đăng ký của mỗi client.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback"
    }
  ]
}
```

## Email

Bộ gửi email tích hợp sử dụng [Resend](https://resend.com) và **tự động kích hoạt** khi `Email:ResendApiKey` được cấu hình, không cần đăng ký dịch vụ. Để dùng một nhà cung cấp khác, hãy đăng ký triển khai `IEmailService` của riêng bạn trước khi gọi `AddAuthagonal()` (nó được ưu tiên bất kể các khóa `Email:*`).

| Cài đặt | Mô tả |
|---|---|
| `Email:ResendApiKey` | Khóa API Resend. Khi được đặt, bộ gửi Resend tích hợp sẽ được dùng. |
| `Email:SenderEmail` | Địa chỉ email người gửi |
| `Email:SenderName` | Tên hiển thị người gửi (mặc định là `"Authagonal"`) |

> ⚠️ **Nếu không có bộ gửi email nào, việc tự đăng ký sẽ hỏng.** Khi `Email:ResendApiKey` không được đặt và không có `IEmailService` tùy chỉnh nào được đăng ký, một dịch vụ no-op sẽ âm thầm bỏ qua tất cả email: email xác minh và đặt lại mật khẩu không bao giờ đến, và vì đăng nhập theo mặc định yêu cầu email đã xác nhận, người dùng tự đăng ký không bao giờ đăng nhập được. `UseAuthagonal` ghi một cảnh báo khi khởi động trong trạng thái này. Lối thoát cho dev/test: `Auth:AutoConfirmEmailDomains` tự động xác nhận các lượt đăng ký cho những tên miền được liệt kê.

Email gửi đến địa chỉ `@example.com` sẽ được bỏ qua im lặng (hữu ích cho kiểm thử).

## Cluster

Lớp clustering cung cấp **bầu chọn leader** (để các công việc do leader điều phối như xoay vòng khóa ký chỉ chạy trên đúng một node) và một **event bus xuyên node**, phía sau các backend có thể thay thế. Mặc định là in-process: một node đơn luôn là leader của chính nó, đây là cài đặt phù hợp cho triển khai một node và phát triển cục bộ, không cần cấu hình.

| Cài đặt | Biến môi trường | Mặc định | Mô tả |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Công tắc chính. Khi `false`, node chạy độc lập (luôn là leader, event bus in-process). |
| `Cluster:Secret` | `Cluster__Secret` | *(không có)* | Bí mật dùng chung bắt buộc trên endpoint chỉ nội bộ `/_internal/backchannel-logout`. Khi được đặt, người gọi phải xuất trình nó trong header `X-Cluster-Secret` (so sánh trong thời gian hằng số). Khi **không được đặt**, endpoint chỉ có thể truy cập từ các IP nguồn loopback / riêng tư (RFC 1918 / link-local / ULA), một yêu cầu bên ngoài mang IP công khai sẽ bị từ chối. |
| `Cluster:LeaseTtlSeconds` | `Cluster__LeaseTtlSeconds` | `30` | Thời gian lease quyền leader. Được gia hạn ở khoảng một nửa khoảng thời gian này. |
| `Cluster:PollIntervalSeconds` | `Cluster__PollIntervalSeconds` | `3` | Tần suất backend event-bus thăm dò các thông điệp do các node khác phát hành. |

**Triển khai nhiều node** thay bằng một backend thật qua callback `configureClustering` trên `AddAuthagonal` / `AddAuthagonalCore`:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS equivalent (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// Self-hosted PostgreSQL (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` chỉ đăng ký event bus, giữ lease in-process, dành cho các node phải nhận sự kiện cụm nhưng không bao giờ được tranh quyền leader.

Xem [Mở rộng](scaling) để biết cách quyền leader và event bus hoạt động giữa các instance.

## Forwarded Headers (proxy tin cậy)

Authagonal lập khóa giới hạn tốc độ và khóa tài khoản dựa trên IP của client, và chỉ phát HSTS trên các yêu cầu HTTPS. Phía sau một reverse proxy / ingress, IP thực và scheme thực của client đến trong các header `X-Forwarded-For` / `X-Forwarded-Proto`. Các cài đặt này kiểm soát **hop proxy nào được tin cậy** để đặt các giá trị đó, để người gọi không thể giả mạo `X-Forwarded-For` nhằm làm giả IP của client.

| Cài đặt | Biến môi trường | Mặc định | Mô tả |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | Số hop proxy được tôn trọng tính từ bên phải của chuỗi `X-Forwarded-For`. Giá trị mặc định `1` chỉ tin cậy đúng một hop mà ingress của bạn nối thêm và bỏ qua mọi thứ xa hơn về bên trái trong chuỗi. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (mảng) | *(rỗng)* | Các dải CIDR (mảng chuỗi, ví dụ `"10.0.0.0/8"`) được phép đặt forwarded header. Đặt giá trị này thành CIDR của proxy / ingress / pod của bạn. Chính khai báo này mới cho phép `X-Forwarded-Proto` được xét đến — xem bên dưới. |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0` (mảng) | *(rỗng)* | Các địa chỉ IP proxy riêng lẻ (mảng chuỗi) được phép đặt forwarded header. Dùng cùng với hoặc thay cho `KnownNetworks`. |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

### Hai header này không được tin cậy theo cùng một điều kiện

`X-Forwarded-For` điều chỉnh **IP của client** — khóa mà giới hạn tần suất, khóa tài khoản và bộ bảo vệ `/_internal` dựa vào. Khi chưa khai báo gì, Authagonal vẫn chấp nhận nó từ loopback và các dải RFC1918, đồng thời ghi một cảnh báo. Đó là mặc định theo kiểu nỗ lực tối đa, và nó tốt hơn hành vi của framework khi tập tin cậy rỗng: khi đó header được chấp nhận từ *bất kỳ* caller nào.

`X-Forwarded-Proto` thay đổi **scheme**, và scheme quyết định `/connect/*` có trả lời hay không (RFC 6749 §3.1/§3.2), cookie có được đánh dấu `Secure` hay không, và các URL tuyệt đối sinh ra có phải https hay không. Nó **chỉ** được chấp nhận từ một proxy mà bạn đã khai báo trong `KnownNetworks` / `KnownProxies`. Một địa chỉ riêng (private) không phải là một khai báo: Authagonal được phát hành dưới dạng thư viện và không thể nhìn thấy mạng mà nó được triển khai lên, nên "peer có địa chỉ riêng" chỉ là một phỏng đoán về topology. Trên một mạng LAN phẳng, một VPC dùng chung hay một container bridge dùng chung, mọi workload lân cận đều nằm trong các dải đó và có thể khẳng định `https` cho một request thực ra đã đến ở dạng văn bản thuần.

**Nếu proxy của bạn không có địa chỉ cố định** — một ingress Kubernetes, một load balancer luân phiên, một nền tảng không cho bạn biết CIDR của hop — hãy khai báo mọi peer đều là proxy:

```json
{
  "ForwardedHeaders": {
    "KnownNetworks": ["0.0.0.0/0", "::/0"]
  }
}
```

Cách này an toàn đúng khi không gì ngoài proxy có thể tiếp cận tiến trình, và đó chính là giả định mà một triển khai như vậy vốn đã dựa vào. Viết nó ra sẽ đặt giả định vào chỗ có thể rà soát được, thay vì để thư viện tự suy đoán. Nếu các workload khác *có thể* tiếp cận Kestrel trực tiếp thì với thiết lập này chúng có thể giả mạo scheme và IP của client — khi đó hãy ghim CIDR thật.

> ⚠️ **Yêu cầu proxy kết thúc TLS, và nó phải được khai báo.** Authagonal phải chạy phía sau một reverse proxy kết thúc TLS (hoặc tự kết thúc TLS). HSTS (`Strict-Transport-Security`) chỉ được phát trên các yêu cầu HTTPS, và các endpoint OAuth từ chối thẳng các request văn bản thuần trừ khi bật `Auth:AllowInsecureHttp` — nên proxy phải chuyển tiếp `X-Forwarded-Proto: https` **và** được nêu tên trong `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` thì HSTS mới được gửi và `/connect/*` mới trả lời. Không khai báo gì là lỗi nâng cấp thường gặp nhất: header vẫn đến, nhưng không có gì được phép áp dụng nó, và mọi request `/connect/*` đều trả về 400 trên một triển khai thực sự đang chạy TLS. Log khởi động nói điều đó, và phần thân của phản hồi từ chối cũng vậy.

## Giới hạn tốc độ

Giới hạn tốc độ tích hợp sẵn bảo vệ các endpoint dễ bị lạm dụng:

| Endpoint | Giới hạn | Cửa sổ | Lập khóa theo |
|---|---|---|---|
| `POST /api/auth/register` | 5 (`Auth:MaxRegistrationsPerIp`) | 1 giờ (`Auth:RegistrationWindowMinutes`) | IP của client |
| `POST /api/auth/forgot-password` | 3 (`Auth:MaxPasswordResetsPerEmail`) | 1 giờ (`Auth:PasswordResetWindowMinutes`) | Email đích |
| `POST /connect/register` (khi được bật) | 10 | 1 giờ | IP của client |
| Các endpoint SCIM | 200 | 1 phút | SCIM client |

Các giới hạn được áp dụng **in-process theo từng node** (phía sau seam `IRateLimiter`), nên với N instance thì trần hiệu dụng là N× giá trị được cấu hình. Hãy coi chúng như một lớp phòng bị và thực thi giới hạn toàn cục có thẩm quyền ở biên (WAF / ingress / CDN). Xem [Mở rộng](scaling#rate-limiting).

## CORS

CORS được cấu hình động. Các origin từ `AllowedCorsOrigins` của tất cả client đã đăng ký được tự động cho phép, với bộ nhớ đệm 60 phút.

## HashiCorp Vault Transit

Authagonal có thể ký JWT bằng công cụ secrets Transit của HashiCorp Vault. Khóa riêng không bao giờ rời khỏi Vault: chỉ thao tác ký được ủy quyền từ xa. Khóa công khai được lưu trong bộ nhớ đệm cục bộ để xác minh.

Điều này được cấu hình bằng lập trình khi host dưới dạng thư viện. Xem [Khả năng mở rộng](extensibility) để biết chi tiết.

## Ví dụ đầy đủ

```json
{
  "Storage": {
    "TableServiceUri": "https://myaccount.table.core.windows.net/",
    "NameIndexesEnabled": true
  },
  "Issuer": "https://auth.example.com",
  "LoginAppUrl": "/login",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "RefreshTokenReuseGraceSeconds": 0,
    "DynamicClientRegistrationEnabled": false,
    "SigningKeyLifetimeDays": 90
  },
  "SecretProvider": {
    "VaultUri": "https://my-vault.vault.azure.net/"
  },
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"]
  },
  "Cluster": {
    "Enabled": true,
    "Secret": "shared-secret-here"
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
    "ResendApiKey": "re_xxx",
    "SenderEmail": "noreply@example.com",
    "SenderName": "Example Auth"
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
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
