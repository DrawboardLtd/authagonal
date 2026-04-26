---
layout: default
title: Bắt đầu nhanh
locale: vi
---

# Bắt đầu nhanh

Chạy Authagonal trên máy local trong 5 phút.

## 1. Khởi động máy chủ

```bash
docker compose up
```

Lệnh này khởi động Authagonal tại `http://localhost:8080` với Azurite làm hệ thống lưu trữ.

## 2. Xác minh đang chạy

```bash
# Kiểm tra sức khỏe
curl http://localhost:8080/health

# Khám phá OIDC
curl http://localhost:8080/.well-known/openid-configuration

# Trang đăng nhập (trả về SPA)
curl http://localhost:8080/login
```

## 3. Đăng ký Client

Thêm client vào tệp `appsettings.json` (hoặc truyền qua biến môi trường):

```json
{
  "Clients": [
    {
      "ClientId": "my-web-app",
      "ClientName": "My Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["http://localhost:3000/callback"],
      "PostLogoutRedirectUris": ["http://localhost:3000"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["http://localhost:3000"],
      "RequirePkce": true,
      "RequireClientSecret": false
    }
  ]
}
```

Các client được khởi tạo khi khởi động — an toàn để chạy trong mỗi lần triển khai.

## 4. Khởi tạo đăng nhập

Chuyển hướng người dùng đến:

```
http://localhost:8080/connect/authorize
  ?client_id=my-web-app
  &redirect_uri=http://localhost:3000/callback
  &response_type=code
  &scope=openid profile email
  &state=random-state
  &code_challenge=...
  &code_challenge_method=S256
```

Người dùng sẽ thấy trang đăng nhập, xác thực, và được chuyển hướng trở lại với mã ủy quyền.

## 5. Đổi mã lấy token

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

Phản hồi:

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800
}
```

## Demo hoạt động

Thư mục `demos/sample-app/` chứa một ứng dụng React SPA + API hoàn chỉnh triển khai đầy đủ luồng OIDC ở trên. Xem [README của demos](https://github.com/authagonal/authagonal/tree/master/demos) để biết hướng dẫn.

## Bước tiếp theo

- [Cấu hình](configuration) — tài liệu tham khảo đầy đủ cho tất cả cài đặt
- [Khả năng mở rộng](extensibility) — tích hợp như thư viện, thêm hook tùy chỉnh
- [Tùy chỉnh giao diện](branding) — tùy chỉnh giao diện đăng nhập
- [SAML](saml) — thêm nhà cung cấp SSO qua SAML
- [Cấp phát](provisioning) — cấp phát người dùng vào các ứng dụng phía sau
