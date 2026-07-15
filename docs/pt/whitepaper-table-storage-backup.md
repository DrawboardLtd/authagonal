---
layout: default
title: Table Storage Backup Whitepaper
locale: pt
---

# Fazendo Backup do Azure Table Storage: Uma Abordagem Prática

**Como o Authagonal implementa backup completo e incremental para um store NoSQL sem esquema**

---

## O Problema

O Azure Table Storage é um store chave-valor económico e massivamente escalável, mas não oferece nenhuma facilidade de backup nativa. Não há snapshots, não há restauração point-in-time, não há botão de exportação. Se uma implantação defeituosa corromper dados, ou um operador eliminar acidentalmente uma tabela, a recuperação depende inteiramente daquilo que você mesmo construiu.

Para uma plataforma de identidade como o Authagonal (onde as tabelas guardam utilizadores, credenciais, concessões OAuth, chaves de assinatura, configurações de SSO e estado de provisionamento SCIM), as apostas são altas. Perder estes dados não apenas quebra uma aplicação; deixa as pessoas trancadas do lado de fora.

Este documento descreve a estratégia de backup que o Authagonal usa: como exporta dados, como os backups incrementais funcionam apesar do modelo de consulta limitado do Table Storage, como as exclusões são rastreadas e como as peças se compõem num pipeline de backup pronto para produção.

## Objetivos de Design

1. **Backups completos e incrementais.** Um backup completo diário é suficiente para implantações pequenas, mas em escala, incrementais a cada hora mantêm a janela de backup curta e os custos de armazenamento baixos.
2. **Round-trip fiel.** Cada propriedade de entidade (strings, inteiros, booleanos, DateTimeOffsets, GUIDs, binário) deve sobreviver a um ciclo de backup/restauração sem coerção de tipo ou perda de dados.
3. **Suporte multi-tenant.** O Authagonal usa prefixos de nome de tabela para isolar tenants (por exemplo, `acmecorpUsers`, `acmecorpClients`). O backup e a restauração devem estar cientes do prefixo para que uma única conta de armazenamento possa hospedar muitos tenants com agendamentos de backup independentes.
4. **Armazenamento plugável.** Os backups devem funcionar para um sistema de arquivos local durante o desenvolvimento e para blob storage (ou qualquer outro alvo) em produção, sem alterar a lógica central.
5. **Saída legível por humanos.** Quando algo corre mal, um operador deve conseguir abrir um arquivo de backup num editor de texto e ver o que há nele.

## Arquitetura

O sistema de backup é estruturado como uma biblioteca .NET (`Authagonal.Backup`) com invólucros CLI finos para as operações de backup e restauração. A biblioteca é separada do servidor principal do Authagonal para que possa ser usada como uma ferramenta autónoma, num contêiner Docker ou incorporada num job agendado.

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

### Abstração de Armazenamento

Os serviços centrais nunca tocam no sistema de arquivos diretamente. Operam contra duas interfaces:

**IBackupTarget** fornece quatro operações: abrir um stream gravável para um arquivo de backup, gravar um manifesto, obter a última marca d'água (para o agendamento incremental) e definir uma nova marca d'água.

**IBackupSource** fornece o lado de leitura: ler um manifesto, abrir um stream legível, listar os IDs de backup cronologicamente, listar os arquivos dentro de um backup e eliminar um backup.

As implementações de sistema de arquivos são diretas (diretórios com carimbo de data/hora com arquivos JSONL dentro), mas a abstração significa que trocar para Azure Blob Storage ou S3 exige implementar apenas estas duas interfaces.

## Backup Completo

Um backup completo itera sobre cada tabela do Authagonal, consulta todas as entidades e grava-as em arquivos JSONL (um objeto JSON por linha, um arquivo por tabela).

O processo de backup:

1. Gera um ID de backup a partir do carimbo de data/hora UTC atual (por exemplo, `20260329-120000`).
2. Para cada uma das 20 tabelas padrão do Authagonal, consulta o `QueryAsync<TableEntity>` do SDK do Azure Table Storage com um tamanho de página de 1.000.
3. Serializa cada entidade num dicionário JSON plano, preservando todas as propriedades, incluindo as propriedades de sistema (`PartitionKey`, `RowKey`, `Timestamp`, `ETag`).
4. Grava cada entidade serializada como uma única linha em `{TableName}.jsonl` (ou `{TableName}.jsonl.gz` se a compressão estiver habilitada).
5. Regista as contagens de entidades por tabela e as durações num manifesto (`_manifest.json`).
6. Atualiza o arquivo de marca d'água `.lastbackup` com a hora de início do backup.

