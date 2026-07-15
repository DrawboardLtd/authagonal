---
layout: default
title: Table Storage 备份白皮书
locale: zh-Hans
---

# 备份 Azure Table Storage：一种实用方法

**Authagonal 如何为无模式（schemaless）NoSQL 存储实现全量与增量备份**

---

## 问题

Azure Table Storage 是一种经济高效、可大规模扩展的键值存储——但它不提供任何原生的备份设施。没有快照，没有时间点恢复，没有导出按钮。如果一次糟糕的部署损坏了数据，或者运维人员误删了一张表，恢复就完全取决于你自己所构建的一切。

对于像 Authagonal 这样的身份平台——其表中保存着用户、凭据、OAuth 授权、签名密钥、SSO 配置以及 SCIM 预配状态——风险极高。丢失这些数据不仅会让应用瘫痪；它会把人们锁在门外。

本文描述 Authagonal 所采用的备份策略：它如何导出数据、增量备份如何在 Table Storage 受限的查询模型之下工作、删除如何被追踪，以及这些部件如何组合成一条可投入生产的备份流水线。

## 设计目标

1. **全量与增量备份。** 对小型部署而言，每日一次全量备份就够了；但在规模化时，每小时的增量备份能让备份窗口保持短小、存储成本保持低廉。
2. **忠实的往返。** 每个实体属性——字符串、整数、布尔值、DateTimeOffset、GUID、二进制——都必须在一次备份/恢复循环中存活下来，不发生类型强制转换或数据丢失。
3. **多租户支持。** Authagonal 使用表名前缀来隔离租户（例如 `acmecorpUsers`、`acmecorpClients`）。备份和恢复必须感知前缀，这样单个存储账户就能承载许多具有各自独立备份计划的租户。
4. **可插拔的存储。** 备份应当在开发时能写入本地文件系统、在生产时能写入 blob 存储（或任何其他目标），而无需更改核心逻辑。
5. **人类可读的输出。** 当出问题时，运维人员应当能够在文本编辑器中打开一个备份文件，看到里面的内容。

## 架构

备份系统被构建为一个 .NET 库（`Authagonal.Backup`），并为备份和恢复操作配以精简的 CLI 包装器。该库与主 Authagonal 服务器相分离，因此可以作为独立工具使用、在 Docker 容器中使用，或嵌入到定时作业中。

```
Authagonal.Backup (library)
  BackupService         -- orchestrates full/incremental export
  RestoreService        -- imports backup data into Table Storage
  MergeService          -- consolidates full + incrementals into one snapshot
  RollupService         -- merge + cleanup of old backups
  IBackupTarget         -- write abstraction (filesystem, blob, etc.)
  IBackupSource         -- read abstraction
  FileSystemBackupTarget/Source -- local filesystem implementation

tools/Authagonal.Backup     -- CLI entry point for backup
tools/Authagonal.Restore    -- CLI entry point for restore
```

### 存储抽象

核心服务从不直接接触文件系统。它们针对两个接口进行操作：

**IBackupTarget** 提供四种操作：为备份文件打开一个可写流、写入清单（manifest）、获取上一个水位线（watermark，用于增量调度），以及设置新的水位线。

**IBackupSource** 提供读取侧：读取清单、打开一个可读流、按时间顺序列出备份 ID、列出某个备份内的文件，以及删除一个备份。

文件系统实现很直白——带时间戳的目录，里面装着 JSONL 文件——但这层抽象意味着切换到 Azure Blob Storage 或 S3 只需实现这两个接口。

## 全量备份

一次全量备份会遍历每一张 Authagonal 表，查询所有实体，并将它们写入 JSONL 文件（每行一个 JSON 对象，每张表一个文件）。

备份过程：

1. 从当前 UTC 时间戳生成一个备份 ID（例如 `20260329-120000`）。
2. 对 20 张默认的 Authagonal 表中的每一张，以 1,000 的页大小调用 Azure Table Storage SDK 的 `QueryAsync<TableEntity>` 进行查询。
3. 将每个实体序列化为一个扁平的 JSON 字典，保留所有属性，包括系统属性（`PartitionKey`、`RowKey`、`Timestamp`、`ETag`）。
4. 将每个序列化后的实体作为单独一行写入 `{TableName}.jsonl`（如果启用了压缩，则写入 `{TableName}.jsonl.gz`）。
5. 在清单（`_manifest.json`）中记录每张表的实体数量和耗时。
6. 用备份开始时间更新 `.lastbackup` 水位线文件。

存储账户中不存在的表会被静默跳过（HTTP 404 会被捕获并忽略）。像 `SamlReplayCache` 和 `OidcStateStore` 这样的临时表默认被排除，因为它们的内容是短暂的。

