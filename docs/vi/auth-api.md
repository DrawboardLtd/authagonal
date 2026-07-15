---
layout: default
title: API Xác thực
locale: vi
---

# API Xác thực

Các endpoint này cung cấp sức mạnh cho SPA đăng nhập. Chúng sử dụng xác thực cookie (`SameSite=Lax`, `HttpOnly`).

Nếu bạn đang xây dựng giao diện đăng nhập tùy chỉnh, đây là các endpoint bạn cần triển khai.

## Endpoint

### Đăng nhập

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Thành công (200):** Đặt cookie xác thực và trả về:

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

`mfaAvailable` là `true` khi `MfaPolicy` của client là `Enabled` nhưng người dùng chưa đăng ký (giao diện có thể mời thiết lập); trong trường hợp đó phản hồi cũng bao gồm một trường `clientId`.

**Yêu cầu MFA (200):** Nếu người dùng đã đăng ký MFA, họ **luôn** bị yêu cầu xác minh, bất kể `MfaPolicy` của client gửi yêu cầu (MFA là thuộc tính của người dùng/phiên, không phải của client):

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

Client nên chuyển hướng đến trang xác thực MFA và gọi `POST /api/auth/mfa/verify`.

**Yêu cầu thiết lập MFA (200):** Nếu `MfaPolicy` là `Required` và người dùng chưa đăng ký MFA:

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

Client nên chuyển hướng đến trang thiết lập MFA. Token thiết lập xác thực người dùng đến các endpoint thiết lập MFA qua header `X-MFA-Setup-Token`.

**Phản hồi lỗi:**

| `error` | Trạng thái | Mô tả |
|---|---|---|
| `invalid_credentials` | 401 | Email hoặc mật khẩu sai. Cố ý giống hệt nhau với email không xác định (chống liệt kê). |
| `locked_out` | 423 | Quá nhiều lần thất bại. `retryAfter` (giây) được bao gồm. |
| `account_disabled` | 403 | Tài khoản đã bị vô hiệu hóa (chỉ hiển thị sau khi nhập đúng mật khẩu) |
| `email_not_confirmed` | 403 | Email chưa được xác minh (chỉ hiển thị sau khi nhập đúng mật khẩu) |
| `sso_required` | 409 | Tên miền yêu cầu SSO. `redirectUrl` trỏ đến trang đăng nhập SSO. |
| `captcha_failed` | 400 | Xác minh Turnstile thất bại (chỉ khi Turnstile được cấu hình; khi đó các yêu cầu cần một trường `turnstileToken`) |
| `email_required` | 400 | Trường email trống |
| `password_required` | 400 | Trường mật khẩu trống |

### Đăng ký

```
POST /api/auth/register
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Tạo tài khoản người dùng mới và gửi email xác minh. Trả về `201 { "success": true, "userId": "..." }`. Các trường tùy chọn: `locale` (thẻ BCP-47 được lưu trên người dùng) và `customAttributes` (một ánh xạ chuỗi tới chuỗi).

Việc đăng ký cố ý **trung lập với liệt kê**: nếu email đã được đăng ký, phản hồi vẫn là `201` trung lập như cũ (với một `userId` dùng một lần) và chủ sở hữu thật sự được gửi email thông báo đăng nhập/đặt lại thay vào đó. Việc đăng ký cũng bị giới hạn tốc độ theo IP: `429 rate_limited` khi vượt quá (khoảng thời gian và giới hạn có thể cấu hình qua `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes`).

### Xác nhận email

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

Xác nhận địa chỉ email của người dùng bằng token từ email xác minh. `GET` là liên kết có thể nhấp trong email: nó chuyển hướng đến `/login?email_confirmed=1` (kèm một tham số `continue_client` khi việc đăng ký bắt nguồn từ một luồng OAuth). `POST` là đường dẫn lập trình và trả về JSON (token cũng có thể được cung cấp trong body JSON dưới dạng `{ "token": "..." }`); phản hồi bao gồm một `appLink` tùy chọn (đích "tiếp tục đến ứng dụng").

### Nhà cung cấp

```
GET /api/auth/providers
```

Trả về danh sách các nhà cung cấp danh tính bên ngoài đã cấu hình (để hiển thị các nút SSO):

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "type": "oidc", "iconUrl": null, "loginUrl": "/oidc/google/login" }
  ],
  "turnstileSiteKey": null
}
```