As tabelas que não existem na conta de armazenamento são silenciosamente ignoradas (o HTTP 404 é capturado e ignorado). Tabelas transitórias como `SamlReplayCache` e `OidcStateStore` são excluídas por padrão, já que o seu conteúdo é efémero.

### Formato de Saída

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

Uma única linha em `Users.jsonl` parece-se com:

```json
{"PartitionKey":"u_abc123","RowKey":"profile","Timestamp":"2026-03-28T09:14:22+00:00","ETag":"W/\"...\"","Email":"alice@example.com","DisplayName":"Alice","CreatedAt":"2025-11-01T00:00:00+00:00"}
```

O JSONL foi escolhido em vez de CSV ou de um formato binário porque preserva a natureza sem esquema e heterogénea das entidades do Table Storage (entidades diferentes na mesma tabela podem ter propriedades diferentes), é transmitível por stream (sem necessidade de bufferizar a tabela inteira em memória) e é diretamente inspecionável com ferramentas padrão como o `jq` ou qualquer editor de texto.

### Compressão

Quando a flag `--gzip` está definida, cada arquivo JSONL é envolvido num stream GZip em `CompressionLevel.Optimal` antes de gravar. A extensão do arquivo muda para `.jsonl.gz`. A ferramenta de restauração deteta o GZip automaticamente ao inspecionar os magic bytes (`0x1f 0x8b`) no início de cada arquivo, portanto nenhuma flag é necessária durante a restauração.

## Backup Incremental

### O Truque do Timestamp

O Azure Table Storage mantém automaticamente uma propriedade `Timestamp` em cada entidade, atualizada em cada insert ou replace. Esta é uma propriedade gerida pelo servidor: as aplicações não a podem definir. O sistema de backup explora isto filtrando as consultas para `Timestamp gt datetime'{watermark}'`, onde a marca d'água é a hora de início do último backup bem-sucedido.

Isto significa que um backup incremental só descarrega entidades que foram criadas ou modificadas desde a execução anterior. Para um sistema com 500.000 entidades onde 200 mudaram na última hora, o backup incremental transfere 200 linhas em vez de 500.000.

A marca d'água é armazenada num arquivo `.lastbackup` no diretório raiz de backup. Se o arquivo não existir (primeira execução, ou após uma limpeza manual), o backup recorre a uma exportação completa. Os IDs de backup incremental incluem um sufixo `-incr` (por exemplo, `20260329-180000-incr`) e o manifesto regista `"mode": "incremental"` com o valor de marca d'água que foi usado para a filtragem.

### Custo do Filtro de Timestamp

Vale a pena ser honesto sobre uma limitação: `Timestamp` não é indexado. O Azure Table Storage indexa apenas `PartitionKey` e `RowKey`. Um filtro em `Timestamp gt datetime'...'` resulta numa varredura completa da tabela: o Azure lê cada entidade do lado do servidor e avalia o predicado antes de retornar as correspondências. A filtragem reduz a transferência de dados (apenas as entidades alteradas atravessam a rede), mas não o custo de leitura do lado do servidor.

Mais importante, a abordagem atual varre **todas as 20 tabelas** individualmente, mesmo que apenas uma tabela tenha tido alterações. Isso são 20 varreduras completas de tabela por backup incremental, independentemente de quão poucas entidades realmente mudaram.