### 输出格式

```
backups/
  20260329-120000/
    Users.jsonl
    Clients.jsonl
    Grants.jsonl
    GrantsBySubject.jsonl
    ...
    _manifest.json
```

`Users.jsonl` 中的单独一行看起来像这样：

```json
{"PartitionKey":"u_abc123","RowKey":"profile","Timestamp":"2026-03-28T09:14:22+00:00","ETag":"W/\"...\"","Email":"alice@example.com","DisplayName":"Alice","CreatedAt":"2025-11-01T00:00:00+00:00"}
```

选择 JSONL 而非 CSV 或某种二进制格式，是因为它保留了 Table Storage 实体无模式、异构的本质（同一张表中的不同实体可以有不同的属性）、可流式处理（无需将整张表缓冲到内存中），并且可以用 `jq` 之类的标准工具或任何文本编辑器直接查看。

### 压缩

当设置了 `--gzip` 标志时，每个 JSONL 文件在写入前都会以 `CompressionLevel.Optimal` 包裹在一个 GZip 流中。文件扩展名变为 `.jsonl.gz`。恢复工具通过检查每个文件开头的魔数字节（`0x1f 0x8b`）来自动检测 GZip，因此恢复时无需任何标志。

## 增量备份

### Timestamp 技巧

Azure Table Storage 会在每个实体上自动维护一个 `Timestamp` 属性，并在每次插入或替换时更新。这是一个由服务器管理的属性——应用程序无法设置它。备份系统利用这一点，将查询过滤为 `Timestamp gt datetime'{watermark}'`，其中水位线是上一次成功备份的开始时间。

这意味着增量备份只会下载自上一次运行以来创建或修改的实体。对于一个拥有 500,000 个实体、其中在过去一小时内有 200 个发生了变更的系统，增量备份传输的是 200 行，而非 500,000 行。

水位线存储在备份根目录中的一个 `.lastbackup` 文件里。如果该文件不存在（首次运行，或手动清理之后），备份会回退到全量导出。增量备份 ID 带有 `-incr` 后缀（例如 `20260329-180000-incr`），清单会记录 `"mode": "incremental"` 以及用于过滤的水位线值。

### Timestamp 过滤的代价

有一个局限值得坦诚：`Timestamp` 没有被索引。Azure Table Storage 只索引 `PartitionKey` 和 `RowKey`。对 `Timestamp gt datetime'...'` 的过滤会导致一次全表扫描——Azure 在服务器端读取每一个实体并在返回匹配项之前对谓词求值。这种过滤减少了数据传输量（只有变更的实体会经过网络），但并不减少服务器端的读取成本。

更重要的是，当前的做法会逐一扫描**全部 20 张表**，即便只有一张表发生了变更。这意味着每次增量备份都要进行 20 次全表扫描，无论实际变更的实体有多少。

