---
layout: default
title: Backup & Restore
---

# 备份与恢复

Authagonal 提供两个 CLI 工具，用于备份和恢复 Azure Table Storage 数据。两者都是 `tools/` 目录中的 .NET 控制台应用程序，并且都是 `Authagonal.Backup` NuGet 包之上的轻量封装。需要计划备份、多租户备份或非文件系统备份的宿主可以直接使用该库（参见[使用库](#using-the-library)）。

## 备份

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### 选项

| 选项 | 说明 |
|---|---|
| `--connection-string <conn>` | Azure Table Storage 连接字符串（或设置 `STORAGE_CONNECTION_STRING` 环境变量） |
| `--output <dir>` | 输出目录（默认：`./backups`） |
| `--incremental` | 仅备份自上次备份以来更改的实体 |
| `--tables <t1,t2,...>` | 逗号分隔的表列表（默认：所有 Authagonal 表） |
| `--prefix <prefix>` | 表名前缀（用于多租户存储） |
| `--gzip` | 使用 gzip 压缩备份文件（`.jsonl.gz`） |
| `--dry-run` | 显示将要备份的内容，但不写入 |

### 输出格式

每次备份创建一个带时间戳的目录：

```
backups/
  20260329-120000/          （完整备份）
    Users.jsonl
    Clients.jsonl
    Grants.jsonl
    ...
    _manifest.json
  20260329-180000-incr/     （增量，已压缩）
    Users.jsonl.gz
    _tombstones.jsonl.gz
    _manifest.json
```

每个 `.jsonl` 文件每行包含一个 JSON 对象（每个表实体一个）。使用 `--gzip` 时，文件将压缩为 `.jsonl.gz`。`_manifest.json` 记录备份 id、时间戳、模式（`full` 或 `incremental`）、压缩状态、增量水位标记、按表的实体计数、墓碑计数、哪些表（如有）是通过变更日志读取的（`ChangeLogTables`，null 表示全量扫描覆盖），以及用于完整性验证的 SHA-256 文件哈希。

增量备份还会写入一个 `_tombstones.jsonl(.gz)` 文件，记录自水位标记以来的删除：每个被删除的行占一行，包含 `Table`、`PartitionKey`、`RowKey` 和 `DeletedAt`。恢复时会重放这些记录，使已删除的行不会复活（参见[墓碑重放](#tombstone-replay)）。

实体值可精确往返：每个被备份的行都携带一个 `"@v"` 格式标记，并为每个 JSON 无法无歧义表示的列携带显式的 `"{column}@odata.type"` 注解（`Edm.Guid`、`Edm.DateTime`、`Edm.Binary`、`Edm.Int64`、`Edm.Double`），因此恢复时写回的是原始类型，而不是字符串化或重新推断的值。

### 完整性验证

每个备份清单包含一个 `FileHashes` 字典，将文件名映射到其 SHA-256 哈希。恢复期间，在写入某个文件的任何数据之前，会先根据这些哈希验证该文件的完整性；校验失败的文件，或清单中不存在的数据文件，都会使恢复中止并报错。在完整性哈希机制存在之前写入的备份（清单中没有 `FileHashes`）无法验证，会在恢复时给出醒目警告后继续恢复。验证可以通过编程方式经 `RestoreOptions.VerifyIntegrity` 禁用（默认 `true`）。

### 增量备份

使用 `--incremental` 仅备份自上次成功备份以来修改的实体。该工具使用 Azure Table Storage 内置的 `Timestamp` 属性进行过滤，并在输出目录中的 `.lastbackup` 文件中跟踪高水位标记。

如果不存在 `.lastbackup` 文件，第一次增量运行将执行完整备份。

每个增量的 `Timestamp` 过滤器在过滤前都会减去一个小的安全余量（`BackupDefaults.WatermarkSkewMargin`，5 分钟）。水位标记来自调用方的时钟，而行时间戳由存储服务盖章，因此在时钟偏差窗口内提交的变更否则会被本次以及之后的每次运行遗漏。重读这段余量的代价是每次运行多出几行重复数据，恢复的 upsert 语义会将其去重。

### 默认表

备份工具默认包含所有 Authagonal 表（`BackupDefaults.Tables`）：

`Users`、`UserEmails`、`UserFirstNames`、`UserLastNames`、`UserLogins`、`UserExternalIds`、`UserEmailDomains`、`UserEmailLocalPrefixes`、`Clients`、`Grants`、`GrantsBySubject`、`GrantsByExpiry`、`SigningKeys`、`SsoDomains`、`SamlProviders`、`OidcProviders`、`UserProvisions`、`MfaCredentials`、`MfaChallenges`、`MfaWebAuthnIndex`、`ScimTokens`、`ScimGroups`、`ScimGroupExternalIds`、`ScimGroupRoleMappings`、`Roles`、`Scopes`、`ProvisioningApps`

临时表（`SamlReplayCache`、`OidcStateStore`、`RevokedTokens`）默认排除，因为其条目受令牌生命周期约束；如需要，请使用 `--tables` 明确包含。`Tombstones` 变更日志表由备份引擎单独处理，不应列出。

### 签名密钥默认排除

`SigningKeys` 表在默认表列表中，但**默认从备份中过滤掉**（`BackupOptions.IncludeSigningKeys`，默认 `false`；CLI 从不启用它）。对于使用本地（表存储）密钥源的宿主，此表保存着 JWT 签名**私钥**，将其写入纯文本备份文件会让任何读取该备份的人都能伪造令牌。（通过 HashiCorp Vault Transit 签名的宿主不会在表中保留私钥，因此此问题不适用于它们。）

> ⚠️ 仅当备份目标本身静态加密且受访问控制时，才通过 `BackupOptions.IncludeSigningKeys` 选择启用。这同样适用于备份的其余部分：使用默认的**纯文本**密钥提供者时，备份还会以明文包含上游 OIDC 客户端密钥和 TOTP / MFA 种子。参见[配置 → 密钥提供者](configuration#secret-provider)。

## 恢复

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### 选项

| 选项 | 说明 |
|---|---|
| `--connection-string <conn>` | Azure Table Storage 连接字符串（或设置 `STORAGE_CONNECTION_STRING` 环境变量） |
| `--input <dir>` | 要恢复的备份目录 |
| `--mode <mode>` | 恢复模式：`upsert`（默认）、`merge` 或 `clean` |
| `--tables <t1,t2,...>` | 逗号分隔的要恢复的表列表（默认：备份中所有 `.jsonl`/`.jsonl.gz` 文件） |
| `--prefix <prefix>` | 表名前缀（用于多租户存储） |
| `--dry-run` | 显示将要恢复的内容，但不写入 |

### 恢复模式

| 模式 | 行为 |
|---|---|
| `upsert` | 插入或替换每个实体。现有数据将被覆盖。 |
| `merge` | 插入或合并。备份中没有的现有属性将被保留。 |
| `clean` | 恢复前删除每个表中的所有现有数据。 |

Gzip 压缩的备份文件（`.jsonl.gz`）会被自动检测并解压缩；无需额外标志。

### 墓碑重放

在数据文件之后，恢复会应用备份的 `_tombstones` 文件：每个被记录的键都会从已恢复的表中删除（`RestoreOptions.ApplyTombstones`，默认 `true`）。增量备份的删除与其 upsert 同样是其状态的一部分；跳过它们会在恢复“全量 + 增量”序列时使已删除的行复活，包括已按 GDPR 抹除的行。完整备份不携带墓碑文件。恢复一个完整备份及其后续增量时，请按最旧优先的顺序应用，使较晚的重建落在较早的删除之后。墓碑文件的哈希与数据文件一样会根据清单进行验证。

### 精确类型往返

带有 `"@v"` 格式标记写入的行携带显式的 EDM 类型注解，因此恢复能重建完全一致的原始列类型（`Int64`、`Guid`、`Binary`、`DateTime`、`Double`）；未加注解的字符串按字符串恢复。没有该标记的旧版备份文件回退到基于形态的推断，保留它只是为了让旧备份仍可恢复（推断可能把形如 GUID 或日期的字符串列误判类型）。

### 退出码

| 代码 | 含义 |
|---|---|
| `0` | 成功 |
| `1` | 错误（缺少参数、无效输入） |
| `2` | 部分成功（某些实体有错误） |

## 使用库

`Authagonal.Backup` NuGet 包以编程方式暴露相同的操作，供后台服务或自定义编排使用：

| 类型 | 用途 |
|---|---|
| `BackupService` | 针对 `TableServiceClient` 运行完整或增量备份，写入 `IBackupTarget` |
| `RestoreService` | 验证哈希并将备份写回 Table Storage |
| `MergeService` | 将一个完整备份加若干增量（及其墓碑）流式合成为一个当前状态视图 |
| `RollupService` | 将增量折叠为一个新的完整备份，可选择删除输入 |
| `BackupOptions` / `RestoreOptions` | 按次运行的配置 |
| `BackupDefaults` | 默认表列表与变更日志预设 |
| `IBackupSource` / `IBackupTarget` | 存储抽象；`FileSystemBackupSource` / `FileSystemBackupTarget` 是内置实现。实现 `IBackupTarget` 即可写入 Blob 存储或其他位置。 |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### 变更日志驱动的增量备份

Azure Table Storage 只对 `PartitionKey` 和 `RowKey` 建索引，因此按 `Timestamp` 过滤的增量备份仍然是对每个表的全量扫描。为避免这一点，Authagonal 的存储层通过 `IChangeWriter` 接缝（`Authagonal.Core`）在变更日志中记录每次变更，Azure 侧的实现是 `TableChangeWriter`（`Authagonal.AzureProvider`）。它是一张物理表，名字仍为 `Tombstones`：PK = 逻辑表名，RK = `"{pk}|{rk}"`，`Op` 列为 `"U"`（upsert）或 `"D"`（删除），并带有权威的 `OrigPK`/`OrigRK` 列（原始 PartitionKey 中若含 `|`，拆分复合 RowKey 就有歧义，因此备份读取端信任这两列，仅对旧行回退到拆分）。每个键只保留一行（upsert 替换），因此一个备份窗口内的最后一次操作胜出。

启用变更日志路径后，增量备份会枚举某表自水位标记以来 `Op = "U"` 的变更日志条目，并对每个活动行进行点读，而不是扫描整张表。该功能**为可选启用，默认关闭**：`BackupOptions.ChangeLoggedTables` 为 null 或空表示每个表都留在扫描路径上，因此该机制以惰性状态发布，直到刻意切换（部署不会悄然遗漏由捕获前代码写入的行）。有两个预设：

| 预设 | 内容 |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | 其写入已被变更日志完整捕获的表 |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | 同一集合加上 `Users`。Users 的登录状态写入被刻意不捕获（热路径、低价值），因此该预设**仅在同时运行下述全量扫描兜底时才安全** |

清单的 `ChangeLogTables` 属性列出本次运行通过变更日志读取的表；null 或空表示该次运行具有全量扫描覆盖（完整备份、普通扫描增量或兜底扫描）。

### 全量扫描兜底

由于变更日志捕获可能遗漏写入（登录状态字段、非存储层写入者、部署期间运行捕获前代码的 Pod），应将变更日志增量与周期性的全量重扫配对。为该次运行把 `BackupOptions.WatermarkOverride` 设为上次全量覆盖扫描的时间戳，并保持 `ChangeLoggedTables` 未设置：增量随即在自那次扫描以来的整个窗口上按 `Timestamp` 过滤，捕获所有变更日志从未记录的内容。每日一次兜底搭配每小时的变更日志增量是合理的节奏。删除是唯一没有自愈能力的变更类别（活动行扫描看不到已消失的行），这也是存储层在删除数据行**之前**先写删除墓碑的原因。

所有增量过滤器（包括兜底）都会从水位标记中减去 `BackupDefaults.WatermarkSkewMargin`（5 分钟）；在备份后清理变更日志的调用方必须用同一余量约束清理范围，否则会删掉下次运行仍然需要的行。

### 汇总（Rollup）

`RollupService.RollupAsync` 将一个完整备份及其增量合并为一个新的完整备份；`RollupAndCleanAsync` 在此之后还会删除输入。可选参数 `newBackupId` 为结果命名（null 则派生时间戳 id）；需要特殊保留的快照（例如每周汇总）必须在此传入其 id，因为基于 id 的保留策略列出的是物理备份 id，而非清单。

合并期间，墓碑按时间戳顺序应用：仅当行的 `Timestamp` 不晚于墓碑的 `DeletedAt` 时，删除才会移除已捕获的行。一个在窗口早期被删除、之后又被重建的键会同时拥有墓碑和活动捕获，重建的行在汇总中得以保留。没有 `DeletedAt` 的旧版墓碑则无条件移除。

## Docker

备份工具附带一个 Dockerfile（`tools/Authagonal.Backup/Dockerfile`），用于在 CI 中运行或无需安装 .NET SDK：

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

恢复工具没有镜像；请使用 .NET SDK 运行（`dotnet run --project tools/Authagonal.Restore`）。

## 计划备份

在生产环境中，按计划运行备份工具（例如每日完整备份 + 每小时增量备份）：

```bash
# 每日完整备份（压缩）
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# 每小时增量备份（压缩）
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

嵌入该库的宿主通常运行开启变更日志路径的每小时增量、每日一次全量扫描兜底，以及周期性汇总以约束增量链的长度。