Nos volumes típicos de dados de identidade do Authagonal (dezenas de milhares de entidades, não milhões), isto é perfeitamente aceitável: as varreduras são rápidas, as leituras são baratas ($0,00036 por 10.000 transações) e a operação é apenas de leitura, sem impacto no tráfego ativo. A secção sobre [escalando além das varreduras por timestamp](#escalando-além-das-varreduras-por-timestamp) discute como isto poderia evoluir.

### O Problema da Exclusão

O filtro `Timestamp` captura elegantemente inserts e updates, mas não consegue capturar exclusões. Uma entidade excluída simplesmente desaparece: não há `Timestamp` para filtrar, nenhum tombstone deixado para trás pelo próprio Table Storage.

O Authagonal resolve isto com rastreamento de tombstones ao nível da aplicação.

## Rastreamento de Tombstones

Cada data store no Authagonal (utilizadores, clientes, concessões, chaves de assinatura, domínios de SSO, provedores SAML/OIDC, credenciais MFA, recursos SCIM, roles) aceita uma dependência `ITombstoneWriter` opcional. Quando um store elimina uma entidade, grava um registo de tombstone numa tabela `Tombstones` dedicada:

| Coluna | Valor |
|---|---|
| `PartitionKey` | Nome lógico da tabela (por exemplo, `"Users"`) |
| `RowKey` | `"{originalPartitionKey}\|{originalRowKey}"` |
| `DeletedAt` | Carimbo de data/hora UTC da exclusão |

Este é um canal lateral leve, maioritariamente de anexação. A gravação do tombstone é um upsert simples, agrupado até ao limite de transação de 100 entidades do Azure para operações em lote.

Durante um backup incremental, após exportar as entidades modificadas de cada tabela, o serviço de backup consulta a tabela `Tombstones` por registos com `Timestamp > watermark`. Estes são gravados num arquivo `_tombstones.jsonl` separado no diretório de backup, com um formato normalizado:

```json
{"Table":"Users","PartitionKey":"u_abc123","RowKey":"profile","DeletedAt":"2026-03-29T14:30:00+00:00"}
```

Isto significa que um backup incremental captura um retrato completo do que mudou: entidades adicionadas/modificadas (a partir dos arquivos JSONL por tabela) e entidades excluídas (a partir do arquivo de tombstones).

## Mesclagem e Rollup

Com o tempo, um diretório de backup acumula um backup completo e muitos incrementais. Para restaurar ao estado atual, todos eles precisariam de ser aplicados em ordem. O **MergeService** consolida-os num único backup completo.

O algoritmo de mesclagem:

1. Carrega o conjunto de entidades do backup completo, uma tabela de cada vez (para limitar o uso de memória).
2. Sobrepõe cada incremental por cima em ordem cronológica: os valores mais recentes sobrescrevem os mais antigos, indexados por `(PartitionKey, RowKey)`.
3. Aplica os tombstones: para cada tupla `(Table, PartitionKey, RowKey)` nos arquivos de tombstone, remove a entidade do conjunto mesclado.
4. Grava o conjunto de entidades resultante como um novo backup completo.

O **RollupService** envolve isto com limpeza: após uma mesclagem bem-sucedida, elimina o backup completo antigo e todos os incrementais que foram incorporados. Isto impede que o uso de armazenamento cresça sem limite.

Um agendamento típico de produção poderia ser assim:

- **A cada hora:** Backup incremental
- **Diariamente (2h):** Backup completo
- **Semanalmente:** Rollup (mescla os incrementais diários + horários da semana anterior, elimina os originais)

## Restauração

A ferramenta de restauração lê um diretório de backup e grava as entidades de volta no Azure Table Storage. Suporta três modos:

**Upsert** (padrão): Cada entidade é inserida ou substituída. Entidades existentes com a mesma chave são sobrescritas. Este é o modo mais seguro para recuperação de desastres.

**Merge**: Cada entidade é inserida ou mesclada. As propriedades presentes no backup sobrescrevem as propriedades correspondentes na entidade existente, mas as propriedades que existem na tabela ativa mas não no backup são preservadas. Útil para restaurações parciais.

**Clean**: Todas as entidades existentes em cada tabela de destino são excluídas antes de restaurar. Isto produz uma réplica exata do estado do backup, ao custo de uma varredura completa de tabela (potencialmente lenta) para excluir os dados existentes.

### Fidelidade de Tipos

Um desafio central ao fazer round-trip de dados do Table Storage através de JSON é preservar os tipos de propriedade. O Table Storage suporta nativamente strings, inteiros (Int32/Int64), doubles, booleanos, DateTimeOffset, Guid e binário. O JSON não tem representação nativa para a maioria destes.

O serviço de restauração usa heurísticas para recuperar os tipos a partir da sua representação em string JSON:

- **DateTimeOffset**: Strings com 19-35 caracteres de comprimento, que começam com um dígito e são interpretadas como ISO 8601 são restauradas como `DateTimeOffset`.
- **Guid**: Strings com exatamente 36 caracteres e que são interpretadas como um GUID são restauradas como `Guid`.
- **Números**: Os números JSON são tentados como `Int32`, depois `Int64`, depois `double`, nessa ordem.
- **Booleanos e nulos**: Mapeiam diretamente.

Esta abordagem heurística cobre os padrões de dados reais do Authagonal sem exigir um registo de esquema ou anotações de tipo no formato de backup.

### Tratamento de Erros

As operações de restauração são tolerantes a falhas ao nível da entidade. Se uma entidade individual falhar ao gravar (por exemplo, devido a um erro transitório do Azure), a contagem de erros é incrementada mas a restauração continua. O resultado final reporta as contagens de sucesso e de erro por tabela, e o processo termina com o código `2` para sucesso parcial, distinto de `0` (sucesso completo) e `1` (erro fatal).

## Multi-Tenancy

O Authagonal suporta implantações multi-tenant onde as tabelas de cada tenant têm prefixo (por exemplo, `acmecorpUsers`, `contosoclients`). Tanto o backup quanto a restauração aceitam uma flag `--prefix` que é anteposta aos nomes lógicos de tabela ao comunicar com o Azure Table Storage.

Isto significa:
- O backup com `--prefix acmecorp` lê de `acmecorpUsers`, `acmecorpClients`, etc., mas grava arquivos com os nomes `Users.jsonl`, `Clients.jsonl` (nomes lógicos).
- A restauração com `--prefix contoso` lê `Users.jsonl` e grava em `contosoUsers`.

Isto torna simples clonar os dados de um tenant, migrar entre ambientes ou restaurar um tenant sem afetar os outros.

## Manifesto

Cada backup inclui um arquivo `_manifest.json` que regista:

- **BackupId**: Identificador com carimbo de data/hora (por exemplo, `20260329-120000` ou `20260329-180000-incr`)
- **Mode**: `"full"` ou `"incremental"`
- **BackupTimestamp**: Quando o backup começou (UTC)
- **Watermark**: Para incrementais, o carimbo de data/hora de corte usado para a filtragem
- **Compressed**: Se os arquivos estão compactados com GZip
- **Tables**: Um dicionário de nomes de tabela para contagens de entidades e durações
- **TombstoneCount**: Número de registos de tombstone (apenas incremental)
- **TotalEntities**: Contagem agregada de entidades em todas as tabelas
- **DurationSeconds**: Tempo de relógio para a execução do backup
- **FileHashes**: Hashes SHA-256 de cada arquivo de backup para verificação de integridade

O manifesto serve tanto como um painel operacional (qual foi o tamanho do backup? quanto tempo demorou? quais tabelas são as maiores?) quanto como uma rede de segurança (a verificação de hash durante a restauração deteta arquivos corrompidos ou adulterados).

## Características Operacionais

**A velocidade de backup** é limitada pelo débito de consulta do Azure Table Storage, que é tipicamente de 5.000-10.000 entidades por segundo por tabela. Um backup completo de 100.000 entidades em 20 tabelas completa-se em menos de um minuto. Os backups incrementais de algumas centenas de entidades alteradas terminam em segundos.

**O uso de memória** é mínimo. O serviço de backup transmite as entidades diretamente para o disco: nunca carrega uma tabela inteira em memória. O serviço de mesclagem processa uma tabela de cada vez, carregando apenas o conjunto de entidades dessa tabela. Para tabelas muito grandes (milhões de entidades), a pegada de memória da mesclagem é proporcional à maior tabela individual.

**A política de repetição** é configurada com backoff exponencial: 5 repetições, começando em 500ms, limitadas a 30 segundos. Isto cobre a limitação transitória que o Table Storage aplica sob carga pesada.

**O modo dry run** (`--dry-run`) enumera as entidades sem gravar nenhum arquivo, útil para validar a conectividade e estimar o tamanho do backup antes de se comprometer com uma execução completa.

## Escalando Além das Varreduras por Timestamp

A abordagem baseada em `Timestamp` é pragmática em escala moderada, mas o seu custo é proporcional ao tamanho total dos dados, não ao número de alterações. À medida que as tabelas crescem, 20 varreduras completas de tabela por backup incremental tornam-se cada vez mais desperdiçadoras. A evolução natural é uma **tabela de change log unificada**.

A ideia é que o mecanismo de tombstones já comprova este padrão para as exclusões. A tabela `Tombstones` é um índice único, compacto e cross-table: cada exclusão em todas as 20 tabelas de dados é registada num só lugar, consultável por timestamp. Estender isto para cobrir todas as mutações (inserts, updates e deletes) eliminaria por completo a necessidade de varrer as tabelas de dados.

### Design do Change Log

Uma tabela de change log com chaves de partição agrupadas por tempo pareceria assim:

| PartitionKey | RowKey | Properties |
|---|---|---|
| `2026-03-29T18` | `Users\|u_abc123\|profile` | `Op = "upsert"` |
| `2026-03-29T18` | `Clients\|c_456\|config` | `Op = "upsert"` |
| `2026-03-29T18` | `Users\|u_xyz789\|profile` | `Op = "delete"` |

A chave de partição é um bucket de hora, portanto encontrar todas as alterações desde o último backup torna-se um conjunto de **consultas pontuais por chave de partição**, a operação mais rápida que o Table Storage suporta. O serviço de backup faria:

1. Consultar o change log por todas as partições de bucket de hora desde a marca d'água. Esta é uma operação indexada, não uma varredura.
2. Para cada entrada `upsert`, buscar a entidade atual na tabela de dados pela sua `PartitionKey`/`RowKey` exata, também uma leitura pontual indexada.
3. Para cada entrada `delete`, registar o tombstone diretamente a partir do change log. Sem necessidade de uma tabela de tombstones separada.

Isto torna o custo do backup proporcional ao número de alterações, não ao tamanho total dos dados. Uma consulta contra uma tabela de índice compacta substitui 20 varreduras completas de tabela. Também unifica o mecanismo de tombstones: o change log captura criações, atualizações e exclusões de modo uniforme, portanto a tabela `Tombstones` separada torna-se redundante.

### Porque Ainda Não

O compromisso é a sobrecarga no caminho de escrita. Cada mutação em cada store precisaria de uma gravação adicional na tabela de change log. A infraestrutura já está quase toda lá: o `ITombstoneWriter` já é injetado em cada store e chamado em cada exclusão. Alargá-lo para um `IChangeTracker` que dispara também em upserts é uma refatoração direta.

Mas "direto" não é "grátis". Adiciona latência a cada operação voltada ao utilizador (uma gravação extra no Table Storage), aumenta as transações de armazenamento e introduz uma nova preocupação de consistência (e se a gravação dos dados tiver sucesso mas a gravação do change log falhar?). Nos volumes atuais, as 20 varreduras filtradas por timestamp completam-se em segundos e custam frações de um cêntimo. O change log seria a jogada certa se as tabelas crescessem para milhões de entidades, mas por agora, a abordagem mais simples vence.

## Resumo

A abordagem é deliberadamente simples. Em vez de construir um pipeline complexo de change-data-capture ou depender de funcionalidades específicas do Azure que podem não existir para o Table Storage, o Authagonal usa a única peça de metadados que o Azure *de facto* garante, o `Timestamp` gerido pelo servidor, combinada com o rastreamento de tombstones ao nível da aplicação para as exclusões.

O resultado é um sistema de backup que:

- Produz arquivos JSONL portáteis e legíveis por humanos
- Suporta modos completo e incremental com gestão automática de marca d'água
- Captura corretamente criações, atualizações *e* exclusões
- Trata o prefixo de tabela multi-tenant de forma transparente
- Compõe-se de forma limpa (mesclagem, rollup, restauração seletiva)
- Executa como uma ferramenta autónoma sem dependência do servidor do Authagonal

A abstração de armazenamento significa que a mesma lógica pode ter como alvo o disco local, o Azure Blob Storage, o S3 ou qualquer outro destino. O formato é suficientemente simples para que, mesmo sem a ferramenta de restauração, um operador pudesse reconstruir os dados com o `jq` e a Azure CLI.