Các kết nối có cấu hình `AllowedDomains` sẽ bị **loại trừ**: những kết nối đó được tiếp cận theo hướng email trước qua `/api/auth/sso-check` thay vì một nút bấm. `turnstileSiteKey` được đặt khi Cloudflare Turnstile được cấu hình (khi đó giao diện đăng nhập phải gửi kèm một `turnstileToken` với các yêu cầu đăng nhập/đăng ký/mật khẩu).

### Đăng xuất

```
POST /api/auth/logout
```

Xóa cookie xác thực. Trả về `200 { success: true }`.

### Quên mật khẩu

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

Luôn trả về `200` (chống liệt kê). Nếu người dùng tồn tại, gửi email đặt lại.

### Đặt lại mật khẩu

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | Mô tả |
|---|---|
| `weak_password` | Không đáp ứng yêu cầu độ mạnh |
| `invalid_token` | Token bị lỗi định dạng |
| `token_expired` | Token đã hết hạn (hiệu lực 60 phút mặc định, cấu hình qua `Auth:PasswordResetExpiryMinutes`) |

### Phiên

```
GET /api/auth/session
```

Trả về thông tin phiên hiện tại nếu đã xác thực:

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

Trả về `401` nếu chưa xác thực.

### Ứng dụng

```
GET /api/auth/apps
```

Trả về các liên kết ứng dụng của tenant cho trình khởi chạy "quay lại ứng dụng" trên trang tài khoản: các client đang bật có một home URI (`initiateLoginUri` được ưu tiên hơn `clientUri`). Mỗi mục là `{ clientId, clientName, homeUri, logoUri, isDefault }`; đúng một ứng dụng được đánh dấu mặc định (client được gắn cờ, hoặc client duy nhất có home URI). Yêu cầu xác thực cookie.

### Hồ sơ (tự phục vụ)

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

Người dùng đã xác thực đọc/cập nhật các trường hồ sơ không nhạy cảm của chính họ: `firstName`, `lastName`, `companyName`, `phone`, `locale`. Các trường null giữ nguyên; email, mật khẩu, vai trò, trạng thái kích hoạt và tổ chức **không** thể chỉnh sửa ở đây. Cả hai đều trả về hồ sơ `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }`.

### Kiểm tra SSO

```
GET /api/auth/sso-check?email=user@acme.com
```

Kiểm tra xem tên miền email có yêu cầu SSO không:

```json
{
  "ssoRequired": true,
  "providerType": "saml",
  "connectionId": "acme-azure",
  "redirectUrl": "/saml/acme-azure/login"
}
```

Nếu không yêu cầu SSO:

```json
{
  "ssoRequired": false
}
```

### Chính sách mật khẩu

```
GET /api/auth/password-policy
```

Trả về yêu cầu mật khẩu của máy chủ (được cấu hình qua `PasswordPolicy` trong cài đặt):

```json
{
  "rules": [
    { "rule": "minLength", "value": 8, "label": "At least 8 characters" },
    { "rule": "uppercase", "value": null, "label": "Uppercase letter" },
    { "rule": "lowercase", "value": null, "label": "Lowercase letter" },
    { "rule": "digit", "value": null, "label": "Number" },
    { "rule": "specialChar", "value": null, "label": "Special character" }
  ]
}
```

Giao diện đăng nhập mặc định lấy endpoint này trên trang đặt lại mật khẩu để hiển thị yêu cầu một cách động.

## Yêu cầu mật khẩu mặc định

Với cấu hình mặc định, mật khẩu phải đáp ứng tất cả các yêu cầu sau:

- Ít nhất 8 ký tự
- Ít nhất một chữ cái viết hoa
- Ít nhất một chữ cái viết thường
- Ít nhất một chữ số
- Ít nhất một ký tự không phải chữ và số
- Ít nhất 2 ký tự khác nhau

Các yêu cầu này có thể được tùy chỉnh qua phần cấu hình `PasswordPolicy`, xem [Cấu hình](configuration).

## Endpoint MFA

### Xác minh MFA

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

Xác minh thử thách MFA. Khi thành công, đặt cookie xác thực và trả về thông tin người dùng.

**Các phương thức:**

| `method` | Trường bắt buộc | Mô tả |
|---|---|---|
| `totp` | `code` (6 chữ số) | Mật khẩu một lần dựa trên thời gian từ ứng dụng xác thực |
| `webauthn` | `assertion` (chuỗi JSON) | Phản hồi xác nhận WebAuthn từ `navigator.credentials.get()` |
| `recovery` | `code` (`XXXX-XXXX`) | Mã khôi phục một lần (được tiêu thụ khi sử dụng) |