在 Authagonal 典型的身份数据量下（数万个实体，而非数百万），这完全可以接受——扫描很快，读取很便宜（每 10,000 次事务 $0.00036），而且该操作是只读的，对线上流量没有影响。关于[超越时间戳扫描的扩展](#scaling-beyond-timestamp-scans)一节讨论了这一点可以如何演进。

### 删除问题

`Timestamp` 过滤优雅地捕获了插入和更新，但它无法捕获删除。一个被删除的实体就这样消失了——没有可供过滤的 `Timestamp`，Table Storage 自身也不会留下任何墓碑（tombstone）。

Authagonal 通过应用层的墓碑追踪来解决这个问题。

## 墓碑追踪

Authagonal 中的每个数据存储（用户、客户端、授权、签名密钥、SSO 域、SAML/OIDC 提供者、MFA 凭据、SCIM 资源、角色）都接受一个可选的 `ITombstoneWriter` 依赖。当某个存储删除一个实体时，它会向专用的 `Tombstones` 表写入一条墓碑记录：

| 列 | 值 |
|---|---|
| `PartitionKey` | 逻辑表名（例如 `"Users"`） |
| `RowKey` | `"{originalPartitionKey}\|{originalRowKey}"` |
| `DeletedAt` | 删除的 UTC 时间戳 |

这是一个轻量、以追加为主的旁路通道。墓碑写入是一次简单的 upsert，对于批量操作会批处理到 Azure 的 100 个实体事务上限。

在增量备份期间，从每张表导出已修改的实体之后，备份服务会查询 `Tombstones` 表中 `Timestamp > watermark` 的记录。这些记录会以规范化的格式写入备份目录中一个单独的 `_tombstones.jsonl` 文件：

```json
{"Table":"Users","PartitionKey":"u_abc123","RowKey":"profile","DeletedAt":"2026-03-29T14:30:00+00:00"}
```

这意味着增量备份捕获了变更内容的完整图景：新增/修改的实体（来自各表的 JSONL 文件）以及被删除的实体（来自墓碑文件）。

## 合并与汇总

随着时间推移，一个备份目录会积累一次全量备份和许多增量备份。要恢复到当前状态，需要按顺序应用它们全部。**MergeService** 会把它们合并成单个全量备份。

合并算法：

1. 一次加载一张表的全量备份实体集（以约束内存用量）。
2. 按时间顺序将每个增量叠加在其上——以 `(PartitionKey, RowKey)` 为键，较新的值覆盖较旧的值。
3. 应用墓碑：对墓碑文件中的每一个 `(Table, PartitionKey, RowKey)` 元组，从合并后的集合中移除该实体。
4. 将得到的实体集写为一个新的全量备份。

**RollupService** 在此之外包裹了清理：一次成功的合并之后，它会删除旧的全量备份以及所有被折叠进去的增量备份。这可以防止存储用量无上限地增长。

一个典型的生产计划可能如下所示：

- **每小时：** 增量备份
- **每日（凌晨 2 点）：** 全量备份
- **每周：** 汇总（合并上一周的每日 + 每小时增量备份，删除原件）

## 恢复

恢复工具读取一个备份目录，并将实体写回 Azure Table Storage。它支持三种模式：

**Upsert**（默认）：每个实体被插入或替换。具有相同键的现有实体会被覆盖。这是灾难恢复最安全的模式。

**Merge**：每个实体被插入或合并。备份中存在的属性会覆盖现有实体中相应的属性，但存在于线上表却不在备份中的属性会被保留。适用于部分恢复。

**Clean**：在恢复之前，每张目标表中的所有现有实体都会被删除。这会产出备份状态的精确副本，代价是一次（可能很慢的）全表扫描以删除现有数据。

### 类型保真

让 Table Storage 数据经由 JSON 往返的一个关键挑战是保留属性类型。Table Storage 原生支持字符串、整数（Int32/Int64）、双精度浮点数、布尔值、DateTimeOffset、Guid 和二进制。JSON 对其中大多数并没有原生表示。

恢复服务使用启发式方法从其 JSON 字符串表示中恢复类型：

- **DateTimeOffset**：长度为 19-35 个字符、以数字开头且能按 ISO 8601 解析的字符串，会被恢复为 `DateTimeOffset`。
- **Guid**：恰好 36 个字符且能解析为 GUID 的字符串，会被恢复为 `Guid`。
- **数字**：JSON 数字会按 `Int32`、然后 `Int64`、然后 `double` 的顺序依次尝试。
- **布尔值和 null**：直接映射。

这种启发式方法覆盖了 Authagonal 实际的数据模式，无需模式注册表，也无需在备份格式中加入类型注解。

### 错误处理

恢复操作在实体级别是容错的。如果某个单独的实体写入失败（例如由于一次瞬时的 Azure 错误），错误计数会递增，但恢复仍会继续。最终结果会报告每张表的成功数和错误数，并且进程在部分成功时以退出码 `2` 结束——区别于 `0`（完全成功）和 `1`（致命错误）。

## 多租户

Authagonal 支持多租户部署，其中每个租户的表都带有前缀（例如 `acmecorpUsers`、`contosoclients`）。备份和恢复都接受一个 `--prefix` 标志，在与 Azure Table Storage 通信时它会被前置到逻辑表名之前。

这意味着：
- 使用 `--prefix acmecorp` 的备份从 `acmecorpUsers`、`acmecorpClients` 等读取，但写出的文件名为 `Users.jsonl`、`Clients.jsonl`（逻辑名）。
- 使用 `--prefix contoso` 的恢复读取 `Users.jsonl` 并写入 `contosoUsers`。

这使得克隆某个租户的数据、在环境之间迁移，或在不影响其他租户的情况下恢复单个租户变得直截了当。

## 清单

每个备份都包含一个记录以下内容的 `_manifest.json` 文件：

- **BackupId**：带时间戳的标识符（例如 `20260329-120000` 或 `20260329-180000-incr`）
- **Mode**：`"full"` 或 `"incremental"`
- **BackupTimestamp**：备份开始的时间（UTC）
- **Watermark**：对增量备份而言，用于过滤的截止时间戳
- **Compressed**：文件是否经过 GZip 压缩
- **Tables**：表名到实体数量与耗时的字典
- **TombstoneCount**：墓碑记录的数量（仅增量）
- **TotalEntities**：所有表的实体总数
- **DurationSeconds**：备份运行的挂钟时间
- **FileHashes**：每个备份文件的 SHA-256 哈希，用于完整性校验

清单既充当运维仪表盘（备份有多大？花了多长时间？哪些表最大？），又充当安全网（恢复期间的哈希校验能检测出损坏或被篡改的文件）。

## 运维特性

**备份速度**受制于 Azure Table Storage 的查询吞吐量，通常为每张表每秒 5,000-10,000 个实体。一次跨 20 张表、包含 100,000 个实体的全量备份可在一分钟内完成。仅几百个变更实体的增量备份则在数秒内完成。

**内存用量**极小。备份服务将实体直接流式写入磁盘——它从不将整张表加载到内存中。合并服务一次处理一张表，只加载该表的实体集。对于非常大的表（数百万个实体），合并的内存占用与最大的单张表成正比。

**重试策略**配置为指数退避：5 次重试，从 500ms 起，上限为 30 秒。这可以应对 Table Storage 在重负载下施加的瞬时限流。

**试运行**模式（`--dry-run`）枚举实体但不写入任何文件，可用于在投入一次完整运行之前验证连通性并估算备份大小。

## 超越时间戳扫描的扩展

基于 `Timestamp` 的做法在中等规模下很务实，但它的成本与数据总量成正比，而非与变更数量成正比。随着表的增长，每次增量备份 20 次全表扫描变得越来越浪费。自然的演进方向是一张**统一的变更日志表**。

洞见在于：墓碑机制已经为删除验证了这一模式。`Tombstones` 表是一个单一、紧凑、跨表的索引：全部 20 张数据表中的每一次删除都被记录在同一个地方，可按时间戳查询。将其扩展到覆盖所有变更——插入、更新和删除——就能彻底消除扫描数据表的必要。

### 变更日志设计

一张采用按时间分桶的分区键的变更日志表看起来会是这样：

| PartitionKey | RowKey | 属性 |
|---|---|---|
| `2026-03-29T18` | `Users\|u_abc123\|profile` | `Op = "upsert"` |
| `2026-03-29T18` | `Clients\|c_456\|config` | `Op = "upsert"` |
| `2026-03-29T18` | `Users\|u_xyz789\|profile` | `Op = "delete"` |

分区键是一个小时桶，因此找出自上次备份以来的所有变更就变成了一组**分区键点查询**——这是 Table Storage 所支持的最快操作。备份服务将会：

1. 查询变更日志中自水位线以来的所有小时桶分区。这是一次带索引的操作，而非一次扫描。
2. 对每一个 `upsert` 条目，按其精确的 `PartitionKey`/`RowKey` 从数据表中取回当前实体——同样是一次带索引的点读取。
3. 对每一个 `delete` 条目，直接从变更日志中记录墓碑。无需单独的墓碑表。

这使得备份成本与变更数量成正比，而非与数据总量成正比。对一张紧凑索引表的一次查询取代了 20 次全表扫描。它还统一了墓碑机制——变更日志以统一的方式捕获创建、更新和删除，因此单独的 `Tombstones` 表变得多余。

### 为何尚未采用

这个取舍在于写入路径的开销。每个存储中的每一次变更都需要额外向变更日志表写入一次。管道大体已经就绪——`ITombstoneWriter` 已经被注入到每个存储中，并在每次删除时被调用。将其拓宽为一个在 upsert 时也会触发的 `IChangeTracker` 是一次直截了当的重构。

但“直截了当”并不等于“免费”。它会给每一个面向用户的操作增加延迟（一次额外的 Table Storage 写入）、增加存储事务，并引入一个新的一致性问题（如果数据写入成功但变更日志写入失败了怎么办？）。在当前的数据量下，20 次时间戳过滤的扫描在数秒内完成，成本仅为几分之一美分。如果表增长到数百万个实体，变更日志会是正确的举措；但就目前而言，更简单的做法胜出。

## 小结

这套做法刻意保持简单。Authagonal 没有构建复杂的变更数据捕获（CDC）流水线，也没有依赖 Table Storage 可能并不具备的 Azure 专有特性，而是使用了 Azure *确实*保证的那一项元数据——由服务器管理的 `Timestamp`——再结合针对删除的应用层墓碑追踪。

其结果是一个具备如下特性的备份系统：

- 产出人类可读、可移植的 JSONL 文件
- 支持全量与增量模式，并自动管理水位线
- 正确捕获创建、更新*以及*删除
- 透明地处理多租户表前缀
- 组合清晰（合并、汇总、选择性恢复）
- 作为独立工具运行，不依赖于 Authagonal 服务器

存储抽象意味着同一套逻辑可以面向本地磁盘、Azure Blob Storage、S3 或任何其他目的地。这套格式足够简单，以至于即便没有恢复工具，运维人员也能用 `jq` 和 Azure CLI 重建数据。
