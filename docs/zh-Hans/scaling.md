---
layout: default
title: 扩展
locale: zh-Hans
---

# 扩展

Authagonal 设计为无需特殊配置即可进行垂直和水平扩展。

## 无状态设计

所有持久化状态存储在后端表存储中——Azure Table Storage，或 AWS 后端上的 DynamoDB。没有需要粘性会话或实例间协调的进程内状态：

- **签名密钥**：从 Table Storage 加载，每小时刷新
- **授权码和刷新令牌**：存储在 Table Storage 中，并强制单次使用
- **SAML 重放防护**：请求 ID 在 Table Storage 中跟踪，并使用原子删除
- **OIDC state 和 PKCE 验证器**：存储在 Table Storage 中
- **客户端和提供者配置**：每次请求从 Table Storage 获取

## Cookie 加密（Data Protection）

ASP.NET Core 的 Data Protection 密钥在使用真实 Azure Storage 连接字符串时会自动持久化到 Azure Blob Storage。这意味着一个实例签名的 cookie 可以被任何其他实例解密，无需粘性会话。

对于使用 Azurite 的本地开发，Data Protection 密钥会回退到默认的基于文件的存储。

您也可以通过配置指定显式的 blob URI（托管标识路径，生产环境首选）：

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

在 AWS 后端上，将 S3 客户端 + 存储桶传给 `AddAuthagonalAwsStorage`，即可将密钥环持久化到 S3；否则密钥环仅在内存中，Cookie 会在重启后以及跨节点时失效。参见[安装 → AWS 后端](installation#aws-backend)。

## 每实例缓存

少量读取频繁、变化缓慢的值会在每个实例的内存中缓存，以减少 Table Storage 的往返请求：

| 数据 | 缓存时长 | 过期影响 |
|---|---|---|
| OIDC 发现文档 | 60 分钟 | 延迟感知 IdP 密钥轮换 |
| SAML IdP 元数据 | 60 分钟 | 同上 |
| CORS 允许来源 | 60 分钟 | 新来源最多需要一小时才能生效 |

这些缓存适用于生产环境。所有时长都可通过 `Cache` 配置节配置——参见[配置](configuration)。如果需要立即生效，请重启受影响的实例。

## 速率限制

易被滥用的端点（按 IP 的注册、按目标邮箱的密码重置、按客户端的 SCIM、按 IP 的动态客户端注册，参见[配置 → 速率限制](configuration#rate-limiting)）受内置速率限制器保护。

限制在 `IRateLimiter` 接缝之后**按节点在进程内**执行，因此 N 个实例的有效上限是配置值的 N 倍。这是刻意为之：该限制器是针对单个节点失控滥用的兜底，而权威的全局限制应当放在边缘（WAF / 入口 / CDN），因为边缘在流量被负载均衡之前就能看到全部流量。

## 集群

多个实例通过**领导者选举**和**跨节点事件总线**进行协调，两者都位于可插拔后端之后：

- **领导者选举**：基于租约的选举（`Cluster:LeaseTtlSeconds`，默认 30 秒，大约每过一半间隔续约一次）。恰好一个节点持有租约；当领导者下线时，领导权自动转移。需要领导者把关的工作（目前是启用时的签名密钥轮换）仅在领导者上运行，以避免并发的密钥生成。
- **事件总线**：跨节点通知（例如多租户宿主中的缓存失效），按 `Cluster:PollIntervalSeconds`（默认 3 秒）轮询。

每个实例在启动时生成一个随机的 12 位十六进制字符节点 ID 用于标识自己；它不会持久化。

### 后端

**默认是进程内实现**：单个节点始终是自己的领导者，事件仅在本地传递，对单实例而言无需任何配置即是正确的。多节点部署通过 `AddAuthagonal` 上的 `configureClustering` 回调换入真实后端：

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS: leadership + event bus via DynamoDB (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` 仅注册事件总线，保留进程内（始终为领导者）的租约——在那些必须接收集群事件、但绝不能参与领导权竞争的节点上使用它们。

> **注意：** 在多个节点上使用进程内默认实现时，*每个*节点都认为自己是领导者。这对大多数工作负载无害，但在多个实例上开启 `Auth:KeyRotationEnabled` 之前，请先启用真实的租约后端。

有关所有集群设置，请参阅[配置](configuration#cluster)页面。

### 多租户部署

在多租户模式下（`AddAuthagonalCore()`），不会注册任何后台服务——`TokenCleanupService`、`GrantReconciliationService`、`SigningKeyRotationService` 以及配置种子服务都属于单租户 `AddAuthagonal()` 组合的一部分。由宿主按租户管理这些服务。

## 姓名索引热分区

管理员姓名前缀搜索由 `UserFirstNames` / `UserLastNames` 索引表支撑，这些表使用**单个热分区**。在规模化时，这会将索引写入吞吐量限制在大约 2,000 ops/秒，在高负载下可能成为用户创建/更新的瓶颈。如果您不向外暴露管理员姓名搜索，请设置 `Storage:NameIndexesEnabled = false` 以完全跳过这些写入。参见[配置](configuration)。

## 受信任代理与内部端点

在负载均衡器后面运行多个实例时：

- **转发头**：速率限制和锁定以客户端 IP 为键，该 IP 从 `X-Forwarded-For` 解析。将 `ForwardedHeaders:KnownNetworks` 设为您的入口 / Pod CIDR，使客户端 IP 无法跨实例被伪造。`ForwardedHeaders:ForwardLimit` 默认为 `1`。参见[配置](configuration#forwarded-headers-trusted-proxy)。
- **内部端点**：`/_internal/backchannel-logout` 受源 IP 保护（仅环回 / 私有），除非设置了 `Cluster:Secret`；设置后，调用方必须在 `X-Cluster-Secret` 请求头中提供该密钥（以恒定时间比较）。只要内部流量经过任何会改写源 IP 的组件，就应设置该密钥。

## 扩展建议

**垂直扩展**：增加单个实例的 CPU 和内存。适用于提高单个实例的并发请求处理能力。

**水平扩展**：在负载均衡器后运行多个实例。无需粘性会话或共享缓存。每个实例完全独立。

**缩容至零**：Authagonal 支持缩容至零的部署（例如，Azure Container Apps 设置 `minReplicas: 0`）。空闲后的第一个请求会有几秒钟的冷启动时间，用于 .NET 运行时初始化和从存储加载签名密钥。
