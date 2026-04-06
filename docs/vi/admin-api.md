---
layout: default
title: API Quản trị
locale: vi
---

# API Quản trị

Các endpoint quản trị yêu cầu JWT access token với scope `authagonal-admin` (cấu hình qua `AdminApi:Scope`).

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
PUT    /api/v1/saml/connections/{connectionId}     # Cập nhật
DELETE /api/v1/saml/connections/{connectionId}     # Xóa
```

### Nhà cung cấp OIDC

```
POST   /api/v1/oidc/connections                    # Tạo mới
GET    /api/v1/oidc/connections/{connectionId}     # Lấy một
DELETE /api/v1/oidc/connections/{connectionId}     # Xóa
```

### Tên miền SSO

```
GET    /api/v1/sso/domains                 # Liệt kê tất cả
```

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
  "roleId": "role-id"
}
```

### Hủy gán vai trò khỏi người dùng

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleId": "role-id"
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
  "clientId": "client-id"
}
```

Trả về token thô một lần. Lưu trữ an toàn — không thể truy xuất lại.

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
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid,profile
```

Cấp token thay mặt người dùng mà không cần thông tin đăng nhập của họ. Hữu ích cho kiểm thử và hỗ trợ. Các tham số được truyền dưới dạng query string. Tham số `refreshTokenLifetime` tùy chọn điều khiển thời hạn refresh token.
