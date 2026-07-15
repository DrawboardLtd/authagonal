---
layout: default
title: Cung cấp SCIM 2.0
locale: vi
---

# Cung cấp SCIM 2.0

Authagonal hỗ trợ SCIM 2.0 (System for Cross-domain Identity Management) để cung cấp người dùng tự động từ các nhà cung cấp danh tính doanh nghiệp như Microsoft Entra ID, Okta và OneLogin.

## Tổng quan

SCIM là một giao thức cung cấp hướng vào (inbound): nhà cung cấp danh tính của bạn đẩy các thay đổi về người dùng và nhóm đến Authagonal. Điều này bổ trợ cho việc cung cấp hướng ra (outbound) TCC (Try-Confirm-Cancel) hiện có, vốn đẩy người dùng đến các ứng dụng phía sau.

**Các thao tác được hỗ trợ:**
- CRUD người dùng (tạo, đọc, cập nhật, xóa qua vô hiệu hóa mềm)
- CRUD nhóm với quản lý thành viên
- Lọc (toán tử `eq` và `co` trên `userName`, `externalId`, `displayName`)
- Phân trang: dựa trên con trỏ cho danh sách người dùng (`cursor`/`nextCursor`), `startIndex` và `count` cho nhóm
- PATCH cho cập nhật một phần (bao gồm vô hiệu hóa `active=false`)
- Ánh xạ nhóm sang vai trò được phân giải tại thời điểm cấp token

**Không được hỗ trợ:** thao tác hàng loạt, sắp xếp, ETag, quản lý mật khẩu qua SCIM.

Mọi tài nguyên đều bị giới hạn phạm vi theo SCIM client đã cung cấp chúng: một người dùng hoặc nhóm được tạo bởi client của một SCIM token là vô hình (404) với mọi SCIM client khác.

## Tạo một SCIM Token

Các endpoint SCIM được xác thực bằng Bearer token tĩnh. Tạo token qua API Quản trị:

```http
POST /api/v1/scim/tokens
Authorization: Bearer {admin-token}
Content-Type: application/json

{
  "clientId": "your-client-id",
  "description": "Entra ID SCIM token",
  "expiresInDays": 365
}
```

Phản hồi bao gồm token thô **một lần duy nhất**. Nó được lưu dưới dạng băm SHA-256 và không thể khôi phục về sau, nên hãy lưu trữ nó một cách an toàn:

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "description": "Entra ID SCIM token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z"
}
```

Bỏ qua `expiresInDays` (hoặc truyền `0`) để có một token không hết hạn.

### Liệt kê token

```http
GET /api/v1/scim/tokens?clientId=your-client-id
Authorization: Bearer {admin-token}
```

### Thu hồi một token

```http
DELETE /api/v1/scim/tokens/{tokenId}?clientId=your-client-id
Authorization: Bearer {admin-token}
```

## Cấu hình nhà cung cấp danh tính của bạn

### URL Tenant

```
https://your-authagonal-instance/scim/v2
```

### Xác thực

Sử dụng **OAuth Bearer Token** với token đã tạo ở trên.

### Microsoft Entra ID

1. Trong Azure portal, đi tới **Enterprise Applications** > ứng dụng của bạn > **Provisioning**
2. Đặt Provisioning Mode thành **Automatic**
3. Nhập Tenant URL: `https://your-instance/scim/v2`
4. Nhập Secret Token: token thô từ bước tạo
5. Nhấp **Test Connection** để xác minh
6. Cấu hình ánh xạ thuộc tính (xem bên dưới)

### Okta

1. Trong bảng điều khiển quản trị Okta, đi tới **Applications** > ứng dụng của bạn > **Provisioning**
2. Bật **SCIM connector**
3. Đặt Base URL: `https://your-instance/scim/v2`
4. Đặt Authentication Mode: **HTTP Header**
5. Nhập Bearer token

### OneLogin

1. Trong trang quản trị OneLogin, đi tới **Applications** > ứng dụng của bạn > **Provisioning**
2. Bật cung cấp (provisioning)
3. Đặt SCIM Base URL: `https://your-instance/scim/v2`
4. Đặt SCIM Bearer Token

## Các endpoint SCIM

