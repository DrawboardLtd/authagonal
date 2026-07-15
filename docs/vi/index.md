---
layout: default
title: Trang chủ
locale: vi
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

Máy chủ xác thực OAuth 2.0 / OpenID Connect / SAML 2.0 cho .NET, được hỗ trợ bởi lưu trữ đám mây có thể thay thế — Azure Table Storage hoặc AWS (DynamoDB / S3 / Secrets Manager).

Một triển khai duy nhất, khép kín. Máy chủ và giao diện đăng nhập được đóng gói thành một Docker image duy nhất — SPA được phục vụ từ cùng origin với API, nên xác thực cookie, chuyển hướng và CSP đều hoạt động mà không cần xử lý phức tạp cross-origin.

> **Thích dịch vụ được quản lý hơn?** [Authagonal Cloud](https://authagonal.io) vận hành tất cả những điều này cho bạn — đa người thuê (multi-tenant), mọi tính năng trên mọi gói, không tính phí SSO theo từng kết nối. → [authagonal.io](https://authagonal.io)

## Tính năng chính

- **Nhà cung cấp OIDC**: các loại cấp quyền authorization_code + PKCE, client_credentials, refresh_token, device_code với xoay vòng sử dụng một lần
- **SAML 2.0 SP**: triển khai tự phát triển với hỗ trợ đầy đủ Azure AD (phản hồi có chữ ký, assertion, hoặc cả hai), một cặp khóa SP theo từng kết nối cho các AuthnRequest có chữ ký cùng khả năng giải mã `EncryptedAssertion`, và Single Logout (khởi tạo từ SP và từ IdP)
- **Liên kết OIDC động**: kết nối với Google, Apple, Azure AD, hoặc bất kỳ IdP tương thích OIDC nào
- **Xác thực đa yếu tố**: TOTP, WebAuthn/passkey, mã khôi phục; chính sách theo từng client (`Disabled` / `Enabled` / `Required`) với tùy chỉnh ghi đè theo từng người dùng qua `IAuthHook`, được áp dụng cả cho các lần đăng nhập liên kết
- **Cấp phát SCIM 2.0**: cấp phát người dùng/nhóm đầu vào từ Entra ID, Okta, OneLogin; liệt kê phân trang theo con trỏ và bộ lọc `eq` dựa trên chỉ mục mù
- **Màn hình đồng ý OAuth**: đồng ý theo từng client với nhắc lại theo phạm vi và quản lý cấp quyền
- **Cấp quyền ủy quyền thiết bị**: luồng RFC 8628 cho các thiết bị hạn chế đầu vào (smart TV, CLI, IoT)
- **Xem xét token (Introspection)**: RFC 7662 để các máy chủ tài nguyên xác minh tính hợp lệ của token
- **Đăng xuất Back-Channel**: thông báo OIDC Back-Channel Logout 1.0 đến các relying party
- **Tự phục vụ GDPR**: xuất dữ liệu và lên lịch xóa tài khoản từ trang tài khoản được lưu trữ
- **Cấp phát TCC**: cấp phát người dùng theo mô hình Try-Confirm-Cancel vào các ứng dụng phía sau tại thời điểm ủy quyền
- **Giao diện đăng nhập tùy chỉnh**: cấu hình tại thời điểm chạy qua tệp JSON (logo, màu sắc, thuộc tính CSS tùy chỉnh), không cần build lại; được bản địa hóa sang 10 ngôn ngữ
- **Auth Hooks**: khả năng mở rộng `IAuthHook` cho ghi nhật ký kiểm tra, xác thực tùy chỉnh, webhooks
- **Seam mã hóa PII**: các điểm mở rộng `IFieldCipher` / `IIndexTokenizer` cho mã hóa cấp trường khi lưu trữ với tìm kiếm bằng chỉ mục mù có khóa (HMAC); mã khôi phục được mã hóa qua `ISecretProvider`
- **HashiCorp Vault Transit**: ký JWT từ xa mà không cần truy cập khóa riêng cục bộ
- **Thư viện có thể kết hợp**: `AddAuthagonal()` / `UseAuthagonal()` để tích hợp vào dự án của bạn với các tùy chỉnh dịch vụ
- **Sẵn sàng Native AOT**: cắt tỉa IL và tuần tự hóa JSON được sinh từ nguồn để khởi động nhanh
- **Lưu trữ đám mây có thể thay thế**: Azure Table Storage hoặc AWS (DynamoDB / S3 / Secrets Manager); các backend chi phí thấp, thân thiện với serverless
- **Sao lưu & Khôi phục**: sao lưu tăng dần (dựa trên nhật ký thay đổi với dự phòng quét toàn bộ), xác minh tính toàn vẹn, theo dõi xóa dựa trên tombstone
- **API Quản trị**: CRUD người dùng, quản lý nhà cung cấp SAML/OIDC, định tuyến tên miền SSO, giả mạo token

## Kiến trúc

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    │                             │                                    │
    ├─ GET /connect/authorize ──► │                                    │
    │                             ├─ 302 → /login (SPA)                │
    │                             │   ├─ SSO check                     │
    │                             │   └─ SAML/OIDC redirect ─────────► │
    │                             │                                    │
    │                             │ ◄── SAML Response / OIDC callback ─┤
    │                             │   └─ Create user + cookie          │
    │                             │                                    │
    │                             ├─ TCC provisioning (try/confirm)    │
    │                             ├─ Issue authorization code          │
    │ ◄─ 302 ?code=...&state=... ┤                                    │
    │                             │                                    │
    ├─ POST /connect/token ─────► │                                    │
    │ ◄─ { access_token, ... } ──┤                                    │
```

Bắt đầu với hướng dẫn [Cài đặt](installation) hoặc chuyển thẳng đến [Bắt đầu nhanh](quickstart). Để tích hợp Authagonal vào dự án của bạn, xem [Khả năng mở rộng](extensibility).
