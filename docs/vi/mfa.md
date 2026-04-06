---
layout: default
title: Xác thực đa yếu tố
locale: vi
---

# Xác thực đa yếu tố (MFA)

Authagonal hỗ trợ xác thực đa yếu tố cho các đăng nhập dựa trên mật khẩu. Ba phương thức có sẵn: TOTP (ứng dụng xác thực), WebAuthn/passkey (khóa phần cứng và sinh trắc học) và mã khôi phục một lần.

Đăng nhập liên kết (SAML/OIDC) bỏ qua MFA — nhà cung cấp danh tính bên ngoài xử lý xác thực yếu tố thứ hai.

## Các phương thức được hỗ trợ

| Phương thức | Mô tả |
|---|---|
| **TOTP** | Mật khẩu một lần dựa trên thời gian (RFC 6238). Hoạt động với bất kỳ ứng dụng xác thực nào — Google Authenticator, Authy, 1Password, v.v. |
| **WebAuthn / Passkey** | Khóa bảo mật phần cứng FIDO2, sinh trắc học nền tảng (Touch ID, Windows Hello) và passkey được đồng bộ. |
| **Mã khôi phục** | 10 mã dự phòng một lần (định dạng `XXXX-XXXX`) để khôi phục tài khoản khi các phương thức khác không khả dụng. |

## Chính sách MFA

Việc thực thi MFA được cấu hình **theo từng client** thông qua thuộc tính `MfaPolicy` trong `appsettings.json`:

| Giá trị | Hành vi |
|---|---|
| `Disabled` (mặc định) | Không có thách thức MFA, ngay cả khi người dùng đã đăng ký MFA |
| `Enabled` | Thách thức những người dùng đã đăng ký MFA; không bắt buộc đăng ký |
| `Required` | Thách thức những người dùng đã đăng ký; bắt buộc đăng ký cho người dùng chưa có MFA |

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "MfaPolicy": "Enabled"
    },
    {
      "ClientId": "admin-portal",
      "MfaPolicy": "Required"
    }
  ]
}
```

Mặc định là `Disabled`, vì vậy các client hiện có không bị ảnh hưởng cho đến khi bạn chọn tham gia.

### Ghi đè theo người dùng

Triển khai `IAuthHook.ResolveMfaPolicyAsync` để ghi đè chính sách client cho những người dùng cụ thể:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    // Exempt service accounts
    if (email.EndsWith("@service.internal"))
        return Task.FromResult(MfaPolicy.Disabled);

    return Task.FromResult(clientPolicy);
}
```

Xem [Khả năng mở rộng](extensibility) để biết tài liệu hook đầy đủ.

## Luồng đăng nhập

Luồng đăng nhập với MFA hoạt động như sau:

1. Người dùng gửi email và mật khẩu đến `POST /api/auth/login`
2. Máy chủ xác minh mật khẩu, sau đó giải quyết chính sách MFA hiệu quả
3. Dựa trên chính sách và trạng thái đăng ký của người dùng:

| Chính sách | Người dùng có MFA? | Kết quả |
|---|---|---|
| `Disabled` | — | Cookie được đặt, đăng nhập hoàn tất |
| `Enabled` | Không | Cookie được đặt, đăng nhập hoàn tất |
| `Enabled` | Có | Trả về `mfaRequired` — người dùng phải xác minh |
| `Required` | Không | Trả về `mfaSetupRequired` — người dùng phải đăng ký |
| `Required` | Có | Trả về `mfaRequired` — người dùng phải xác minh |

### Thách thức MFA

Khi `mfaRequired` được trả về, phản hồi đăng nhập bao gồm `challengeId` và các phương thức khả dụng của người dùng. Client chuyển hướng đến trang thách thức MFA nơi người dùng xác minh bằng một trong các phương thức đã đăng ký của họ thông qua `POST /api/auth/mfa/verify`.

Các thách thức hết hạn sau 5 phút và chỉ sử dụng một lần.

### Đăng ký bắt buộc

Khi `mfaSetupRequired` được trả về, phản hồi bao gồm `setupToken`. Token này xác thực người dùng với các endpoint thiết lập MFA (thông qua header `X-MFA-Setup-Token`) để họ có thể đăng ký một phương thức trước khi nhận được phiên cookie.

## Đăng ký MFA

Người dùng đăng ký MFA thông qua các endpoint thiết lập tự phục vụ. Các endpoint này yêu cầu phiên cookie đã xác thực hoặc token thiết lập.

### Thiết lập TOTP

1. Gọi `POST /api/auth/mfa/totp/setup` — trả về mã QR (`data:image/png;base64,...`), `manualKey` (Base32 để nhập thủ công) và token thiết lập
2. Người dùng quét mã QR bằng ứng dụng xác thực của họ
3. Người dùng nhập mã 6 chữ số để xác nhận: `POST /api/auth/mfa/totp/confirm`

### Thiết lập WebAuthn / Passkey

1. Gọi `POST /api/auth/mfa/webauthn/setup` — trả về `PublicKeyCredentialCreationOptions`
2. Client gọi `navigator.credentials.create()` với các tùy chọn
3. Gửi phản hồi chứng thực đến `POST /api/auth/mfa/webauthn/confirm`

### Mã khôi phục

Gọi `POST /api/auth/mfa/recovery/generate` để tạo 10 mã một lần. Phải đăng ký ít nhất một phương thức chính (TOTP hoặc WebAuthn) trước.

Tạo lại mã sẽ thay thế tất cả các mã khôi phục hiện có. Mỗi mã chỉ có thể sử dụng một lần.

## Quản lý MFA

### Tự phục vụ người dùng

- `GET /api/auth/mfa/status` — xem các phương thức đã đăng ký
- `DELETE /api/auth/mfa/credentials/{id}` — xóa một thông tin xác thực cụ thể

Nếu phương thức chính cuối cùng bị xóa, MFA sẽ bị vô hiệu hóa cho người dùng.

### API quản trị

Quản trị viên có thể quản lý MFA cho bất kỳ người dùng nào thông qua [API quản trị](admin-api):

- `GET /api/v1/profile/{userId}/mfa` — xem trạng thái MFA của người dùng
- `DELETE /api/v1/profile/{userId}/mfa` — đặt lại tất cả MFA (cho người dùng bị khóa)
- `DELETE /api/v1/profile/{userId}/mfa/{id}` — xóa một thông tin xác thực cụ thể

### Hook kiểm tra

Triển khai `IAuthHook.OnMfaVerifiedAsync` để ghi lại các sự kiện MFA:

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

## Giao diện đăng nhập tùy chỉnh

Nếu bạn đang xây dựng giao diện đăng nhập tùy chỉnh, hãy xử lý các phản hồi này từ `POST /api/auth/login`:

1. **Đăng nhập bình thường** — `{ userId, email, name }` với cookie được đặt. Chuyển hướng đến `returnUrl`.
2. **MFA bắt buộc** — `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Hiển thị biểu mẫu thách thức MFA.
3. **Cần thiết lập MFA** — `{ mfaSetupRequired: true, setupToken }`. Hiển thị luồng đăng ký MFA.

Xem [API xác thực](auth-api) để biết tài liệu tham khảo endpoint đầy đủ.
