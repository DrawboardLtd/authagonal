---
layout: default
title: API Quản trị
locale: vi
---

# API Quản trị

Các endpoint quản trị yêu cầu JWT access token với scope `authagonal-admin` (cấu hình qua `AdminApi:Scope`).

Tất cả endpoint nằm dưới `/api/v1/`.

## Khởi tạo token quản trị đầu tiên

Mọi endpoint `/api/v1/*` đều yêu cầu một bearer token mang scope quản trị, nhưng bản thân API quản trị (và [đăng ký client động](client-registration)) **từ chối tạo hoặc cập nhật bất kỳ client nào giữ scope đó** (`403 forbidden_scope`), nên một client được tạo tại thời điểm chạy không bao giờ có thể leo thang thành quản trị. Cách duy nhất để cấp một token quản trị là một **client được seed từ cấu hình**: các mục trong phần cấu hình `Clients:` được upsert khi khởi động bởi `ClientSeedService`, và cấu hình được tin cậy, nên lớp bảo vệ forbidden-scope chỉ áp dụng cho các API tại thời điểm chạy.

Seed một client `client_credentials` với scope quản trị trong `appsettings.json` (hoặc các biến môi trường / kho bí mật tương đương):

```json
{
  "Clients": [
    {
      "Id": "admin-cli",
      "Name": "Admin CLI",
      "ClientSecret": "a-long-random-secret",
      "GrantTypes": ["client_credentials"],
      "Scopes": ["authagonal-admin"]
    }
  ]
}
```

(`ClientSecret` được băm khi khởi động; hãy cung cấp `SecretHashes` thay thế nếu bạn muốn chỉ giữ một giá trị đã băm sẵn trong cấu hình. `ClientId`/`ClientName`/`AllowedGrantTypes`/`AllowedScopes` được chấp nhận như các bí danh cho `Id`/`Name`/`GrantTypes`/`Scopes`.)

Sau đó đổi thông tin đăng nhập lấy token tại endpoint token tiêu chuẩn:

```bash
curl -X POST https://auth.example.com/connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=admin-cli" \
  -d "client_secret=a-long-random-secret" \
  -d "scope=authagonal-admin"
```

```json
{ "access_token": "eyJhbGci...", "token_type": "Bearer", "expires_in": 1800, "scope": "authagonal-admin" }
```

Cấp quyền `client_credentials` kiểm tra scope được yêu cầu so với `AllowedScopes` của client, và vì client được seed giữ `authagonal-admin`, token sẽ được cấp. Dùng nó dưới dạng `Authorization: Bearer {access_token}` trên mọi lệnh gọi quản trị:

```bash
curl https://auth.example.com/api/v1/clients -H "Authorization: Bearer eyJhbGci..."
```

Hãy giữ bí mật của client được seed trong kho bí mật của triển khai của bạn; xoay vòng nó là một thay đổi cấu hình + khởi động lại.

## Người dùng

### Lấy thông tin người dùng

```
GET /api/v1/profile/{userId}
```

Trả về chi tiết người dùng bao gồm các liên kết đăng nhập bên ngoài.

### Người dùng có tồn tại

```
GET /api/v1/profile/{userId}/exists
```

Trả về `204` nếu người dùng tồn tại, `404` nếu không (một phép thăm dò tồn tại chi phí thấp, không có body).

### Đăng ký người dùng

```
POST /api/v1/profile/
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Tạo người dùng và gửi email xác minh. Trả về `409 user_exists` nếu email đã được sử dụng.

Các trường tùy chọn chỉ dành cho quản trị: `userId` (id do người gọi cung cấp, `409 user_id_in_use` khi trùng), `emailConfirmed` (tạo người dùng đã được xác minh sẵn, bỏ qua email xác minh), `companyName`, `organizationId`, `phone`, `locale`, và `customAttributes` (một map chuỗi được lưu trên người dùng và chuyển tiếp đến các đích cấp phát).

### Cập nhật người dùng

```
PUT /api/v1/profile/
Content-Type: application/json

