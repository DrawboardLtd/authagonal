---
layout: default
title: API Quản trị
locale: vi
---

# API Quản trị

Các endpoint quản trị yêu cầu JWT access token với scope `authagonal-admin`.

Tất cả endpoint nằm dưới `/api/v1/`.

## Người dùng

### Lấy thông tin người dùng

```
GET /api/v1/profile/{userId}
```

Trả về chi tiết người dùng bao gồm các liên kết đăng nhập bên ngoài.

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

Tạo người dùng và gửi email xác minh. Trả về `409` nếu email đã được sử dụng.

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

Tất cả trường là tùy chọn — chỉ các trường được cung cấp mới được cập nhật. Thay đổi `organizationId` kích hoạt:
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

## Nhà cung cấp SSO

### Nhà cung cấp SAML

```
GET    /api/v1/sso/saml                    # Liệt kê tất cả
GET    /api/v1/sso/saml/{connectionId}     # Lấy một
POST   /api/v1/sso/saml                    # Tạo mới
PUT    /api/v1/sso/saml/{connectionId}     # Cập nhật
DELETE /api/v1/sso/saml/{connectionId}     # Xóa
```

### Nhà cung cấp OIDC

```
GET    /api/v1/sso/oidc                    # Liệt kê tất cả
GET    /api/v1/sso/oidc/{connectionId}     # Lấy một
POST   /api/v1/sso/oidc                    # Tạo mới
PUT    /api/v1/sso/oidc/{connectionId}     # Cập nhật
DELETE /api/v1/sso/oidc/{connectionId}     # Xóa
```

### Tên miền SSO

```
GET    /api/v1/sso/domains                 # Liệt kê tất cả
GET    /api/v1/sso/domains/{domain}        # Lấy một
POST   /api/v1/sso/domains                 # Tạo mới
DELETE /api/v1/sso/domains/{domain}        # Xóa
```

## Token

### Giả mạo người dùng

```
POST /api/v1/tokens/impersonate
Content-Type: application/json

{
  "userId": "user-id",
  "clientId": "client-id",
  "scopes": ["openid", "profile"]
}
```

Cấp token thay mặt người dùng mà không cần thông tin đăng nhập của họ. Hữu ích cho kiểm thử và hỗ trợ.
