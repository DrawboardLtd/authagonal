---
layout: default
title: Migração
locale: pt
---

# Migração do Duende IdentityServer

O Authagonal inclui uma ferramenta de migração para mover do Duende IdentityServer + SQL Server para o Azure Table Storage.

## Executar a Migração

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Ou a partir do código-fonte:

```bash
dotnet run --project tools/Authagonal.Migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] [--MigrateRefreshTokens true]
```

## O que é Migrado

| Origem (SQL Server) | Destino (Table Storage) | Notas |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Users + UserEmails | Query JOIN única. Claims: given_name, family_name, company, org_id. Hashes de senha mantidos como estão (BCrypt faz upgrade automático no login). |
| `AspNetUserLogins` | UserLogins (índice direto + reverso) | `409 Conflict` = ignorar (idempotente) |
| Duende `SamlProviderConfigurations` | SamlProviders + SsoDomains | `AllowedDomains` CSV dividido em registos individuais de domínio SSO |
| Duende `OidcProviderConfigurations` | OidcProviders + SsoDomains | Mesma divisão de domínios |
| Duende `Clients` + tabelas filhas | Clients | ClientSecrets, GrantTypes, RedirectUris, PostLogoutRedirectUris, Scopes, CorsOrigins todos fundidos numa única entidade |
| Duende `PersistedGrants` (refresh tokens) | Grants + GrantsBySubject | Opcional via `--MigrateRefreshTokens true`. Apenas tokens não expirados. Se omitido, os utilizadores simplesmente fazem login novamente. |

## Opções

| Opção | Padrão | Descrição |
|---|---|---|
| `--DryRun` | `false` | Registar o que seria migrado sem escrever no armazenamento |
| `--MigrateRefreshTokens` | `false` | Incluir refresh tokens ativos. Se falso, os utilizadores reautenticam-se após a transição. |

## Idempotência

A migração é idempotente — segura para executar múltiplas vezes. Os registos existentes são inseridos/atualizados (não duplicados). Isto permite-lhe:

1. Executar a migração dias antes da transição
2. Executar uma migração delta final perto da transição
3. Re-executar se algo correr mal

## Migração da Chave de Assinatura

Ainda não automatizada. Para manter os tokens existentes válidos durante a transição:

1. Exporte a chave de assinatura RSA do Duende (tipicamente nas appsettings como Base64 PKCS8)
2. Importe-a para a tabela `SigningKeys`
3. Faça isto perto do momento da transição

## Estratégia de Transição

1. Execute a migração de utilizadores + provedores + clientes (pode ser feito dias antes)
2. Semeie as configurações de clientes no Authagonal
3. Importe a chave de assinatura (perto da transição)
4. Opcional: migre os refresh tokens ativos
5. Implante o Authagonal em staging, teste
6. Modo de manutenção no IdentityServer existente
7. Migração delta final
8. Alteração de DNS (defina o TTL para 60s previamente)
9. Monitorize 30 minutos
10. Se houver problemas: reverta o DNS (a chave de assinatura partilhada significa que os tokens funcionam em ambos os sistemas)
