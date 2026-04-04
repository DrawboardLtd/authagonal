---
layout: default
title: 预配
locale: zh-Hans
---

# TCC 预配

Authagonal 使用 **Try-Confirm-Cancel (TCC)** 模式将用户预配到下游应用程序。这确保所有应用在用户获得访问权限之前达成一致，如有任何应用拒绝，则进行干净的回滚。

## 预配何时运行

预配在**授权端点** (`/connect/authorize`) 处运行，在用户认证之后但授权码签发之前。这意味着：

- 它在用户首次通过需要预配的客户端登录时运行
- 已预配的应用/用户组合会被跳过（在 `UserProvisions` 表中跟踪）
- 如果预配失败，授权请求返回 `access_denied` -- 不签发授权码

## 配置

### 1. 定义预配应用

在 `appsettings.json` 中：

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

### 2. 将应用分配给客户端

每个客户端声明其用户必须被预配到哪些应用：

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

当用户通过 `web-app` 授权时，如果尚未预配，则会被预配到 `my-backend`。

## TCC 协议

Authagonal 向您的预配端点发出三种类型的 HTTP 调用。所有调用都使用 `POST` 方法，发送 JSON 请求体，并附带 `Authorization: Bearer {ApiKey}`。

### 阶段 1：尝试（Try）

**请求：** `POST {CallbackUrl}/try`

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

**预期响应：**

| 状态码 | 响应体 | 含义 |
|---|---|---|
| `200` | `{ "approved": true }` | 用户可以被预配。应用创建一条**待定**记录。 |
| `200` | `{ "approved": false, "reason": "..." }` | 用户被拒绝。不创建记录。 |
| 非 2xx | 任何 | 视为失败。 |

`transactionId` 标识此次预配尝试。您的应用应将其与待定记录一起存储。

### 阶段 2：确认（Confirm）

仅在**所有**应用在尝试阶段返回 `approved: true` 时才调用。

**请求：** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**预期响应：** `200`（任何响应体）。您的应用将待定记录提升为已确认。

### 阶段 3：取消（Cancel）

当**任何**应用的尝试被拒绝或失败时调用，以清理在尝试阶段成功的应用。

**请求：** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**预期响应：** `200`（任何响应体）。您的应用删除待定记录。

取消操作是尽力而为的 -- 如果失败，Authagonal 会记录错误并继续。您的应用应**在 TTL 过期后垃圾回收未确认的记录**（例如 1 小时）作为安全网。

## 流程图

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

### 失败时

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

### 部分确认失败时

如果部分确认成功但有一个失败，成功确认的应用会存储其预配记录（这样就不会重试）。用户会看到错误消息并可以重试 -- 只有失败的应用会在下次尝试。

## 取消预配

当通过管理 API 删除用户时（`DELETE /api/v1/profile/{userId}`），Authagonal 会对用户被预配到的每个应用调用 `DELETE {CallbackUrl}/users/{userId}`。这是尽力而为的 -- 失败会被记录但不会阻止删除。

## 实现上游端点

### 最小示例（Node.js/Express）

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
