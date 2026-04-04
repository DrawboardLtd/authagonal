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
  "name": "Jane Doe"
}
```

**Yêu cầu MFA (200):** Nếu người dùng đã đăng ký MFA và `MfaPolicy` của client là `Enabled` hoặc `Required`:

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
| `invalid_credentials` | 401 | Email hoặc mật khẩu sai |
| `locked_out` | 423 | Quá nhiều lần thất bại. `retryAfter` (giây) được bao gồm. |
| `email_not_confirmed` | 403 | Email chưa được xác minh |
| `sso_required` | 403 | Tên miền yêu cầu SSO. `redirectUrl` trỏ đến trang đăng nhập SSO. |
| `email_required` | 400 | Trường email trống |
| `password_required` | 400 | Trường mật khẩu trống |

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
| `token_expired` | Token đã hết hạn (hiệu lực 24 giờ) |

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

Các yêu cầu này có thể được tùy chỉnh qua phần cấu hình `PasswordPolicy` — xem [Cấu hình](configuration).

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

### Trạng thái MFA

```
GET /api/auth/mfa/status
```

Trả về các phương thức MFA đã đăng ký của người dùng. Yêu cầu xác thực cookie hoặc header `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

### Thiết lập TOTP

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/svg+xml;base64,..." }

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

Xóa một thông tin xác thực MFA cụ thể. Nếu phương thức chính cuối cùng bị xóa, MFA sẽ bị vô hiệu hóa cho người dùng.

## Xây dựng giao diện đăng nhập tùy chỉnh

SPA mặc định (`login-app/`) là một triển khai của API này. Để xây dựng giao diện riêng:

1. Phục vụ giao diện tại các đường dẫn `/login`, `/forgot-password`, `/reset-password`
2. Endpoint ủy quyền chuyển hướng người dùng chưa xác thực đến `/login?returnUrl={encoded-authorize-url}`
3. Sau khi đăng nhập thành công (cookie được đặt), chuyển hướng người dùng đến `returnUrl`
4. Liên kết đặt lại mật khẩu sử dụng `{Issuer}/reset-password?p={token}`

Giao diện của bạn phải được phục vụ từ **cùng origin** với API vì:
- Xác thực cookie sử dụng `SameSite=Lax` + `HttpOnly`
- Endpoint ủy quyền chuyển hướng đến `/login` (tương đối)
- Liên kết đặt lại sử dụng `{Issuer}/reset-password`