| Phương thức | Đường dẫn | Mô tả |
|--------|------|-------------|
| GET | `/scim/v2/Users` | Liệt kê/lọc người dùng |
| GET | `/scim/v2/Users/{id}` | Lấy một người dùng |
| POST | `/scim/v2/Users` | Tạo một người dùng |
| PUT | `/scim/v2/Users/{id}` | Thay thế một người dùng |
| PATCH | `/scim/v2/Users/{id}` | Cập nhật một phần |
| DELETE | `/scim/v2/Users/{id}` | Vô hiệu hóa mềm |
| GET | `/scim/v2/Groups` | Liệt kê/lọc nhóm |
| GET | `/scim/v2/Groups/{id}` | Lấy một nhóm |
| POST | `/scim/v2/Groups` | Tạo một nhóm |
| PUT | `/scim/v2/Groups/{id}` | Thay thế một nhóm |
| PATCH | `/scim/v2/Groups/{id}` | Thêm/xóa thành viên |
| DELETE | `/scim/v2/Groups/{id}` | Xóa một nhóm |
| GET | `/scim/v2/ServiceProviderConfig` | Khả năng |
| GET | `/scim/v2/Schemas` | Định nghĩa lược đồ |
| GET | `/scim/v2/ResourceTypes` | Loại tài nguyên |

Mỗi endpoint cũng được ánh xạ mà không có đoạn `/v2` (ví dụ `/scim/Users`) cho các nhà cung cấp danh tính tự nối thêm đường dẫn của riêng họ. Các endpoint khám phá (`ServiceProviderConfig`, `Schemas`, `ResourceTypes`, và các URL cơ sở trần `/scim/` và `/scim/v2/`, vốn trả về ServiceProviderConfig) là ẩn danh; mọi thứ khác đều yêu cầu một SCIM Bearer token.

Các endpoint người dùng bị giới hạn tốc độ ở mức 200 yêu cầu mỗi phút cho mỗi SCIM client; các yêu cầu vượt mức nhận một lỗi SCIM với trạng thái `429`.

## Ánh xạ thuộc tính

### Thuộc tính người dùng

| Thuộc tính SCIM | Trường Authagonal |
|---------------|------------------|
| `userName` | `Email` |
| `name.givenName` | `FirstName` |
| `name.familyName` | `LastName` |
| `displayName` | `FirstName LastName` |
| `emails[type eq "work"].value` | `Email` |
| `active` | `IsActive` |
| `externalId` | `ExternalId` |
| `preferredLanguage` (dự phòng về `locale`) | `Locale` |

### Thuộc tính nhóm

| Thuộc tính SCIM | Trường Authagonal |
|---------------|------------------|
| `displayName` | `DisplayName` |
| `externalId` | `ExternalId` |
| `members` | `MemberUserIds` |

## Chi tiết hành vi

### Tạo người dùng
- Người dùng được cung cấp qua SCIM được tạo với `EmailConfirmed = true` (chỉ SSO, không mật khẩu).
- Trường `ScimProvisionedByClientId` theo dõi SCIM client nào đã tạo người dùng.
- Nếu client có cấu hình `ProvisioningApps`, việc cung cấp TCC được kích hoạt tự động. Nếu việc cung cấp từ chối người dùng, thao tác tạo SCIM sẽ được hoàn tác với phản hồi `422`.
- Việc tạo một người dùng có `userName` hoặc `externalId` đã tồn tại sẽ trả về xung đột SCIM `409`. Các thay đổi email qua PUT hoặc PATCH cũng được kiểm tra xung đột theo cách tương tự.

### Vô hiệu hóa người dùng
- `DELETE /scim/v2/Users/{id}` thực hiện một **xóa mềm** bằng cách đặt `IsActive = false`. Bản ghi người dùng được giữ lại: một `GET /scim/v2/Users/{id}` sau đó vẫn trả về nó (với `active: false`) thay vì 404.
- `PATCH` với `active = false` cũng vô hiệu hóa người dùng.
- Người dùng đã bị vô hiệu hóa không thể đăng nhập qua mật khẩu, SAML, hoặc OIDC.
- Tất cả cấp quyền (refresh token, phiên) đều bị thu hồi khi vô hiệu hóa.
- Việc hủy cung cấp các ứng dụng phía sau chỉ được kích hoạt bởi `DELETE`; một lần vô hiệu hóa bằng `PATCH` sẽ thu hồi cấp quyền nhưng để nguyên các ứng dụng phía sau.

### Lọc
Các biểu thức lọc được hỗ trợ:
- `userName eq "user@example.com"`
- `externalId eq "12345"`
- `displayName co "John"`

Chỉ hỗ trợ các bộ lọc một thuộc tính. Các biểu thức boolean phức tạp (`and`, `or`) không được hỗ trợ.

