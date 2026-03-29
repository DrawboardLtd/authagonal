---
layout: default
title: Backup & Restore
---

# Backup e restauracao

O Authagonal fornece duas ferramentas CLI para fazer backup e restaurar dados do Azure Table Storage. Ambas sao aplicacoes de console .NET no diretorio `tools/`.

## Backup

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### Opcoes

| Option | Descricao |
|---|---|
| `--connection-string <conn>` | String de conexao do Azure Table Storage (ou definir a variavel de ambiente `STORAGE_CONNECTION_STRING`) |
| `--output <dir>` | Diretorio de saida (padrao: `./backups`) |
| `--incremental` | Fazer backup apenas das entidades alteradas desde o ultimo backup |
| `--tables <t1,t2,...>` | Lista de tabelas separadas por virgulas (padrao: todas as tabelas do Authagonal) |
| `--gzip` | Compactar arquivos de backup com gzip (`.jsonl.gz`) |
| `--dry-run` | Mostrar o que seria feito backup sem gravar |

### Formato de saida

Cada backup cria um diretorio com carimbo de data/hora:

```
backups/
  20260329-120000/          (full backup)
    Users.jsonl
    Clients.jsonl
    Grants.jsonl
    ...
    _manifest.json
  20260329-180000-incr/     (incremental, compressed)
    Users.jsonl.gz
    _manifest.json
```

Cada arquivo `.jsonl` contem um objeto JSON por linha (um por entidade de tabela). Com `--gzip`, os arquivos sao compactados como `.jsonl.gz`. O arquivo `_manifest.json` registra o carimbo de data/hora do backup, o modo, a compressao e a contagem de entidades.

### Backups incrementais

Passe `--incremental` para fazer backup apenas das entidades modificadas desde o ultimo backup bem-sucedido. A ferramenta utiliza a propriedade integrada `Timestamp` do Azure Table Storage para filtragem e rastreia a marca d'agua alta em um arquivo `.lastbackup` no diretorio de saida.

Se nenhum arquivo `.lastbackup` existir, a primeira execucao incremental realiza um backup completo.

### Tabelas padrao

A ferramenta de backup inclui todas as tabelas do Authagonal por padrao:

`Users`, `UserEmails`, `UserLogins`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`

Tabelas transitorias (`SamlReplayCache`, `OidcStateStore`) sao excluidas por padrao — inclua-as explicitamente com `--tables` se necessario.

## Restauracao

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### Opcoes

| Option | Descricao |
|---|---|
| `--connection-string <conn>` | String de conexao do Azure Table Storage (ou definir a variavel de ambiente `STORAGE_CONNECTION_STRING`) |
| `--input <dir>` | Diretorio de backup a partir do qual restaurar |
| `--mode <mode>` | Modo de restauracao: `upsert` (padrao), `merge` ou `clean` |
| `--tables <t1,t2,...>` | Lista de tabelas a restaurar separadas por virgulas (padrao: todos os arquivos `.jsonl`/`.jsonl.gz` no backup) |
| `--dry-run` | Mostrar o que seria restaurado sem gravar |

### Modos de restauracao

| Modo | Comportamento |
|---|---|
| `upsert` | Inserir ou substituir cada entidade. Os dados existentes sao sobrescritos. |
| `merge` | Inserir ou mesclar. Propriedades existentes que nao estao no backup sao preservadas. |
| `clean` | Excluir todos os dados existentes em cada tabela antes de restaurar. |

Arquivos de backup compactados com gzip (`.jsonl.gz`) sao detectados e descompactados automaticamente — nenhuma flag adicional e necessaria.

### Codigos de saida

| Codigo | Significado |
|---|---|
| `0` | Sucesso |
| `1` | Erro (argumentos ausentes, entrada invalida) |
| `2` | Sucesso parcial (algumas entidades tiveram erros) |

## Docker

Ambas as ferramentas possuem imagens Docker para execucao em CI ou sem instalar o SDK .NET:

```bash
# Backup
docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  drawboardci/authagonal-backup --output /backups

# Restore
docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  drawboardci/authagonal-restore --input /backups/20260329-120000
```

## Agendamento de backups

Para uso em producao, execute a ferramenta de backup em um agendamento (por exemplo, backup completo diario + incremental a cada hora):

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```
