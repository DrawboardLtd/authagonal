---
layout: default
title: Đăng ký Client động
locale: vi
---

# Đăng ký Client động

Authagonal triển khai **OAuth 2.0 Dynamic Client Registration** ([RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)), cho phép các ứng dụng client tự đăng ký tại thời điểm chạy mà không cần sự tham gia của quản trị viên.

## Bật Endpoint

Đăng ký động **bị tắt theo mặc định**. Bật tham gia qua cấu hình:

```json
{
  "Auth": {
    "DynamicClientRegistrationEnabled": true
  }
}
```

Hoặc đặt `Auth__DynamicClientRegistrationEnabled=true` làm biến môi trường.

Khi được bật, tài liệu khám phá sẽ quảng bá endpoint:

```
GET /.well-known/openid-configuration
```
```json
{
  "registration_endpoint": "https://auth.example.com/connect/register"
}
```

## Đăng ký một Client

```
POST /connect/register
Content-Type: application/json

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access",
  "audiences": ["https://api.myapp.example.com"],
  "allowed_cors_origins": ["https://myapp.example.com"],
  "backchannel_logout_uri": "https://myapp.example.com/oidc/backchannel",
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

### Phản hồi

```
HTTP/1.1 201 Created
Content-Type: application/json

{
  "client_id": "a1b2c3d4e5f6...",
  "client_secret": "xkCd2_base64url...",
  "client_id_issued_at": 1745000000,
  "client_secret_expires_at": 0,
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email offline_access",
  "token_endpoint_auth_method": "client_secret_basic"
}
```

`client_secret` được trả về **một lần duy nhất** và không thể lấy lại về sau. Hãy lưu trữ nó một cách an toàn.

## Các tham số yêu cầu

| Tham số | Bắt buộc | Ghi chú |
|---|---|---|
| `client_name` | không | Mặc định là `client_id` được tạo nếu bỏ qua |
| `redirect_uris` | có điều kiện | Bắt buộc khi `grant_types` chứa `authorization_code`. Phải là URI tuyệt đối; các scheme `javascript:`/`data:`/`vbscript:`/`file:` bị từ chối (các scheme tùy chỉnh gốc cho deep link di động thì được chấp nhận). |
| `post_logout_redirect_uris` | không | Các đích chuyển hướng hợp lệ sau khi đăng xuất |
| `grant_types` | không | Mặc định là `["authorization_code"]`. **Chỉ `authorization_code` và `refresh_token` là có thể đăng ký**: `client_credentials`, `implicit`, device và bất kỳ loại cấp quyền nào khác đều bị từ chối với `invalid_client_metadata`, nên đăng ký mở không bao giờ có thể tạo ra một client máy-với-máy. `refresh_token` được thêm tự động nếu `offline_access` được yêu cầu. |
| `token_endpoint_auth_method` | không | `client_secret_basic` (mặc định), `client_secret_post`, hoặc `none` cho các client công khai |
| `scope` | không | Các scope phân tách bằng dấu cách: tất cả phải là scope tích hợp sẵn hoặc đã được đăng ký trước đó (xem [Scope](scopes)). Scope quản trị (`AdminApi:Scope`, mặc định `authagonal-admin`) không bao giờ có thể được đăng ký. |
| `audiences` | không | Các giá trị `aud` của JWT được thêm vào access token |
| `allowed_cors_origins` | không | Các origin được phép gọi endpoint token từ trình duyệt |
| `backchannel_logout_uri` | không | Bật [Back-Channel Logout](index#features) |
| `frontchannel_logout_uri` | không | Bật [Front-Channel Logout](front-channel-logout) |
| `frontchannel_logout_session_required` | không | Mặc định là `true`; khi `true`, URL đăng xuất mang các tham số `iss` và `sid` |

## Mặc định & bất biến

- **Yêu cầu PKCE**: `RequirePkce` luôn là `true` đối với các client được đăng ký động.
- **Client công khai**: `token_endpoint_auth_method: "none"` tạo ra một client không có secret. PKCE vẫn được yêu cầu.
- **Truy cập ngoại tuyến**: yêu cầu scope `offline_access` sẽ ngầm thêm `refresh_token` vào `grant_types`.

## Các phản hồi lỗi

| HTTP | `error` | Nguyên nhân |
|---|---|---|
| `400` | `invalid_redirect_uri` | Một trong các `redirect_uris` không phải là URI tuyệt đối hợp lệ, hoặc dùng một pseudo-scheme script/data/file |
| `400` | `invalid_client_metadata` | Một loại cấp quyền không thể đăng ký đã được yêu cầu, hoặc thiếu `redirect_uris` cho một loại cấp quyền yêu cầu nó |
| `400` | `invalid_scope` | Một scope được yêu cầu không phải là scope tích hợp sẵn cũng không được đăng ký |
| `403` | `invalid_scope` | Scope quản trị đã được yêu cầu: nó không bao giờ có thể được cấp qua đăng ký |
| `403` | `not_supported` | Đăng ký client động không được bật |
| `429` | `rate_limited` | Quá nhiều lượt đăng ký từ IP này (10 mỗi giờ) |

## Cân nhắc bảo mật

Endpoint đăng ký **không được xác thực**, nhưng bị ràng buộc theo thiết kế:

- **Giới hạn tốc độ**: 10 lượt đăng ký mỗi IP trong mỗi giờ trượt (`429 rate_limited`), nên kho client không thể bị làm ngập.
- **Hạn chế loại cấp quyền**: chỉ `authorization_code` + `refresh_token`; một client đã đăng ký luôn yêu cầu một luồng có sự trung gian của người dùng và không bao giờ có thể hoạt động như một client máy-với-máy.
- **Scope quản trị được dành riêng**: scope `authagonal-admin` (hoặc bất kỳ giá trị nào `AdminApi:Scope` được đặt) bị từ chối, nên đăng ký không bao giờ có thể tạo ra một client tiếp cận được [API Quản trị](admin-api).
- **PKCE luôn được yêu cầu** trên các client đã đăng ký.

Để kiểm soát chặt hơn (initial access token, mTLS, software statement), hãy đặt trước endpoint bằng middleware của riêng bạn hoặc một `IAuthHook`. Hãy cân nhắc tắt hẳn đăng ký động và quản lý client qua API Quản trị trong các môi trường mà đăng ký tự phục vụ không phải là một yêu cầu.
