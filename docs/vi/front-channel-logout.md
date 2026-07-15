---
layout: default
title: Front-Channel Logout
locale: vi
---

# Front-Channel Logout

Authagonal triển khai **OpenID Connect Front-Channel Logout 1.0**, một cơ chế đăng xuất do trình duyệt điều khiển, bổ trợ cho [back-channel logout](index#features). Trong khi back-channel logout là một POST máy chủ tới máy chủ, front-channel logout kết xuất URL đăng xuất của mỗi relying party trong một iframe ẩn để phiên trình duyệt của mỗi ứng dụng (cookie, local storage) được dọn dẹp từ bên trong trình duyệt của người dùng.

## Khi nào dùng cái nào

| Vấn đề quan tâm | Back-Channel | Front-Channel |
|---|---|---|
| Phiên phía máy chủ | ✅ | ❌ |
| Cookie trình duyệt / local storage | ❌ | ✅ |
| Hoạt động khi trình duyệt của người dùng ngoại tuyến | ✅ | ❌ |
| Chịu được lỗi mạng (thử lại) | ✅ | ❌ (một lần thử nỗ lực tốt nhất) |

Hầu hết các ứng dụng đều hưởng lợi từ việc cấu hình **cả hai**. Back-channel đảm bảo máy chủ được thông báo; front-channel dọn sạch trình duyệt.

## Cấu hình Client

Thêm một URI front-channel logout vào bản ghi `OAuthClient`:

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| Trường | Mô tả |
|---|---|
| `FrontChannelLogoutUri` | Endpoint đăng xuất mà trình duyệt của client nhìn thấy |
| `FrontChannelLogoutSessionRequired` | Nếu `true` (mặc định), URL được gọi với các tham số truy vấn `iss` và `sid` để client có thể liên kết việc đăng xuất với phiên cụ thể |

## Cách hoạt động

Khi trình duyệt truy cập `/connect/endsession`:

1. Máy chủ tìm tất cả các client mà người dùng hiện đang có cấp quyền với chúng.
2. Với mỗi client có `FrontChannelLogoutUri`, máy chủ dựng một URL, nối thêm `iss=<issuer>` (và `sid=<session_id>`, khi phiên có một cái) nếu `FrontChannelLogoutSessionRequired` là `true`.
3. Máy chủ đăng xuất người dùng khỏi cookie của máy chủ ủy quyền, kích hoạt các thông báo back-channel logout ở nền, và trả về một trang HTML chứa một `<iframe>` ẩn cho mỗi URL đăng xuất của client:
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
4. Sau một khoảng ân hạn 2 giây, trình duyệt được chuyển hướng đến `post_logout_redirect_uri`, chỉ được tôn trọng khi yêu cầu cũng mang một `id_token_hint` xác định client và URI đó nằm trong `PostLogoutRedirectUris` đã đăng ký của client đó (một tham số `state`, nếu được cung cấp, sẽ được nối vào chuyển hướng). Nếu không, một xác nhận "đã đăng xuất" sẽ được hiển thị.

## Trình xử lý đăng xuất phía Client

Mỗi relying party nên triển khai URL được tham chiếu bởi `FrontChannelLogoutUri`. Một trình xử lý tối thiểu:

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. Xác minh `iss` khớp với máy chủ ủy quyền mong đợi.
2. Nếu `sid` được cung cấp, xác nhận nó khớp với session ID của cookie phiên.
3. Xóa phiên cục bộ (cookie, phiên phía máy chủ, bộ lưu trữ SPA).
4. Phản hồi với `200 OK` và một body rỗng (hoặc một trang nhỏ xíu): phản hồi không bao giờ hiển thị với người dùng.

```csharp
app.MapGet("/oidc/frontchannel", (HttpContext ctx) =>
{
    var iss = ctx.Request.Query["iss"].ToString();
    var sid = ctx.Request.Query["sid"].ToString();
    // Validate iss/sid, then clear local session
    ctx.SignOutAsync();
    return Results.Ok();
});
```

## Tài liệu khám phá

Front-channel logout được quảng bá trong `/.well-known/openid-configuration`:

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## Đăng ký Client động

Các client được đăng ký qua [Đăng ký Client động](client-registration) có thể bao gồm:

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

## Các giới hạn

- **Nỗ lực tốt nhất**: các iframe được tải một lần. Nếu một lỗi mạng hoặc một tiện ích mở rộng trình duyệt chặn chúng, sẽ không có thử lại. Hãy kết hợp với back-channel logout để có độ tin cậy.
- **Cookie của bên thứ ba**: một số trình duyệt chặn cookie trong các iframe xuyên trang theo mặc định. Nếu RP của bạn dựa vào cookie của bên thứ nhất, hãy xác nhận trình xử lý đăng xuất không phụ thuộc vào việc cookie được gửi đi.
- **Thời gian chờ**: trang chờ khoảng 2 giây trước khi chuyển hướng/xác nhận. Các trình xử lý đăng xuất RP nặng có thể không hoàn tất kịp thời.

## Liên quan

- [Đăng ký Client động](client-registration): các tham số front-channel trong yêu cầu đăng ký
- [OAuth Scope](scopes): sự đồng ý nhận biết scope bổ trợ cho luồng đăng xuất
