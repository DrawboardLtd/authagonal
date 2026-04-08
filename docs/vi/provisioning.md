---
layout: default
title: Cấp phát
locale: vi
---

# Cấp phát TCC

Authagonal cấp phát người dùng vào các ứng dụng phía sau sử dụng mô hình **Try-Confirm-Cancel (TCC)**. Điều này đảm bảo tất cả ứng dụng đồng ý trước khi người dùng được cấp quyền truy cập, với khả năng rollback sạch sẽ nếu bất kỳ ứng dụng nào từ chối.

## Khi nào cấp phát chạy

Cấp phát chạy tự động mỗi khi người dùng được tạo, bất kể đường dẫn tạo:

| Endpoint | Trình kích hoạt |
|---|---|
| `POST /api/v1/profile/` | Quản trị viên tạo người dùng |
| `POST /api/auth/register` | Đăng ký tự phục vụ |
| SAML ACS (`POST /saml/{id}/acs`) | Đăng nhập SSO đầu tiên (người dùng mới) |
| OIDC callback (`GET /oidc/callback`) | Đăng nhập SSO đầu tiên (người dùng mới) |
| SCIM (`POST /scim/v2/Users`) | Cấp phát từ nhà cung cấp danh tính |
| `GET /connect/authorize` | Ủy quyền đầu tiên qua client có `ProvisioningApps` |

Các tổ hợp ứng dụng/người dùng đã được cấp phát sẽ bị bỏ qua (được theo dõi trong bảng `UserProvisions`).

**Khi bị từ chối:** Nếu bất kỳ ứng dụng cấp phát nào từ chối người dùng trong giai đoạn Try, người dùng sẽ bị xóa và endpoint trả về `422 Unprocessable Entity` với lý do từ chối. Điều này ngăn chặn việc tạo người dùng không hoàn chỉnh.

## Cấu hình

### 1. Định nghĩa ứng dụng cấp phát

Trong `appsettings.json`:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-bearer-token"
    }
  }
}
```

### 2. Gán ứng dụng cho client

Mỗi client khai báo các ứng dụng mà người dùng phải được cấp phát vào:

```json
{
  "Clients": [
    {
      "ClientId": "web-app",
      "ProvisioningApps": ["my-backend"],
      ...
    }
  ]
}
```

Khi người dùng ủy quyền qua `web-app`, họ sẽ được cấp phát vào `my-backend` nếu chưa được cấp phát trước đó.

## Giao thức TCC

Authagonal thực hiện ba loại gọi HTTP đến endpoint cấp phát của bạn. Tất cả sử dụng `POST` với body JSON và `Authorization: Bearer {ApiKey}`.

### Giai đoạn 1: Try

**Yêu cầu:** `POST {CallbackUrl}/try`

```json
{
  "transactionId": "a1b2c3d4...",
  "userId": "user-id",
  "email": "user@example.com",
  "firstName": "Jane",
  "lastName": "Doe",
  "organizationId": "org-id-or-null"
}
```

**Phản hồi mong đợi:**

| Trạng thái | Body | Ý nghĩa |
|---|---|---|
| `200` | `{ "approved": true }` | Người dùng có thể được cấp phát. Ứng dụng tạo bản ghi **đang chờ**. |
| `200` | `{ "approved": false, "reason": "..." }` | Người dùng bị từ chối. Không tạo bản ghi. |
| Không phải 2xx | Bất kỳ | Được xử lý như thất bại. |

`transactionId` xác định lần cấp phát này. Ứng dụng của bạn nên lưu nó cùng với bản ghi đang chờ.

### Giai đoạn 2: Confirm

Chỉ được gọi nếu **tất cả** ứng dụng trả về `approved: true` trong giai đoạn try.

**Yêu cầu:** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Phản hồi mong đợi:** `200` (body bất kỳ). Ứng dụng của bạn chuyển bản ghi đang chờ sang đã xác nhận.

### Giai đoạn 3: Cancel

Được gọi nếu **bất kỳ** lần try của ứng dụng nào bị từ chối hoặc thất bại, để dọn dẹp các ứng dụng đã thành công trong giai đoạn try.

**Yêu cầu:** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Phản hồi mong đợi:** `200` (body bất kỳ). Ứng dụng của bạn xóa bản ghi đang chờ.

Cancel là nỗ lực tốt nhất — nếu thất bại, Authagonal ghi nhật ký lỗi và tiếp tục. Ứng dụng của bạn nên **dọn dẹp các bản ghi chưa xác nhận sau TTL** (ví dụ: 1 giờ) như biện pháp an toàn.

## Sơ đồ luồng

```
Authorize Endpoint
    │
    ├─ User authenticated ✓
    ├─ Client requires apps: [A, B]
    ├─ User already provisioned into: [A]
    ├─ Need to provision: [B]
    │
    ├─ TRY B ──────────► App B: create pending record
    │   └─ approved: true
    │
    ├─ CONFIRM B ──────► App B: promote to confirmed
    │   └─ 200 OK
    │
    ├─ Store provision record (userId, "B")
    ├─ Issue authorization code
    └─ Redirect to client