{
  "userId": "user-id",
  "firstName": "Jane",
  "lastName": "Smith",
  "organizationId": "new-org-id"
}
```

`userId` là bắt buộc; mọi trường khác là tùy chọn, chỉ các trường được cung cấp mới được cập nhật. Thay đổi `organizationId` kích hoạt:
- Xoay vòng SecurityStamp (vô hiệu hóa tất cả phiên cookie trong vòng 30 phút)
- Thu hồi tất cả refresh token

### Xóa người dùng

```
DELETE /api/v1/profile/{userId}
```

Xóa người dùng, thu hồi tất cả cấp quyền, và hủy cấp phát khỏi tất cả ứng dụng phía sau (nỗ lực tốt nhất).

### Xác nhận email

```
POST /api/v1/profile/confirm-email?token={token}
```

### Gửi email xác minh

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Liên kết danh tính bên ngoài

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### Hủy liên kết danh tính bên ngoài

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

## Quản lý MFA

### Lấy trạng thái MFA

```
GET /api/v1/profile/{userId}/mfa
```

Trả về trạng thái MFA và các phương thức đã đăng ký của người dùng.

### Đặt lại toàn bộ MFA

```
DELETE /api/v1/profile/{userId}/mfa
```

Xóa tất cả thông tin xác thực MFA và đặt `MfaEnabled=false`. Người dùng sẽ cần đăng ký lại nếu được yêu cầu.

### Xóa thông tin xác thực MFA cụ thể

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Xóa một thông tin xác thực MFA cụ thể (ví dụ: ứng dụng xác thực bị mất). Nếu phương thức chính cuối cùng bị xóa, MFA sẽ bị vô hiệu hóa.

## Nhà cung cấp SSO

### Nhà cung cấp SAML

```
POST   /api/v1/saml/connections                    # Tạo mới
GET    /api/v1/saml/connections/{connectionId}     # Lấy một
PUT    /api/v1/saml/connections/{connectionId}     # Cập nhật (một phần, chỉ các trường được cung cấp mới thay đổi)
DELETE /api/v1/saml/connections/{connectionId}     # Xóa
```

Việc tạo yêu cầu `connectionName`, `entityId`, và **đúng một trong** `metadataLocation` (một URL metadata) hoặc `metadataXml` (metadata IdP được dán vào, cho các IdP không có URL metadata, nó được kiểm tra cú pháp và cô đọng khi lưu). Tùy chọn: `nameIdFormat` (bỏ qua để dùng mặc định emailAddress, `"none"` để bỏ NameIDPolicy, được khuyến nghị cho ADFS, hoặc một URN định dạng NameID), `signAuthnRequests`, `iconUrl`, `allowedDomains`, `disableJitProvisioning`. Mỗi connection nhận một cặp khóa SP do máy chủ tạo; nó không bao giờ được API trả về. Xem [SAML](saml) để biết chi tiết.

### Nhà cung cấp OIDC

```
POST   /api/v1/oidc/connections                    # Tạo mới
GET    /api/v1/oidc/connections/{connectionId}     # Lấy một
DELETE /api/v1/oidc/connections/{connectionId}     # Xóa
```

Việc tạo yêu cầu `connectionName`, `metadataLocation`, `clientId`, `clientSecret`, `redirectUrl`. Tùy chọn: `iconUrl`, `allowedDomains`, `passthroughParams`. Client secret được bảo vệ khi lưu trữ và không bao giờ được trả về. Xem [Liên kết OIDC](oidc-federation).

### Tên miền SSO

```
GET    /api/v1/sso/domains                 # Liệt kê tất cả
```

## Client

Quản lý các OAuth client tại thời điểm chạy. Tất cả route yêu cầu policy `IdentityAdmin` (scope quản trị).

```
GET    /api/v1/clients              # Liệt kê tất cả client
GET    /api/v1/clients/{clientId}   # Lấy một client
POST   /api/v1/clients              # Tạo một client
PUT    /api/v1/clients/{clientId}   # Cập nhật một client
DELETE /api/v1/clients/{clientId}   # Xóa một client
```

### Tạo / Cập nhật Client

```
POST /api/v1/clients
Content-Type: application/json

{
  "clientId": "my-app",
  "clientName": "My Application",
  "allowedGrantTypes": ["authorization_code"],
  "redirectUris": ["https://app.example.com/callback"],
  "allowedScopes": ["openid", "profile", "email"]
}
```

`POST` trả về `409` nếu client đã tồn tại. `PUT` cập nhật một client hiện có (`404` nếu không tìm thấy); khi cập nhật, chỉ các scope mới được thêm vào mới bị kiểm tra leo thang đặc quyền.

Lưu ý:

- **Hash bí mật không bao giờ được trả về.** `clientSecretHashes` bị loại bỏ khỏi mọi phản hồi (liệt kê, lấy, tạo, cập nhật). Khi cập nhật, việc bỏ qua `clientSecretHashes` sẽ giữ nguyên bí mật đã lưu; cung cấp hash mới sẽ xoay vòng nó.
- **Scope quản trị không thể được cấp cho một client.** Yêu cầu `AdminApi:Scope` (mặc định `authagonal-admin`) trong `allowedScopes` sẽ trả về `403 forbidden_scope` — không client nào được giữ scope quản trị, nếu không một client `client_credentials` có thể cấp token quản trị vô thời hạn.
- Thêm các scope mà người gọi không được phép cấp sẽ trả về `403`.

## Scope

Quản lý các OAuth scope tùy chỉnh tại thời điểm chạy. Xem [OAuth Scopes](scopes) để biết mô hình scope đầy đủ.

```
GET    /api/v1/scopes           # Liệt kê tất cả scope
GET    /api/v1/scopes/{name}    # Lấy một scope
POST   /api/v1/scopes           # Tạo một scope
PUT    /api/v1/scopes/{name}    # Cập nhật một scope (chỉ các trường được cung cấp mới thay đổi)
DELETE /api/v1/scopes/{name}    # Xóa một scope
```

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing — read-only",
  "description": "View invoices and payment history",
  "userClaims": ["billing_plan"]
}
```