**Ngữ nghĩa thử lại:** một mã sai **không** hủy thử thách: mã được xác thực trước và thử thách chỉ bị tiêu thụ khi thành công, nên người dùng có thể thử lại cùng một `challengeId` sau khi gõ nhầm một chữ số (`401 invalid_code` / `assertion_failed`). Mỗi thử thách chịu được **5 lần thất bại**; lần thất bại thứ 5 tiêu thụ nó và trả về `401 too_many_attempts`, buộc phải đăng nhập lại từ đầu (điều này giới hạn tấn công vét cạn TOTP ở mức 5 lần đoán mỗi thử thách). Thử thách cũng hết hạn (mặc định 5 phút, `Auth:MfaChallengeExpiryMinutes`); một `challengeId` đã hết hạn, không xác định, hoặc đã bị tiêu thụ sẽ trả về `invalid_challenge`. Mã TOTP còn được bảo vệ chống phát lại: một mã từ bước thời gian đã dùng sẽ bị từ chối.

### Trạng thái MFA

```
GET /api/auth/mfa/status
```

Trả về các phương thức MFA đã đăng ký của người dùng. Yêu cầu xác thực cookie hoặc header `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

`offered` là `false` khi `MfaPolicy` của mọi client đều là `Disabled`: tenant đã tắt MFA, nên giao diện thiết lập có thể tự ẩn. Các mục mã khôi phục còn mang thêm `isConsumed`.

### Thiết lập TOTP

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
→ { "success": true }
```

### Thiết lập WebAuthn / Passkey

```
POST /api/auth/mfa/webauthn/setup
→ { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
→ { "success": true, "credentialId": "..." }
```

Việc đăng ký passkey yêu cầu **có một thông tin xác thực TOTP đã xác nhận trước** (`400 totp_required_first`): passkey là một tiện lợi theo từng thiết bị được xếp lên trên một yếu tố cơ sở có thể mang theo, nên một tài khoản không bao giờ có thể chỉ có passkey và bị khóa vào một thiết bị. Người dùng có tên miền email được định tuyến SSO không thể đăng ký passkey cục bộ (`400 sso_managed`): nó sẽ bỏ qua IdP của tenant. Một credential ID đã được đăng ký cho một người dùng khác sẽ bị từ chối với `409 credential_already_registered`.

### Mã khôi phục

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Tạo 10 mã khôi phục một lần. Yêu cầu ít nhất một phương thức chính (TOTP hoặc WebAuthn) đã được đăng ký. Tạo lại sẽ thay thế tất cả mã khôi phục hiện có.

### Xóa thông tin xác thực MFA

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

Xóa một thông tin xác thực MFA cụ thể. Nếu phương thức chính cuối cùng bị xóa, MFA sẽ bị vô hiệu hóa cho người dùng. Yêu cầu một phiên cookie thật sự: một token thiết lập sẽ bị từ chối với `403 session_required` (token thiết lập chỉ tồn tại để thêm yếu tố đầu tiên, không bao giờ để hạ cấp MFA).

### Đăng nhập passkey không mật khẩu

```
POST /api/auth/mfa/passwordless/begin
→ { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
→ { "userId": "...", "email": "...", "name": "..." }
```

Đăng nhập bằng thông tin xác thực có thể khám phá (resident passkey) mà không cần ngữ cảnh người dùng trước đó: `begin` phát hành một thử thách xác nhận với danh sách `allowCredentials` rỗng, và `complete` phân giải người dùng **từ** chính passkey được chọn, xác minh assertion, rồi đăng nhập cho họ (phiên mang dấu hiệu MFA: passkey là xác thực mạnh chống lừa đảo). Nếu tên miền email của người dùng được phân giải bị định tuyến SSO, việc đăng nhập bị từ chối với `409 sso_required` + `redirectUrl` để một passkey cục bộ không thể lách qua một IdP bắt buộc.

## Ủy quyền thiết bị (RFC 8628)

### Yêu cầu mã thiết bị

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

Trả về một mã thiết bị, mã người dùng và URI xác minh:

```json
{
  "device_code": "abc123...",
  "user_code": "ABCD-EFGH",
  "verification_uri": "https://auth.example.com/device",
  "verification_uri_complete": "https://auth.example.com/device?user_code=ABCD-EFGH",
  "expires_in": 300,
  "interval": 5
}
```

