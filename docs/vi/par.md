---
layout: default
title: Pushed Authorization Requests
locale: vi
---

# Pushed Authorization Requests (PAR)

[RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) cho phép một client POST các tham số của yêu cầu ủy quyền trực tiếp đến máy chủ với xác thực client tiêu chuẩn và nhận về một `request_uri` mờ, ngắn hạn để trao cho trình duyệt. Sau đó trình duyệt truy cập `/connect/authorize?request_uri=...&client_id=...` thay vì mang mọi tham số trên URL.

Vì sao nên dùng nó:

- Các tham số ủy quyền không bao giờ xuất hiện trong lịch sử trình duyệt, nhật ký máy chủ, hoặc header `Referer`.
- Máy chủ xác thực client tại thời điểm đẩy (push), nên các tham số được kiểm tra tính toàn vẹn trước khi bất kỳ chuyển hướng nào xảy ra.
- Các tập tham số dài (các yêu cầu `claims` lớn, các luồng đa tài nguyên) không làm vỡ giới hạn độ dài URL.

## Endpoint

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

Việc xác thực giống như `/connect/token`: HTTP Basic với `client_id`/`client_secret`, hoặc thông tin xác thực mã hóa dạng biểu mẫu. Các client bí mật phải xác thực; các client công khai đẩy mà không có secret. Các lỗi xác thực client trả về `401` (theo RFC 9126, khác với endpoint token, nơi chỉ `invalid_client` mới là 401).

Body của biểu mẫu mang cùng các tham số mà thông thường sẽ đi trên `/connect/authorize` (`response_type`, `redirect_uri`, `scope`, `state`, `code_challenge`, `code_challenge_method`, `nonce`, `resource`, v.v.). Bản thân `request_uri` bị từ chối: việc nối chuỗi một PAR bị cấm bởi §2.1 của đặc tả. Nếu body mang một `client_id`, nó phải khớp với client đã được xác thực.

### Phản hồi

```
HTTP/1.1 201 Created
```
```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

`request_uri` chỉ dùng một lần. Nó được xóa khỏi kho một khi yêu cầu `/connect/authorize` khớp tiêu thụ nó (hoặc khi cửa sổ 90 giây hết hạn, tùy cái nào đến sớm hơn).

### Bước ủy quyền

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

Khi `request_uri` hiện diện, tất cả các tham số khác được lấy từ payload đã đẩy: mọi thứ khác trên URL đều bị bỏ qua. `client_id` trên yêu cầu này phải khớp với client đã đẩy payload.

## Yêu cầu PAR theo từng client

Đặt `RequirePushedAuthorizationRequests = true` trên một client để từ chối các yêu cầu `/connect/authorize` thuần túy từ nó. Bất kỳ nỗ lực ủy quyền không phải PAR nào cũng trả về `invalid_request` với mô tả "This client requires requests to be pushed via /connect/par".

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

Đây là tư thế được khuyến nghị cho các client xử lý các scope nhạy cảm: kết hợp với PKCE, nó loại bỏ thanh URL như một bề mặt tấn công.

## Thời gian sống và lưu trữ

Thời gian sống của `request_uri` do máy chủ đặt ở 90 giây, khớp với giá trị IdP tham chiếu điển hình. Các payload đã đẩy được lưu trữ qua cùng `IGrantStore` như mã ủy quyền và refresh token, nên chúng tự động kế thừa chiến lược lưu trữ bền vững và sao chép của host.

## Khám phá

Endpoint PAR tự quảng bá trong `.well-known/openid-configuration` dưới dạng:

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```
