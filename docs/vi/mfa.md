---
layout: default
title: Xác thực đa yếu tố
locale: vi
---

# Xác thực đa yếu tố (MFA)

Authagonal hỗ trợ xác thực đa yếu tố. Ba phương thức có sẵn: TOTP (ứng dụng xác thực), WebAuthn/passkey (khóa phần cứng và sinh trắc học) và mã khôi phục một lần. Passkey cũng có thể được dùng để [đăng nhập không mật khẩu](#đăng-nhập-passkey-không-mật-khẩu).

Đăng nhập liên kết (SAML/OIDC) cũng được xử lý: một assertion SAML hoặc OIDC chứng minh yếu tố thứ nhất, không phải yếu tố thứ hai. Một người dùng liên kết đã đăng ký MFA được định tuyến qua cùng thách thức MFA cục bộ như một đăng nhập bằng mật khẩu, và một chính sách `Required` bắt buộc đăng ký trước khi bất kỳ phiên nào được cấp. Chỉ khi MFA không được đăng ký cũng không bắt buộc thì liên kết mới đứng một mình.

## Các phương thức được hỗ trợ

| Phương thức | Mô tả |
|---|---|
| **TOTP** | Mật khẩu một lần dựa trên thời gian (RFC 6238): 6 chữ số, bước 30 giây, SHA-1, được xác minh với cửa sổ lệch đồng hồ một bước. Hoạt động với bất kỳ ứng dụng xác thực nào (Google Authenticator, Authy, 1Password, v.v.). Một mã đã được chấp nhận không thể bị phát lại trong cửa sổ hiệu lực của nó. |
| **WebAuthn / Passkey** | Khóa bảo mật phần cứng FIDO2, sinh trắc học nền tảng (Touch ID, Windows Hello) và passkey được đồng bộ. Người dùng có thể đăng ký nhiều passkey, và passkey có thể đăng nhập không mật khẩu. |
| **Mã khôi phục** | 10 mã dự phòng một lần (định dạng `XXXX-XXXX`) để khôi phục tài khoản khi các phương thức khác không khả dụng. Được lưu ở dạng băm và mã hóa khi lưu trữ. |

## Chính sách MFA

Việc thực thi MFA được cấu hình **theo từng client** thông qua thuộc tính `MfaPolicy` trong `appsettings.json`:

| Giá trị | Hành vi |
|---|---|
| `Disabled` (mặc định) | Không bắt buộc đăng ký; giao diện thiết lập tự phục vụ ẩn MFA khi mọi client đều là `Disabled` |
| `Enabled` | Mời đăng ký MFA; không bắt buộc |
| `Required` | Bắt buộc đăng ký cho người dùng chưa có MFA |

Một người dùng đã đăng ký MFA **luôn bị yêu cầu xác minh khi đăng nhập, bất kể chính sách của client**. MFA là thuộc tính của người dùng và phiên của họ, không phải của client gửi yêu cầu, nên một yêu cầu được định tuyến qua một client `Disabled` không thể được dùng để bỏ qua yếu tố thứ hai của người dùng đã đăng ký.

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

Chính sách được giải quyết chi phối việc đăng ký (nó được mời hay bị bắt buộc). Nó không miễn cho một người dùng đã đăng ký khỏi thách thức; người dùng đã đăng ký luôn bị yêu cầu xác minh.

Xem [Khả năng mở rộng](extensibility) để biết tài liệu hook đầy đủ.

## Luồng đăng nhập

Luồng đăng nhập với MFA hoạt động như sau:

1. Người dùng gửi email và mật khẩu đến `POST /api/auth/login`
2. Máy chủ xác minh mật khẩu, sau đó giải quyết chính sách MFA hiệu quả
3. Dựa trên chính sách và trạng thái đăng ký của người dùng:

| Chính sách | Người dùng có MFA? | Kết quả |
|---|---|---|
| Bất kỳ | Có | Trả về `mfaRequired`: người dùng phải xác minh |
| `Disabled` / `Enabled` | Không | Cookie được đặt, đăng nhập hoàn tất |
| `Required` | Không | Trả về `mfaSetupRequired`: người dùng phải đăng ký |

### Thách thức MFA

Khi `mfaRequired` được trả về, phản hồi đăng nhập bao gồm `challengeId`, các phương thức khả dụng của người dùng (`methods`), và (khi người dùng có passkey) các tùy chọn assertion `webAuthn`. Client chuyển hướng đến trang thách thức MFA nơi người dùng xác minh bằng một trong các phương thức đã đăng ký của họ thông qua `POST /api/auth/mfa/verify`:

```json
{
  "challengeId": "...",
  "method": "totp",
  "code": "123456"
}
```

`method` là `totp`, `recovery`, hoặc `webauthn` (WebAuthn gửi một `assertion` thay cho một `code`).

Các thách thức hết hạn sau 5 phút (có thể cấu hình qua `Auth:MfaChallengeExpiryMinutes`) và bị tiêu thụ khi xác minh thành công.

#### Ngân sách thử lại

Một mã sai không hủy thách thức. Endpoint xác minh xác thực mã trước và chỉ tiêu thụ thách thức khi thành công, nên một chữ số TOTP gõ nhầm có thể đơn giản được thử lại với cùng `challengeId`. Các lần thất bại trả về `invalid_code` (hoặc `assertion_failed` với WebAuthn) kèm mã 401 và tăng một bộ đếm có giới hạn trên thách thức; lần thử sai thứ năm tiêu thụ thách thức và trả về `too_many_attempts`, buộc phải đăng nhập lại từ đầu. Điều này áp dụng cho cả ba phương thức và giới hạn tấn công vét cạn TOTP ở mức 5 lần đoán mỗi thách thức.

Một thách thức thiếu, đã hết hạn, hoặc đã bị tiêu thụ sẽ trả về `invalid_challenge`.

### Đăng nhập liên kết

Sau một assertion SAML hoặc OIDC thành công, máy chủ giải quyết cùng chính sách MFA hiệu quả. Một người dùng đã đăng ký MFA được chuyển hướng đến trang thách thức MFA được lưu trữ (kèm một `challengeId`) thay vì nhận một phiên; một người dùng chưa có MFA dưới một chính sách `Required` được chuyển hướng đến trang thiết lập MFA (kèm một `setupToken`). Phiên chỉ được đánh dấu là đã xác thực MFA khi việc xác minh hoàn tất.

### Đăng ký bắt buộc

Khi `mfaSetupRequired` được trả về, phản hồi bao gồm `setupToken`. Token này xác thực người dùng với các endpoint thiết lập MFA (thông qua header `X-MFA-Setup-Token`) để họ có thể đăng ký một phương thức trước khi nhận được phiên cookie. Các token thiết lập hết hạn sau 15 phút (có thể cấu hình qua `Auth:MfaSetupTokenExpiryMinutes`).

## Đăng ký MFA

Người dùng đăng ký MFA thông qua các endpoint thiết lập tự phục vụ. Các endpoint này yêu cầu phiên cookie đã xác thực hoặc token thiết lập.

### Thiết lập TOTP

1. Gọi `POST /api/auth/mfa/totp/setup` — trả về mã QR (`data:image/png;base64,...`), `manualKey` (Base32 để nhập thủ công) và token thiết lập
2. Người dùng quét mã QR bằng ứng dụng xác thực của họ
3. Người dùng nhập mã 6 chữ số để xác nhận: `POST /api/auth/mfa/totp/confirm`

### Thiết lập WebAuthn / Passkey

1. Gọi `POST /api/auth/mfa/webauthn/setup` — trả về một `setupToken` và `PublicKeyCredentialCreationOptions`
2. Client gọi `navigator.credentials.create()` với các tùy chọn
3. Gửi phản hồi chứng thực đến `POST /api/auth/mfa/webauthn/confirm`

Việc đăng ký passkey yêu cầu có một thông tin xác thực TOTP đã xác nhận trước (`totp_required_first`). Passkey là một tiện lợi theo từng thiết bị được xếp lên trên một yếu tố cơ sở có thể mang theo, nên mọi tài khoản đều giữ một yếu tố độc lập với thiết bị và một chính sách `Required` không thể được thỏa mãn chỉ bằng một passkey.

Người dùng có thể đăng ký nhiều passkey (mỗi thiết bị một cái). Một credential ID đã được đăng ký cho một người dùng khác sẽ bị từ chối (`credential_already_registered`), và những người dùng có tên miền email được định tuyến đến một IdP bên ngoài qua SSO bắt buộc không thể đăng ký một passkey cục bộ (`sso_managed`), vì nó sẽ bỏ qua IdP và việc hủy cấp phát của IdP đó.

### Mã khôi phục

Gọi `POST /api/auth/mfa/recovery/generate` để tạo 10 mã một lần. Phải đăng ký ít nhất một phương thức chính (TOTP hoặc WebAuthn) trước.

Tạo lại mã sẽ thay thế tất cả các mã khôi phục hiện có. Mỗi mã chỉ có thể sử dụng một lần; một mã đã được dùng được đánh dấu là đã tiêu thụ và không còn được chấp nhận.

Các mã không bao giờ được lưu ở dạng văn bản thuần: mỗi mã được băm, và bản băm còn được mã hóa khi lưu trữ bằng nhà cung cấp bí mật của tenant, nên một bản dump lưu trữ chỉ cho ra bản mã thay vì một bản băm có thể vét cạn ngoại tuyến.

## Đăng nhập passkey không mật khẩu

Passkey không chỉ là yếu tố thứ hai: một người dùng có passkey đã đăng ký có thể đăng nhập mà không cần mật khẩu.

1. `POST /api/auth/mfa/passwordless/begin` trả về một `challengeId` và các `options` assertion cho thông tin xác thực có thể khám phá, để trình xác thực đề xuất bất kỳ resident passkey nào cho trang web
2. Client gọi `navigator.credentials.get()` với các tùy chọn
3. `POST /api/auth/mfa/passwordless/complete` với `{ challengeId, assertion }`: máy chủ phân giải người dùng từ chính passkey và đăng nhập cho họ

Trang đăng nhập được lưu trữ nối việc này vào trường email qua trung gian có điều kiện (conditional mediation, tự động điền passkey): khi trình duyệt hỗ trợ, một passkey khả dụng được đề xuất như một gợi ý tự động điền mà không cần bất kỳ giao diện bổ sung nào.

Một passkey là xác thực mạnh chống lừa đảo, nên phiên tạo ra mang dấu hiệu MFA và không bị yêu cầu xác minh lại. Nếu tên miền email của người dùng được định tuyến đến một IdP bên ngoài qua SSO bắt buộc, đăng nhập không mật khẩu bị từ chối với một phản hồi `sso_required` mã 409 bao gồm URL chuyển hướng SSO, để một passkey cục bộ không thể lách qua IdP.

## Quản lý MFA

### Tự phục vụ người dùng

- `GET /api/auth/mfa/status` — xem các phương thức đã đăng ký (cũng báo cáo liệu MFA có được bất kỳ client nào cung cấp không)
- `DELETE /api/auth/mfa/credentials/{id}` — xóa một thông tin xác thực cụ thể

Việc xóa một thông tin xác thực yêu cầu một phiên đã xác thực thật sự; một token thiết lập chỉ cho phép thêm một yếu tố đầu tiên và nhận `session_required` ở đây, nên một token thiết lập bị rò rỉ không thể hạ cấp MFA của người dùng.

Nếu phương thức chính cuối cùng bị xóa, MFA sẽ bị vô hiệu hóa cho người dùng.

### API quản trị

Quản trị viên có thể quản lý MFA cho bất kỳ người dùng nào thông qua [API quản trị](admin-api):

- `GET /api/v1/profile/{userId}/mfa` — xem trạng thái MFA của người dùng
- `DELETE /api/v1/profile/{userId}/mfa` — đặt lại tất cả MFA (cho người dùng bị khóa)
- `DELETE /api/v1/profile/{userId}/mfa/{id}` — xóa một thông tin xác thực cụ thể

### Các hook kiểm tra

Triển khai `IAuthHook.OnMfaVerifiedAsync` để ghi lại các sự kiện MFA:

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

Toàn bộ vòng đời MFA đều có thể gắn hook: `OnMfaVerifyFailedAsync` (một lần thử xác minh thất bại), `OnMfaEnrolledAsync` (một phương thức được xác nhận), `OnMfaCredentialRemovedAsync` (một thông tin xác thực bị xóa, kèm một cờ cho biết việc đó có vô hiệu hóa MFA không), và `OnRecoveryCodesRegeneratedAsync`.

## Giao diện đăng nhập tùy chỉnh

Nếu bạn đang xây dựng giao diện đăng nhập tùy chỉnh, hãy xử lý các phản hồi này từ `POST /api/auth/login`:

1. **Đăng nhập bình thường** — `{ userId, email, name }` với cookie được đặt. Chuyển hướng đến `returnUrl`.
2. **MFA bắt buộc** — `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Hiển thị biểu mẫu thách thức MFA.
3. **Cần thiết lập MFA** — `{ mfaSetupRequired: true, setupToken }`. Hiển thị luồng đăng ký MFA.

Khi xử lý các lỗi của `POST /api/auth/mfa/verify`: `invalid_code` và `assertion_failed` có thể thử lại với cùng `challengeId` (trong giới hạn ngân sách thử); `too_many_attempts` và `invalid_challenge` là kết thúc, nên hãy đưa người dùng trở lại biểu mẫu đăng nhập.

Xem [API xác thực](auth-api) để biết tài liệu tham khảo endpoint đầy đủ.
