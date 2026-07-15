---
layout: default
title: 预配
locale: zh-Hans
---

# TCC 预配

Authagonal 使用 **Try-Confirm-Cancel (TCC)** 模式将用户预配到下游应用程序。这确保所有应用在用户获得访问权限之前达成一致，如有任何应用拒绝，则进行干净的回滚。

## 预配何时运行

预配在用户被创建时自动运行，与创建路径无关：

| 端点 | 触发条件 |
|---|---|
| `POST /api/v1/profile/` | 管理员创建用户 |
| `POST /api/auth/register` | 自助注册 |
| SAML ACS (`POST /saml/{id}/acs`) | 首次 SSO 登录（新用户） |
| OIDC 回调 (`GET /oidc/callback`) | 首次 SSO 登录（新用户） |
| SCIM (`POST /scim/v2/Users`) | 身份提供者预配 |
| `GET /connect/authorize` | 首次通过带有 `ProvisioningApps` 的客户端授权 |

已预配的应用/用户组合会被跳过（在 `UserProvisions` 表中跟踪）。

用户创建路径会预配到**每一个已配置的应用**。授权端点只预配到客户端的 `ProvisioningApps` 列表中的应用。

**被拒绝时：** 如果任何预配应用在 Try 阶段拒绝了用户，则新创建的用户将被删除。这可以防止产生半创建的用户。API 创建路径（管理员、注册、SCIM）返回 `422 Unprocessable Entity` 并附带拒绝原因；SAML/OIDC SSO 回调返回 `400 Bad Request`；授权端点则带 `error=access_denied` 重定向回客户端。

## 配置

### 1. 定义预配应用

在 `appsettings.json` 中：

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-bearer-token",
      "TryTimeoutSeconds": 60
    }
  }
}
```

`TryTimeoutSeconds` 是可选的（默认 60）。当下游应用在 Try 阶段执行实际工作时，请调高它。Confirm 和 Cancel 始终使用较短的固定超时（10 秒）且不可调；它们应始终是廉价的操作。

### 2. 将应用分配给客户端

每个客户端通过客户端记录上的 `provisioningApps` 字段声明其用户必须被预配到哪些应用。请通过客户端管理 API 设置它（`Clients` 种子配置不携带此字段）：

```
PUT /api/v1/clients/web-app
{
  "clientId": "web-app",
  "provisioningApps": ["my-backend"],
  ...
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
  "organizationId": "org-id-or-null",
  "customAttributes": { "key": "value" }
}
```

空字段（包括用户没有自定义属性时的 `customAttributes`）会从载荷中省略。

**预期响应：**

| 状态码 | 响应体 | 含义 |
|---|---|---|
| `200` | `{ "approved": true }` | 用户可以被预配。应用创建一条**待定**记录。 |
| `200` | `{ "approved": false, "reason": "..." }` | 用户被拒绝。不创建记录。 |
| 非 2xx | 任何 | 视为失败。 |

`transactionId` 标识此次预配尝试。您的应用应将其与待定记录一起存储。

被批准的响应还可以返回 `organizationId` 和/或 `customAttributes`。Authagonal 会将它们合并到用户上：`organizationId` 仅在用户尚未拥有时才应用（同一事务中较晚的应用会看到较早的赋值），`customAttributes` 条目则逐键合并。两者都会流入令牌（`org_id` 声明；自定义属性通过作用域的 `UserClaims` 配置）。

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

取消操作是尽力而为的：如果失败，Authagonal 会记录错误并继续。您的应用应**在 TTL 过期后垃圾回收未确认的记录**（例如 1 小时）作为安全网。

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

如果部分确认成功但有一个失败，成功确认的应用会存储其预配记录（这样就不会重试），任何仍在等待确认的应用都会被取消。用户会看到错误消息并可以重试；只有未确认的应用会在下次尝试。

## 自定义应用解析

默认情况下，预配应用通过 `ConfigProvisioningAppProvider` 从 `ProvisioningApps` 配置节读取。覆盖 `IProvisioningAppProvider` 以动态解析应用，例如从数据库或按租户解析：

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

Provider 返回应用列表及其回调 URL。`TccProvisioningOrchestrator` 对每个应用调用 Try/Confirm/Cancel。

如果无需自定义 provider 就想进行运行时 CRUD，库中提供了由 `IProvisioningAppStore` 支撑的 `StoreProvisioningAppProvider`。显式注册它（与上面相同的模式），并通过 `/api/v1/provisioning/apps` 处的管理 API 管理应用（列出/创建/更新/删除，以及 `POST /{appId}/test` 以探测应用的 Try 端点）。

## 取消预配

当通过管理 API 删除用户时（`DELETE /api/v1/profile/{userId}`）或通过 SCIM 取消预配时（`DELETE /scim/v2/Users/{id}`，一种停用用户的软删除），Authagonal 会对用户被预配到的每个应用调用 `DELETE {CallbackUrl}/users/{userId}`。这是尽力而为的：失败会被记录但不会阻止删除。

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