`expires_in` đến từ `DeviceCodeLifetimeSeconds` của client (mặc định 300). Thiết bị hiển thị `verification_uri` và `user_code` cho người dùng, sau đó thăm dò endpoint token với `device_code`, không nhanh hơn `interval` giây một lần, nếu không endpoint token trả lời `slow_down` (RFC 8628 §3.5). Trong khi người dùng chưa phê duyệt, endpoint token trả về `authorization_pending`. Người dùng truy cập URI xác minh, đăng nhập, và nhập mã người dùng để phê duyệt.

### Phê duyệt thiết bị

```
POST /api/auth/device/approve
Content-Type: application/json

{
  "userCode": "ABCD-EFGH"
}
```

Yêu cầu xác thực cookie. Phê duyệt mã thiết bị cho người dùng hiện tại. Sau đó thiết bị có thể đổi mã thiết bị lấy token qua endpoint token bằng loại cấp quyền `urn:ietf:params:oauth:grant-type:device_code`.

## Nội soi token (RFC 7662)

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

token=eyJhbGci...
```

Hoặc với thông tin xác thực được mã hóa dạng biểu mẫu:

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJhbGci...&client_id=my-app&client_secret=secret
```

Trả về siêu dữ liệu token:

```json
{
  "active": true,
  "sub": "user-id",
  "client_id": "my-app",
  "scope": "openid profile",
  "iss": "https://auth.example.com",
  "exp": 1234567890,
  "iat": 1234567890,
  "token_type": "Bearer"
}
```

Các token không hoạt động hoặc không hợp lệ trả về `{ "active": false }`. Hỗ trợ cả token truy cập JWT và refresh token dạng mờ.

## Các endpoint đồng ý

### Thông tin đồng ý

```
GET /consent/info?client_id=my-app&scope=openid%20profile%20email
```

Trả về chi tiết client và các scope được yêu cầu cho trang đồng ý (`scope` mặc định là `openid` khi bỏ trống):

```json
{
  "clientId": "my-app",
  "clientName": "My Application",
  "description": null,
  "clientUri": null,
  "logoUri": null,
  "scopes": ["openid", "profile", "email"]
}
```

Trả về `404 client_not_found` cho một client không xác định.

### Gửi đồng ý

```
POST /consent
Content-Type: application/json

{
  "clientId": "my-app",
  "decision": "allow",
  "scopes": ["openid", "profile", "email"],
  "returnUrl": "/connect/authorize?..."
}
```

Ghi lại quyết định đồng ý của người dùng (yêu cầu xác thực cookie) và trả về `{ "redirect": "..." }` để SPA điều hướng đến. Khi cho phép, các scope được cấp sẽ được lưu (lọc theo `AllowedScopes` của client: một body bị giả mạo không thể ghi lại các scope mà client không thể yêu cầu) và chuyển hướng trỏ trở lại luồng ủy quyền. Khi `"decision": "deny"`, chuyển hướng trỏ đến `redirect_uri` của client kèm một lỗi `access_denied`.

### Liệt kê các cấp quyền

```
GET /consent/grants
```

Trả về tất cả các ứng dụng mà người dùng đã ủy quyền:

```json
[
  {
    "clientId": "my-app",
    "clientName": "My Application",
    "scopes": ["openid", "profile", "email"],
    "consentedAt": "2026-04-09T12:00:00Z"
  }
]
```

### Thu hồi cấp quyền

```
DELETE /consent/grants/{clientId}
```

Thu hồi sự đồng ý cho một ứng dụng cụ thể. Người dùng sẽ được nhắc đồng ý lại trong lần đăng nhập tiếp theo.

## Xây dựng giao diện đăng nhập tùy chỉnh

SPA mặc định (`login-app/`) là một triển khai của API này. Để xây dựng giao diện riêng:

1. Phục vụ giao diện tại các đường dẫn `/login`, `/forgot-password`, `/reset-password`
2. Endpoint ủy quyền chuyển hướng người dùng chưa xác thực đến `/login?returnUrl={encoded-authorize-url}`
3. Sau khi đăng nhập thành công (cookie được đặt), chuyển hướng người dùng đến `returnUrl`
4. Liên kết đặt lại mật khẩu sử dụng `{Issuer}/login/reset-password?p={token}` (SPA đăng nhập được gắn dưới `/login`)

Giao diện của bạn phải được phục vụ từ **cùng origin** với API vì:
- Xác thực cookie sử dụng `SameSite=Lax` + `HttpOnly`
- Endpoint ủy quyền chuyển hướng đến `/login` (tương đối)
- Liên kết đặt lại sử dụng `{Issuer}/login/reset-password`