Các bộ lọc `eq` trên `userName` và `externalId` (những lượt tra cứu mà Entra và Okta phát ra trước mỗi lần tạo hoặc cập nhật) được phân giải qua các lượt tra cứu điểm có chỉ mục thay vì quét toàn bộ danh sách, nên chúng vẫn nhanh ở bất kỳ số lượng người dùng nào. Các bộ lọc khác (`co`, hoặc bộ lọc trên `displayName`) được áp dụng trong khi phân trang qua danh sách người dùng của client.

### Phân trang
Danh sách người dùng sử dụng **phân trang bằng con trỏ**. Mỗi trang của `GET /scim/v2/Users` trả về một thuộc tính `nextCursor` trong phản hồi danh sách; hãy truyền lại nó dưới dạng `?cursor=` để lấy trang tiếp theo. Khi `nextCursor` vắng mặt, danh sách đã hoàn tất. Kích thước trang được kiểm soát bởi `count` (mặc định 100, tối đa 200).

Việc yêu cầu `startIndex` lớn hơn 1 trên endpoint Users sẽ trả về lỗi `400` hướng bạn đến phân trang bằng con trỏ; không cung cấp phân trang theo offset qua khỏi trang đầu tiên. `totalResults` báo cáo số lượng tài nguyên được trả về trong phản hồi (nó chỉ là tổng thực sự khi `nextCursor` vắng mặt).

Danh sách nhóm vẫn dùng phân trang theo offset `startIndex`/`count`.

### Thành viên nhóm qua PATCH
`PATCH /scim/v2/Groups/{id}` chấp nhận các dạng thành viên mà các nhà cung cấp danh tính lớn thực sự gửi:

- **Thêm thành viên:** `op: "add"` với `path: "members"` và một mảng value gồm các đối tượng `{ "value": "user-id" }`. Các mục trùng lặp bị bỏ qua.
- **Thay thế thành viên:** `op: "replace"` với `path: "members"` thay thế toàn bộ danh sách thành viên bằng mảng được cung cấp.
- **Xóa một thành viên cụ thể (mảng value):** `op: "remove"` với `path: "members"` và một mảng value gồm các id thành viên cần xóa (dạng mà Entra ID gửi).
- **Xóa một thành viên cụ thể (bộ lọc path):** `op: "remove"` với `path: 'members[value eq "user-id"]'`, id được mang trong bộ lọc path mà không có value (dạng Okta gửi để hủy cung cấp).
- **Xóa tất cả thành viên:** `op: "remove"` với `path: "members"` và không có value sẽ làm trống nhóm.

### Ánh xạ nhóm sang vai trò
Việc là thành viên của một nhóm SCIM có thể cấp các vai trò ứng dụng. Mỗi ánh xạ là một hàng cho mỗi cặp (nhóm, vai trò), và một nhóm có thể cấp nhiều vai trò. Chúng được phân giải tại **thời điểm cấp token**: các vai trò hiệu dụng của người dùng là các vai trò được gán trực tiếp cộng với các vai trò của mọi nhóm được ánh xạ mà họ thuộc về, nên việc thêm hoặc xóa một thành viên nhóm sẽ có hiệu lực ở token tiếp theo mà không cần chạm vào bản ghi người dùng. Một kho ánh xạ rỗng là một thao tác không làm gì (no-op).

Các ánh xạ được lưu trữ qua `IScimGroupRoleMappingStore` (được triển khai bởi các nhà cung cấp lưu trữ Azure và AWS; nếu không thì một bản mặc định in-memory được đăng ký) và được quản lý bởi bề mặt quản trị của ứng dụng host, chứ không phải qua chính API SCIM.

Tùy chọn, một client bật `IncludeGroupsInTokens` cũng nhận được các tên hiển thị nhóm SCIM của người dùng dưới dạng một claim `groups` trong các token được cấp.

## Các giới hạn đã biết

- **Không có thao tác hàng loạt:** người dùng và nhóm phải được cung cấp riêng lẻ.
- **Không sắp xếp:** danh sách người dùng trả về theo thứ tự lưu trữ dưới phân trang bằng con trỏ; danh sách nhóm được sắp xếp theo ngày tạo.
- **Tập con bộ lọc:** chỉ các toán tử `eq` và `co` trên `userName`, `externalId`, và `displayName` (nhóm: `displayName` và `externalId`).
- **Không quản lý mật khẩu:** người dùng được cung cấp qua SCIM chỉ xác thực qua SSO.
- **Chỉ xóa mềm:** `DELETE` vô hiệu hóa chứ không xóa vĩnh viễn người dùng.
