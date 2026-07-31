---
layout: default
title: OAuth Scope
locale: vi
---

# OAuth Scope

Authagonal hỗ trợ cả các scope OAuth/OIDC **tích hợp sẵn** lẫn các scope **tùy chỉnh** được quản lý tại thời điểm chạy. Các scope tùy chỉnh được lưu trữ bền vững, được quảng bá qua tài liệu khám phá, và được hiển thị trên màn hình đồng ý cùng với các scope tích hợp sẵn.

## Các scope tích hợp sẵn

Các scope này luôn khả dụng và không cần phải đăng ký:

| Scope | Mục đích |
|---|---|
| `openid` | Bắt buộc để khởi tạo một luồng OIDC. Cấp một ID token. |
| `profile` | Các claim hồ sơ tiêu chuẩn (name, family_name, given_name, v.v.) |
| `email` | Các claim địa chỉ email và `email_verified` |
| `offline_access` | Cấp một refresh token cùng với access token |

## Các scope tùy chỉnh

Các scope tùy chỉnh được quản lý qua API Quản trị tại `/api/v1/scopes`. Chúng yêu cầu một JWT access token với scope `authagonal-admin` (có thể cấu hình qua `AdminApi:Scope`).

### Mô hình Scope

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public string? Group { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> AllowedRoles { get; set; } = [];
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| Trường | Mô tả |
|---|---|
| `Name` | Mã định danh scope được gửi trong các yêu cầu token (ví dụ, `billing.read`) |
| `DisplayName` | Tên dễ đọc được hiển thị trên màn hình đồng ý |
| `Description` | Mô tả dài hơn được hiển thị trên màn hình đồng ý |
| `Emphasize` | Nếu `true`, màn hình đồng ý làm nổi bật scope này như một scope nhạy cảm |
| `Group` | Tiêu đề trên màn hình đồng ý để xếp scope này vào. Chỉ là trình bày -- nó không bao giờ ảnh hưởng đến những gì được cấp |
| `Required` | Nếu `true`, người dùng không thể bỏ chọn scope này khi đồng ý |
| `ShowInDiscoveryDocument` | Nếu `true`, scope xuất hiện trong `/.well-known/openid-configuration` dưới `scopes_supported` |
| `AllowedRoles` | Các vai trò mà người dùng phải nắm giữ để được cấp scope này. Để trống (mặc định) nghĩa là không chặn -- xem [Scope chặn theo vai trò](#role-gated-scopes) |
| `UserClaims` | Các claim được thêm vào access token khi scope này được cấp |

### Scope chặn theo vai trò {#role-gated-scopes}

`AllowedScopes` của một client trả lời câu hỏi *ứng dụng này có được phép yêu cầu scope này không* --
một câu hỏi đã được định đoạt trước khi bất kỳ ai đăng nhập. `AllowedRoles` trả lời nửa còn lại:
*người này có được phép có nó không*. Cả hai lớp chặn đều áp dụng, và không lớp nào thay thế lớp kia.

```json
{
  "name": "staff-admin",
  "displayName": "Staff administration",
  "allowedRoles": ["staff", "super-admin"]
}
```

Với người dùng không nắm giữ vai trò nào trong danh sách, scope bị **loại khỏi grant**, chứ không bị
từ chối: client đã yêu cầu trọn bộ của nó và được cho biết, qua `scope` được phản hồi lại trong token
response (RFC 6749 §3.3), rằng nó nhận được ít hơn. Chính điều này cho phép một ứng dụng phục vụ cả
nhân viên nội bộ lẫn mọi người khác -- bề mặt dành cho nhân viên là một scope trong số nhiều scope, và
chỉ những người có quyền mới nhận được nó.

Một request mà *mọi* scope được yêu cầu đều bị loại sẽ thất bại với `access_denied`, vì không còn gì
để phát hành token cả.

Lớp chặn này áp dụng ở mọi nơi mà một token được đúc cho con người:

| Luồng | Nơi nó chạy |
|---|---|
| Authorization code | Tại `/connect/authorize`, ngay khi biết người dùng và **trước** bước đồng ý -- nhờ vậy màn hình không bao giờ chào mời một quyền không thể được cấp |
| Device code | Tại `/api/auth/device/approve`, điểm đầu tiên trong luồng đó mà subject được biết |
| Refresh | Ở mỗi lần xoay vòng, đối chiếu với các vai trò vừa được phân giải lại. Đây là nơi việc thu hồi một vai trò thực sự có hiệu lực, vì grant vẫn ghi lại những gì đã được phê duyệt lúc đăng nhập |
| Token exchange | Không bị chặn riêng: một lần trao đổi chỉ có thể thu hẹp phạm vi trong chính các scope của subject token, nên không bao giờ chạm tới được scope mà subject chưa được cấp |

Các grant client_credentials không có subject và được cố ý không đụng tới -- thẩm quyền của một client
máy chính là phần đăng ký của nó.

Khởi tạo một scope từ cấu hình có thể thêm hoặc thay đổi `AllowedRoles` nhưng không thể xóa trắng nó
(giống như với `UserClaims`, một trường bị bỏ qua sẽ giữ nguyên giá trị đã lưu). Để gỡ bỏ lớp chặn,
hãy `PUT` scope đó với một mảng rỗng tường minh.

## Các endpoint quản trị

### Liệt kê Scope

```
GET /api/v1/scopes
```

Trả về `{ "scopes": [ ... ] }`.

### Lấy Scope

```
GET /api/v1/scopes/{name}
```

Trả về scope hoặc `404` nếu không tìm thấy.

### Tạo Scope

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing — read-only",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

Trả về `201 Created` kèm scope. Trả về `409` nếu một scope trùng tên đã tồn tại.

### Cập nhật Scope

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing — read",
  "description": "View invoices",
  "emphasize": true
}
```

Chỉ các trường được cung cấp mới được cập nhật; các trường bị bỏ qua giữ nguyên giá trị hiện tại của chúng.

### Xóa Scope

```
DELETE /api/v1/scopes/{name}
```

Trả về `204 No Content` (`404` nếu scope không tồn tại). Các token đã được cấp có chứa scope này vẫn hợp lệ cho đến khi hết hạn: hãy thu hồi chúng một cách tường minh qua `/connect/revocation` nếu cần.

## Tài liệu khám phá

Các scope có `ShowInDiscoveryDocument = true` xuất hiện dưới `scopes_supported` trong `/.well-known/openid-configuration`. Các scope tích hợp sẵn luôn được quảng bá.

```json
{
  "scopes_supported": ["openid", "profile", "email", "offline_access", "billing.read"]
}
```

## Màn hình đồng ý

Khi một client yêu cầu một scope không nằm trong danh sách bỏ qua đồng ý của nó, trang đồng ý liệt kê từng scope được yêu cầu theo `DisplayName` (dự phòng về `Name`) với `Description` bên dưới. Các scope có `Emphasize = true` nhận một cách trình bày trực quan riêng biệt. Các scope `Required` không thể bị bỏ chọn.

Xem [Màn hình đồng ý OAuth](index#features) để biết luồng hướng đến người dùng.

## Đăng ký Client động

Các client được đăng ký qua [Đăng ký Client động](client-registration) chỉ có thể yêu cầu các scope hoặc là tích hợp sẵn hoặc đã được tạo trước đó qua API Quản trị. Các scope không xác định bị từ chối với `invalid_scope`.
