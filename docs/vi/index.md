---
layout: default
title: Trang chủ
locale: vi
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

Máy chủ xác thực OAuth 2.0 / OpenID Connect / SAML 2.0 được hỗ trợ bởi Azure Table Storage.

Một triển khai duy nhất, khép kín. Máy chủ và giao diện đăng nhập được đóng gói thành một Docker image duy nhất — SPA được phục vụ từ cùng origin với API, nên xác thực cookie, chuyển hướng và CSP đều hoạt động mà không cần xử lý phức tạp cross-origin.

## Tính năng chính

- **Nhà cung cấp OIDC** — các loại cấp quyền authorization_code + PKCE, client_credentials, refresh_token với xoay vòng sử dụng một lần
- **SAML 2.0 SP** — triển khai tự phát triển với hỗ trợ đầy đủ Azure AD (phản hồi có chữ ký, assertion, hoặc cả hai)
- **Liên kết OIDC động** — kết nối với Google, Apple, Azure AD, hoặc bất kỳ IdP tương thích OIDC nào
- **Cấp phát TCC** — cấp phát người dùng theo mô hình Try-Confirm-Cancel vào các ứng dụng phía sau tại thời điểm ủy quyền
- **Giao diện đăng nhập tùy chỉnh** — cấu hình tại thời điểm chạy qua tệp JSON — logo, màu sắc, CSS tùy chỉnh — không cần build lại
- **Auth Hooks** — khả năng mở rộng `IAuthHook` cho ghi nhật ký kiểm tra, xác thực tùy chỉnh, webhooks
- **Thư viện có thể kết hợp** — `AddAuthagonal()` / `UseAuthagonal()` để tích hợp vào dự án của bạn với các tùy chỉnh dịch vụ
- **Azure Table Storage** — hệ thống lưu trữ chi phí thấp, thân thiện với serverless
- **API Quản trị** — CRUD người dùng, quản lý nhà cung cấp SAML/OIDC, định tuyến tên miền SSO, giả mạo token

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
