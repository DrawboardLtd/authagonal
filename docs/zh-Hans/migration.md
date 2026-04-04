---
layout: default
title: 迁移
locale: zh-Hans
---

# 从 Duende IdentityServer 迁移

Authagonal 包含一个迁移工具，用于从 Duende IdentityServer + SQL Server 迁移到 Azure Table Storage。

## 运行迁移

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

或从源代码运行：

```bash
dotnet run --project tools/Authagonal.Migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] [--MigrateRefreshTokens true]
```

## 迁移内容

| 源（SQL Server） | 目标（Table Storage） | 说明 |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Users + UserEmails | 单次 JOIN 查询。声明：given_name、family_name、company、org_id。密码哈希保持原样（BCrypt 在登录时自动升级）。 |
| `AspNetUserLogins` | UserLogins（正向 + 反向索引） | `409 Conflict` = 跳过（幂等） |
| Duende `SamlProviderConfigurations` | SamlProviders + SsoDomains | `AllowedDomains` CSV 拆分为单独的 SSO 域记录 |
| Duende `OidcProviderConfigurations` | OidcProviders + SsoDomains | 相同的域拆分 |
| Duende `Clients` + 子表 | Clients | ClientSecrets、GrantTypes、RedirectUris、PostLogoutRedirectUris、Scopes、CorsOrigins 全部合并为单个实体 |
| Duende `PersistedGrants`（刷新令牌） | Grants + GrantsBySubject | 通过 `--MigrateRefreshTokens true` 启用。仅迁移未过期的令牌。如跳过，用户只需重新登录。 |

## 选项

| 选项 | 默认值 | 描述 |
|---|---|---|
| `--DryRun` | `false` | 记录将要迁移的内容但不写入存储 |
| `--MigrateRefreshTokens` | `false` | 包含活跃的刷新令牌。如为 false，用户在切换后需重新认证。 |

## 幂等性

迁移是幂等的 -- 可以安全地多次运行。现有记录会被 upsert（不会重复）。这允许您：

1. 在切换前数天运行迁移
2. 在接近切换时运行最终增量迁移
3. 如果出现问题可以重新运行

## 签名密钥迁移

尚未自动化。要在切换期间保持现有令牌有效：

1. 从 Duende 导出 RSA 签名密钥（通常在 appsettings 中以 Base64 PKCS8 格式）
2. 将其导入到 `SigningKeys` 表中
3. 在接近切换时执行此操作

## 切换策略

1. 运行用户 + 提供者 + 客户端迁移（可提前数天完成）
2. 在 Authagonal 中播种客户端配置
3. 导入签名密钥（接近切换时）
4. 可选：迁移活跃的刷新令牌
5. 将 Authagonal 部署到预发布环境，进行测试
6. 将现有 IdentityServer 设为维护模式
7. 最终增量迁移
8. DNS 切换（事先将 TTL 设置为 60 秒）
9. 监控 30 分钟
10. 如有问题：切回 DNS（共享签名密钥意味着令牌在两个系统上都有效）
