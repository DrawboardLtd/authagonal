---
layout: default
title: 扩展
locale: zh-Hans
---

# 扩展

Authagonal 设计为无需特殊配置即可进行垂直和水平扩展。

## 无状态设计

所有持久化状态存储在 Azure Table Storage 中。没有需要粘性会话或实例间协调的进程内状态：

- **签名密钥** — 从 Table Storage 加载，每小时刷新
- **授权码和刷新令牌** — 存储在 Table Storage 中，并强制单次使用
- **SAML 重放防护** — 请求 ID 在 Table Storage 中跟踪，并使用原子删除
- **OIDC state 和 PKCE 验证器** — 存储在 Table Storage 中
- **客户端和提供者配置** — 每次请求从 Table Storage 获取

## Cookie 加密（Data Protection）

ASP.NET Core 的 Data Protection 密钥在使用真实 Azure Storage 连接字符串时会自动持久化到 Azure Blob Storage。这意味着一个实例签名的 cookie 可以被任何其他实例解密 — 无需粘性会话。

对于使用 Azurite 的本地开发，Data Protection 密钥会回退到默认的基于文件的存储。

您也可以通过配置指定显式的 blob URI：

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

## 每实例缓存

少量读取频繁、变化缓慢的值会在每个实例的内存中缓存，以减少 Table Storage 的往返请求：

| 数据 | 缓存时长 | 过期影响 |
|---|---|---|
| OIDC 发现文档 | 60 分钟 | 延迟感知 IdP 密钥轮换 |
| SAML IdP 元数据 | 60 分钟 | 同上 |
| CORS 允许来源 | 60 分钟 | 新来源最多需要一小时才能生效 |

这些缓存适用于生产环境。如果需要立即生效，请重启受影响的实例。

## 速率限制

注册端点受内置的分布式速率限制器保护（每个 IP 每小时 5 次注册）。运行多个实例时，速率限制计数会通过 gossip 协议在所有实例之间自动共享 — 无需外部协调。

### 工作原理

每个实例使用 CRDT G-Counter 在内存中维护自己的计数器。实例通过 UDP 多播相互发现，并每隔几秒通过 HTTP 交换状态。所有实例的合并计数用于做出速率限制决策。

这意味着速率限制是全局执行的：如果一个客户端访问了 3 个不同的实例，所有 3 个实例都知道总数是 3，而不是各自为 1。

### 集群配置

集群功能**默认启用**，无需任何配置。同一网络上的实例通过 UDP 多播（`239.42.42.42:19847`）自动相互发现。

对于多播不可用的环境（某些云 VPC），可以配置一个负载均衡的内部 URL 作为备用方案：

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

要完全禁用集群功能（仅本地速率限制）：

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

有关所有集群设置，请参阅[配置](configuration)页面。

### 优雅降级

- **未找到对等节点** — 作为仅本地速率限制器运行（每个实例执行自己的限制）
- **对等节点不可达** — 仍使用该节点的最后已知状态；过期节点在 30 秒后被清除
- **多播不可用** — 发现静默失败；如果已配置，gossip 回退到 `InternalUrl`

## 扩展建议

**垂直扩展** — 增加单个实例的 CPU 和内存。适用于提高单个实例的并发请求处理能力。

**水平扩展** — 在负载均衡器后运行多个实例。无需粘性会话或共享缓存。每个实例完全独立。

**缩容至零** — Authagonal 支持缩容至零的部署（例如，Azure Container Apps 设置 `minReplicas: 0`）。空闲后的第一个请求会有几秒钟的冷启动时间，用于 .NET 运行时初始化和从存储加载签名密钥。