Trả về `201` khi tạo (`409` nếu scope đã tồn tại), JSON của scope khi lấy/cập nhật, và `204` khi xóa.

## Ứng dụng cấp phát

Quản lý các đích cấp phát phía sau tại thời điểm chạy. Tất cả route yêu cầu policy `IdentityAdmin`.

```
GET    /api/v1/provisioning/apps               # Liệt kê các app (cũng trả về giới hạn đã cấu hình)
POST   /api/v1/provisioning/apps               # Tạo một app
PUT    /api/v1/provisioning/apps/{appId}       # Cập nhật một app
DELETE /api/v1/provisioning/apps/{appId}       # Xóa một app
POST   /api/v1/provisioning/apps/{appId}/test  # Gửi một lệnh gọi /try thử nghiệm đến callback của app
```

### Tạo / Cập nhật Ứng dụng cấp phát

```
POST /api/v1/provisioning/apps
Content-Type: application/json

{
  "name": "Backend",
  "callbackUrl": "https://api.example.com/provisioning",
  "apiKey": "secret-api-key",
  "tryTimeoutSeconds": 30
}
```

- `name` và `callbackUrl` là bắt buộc; `callbackUrl` phải là một URL `http(s)` tuyệt đối.
- `tryTimeoutSeconds` bị giới hạn trong khoảng 5–300.
- **Khóa API không bao giờ được trả về.** Các phản hồi hiển thị `hasApiKey` (một boolean) thay vì chính khóa đó. Khi cập nhật, việc bỏ qua `apiKey` sẽ giữ nguyên nó, một chuỗi rỗng sẽ xóa nó, và một giá trị sẽ thay thế nó.
- Việc tạo phải tuân theo một hạn ngạch cấu hình được theo từng triển khai (`IProvisioningAppQuota`); vượt quá nó sẽ trả về `400 provisioning_app_limit`. Phản hồi liệt kê bao gồm `limit` hiện tại.

### Thử nghiệm một Ứng dụng cấp phát

```
POST /api/v1/provisioning/apps/{appId}/test
```

Gửi một `POST {callbackUrl}/try` tổng hợp với payload mẫu (và khóa API của app dưới dạng bearer token nếu được đặt) và trả về `{ success, statusCode, body }` để bạn có thể xác minh khả năng kết nối từ giao diện quản trị.

## Vai trò

### Liệt kê vai trò

```
GET /api/v1/roles
```

### Lấy vai trò

```
GET /api/v1/roles/{roleId}
```

### Tạo vai trò

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### Cập nhật vai trò

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### Xóa vai trò

```
DELETE /api/v1/roles/{roleId}
```

### Gán vai trò cho người dùng

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

Việc gán theo **tên vai trò**, không phải id vai trò. Trả về danh sách vai trò đã cập nhật của người dùng.

### Hủy gán vai trò khỏi người dùng

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

### Lấy vai trò của người dùng

```
GET /api/v1/roles/user/{userId}
```

## Token SCIM

### Tạo token

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id",
  "description": "Entra provisioning",
  "expiresInDays": 365
}
```

`description` và `expiresInDays` là tùy chọn (bỏ qua `expiresInDays` để có token không hết hạn). Trả về token thô một lần. Lưu trữ an toàn, không thể truy xuất lại.

### Liệt kê token

```
GET /api/v1/scim/tokens?clientId=client-id
```

Trả về metadata token (ID, ngày tạo) mà không có giá trị token thô.

### Thu hồi token

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## Token

### Giả mạo người dùng

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

Cấp token (access, refresh, và — khi `openid` được yêu cầu — id token) thay mặt người dùng mà không cần thông tin đăng nhập của họ. Hữu ích cho kiểm thử và hỗ trợ. Các tham số được truyền dưới dạng query string.

| Tham số query | Bắt buộc | Mô tả |
|---|---|---|
| `clientId` | Có | Client mà token được cấp cho. Thời hạn token đến từ cấu hình của client này. |
| `userId` | Có | Người dùng cần giả mạo. |
| `scopes` | Không | Danh sách scope **phân cách bằng dấu cách** (mã hóa URL các dấu cách). Mặc định là `AllowedScopes` của client khi bỏ qua. |

Hạn chế:

- Các scope bị giới hạn trong `AllowedScopes` của client — yêu cầu bất kỳ scope nào mà chính client không thể tự yêu cầu sẽ trả về `400 invalid_scope`.
- Scope quản trị (`AdminApi:Scope`, mặc định `authagonal-admin`) **không thể** được cấp qua endpoint này; yêu cầu nó sẽ trả về `403 forbidden_scope`. Điều này ngăn một token quản trị (có thể có thời hạn giới hạn) cấp một access/refresh token quản trị tồn tại lâu dài.

Phản hồi là một phản hồi token tiêu chuẩn với `access_token`, `refresh_token`, `id_token` tùy chọn, `expires_in`, và `scope` được cấp (phân cách bằng dấu cách).
