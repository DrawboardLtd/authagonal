---
layout: default
title: Liên kết OIDC
locale: vi
---

# Liên kết OIDC

Authagonal có thể liên kết xác thực với các nhà cung cấp danh tính OIDC bên ngoài (Google, Apple, Azure AD, v.v.). Điều này cho phép các luồng kiểu "Đăng nhập bằng Google" trong khi Authagonal vẫn là máy chủ xác thực trung tâm.

## Cách hoạt động

1. Người dùng nhập email trên trang đăng nhập
2. SPA gọi `/api/auth/sso-check` — nếu tên miền email được liên kết với nhà cung cấp OIDC, SSO là bắt buộc
3. Người dùng nhấp "Tiếp tục với SSO" và được chuyển hướng đến IdP bên ngoài
4. Sau khi xác thực, IdP chuyển hướng lại `/oidc/callback`
5. Authagonal xác thực id_token, tạo/liên kết người dùng, và đặt cookie phiên

## Thiết lập

### 1. Tạo nhà cung cấp OIDC

**Tùy chọn A — Cấu hình (khuyến nghị cho thiết lập tĩnh):**

Thêm vào `appsettings.json`:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

Các nhà cung cấp được khởi tạo khi khởi động. `ClientSecret` được bảo vệ qua `ISecretProvider` (Key Vault khi được cấu hình, văn bản thuần trong trường hợp khác). Các ánh xạ tên miền SSO được đăng ký tự động từ `AllowedDomains`.

**Tùy chọn B — API Quản trị (cho quản lý tại thời điểm chạy):**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"]
  }'
```

### 2. Định tuyến tên miền SSO

Khi `AllowedDomains` được chỉ định (trong cấu hình hoặc qua API tạo), các ánh xạ tên miền SSO được đăng ký tự động. Nếu không có định tuyến tên miền, người dùng vẫn có thể được chuyển đến trang đăng nhập OIDC qua `/oidc/{connectionId}/login`.

## Endpoint

| Endpoint | Mô tả |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...` | Khởi tạo đăng nhập OIDC. Tạo PKCE + state + nonce, chuyển hướng đến endpoint ủy quyền của IdP. |
| `GET /oidc/callback` | Xử lý callback từ IdP. Đổi mã lấy token, xác thực id_token, tạo/đăng nhập người dùng. |

## Tính năng bảo mật

- **PKCE** — code_challenge với S256 trên mỗi yêu cầu ủy quyền
- **Xác thực nonce** — nonce được lưu trong state, được xác minh trong id_token
- **Xác thực state** — sử dụng một lần, được lưu trong Azure Table Storage với thời hạn
- **Xác thực chữ ký id_token** — khóa được lấy từ endpoint JWKS của IdP
- **Dự phòng userinfo** — nếu id_token không chứa email, endpoint userinfo sẽ được thử

## Đặc thù Azure AD

Azure AD đôi khi trả về email dưới dạng mảng JSON trong claim `emails` (đặc biệt với B2C). Authagonal xử lý điều này bằng cách kiểm tra cả claim `email` và mảng `emails`.

## Nhà cung cấp được hỗ trợ

Bất kỳ nhà cung cấp tương thích OIDC nào hỗ trợ:
- Luồng Authorization Code
- PKCE (S256)
- Tài liệu khám phá (`.well-known/openid-configuration`)

Đã được kiểm thử với:
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C
