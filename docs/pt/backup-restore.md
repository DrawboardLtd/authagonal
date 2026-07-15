---
layout: default
title: Backup & Restore
locale: pt
---

# Backup e restauração

O Authagonal fornece duas ferramentas CLI para fazer backup e restaurar dados do Azure Table Storage. Ambas são aplicações de console .NET no diretório `tools/`, e ambas são invólucros finos sobre o pacote NuGet `Authagonal.Backup`. Hosts que precisam de backups agendados, multi-tenant ou fora do sistema de arquivos podem usar a biblioteca diretamente (consulte [Usando a biblioteca](#usando-a-biblioteca)).

## Backup

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### Opções

| Option | Descrição |
|---|---|
| `--connection-string <conn>` | String de conexão do Azure Table Storage (ou definir a variável de ambiente `STORAGE_CONNECTION_STRING`) |
| `--output <dir>` | Diretório de saída (padrão: `./backups`) |
| `--incremental` | Fazer backup apenas das entidades alteradas desde o último backup |
| `--tables <t1,t2,...>` | Lista de tabelas separadas por vírgulas (padrão: todas as tabelas do Authagonal) |
| `--prefix <prefix>` | Prefixo de nome de tabela (para armazenamento multi-tenant) |
| `--gzip` | Compactar arquivos de backup com gzip (`.jsonl.gz`) |
| `--dry-run` | Mostrar o que seria feito backup sem gravar |

### Formato de saída

Cada backup cria um diretório com carimbo de data/hora:

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
    _tombstones.jsonl.gz
    _manifest.json
```

Cada arquivo `.jsonl` contém um objeto JSON por linha (um por entidade de tabela). Com `--gzip`, os arquivos são compactados como `.jsonl.gz`. O arquivo `_manifest.json` registra o id do backup, o carimbo de data/hora, o modo (`full` ou `incremental`), a compressão, a marca d'água incremental, a contagem de entidades por tabela, a contagem de tombstones, quais tabelas (se houver) foram lidas via change-log (`ChangeLogTables`, null significa cobertura de varredura completa) e os hashes SHA-256 dos arquivos para verificação de integridade.

Backups incrementais também gravam um arquivo `_tombstones.jsonl(.gz)` que registra as exclusões desde a marca d'água: uma linha por linha excluída com `Table`, `PartitionKey`, `RowKey` e `DeletedAt`. A restauração reaplica essas exclusões para que linhas excluídas não sejam ressuscitadas (consulte [Reaplicação de tombstones](#reaplicação-de-tombstones)).

As entidades fazem round-trip de valores exatamente: cada linha do backup carrega um marcador de formato `"@v"` e uma anotação `"{column}@odata.type"` explícita (`Edm.Guid`, `Edm.DateTime`, `Edm.Binary`, `Edm.Int64`, `Edm.Double`) para cada coluna que o JSON não consegue representar sem ambiguidade, portanto a restauração grava de volta os tipos originais em vez de valores convertidos em string ou reinferidos.

### Verificação de integridade

Cada manifesto de backup inclui um dicionário `FileHashes` que mapeia nomes de arquivos para os seus hashes SHA-256. Durante a restauração, a integridade de cada arquivo é verificada contra esses hashes antes de qualquer um dos seus dados ser gravado; um arquivo que falha na verificação, ou um arquivo de dados ausente do manifesto, aborta a restauração com um erro. Backups gravados antes de o hashing de integridade existir (sem `FileHashes` no manifesto) não podem ser verificados e são restaurados com um aviso ruidoso em vez disso. A verificação pode ser desativada programaticamente via `RestoreOptions.VerifyIntegrity` (padrão `true`).

### Backups incrementais

Passe `--incremental` para fazer backup apenas das entidades modificadas desde o último backup bem-sucedido. A ferramenta utiliza a propriedade integrada `Timestamp` do Azure Table Storage para filtragem e rastreia a marca d'água alta em um arquivo `.lastbackup` no diretório de saída.

Se nenhum arquivo `.lastbackup` existir, a primeira execução incremental realiza um backup completo.

Cada filtro de `Timestamp` incremental subtrai uma pequena margem de segurança (`BackupDefaults.WatermarkSkewMargin`, 5 minutos) antes de filtrar. A marca d'água vem do relógio do chamador, enquanto os carimbos de data/hora das linhas são aplicados pelo serviço de armazenamento, portanto uma mutação que confirma dentro do desvio de relógio seria de outra forma perdida por esta execução e por todas as posteriores. Reler a margem custa algumas linhas duplicadas por execução, que a semântica de upsert da restauração remove.

### Tabelas padrão

A ferramenta de backup inclui todas as tabelas do Authagonal por padrão (`BackupDefaults.Tables`):

`Users`, `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `ScimGroupRoleMappings`, `Roles`, `Scopes`, `ProvisioningApps`

Tabelas transitórias (`SamlReplayCache`, `OidcStateStore`, `RevokedTokens`) são excluídas por padrão, pois as suas entradas são limitadas pelo tempo de vida dos tokens; inclua-as explicitamente com `--tables` se necessário. A tabela de change-log `Tombstones` é tratada separadamente pelo mecanismo de backup e não deve ser listada.

### As chaves de assinatura são excluídas por padrão

A tabela `SigningKeys` está na lista de tabelas padrão mas é **filtrada dos backups por padrão** (`BackupOptions.IncludeSigningKeys`, padrão `false`; a CLI nunca a habilita). Para hosts que usam a fonte de chaves local (armazenada em tabela), esta tabela contém a **chave privada** de assinatura JWT, e gravá-la num arquivo de backup em texto simples permitiria que qualquer pessoa que leia o backup forjasse tokens. (Hosts que assinam via HashiCorp Vault Transit não mantêm nenhuma chave privada na tabela, portanto esta preocupação não se aplica a eles.)

> ⚠️ Só opte por incluir via `BackupOptions.IncludeSigningKeys` quando o alvo do backup estiver ele próprio criptografado em repouso e com acesso controlado. O mesmo se aplica ao resto do backup: com o provedor de segredos de **texto simples** padrão, os backups também contêm os segredos de clientes OIDC upstream e as sementes TOTP / MFA em texto claro. Consulte [Configuração → Provedor de Segredos](configuration#secret-provider).

## Restauração

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### Opções

| Option | Descrição |
|---|---|
| `--connection-string <conn>` | String de conexão do Azure Table Storage (ou definir a variável de ambiente `STORAGE_CONNECTION_STRING`) |
| `--input <dir>` | Diretório de backup a partir do qual restaurar |
| `--mode <mode>` | Modo de restauração: `upsert` (padrão), `merge` ou `clean` |
| `--tables <t1,t2,...>` | Lista de tabelas a restaurar separadas por vírgulas (padrão: todos os arquivos `.jsonl`/`.jsonl.gz` no backup) |
| `--prefix <prefix>` | Prefixo de nome de tabela (para armazenamento multi-tenant) |
| `--dry-run` | Mostrar o que seria restaurado sem gravar |

### Modos de restauração

| Modo | Comportamento |
|---|---|
| `upsert` | Inserir ou substituir cada entidade. Os dados existentes são sobrescritos. |
| `merge` | Inserir ou mesclar. Propriedades existentes que não estão no backup são preservadas. |
| `clean` | Excluir todos os dados existentes em cada tabela antes de restaurar. |

Arquivos de backup compactados com gzip (`.jsonl.gz`) são detectados e descompactados automaticamente; nenhuma flag adicional é necessária.

### Reaplicação de tombstones

Após os arquivos de dados, a restauração aplica o arquivo `_tombstones` do backup: cada chave registrada é excluída das tabelas restauradas (`RestoreOptions.ApplyTombstones`, padrão `true`). As exclusões de um incremental fazem parte do seu estado tanto quanto os seus upserts; ignorá-las ressuscitaria linhas excluídas, incluindo as apagadas por GDPR, ao restaurar uma sequência de completo mais incrementais. Backups completos não carregam arquivo de tombstones. Ao restaurar um backup completo seguido de incrementais, aplique-os do mais antigo para o mais recente, para que uma recriação posterior fique após uma exclusão anterior. O hash do arquivo de tombstones é verificado contra o manifesto como os arquivos de dados.

### Round-trip exato de tipos

Linhas gravadas com o marcador de formato `"@v"` carregam anotações de tipo EDM explícitas, portanto a restauração reconstrói os tipos de coluna originais exatos (`Int64`, `Guid`, `Binary`, `DateTime`, `Double`); uma string sem anotação é restaurada como string. Arquivos de backup legados sem o marcador recorrem a inferência baseada em formato, mantida apenas para que backups antigos permaneçam restauráveis (a inferência pode atribuir tipo errado a colunas de string com formato de GUID ou de data).

### Códigos de saída

| Código | Significado |
|---|---|
| `0` | Sucesso |
| `1` | Erro (argumentos ausentes, entrada inválida) |
| `2` | Sucesso parcial (algumas entidades tiveram erros) |

## Usando a biblioteca

O pacote NuGet `Authagonal.Backup` expõe as mesmas operações programaticamente, para serviços em segundo plano ou orquestração personalizada:

| Tipo | Propósito |
|---|---|
| `BackupService` | Executa um backup completo ou incremental contra um `TableServiceClient`, gravando num `IBackupTarget` |
| `RestoreService` | Verifica os hashes e grava um backup de volta no Table Storage |
| `MergeService` | Transmite um backup completo mais incrementais (e os seus tombstones) para uma única visão do estado atual |
| `RollupService` | Consolida incrementais num novo backup completo, opcionalmente excluindo as entradas |
| `BackupOptions` / `RestoreOptions` | Configuração por execução |
| `BackupDefaults` | Lista de tabelas padrão e presets de change-log |
| `IBackupSource` / `IBackupTarget` | Abstrações de armazenamento; `FileSystemBackupSource` / `FileSystemBackupTarget` são as implementações integradas. Implemente `IBackupTarget` para gravar em blob storage ou noutro lugar. |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### Incrementais orientados por change-log

O Azure Table Storage indexa apenas `PartitionKey` e `RowKey`, portanto um backup incremental filtrado por `Timestamp` ainda é uma varredura completa de cada tabela. Para evitar isso, os stores do Authagonal registram cada mutação num change-log via o seam `IChangeWriter` (`Authagonal.Core`), implementado para o Azure por `TableChangeWriter` (`Authagonal.AzureProvider`). É uma única tabela física, ainda chamada `Tombstones`: PK = o nome lógico da tabela, RK = `"{pk}|{rk}"`, uma coluna `Op` de `"U"` (upsert) ou `"D"` (delete) e colunas `OrigPK`/`OrigRK` autoritativas (um `|` dentro do PartitionKey original torna ambígua a divisão do RowKey composto, portanto o leitor do backup confia nas colunas e só recorre à divisão para linhas legadas). Cada chave mantém uma linha (upsert-replace), portanto a última operação numa janela de backup vence.

Com o caminho de change-log habilitado, um backup incremental enumera as entradas de change-log `Op = "U"` de uma tabela desde a marca d'água e faz point-read de cada linha ativa em vez de varrer a tabela. O recurso é **opcional e desligado por padrão**: `BackupOptions.ChangeLoggedTables` null ou vazio significa que cada tabela permanece no caminho de varredura, portanto o mecanismo é entregue inerte até uma virada deliberada (um deploy não pode silenciosamente perder linhas alteradas por código anterior à captura). Dois presets:

| Preset | Conteúdo |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | As tabelas cujas gravações são totalmente capturadas por change-log |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | O mesmo conjunto mais `Users`. As gravações de estado de login de Users não são deliberadamente capturadas (caminho quente, baixo valor), portanto este preset **só é seguro quando você também executa o backstop de varredura completa abaixo** |

A propriedade `ChangeLogTables` do manifesto lista quais tabelas uma execução leu via change-log; null ou vazio significa que a execução teve cobertura de varredura completa (um backup completo, um incremental de varredura simples ou uma varredura de backstop).

### Backstop de varredura completa

Como a captura de change-log pode perder gravações (campos de estado de login, gravadores fora do store, pods executando código anterior à captura durante um deploy), combine incrementais de change-log com uma re-varredura completa periódica. Defina `BackupOptions.WatermarkOverride` como o carimbo de data/hora da última varredura de cobertura completa e deixe `ChangeLoggedTables` sem definir para essa execução: o incremental então filtra por `Timestamp` em toda a janela desde essa varredura, capturando qualquer coisa que o change-log nunca capturou. Um backstop diário junto de incrementais de change-log a cada hora é uma cadência razoável. As exclusões são a única classe de mutação sem auto-recuperação (uma varredura de linhas ativas não consegue ver uma linha que já foi embora), motivo pelo qual os stores gravam o tombstone de exclusão **antes** de excluir a linha de dados.

Todos os filtros incrementais, incluindo o backstop, subtraem `BackupDefaults.WatermarkSkewMargin` (5 minutos) da marca d'água; chamadores que purgam o change-log após um backup devem limitar a purga pela mesma margem, ou excluirão linhas de que a próxima execução ainda precisa.

### Rollups

`RollupService.RollupAsync` mescla um backup completo e os seus incrementais num novo backup completo; `RollupAndCleanAsync` adicionalmente exclui as entradas depois. O parâmetro opcional `newBackupId` nomeia o resultado (null deriva um id com carimbo de data/hora); um snapshot especialmente retido (por exemplo, um rollup semanal) deve passar o seu id aqui, já que a retenção baseada em id lista ids físicos de backup, não manifestos.

Durante uma mesclagem, os tombstones são aplicados com ordenação por carimbo de data/hora: uma exclusão remove uma linha capturada apenas quando o `Timestamp` da linha não é posterior ao `DeletedAt` do tombstone. Uma chave excluída no início da janela e recriada mais tarde tem tanto um tombstone quanto uma captura ativa, e a linha recriada sobrevive ao rollup. Tombstones legados sem `DeletedAt` removem incondicionalmente.

## Docker

A ferramenta de backup fornece um Dockerfile (`tools/Authagonal.Backup/Dockerfile`) para execução em CI ou sem instalar o SDK .NET:

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

A ferramenta de restauração não tem imagem; execute-a com o SDK .NET (`dotnet run --project tools/Authagonal.Restore`).

## Agendamento de backups

Para uso em produção, execute a ferramenta de backup em um agendamento (por exemplo, backup completo diário + incremental a cada hora):

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

Hosts que incorporam a biblioteca normalmente executam incrementais a cada hora com o caminho de change-log ligado, um backstop de varredura completa diário e rollups periódicos para limitar a cadeia incremental.
