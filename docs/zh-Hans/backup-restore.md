---
layout: default
title: Backup & Restore
---

# 备份与恢复

Authagonal 提供两个 CLI 工具，用于备份和恢复 Azure Table Storage 数据。两者都是 `tools/` 目录中的 .NET 控制台应用程序。

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
    _manifest.json
```

每个 `.jsonl` 文件每行包含一个 JSON 对象（每个表实体一个）。使用 `--gzip` 时，文件将压缩为 `.jsonl.gz`。`_manifest.json` 记录备份时间戳、模式、压缩状态、实体计数，以及用于完整性验证的 SHA-256 文件哈希。

### 完整性验证

每个备份清单包含一个 `FileHashes` 字典，将文件名映射到其 SHA-256 哈希。恢复期间，在写入任何数据之前，会自动根据这些哈希验证文件完整性。如果检测到哈希不匹配，恢复将中止并报错。

### 增量备份

使用 `--incremental` 仅备份自上次成功备份以来修改的实体。该工具使用 Azure Table Storage 内置的 `Timestamp` 属性进行过滤，并在输出目录中的 `.lastbackup` 文件中跟踪高水位标记。

如果不存在 `.lastbackup` 文件，第一次增量运行将执行完整备份。

### 默认表

备份工具默认包含所有 Authagonal 表：

`Users`、`UserEmails`、`UserLogins`、`UserExternalIds`、`Clients`、`Grants`、`GrantsBySubject`、`GrantsByExpiry`、`SsoDomains`、`SamlProviders`、`OidcProviders`、`UserProvisions`、`MfaCredentials`、`MfaChallenges`、`MfaWebAuthnIndex`、`ScimTokens`、`ScimGroups`、`ScimGroupExternalIds`、`Roles`

临时表（`SamlReplayCache`、`OidcStateStore`）默认排除——如需要，请使用 `--tables` 明确包含。

### 签名密钥默认排除

`SigningKeys` 表**默认从备份中排除**（`Backup:IncludeSigningKeys` 默认为 `false`）。对于使用本地（表存储）密钥源的宿主，此表保存着 JWT 签名**私钥**——将其写入纯文本备份文件会让任何读取该备份的人都能伪造令牌。（通过 HashiCorp Vault Transit 签名的宿主不会在表中保留私钥，因此此问题不适用于它们。）

> ⚠️ 仅当备份目标本身静态加密且受访问控制时，才通过 `Backup:IncludeSigningKeys` 选择启用。这同样适用于备份的其余部分：使用默认的**纯文本**密钥提供者时，备份还会以明文包含上游 OIDC 客户端密钥和 TOTP / MFA 种子——参见[配置 → 密钥提供者](configuration#secret-provider)。

恢复时，在写入任何数据之前，会根据清单的 SHA-256 哈希验证文件完整性（参见[完整性验证](#integrity-verification)）；哈希不匹配将中止恢复。

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
| `--dry-run` | 显示将要恢复的内容，但不写入 |

### 恢复模式

| 模式 | 行为 |
|---|---|
| `upsert` | 插入或替换每个实体。现有数据将被覆盖。 |
| `merge` | 插入或合并。备份中没有的现有属性将被保留。 |
| `clean` | 恢复前删除每个表中的所有现有数据。 |

Gzip 压缩的备份文件（`.jsonl.gz`）会被自动检测并解压缩——无需额外标志。

### 退出码

| 代码 | 含义 |
|---|---|
| `0` | 成功 |
| `1` | 错误（缺少参数、无效输入） |
| `2` | 部分成功（某些实体有错误） |

## Docker

两个工具都有 Docker 镜像，可在 CI 中运行或无需安装 .NET SDK：

```bash
# 备份
docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  drawboardci/authagonal-backup --output /backups

# 恢复
docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  drawboardci/authagonal-restore --input /backups/20260329-120000
```

## 计划备份

在生产环境中，按计划运行备份工具（例如每日完整备份 + 每小时增量备份）：

```bash
# 每日完整备份（压缩）
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# 每小时增量备份（压缩）
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```
