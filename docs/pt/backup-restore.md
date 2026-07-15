---
layout: default
title: Backup & Restore
---

# Backup e restauracao

O Authagonal fornece duas ferramentas CLI para fazer backup e restaurar dados do Azure Table Storage. Ambas sao aplicacoes de console .NET no diretorio `tools/`, e ambas sao involucros finos sobre o pacote NuGet `Authagonal.Backup`. Hosts que precisam de backups agendados, multi-tenant ou fora do sistema de arquivos podem usar a biblioteca diretamente (consulte [Usando a biblioteca](#usando-a-biblioteca)).

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
| `--prefix <prefix>` | Prefixo de nome de tabela (para armazenamento multi-tenant) |
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
    _tombstones.jsonl.gz
    _manifest.json
```

Cada arquivo `.jsonl` contem um objeto JSON por linha (um por entidade de tabela). Com `--gzip`, os arquivos sao compactados como `.jsonl.gz`. O arquivo `_manifest.json` registra o id do backup, o carimbo de data/hora, o modo (`full` ou `incremental`), a compressao, a marca d'agua incremental, a contagem de entidades por tabela, a contagem de tombstones, quais tabelas (se houver) foram lidas via change-log (`ChangeLogTables`, null significa cobertura de varredura completa) e os hashes SHA-256 dos arquivos para verificacao de integridade.

Backups incrementais tambem gravam um arquivo `_tombstones.jsonl(.gz)` que registra as exclusoes desde a marca d'agua: uma linha por linha excluida com `Table`, `PartitionKey`, `RowKey` e `DeletedAt`. A restauracao reaplica essas exclusoes para que linhas excluidas nao sejam ressuscitadas (consulte [Reaplicacao de tombstones](#reaplicacao-de-tombstones)).

As entidades fazem round-trip de valores exatamente: cada linha do backup carrega um marcador de formato `"@v"` e uma anotacao `"{column}@odata.type"` explicita (`Edm.Guid`, `Edm.DateTime`, `Edm.Binary`, `Edm.Int64`, `Edm.Double`) para cada coluna que o JSON nao consegue representar sem ambiguidade, portanto a restauracao grava de volta os tipos originais em vez de valores convertidos em string ou reinferidos.

### Verificacao de integridade

Cada manifesto de backup inclui um dicionario `FileHashes` que mapeia nomes de arquivos para os seus hashes SHA-256. Durante a restauracao, a integridade de cada arquivo e verificada contra esses hashes antes de qualquer um dos seus dados ser gravado; um arquivo que falha na verificacao, ou um arquivo de dados ausente do manifesto, aborta a restauracao com um erro. Backups gravados antes de o hashing de integridade existir (sem `FileHashes` no manifesto) nao podem ser verificados e sao restaurados com um aviso ruidoso em vez disso. A verificacao pode ser desativada programaticamente via `RestoreOptions.VerifyIntegrity` (padrao `true`).

### Backups incrementais

Passe `--incremental` para fazer backup apenas das entidades modificadas desde o ultimo backup bem-sucedido. A ferramenta utiliza a propriedade integrada `Timestamp` do Azure Table Storage para filtragem e rastreia a marca d'agua alta em um arquivo `.lastbackup` no diretorio de saida.

Se nenhum arquivo `.lastbackup` existir, a primeira execucao incremental realiza um backup completo.

Cada filtro de `Timestamp` incremental subtrai uma pequena margem de seguranca (`BackupDefaults.WatermarkSkewMargin`, 5 minutos) antes de filtrar. A marca d'agua vem do relogio do chamador, enquanto os carimbos de data/hora das linhas sao aplicados pelo servico de armazenamento, portanto uma mutacao que confirma dentro do desvio de relogio seria de outra forma perdida por esta execucao e por todas as posteriores. Reler a margem custa algumas linhas duplicadas por execucao, que a semantica de upsert da restauracao remove.

### Tabelas padrao

A ferramenta de backup inclui todas as tabelas do Authagonal por padrao (`BackupDefaults.Tables`):

`Users`, `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `ScimGroupRoleMappings`, `Roles`, `Scopes`, `ProvisioningApps`

Tabelas transitorias (`SamlReplayCache`, `OidcStateStore`, `RevokedTokens`) sao excluidas por padrao, pois as suas entradas sao limitadas pelo tempo de vida dos tokens; inclua-as explicitamente com `--tables` se necessario. A tabela de change-log `Tombstones` e tratada separadamente pelo mecanismo de backup e nao deve ser listada.

### As chaves de assinatura sao excluidas por padrao

A tabela `SigningKeys` esta na lista de tabelas padrao mas e **filtrada dos backups por padrao** (`BackupOptions.IncludeSigningKeys`, padrao `false`; a CLI nunca a habilita). Para hosts que usam a fonte de chaves local (armazenada em tabela), esta tabela contem a **chave privada** de assinatura JWT, e grava-la num arquivo de backup em texto simples permitiria que qualquer pessoa que leia o backup forjasse tokens. (Hosts que assinam via HashiCorp Vault Transit nao mantem nenhuma chave privada na tabela, portanto esta preocupacao nao se aplica a eles.)

> ⚠️ So opte por incluir via `BackupOptions.IncludeSigningKeys` quando o alvo do backup estiver ele proprio criptografado em repouso e com acesso controlado. O mesmo se aplica ao resto do backup: com o provedor de segredos de **texto simples** padrao, os backups tambem contem os segredos de clientes OIDC upstream e as sementes TOTP / MFA em texto claro. Consulte [Configuracao → Provedor de Segredos](configuration#secret-provider).

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
| `--prefix <prefix>` | Prefixo de nome de tabela (para armazenamento multi-tenant) |
| `--dry-run` | Mostrar o que seria restaurado sem gravar |

### Modos de restauracao

| Modo | Comportamento |
|---|---|
| `upsert` | Inserir ou substituir cada entidade. Os dados existentes sao sobrescritos. |
| `merge` | Inserir ou mesclar. Propriedades existentes que nao estao no backup sao preservadas. |
| `clean` | Excluir todos os dados existentes em cada tabela antes de restaurar. |

Arquivos de backup compactados com gzip (`.jsonl.gz`) sao detectados e descompactados automaticamente; nenhuma flag adicional e necessaria.

### Reaplicacao de tombstones

Apos os arquivos de dados, a restauracao aplica o arquivo `_tombstones` do backup: cada chave registrada e excluida das tabelas restauradas (`RestoreOptions.ApplyTombstones`, padrao `true`). As exclusoes de um incremental fazem parte do seu estado tanto quanto os seus upserts; ignora-las ressuscitaria linhas excluidas, incluindo as apagadas por GDPR, ao restaurar uma sequencia de completo mais incrementais. Backups completos nao carregam arquivo de tombstones. Ao restaurar um backup completo seguido de incrementais, aplique-os do mais antigo para o mais recente, para que uma recriacao posterior fique apos uma exclusao anterior. O hash do arquivo de tombstones e verificado contra o manifesto como os arquivos de dados.

### Round-trip exato de tipos

Linhas gravadas com o marcador de formato `"@v"` carregam anotacoes de tipo EDM explicitas, portanto a restauracao reconstroi os tipos de coluna originais exatos (`Int64`, `Guid`, `Binary`, `DateTime`, `Double`); uma string sem anotacao e restaurada como string. Arquivos de backup legados sem o marcador recorrem a inferencia baseada em formato, mantida apenas para que backups antigos permanecam restauraveis (a inferencia pode atribuir tipo errado a colunas de string com formato de GUID ou de data).

### Codigos de saida

| Codigo | Significado |
|---|---|
| `0` | Sucesso |
| `1` | Erro (argumentos ausentes, entrada invalida) |
| `2` | Sucesso parcial (algumas entidades tiveram erros) |

## Usando a biblioteca

O pacote NuGet `Authagonal.Backup` expoe as mesmas operacoes programaticamente, para servicos em segundo plano ou orquestracao personalizada:

| Tipo | Proposito |
|---|---|
| `BackupService` | Executa um backup completo ou incremental contra um `TableServiceClient`, gravando num `IBackupTarget` |
| `RestoreService` | Verifica os hashes e grava um backup de volta no Table Storage |
| `MergeService` | Transmite um backup completo mais incrementais (e os seus tombstones) para uma unica visao do estado atual |
| `RollupService` | Consolida incrementais num novo backup completo, opcionalmente excluindo as entradas |
| `BackupOptions` / `RestoreOptions` | Configuracao por execucao |
| `BackupDefaults` | Lista de tabelas padrao e presets de change-log |
| `IBackupSource` / `IBackupTarget` | Abstracoes de armazenamento; `FileSystemBackupSource` / `FileSystemBackupTarget` sao as implementacoes integradas. Implemente `IBackupTarget` para gravar em blob storage ou noutro lugar. |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### Incrementais orientados por change-log

O Azure Table Storage indexa apenas `PartitionKey` e `RowKey`, portanto um backup incremental filtrado por `Timestamp` ainda e uma varredura completa de cada tabela. Para evitar isso, os stores do Authagonal registram cada mutacao num change-log via o seam `IChangeWriter` (`Authagonal.Core`), implementado para o Azure por `TableChangeWriter` (`Authagonal.AzureProvider`). E uma unica tabela fisica, ainda chamada `Tombstones`: PK = o nome logico da tabela, RK = `"{pk}|{rk}"`, uma coluna `Op` de `"U"` (upsert) ou `"D"` (delete) e colunas `OrigPK`/`OrigRK` autoritativas (um `|` dentro do PartitionKey original torna ambigua a divisao do RowKey composto, portanto o leitor do backup confia nas colunas e so recorre a divisao para linhas legadas). Cada chave mantem uma linha (upsert-replace), portanto a ultima operacao numa janela de backup vence.

Com o caminho de change-log habilitado, um backup incremental enumera as entradas de change-log `Op = "U"` de uma tabela desde a marca d'agua e faz point-read de cada linha ativa em vez de varrer a tabela. O recurso e **opcional e desligado por padrao**: `BackupOptions.ChangeLoggedTables` null ou vazio significa que cada tabela permanece no caminho de varredura, portanto o mecanismo e entregue inerte ate uma virada deliberada (um deploy nao pode silenciosamente perder linhas alteradas por codigo anterior a captura). Dois presets:

| Preset | Conteudo |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | As tabelas cujas gravacoes sao totalmente capturadas por change-log |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | O mesmo conjunto mais `Users`. As gravacoes de estado de login de Users nao sao deliberadamente capturadas (caminho quente, baixo valor), portanto este preset **so e seguro quando voce tambem executa o backstop de varredura completa abaixo** |

A propriedade `ChangeLogTables` do manifesto lista quais tabelas uma execucao leu via change-log; null ou vazio significa que a execucao teve cobertura de varredura completa (um backup completo, um incremental de varredura simples ou uma varredura de backstop).

### Backstop de varredura completa

Como a captura de change-log pode perder gravacoes (campos de estado de login, gravadores fora do store, pods executando codigo anterior a captura durante um deploy), combine incrementais de change-log com uma re-varredura completa periodica. Defina `BackupOptions.WatermarkOverride` como o carimbo de data/hora da ultima varredura de cobertura completa e deixe `ChangeLoggedTables` sem definir para essa execucao: o incremental entao filtra por `Timestamp` em toda a janela desde essa varredura, capturando qualquer coisa que o change-log nunca capturou. Um backstop diario junto de incrementais de change-log a cada hora e uma cadencia razoavel. As exclusoes sao a unica classe de mutacao sem auto-recuperacao (uma varredura de linhas ativas nao consegue ver uma linha que ja foi embora), motivo pelo qual os stores gravam o tombstone de exclusao **antes** de excluir a linha de dados.

Todos os filtros incrementais, incluindo o backstop, subtraem `BackupDefaults.WatermarkSkewMargin` (5 minutos) da marca d'agua; chamadores que purgam o change-log apos um backup devem limitar a purga pela mesma margem, ou excluirao linhas de que a proxima execucao ainda precisa.

### Rollups

`RollupService.RollupAsync` mescla um backup completo e os seus incrementais num novo backup completo; `RollupAndCleanAsync` adicionalmente exclui as entradas depois. O parametro opcional `newBackupId` nomeia o resultado (null deriva um id com carimbo de data/hora); um snapshot especialmente retido (por exemplo, um rollup semanal) deve passar o seu id aqui, ja que a retencao baseada em id lista ids fisicos de backup, nao manifestos.

Durante uma mesclagem, os tombstones sao aplicados com ordenacao por carimbo de data/hora: uma exclusao remove uma linha capturada apenas quando o `Timestamp` da linha nao e posterior ao `DeletedAt` do tombstone. Uma chave excluida no inicio da janela e recriada mais tarde tem tanto um tombstone quanto uma captura ativa, e a linha recriada sobrevive ao rollup. Tombstones legados sem `DeletedAt` removem incondicionalmente.

## Docker

A ferramenta de backup fornece um Dockerfile (`tools/Authagonal.Backup/Dockerfile`) para execucao em CI ou sem instalar o SDK .NET:

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

A ferramenta de restauracao nao tem imagem; execute-a com o SDK .NET (`dotnet run --project tools/Authagonal.Restore`).

## Agendamento de backups

Para uso em producao, execute a ferramenta de backup em um agendamento (por exemplo, backup completo diario + incremental a cada hora):

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

Hosts que incorporam a biblioteca normalmente executam incrementais a cada hora com o caminho de change-log ligado, um backstop de varredura completa diario e rollups periodicos para limitar a cadeia incremental.