```

### Khi thất bại

```
    ├─ TRY A ──────────► App A: create pending record
    │   └─ approved: true
    │
    ├─ TRY B ──────────► App B: rejects
    │   └─ approved: false, reason: "No license available"
    │
    ├─ CANCEL A ───────► App A: delete pending record
    │
    └─ Redirect with error=access_denied
```

### Khi xác nhận thất bại một phần

Nếu một số xác nhận thành công nhưng một xác nhận thất bại, các ứng dụng được xác nhận thành công sẽ có bản ghi cấp phát được lưu (nên sẽ không bị thử lại). Người dùng thấy lỗi và có thể thử lại — chỉ ứng dụng thất bại sẽ được thử lại lần sau.

## Giải quyết ứng dụng tùy chỉnh

Mặc định, các ứng dụng cấp phát được đọc từ phần cấu hình `ProvisioningApps` thông qua `ConfigProvisioningAppProvider`. Ghi đè `IProvisioningAppProvider` để giải quyết ứng dụng một cách động — ví dụ, từ cơ sở dữ liệu hoặc theo tenant:

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

Provider trả về danh sách các ứng dụng và URL callback của chúng. `TccProvisioningOrchestrator` gọi Try/Confirm/Cancel trên mỗi ứng dụng.

## Hủy cấp phát

Khi người dùng bị xóa qua API quản trị (`DELETE /api/v1/profile/{userId}`), Authagonal gọi `DELETE {CallbackUrl}/users/{userId}` trên mỗi ứng dụng mà người dùng đã được cấp phát vào. Đây là nỗ lực tốt nhất — các lỗi được ghi nhật ký nhưng không chặn việc xóa.

## Triển khai các endpoint phía trên

### Ví dụ tối thiểu (Node.js/Express)

```javascript
const pending = new Map(); // transactionId → user data

app.post('/provisioning/try', (req, res) => {
  const { transactionId, userId, email } = req.body;

  // Your business logic: can this user be provisioned?
  if (!isAllowed(email)) {
    return res.json({ approved: false, reason: 'Domain not allowed' });
  }

  // Store pending record with TTL
  pending.set(transactionId, { userId, email, createdAt: Date.now() });

  res.json({ approved: true });
});

app.post('/provisioning/confirm', (req, res) => {
  const { transactionId } = req.body;
  const data = pending.get(transactionId);

  if (data) {
    createUser(data); // Promote to real record
    pending.delete(transactionId);
  }

  res.sendStatus(200);
});

app.post('/provisioning/cancel', (req, res) => {
  pending.delete(req.body.transactionId);
  res.sendStatus(200);
});

// Cleanup unconfirmed records older than 1 hour
setInterval(() => {
  const cutoff = Date.now() - 3600000;
  for (const [id, data] of pending) {
    if (data.createdAt < cutoff) pending.delete(id);
  }
}, 600000);
```
