---
layout: default
title: Escalabilidade
locale: pt
---

# Escalabilidade

Authagonal é projetado para escalar tanto verticalmente quanto horizontalmente sem configuração especial.

## Sem estado por design

Todo o estado persistente é armazenado no store de tabelas subjacente: Azure Table Storage, ou DynamoDB no backend AWS. Não há estado em processo que exija sessões persistentes ou coordenação entre instâncias:

- **Chaves de assinatura**: carregadas do Table Storage, atualizadas a cada hora
- **Códigos de autorização e tokens de atualização**: armazenados no Table Storage com aplicação de uso único
- **Prevenção de replay SAML**: IDs de requisição são rastreados no Table Storage com exclusão atômica
- **OIDC state e verificadores PKCE**: armazenados no Table Storage
- **Configuração de clientes e provedores**: obtida por requisição do Table Storage

## Criptografia de cookies (Data Protection)

As chaves de Data Protection do ASP.NET Core são automaticamente persistidas no Azure Blob Storage ao usar uma string de conexão real do Azure Storage. Isso significa que cookies assinados por uma instância podem ser descriptografados por qualquer outra instância, sem necessidade de sessões persistentes.

Para desenvolvimento local com Azurite, as chaves de Data Protection utilizam o armazenamento padrão baseado em arquivos.

Você também pode apontar para uma URI de blob explícita via configuração (o caminho de identidade gerida, preferido em produção):

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

No backend AWS, passe um cliente S3 + bucket a `AddAuthagonalAwsStorage` para persistir o conjunto de chaves no S3; sem isso, o conjunto de chaves fica em memória e os cookies quebram na reinicialização e entre nós. Consulte [Instalação → backend AWS](installation#aws-backend).

## Caches por instância

Um pequeno número de valores lidos com frequência e que mudam lentamente são armazenados em cache na memória por instância para reduzir as viagens de ida e volta ao Table Storage:

| Dados | Duração do cache | Impacto da obsolescência |
|---|---|---|
| Documentos de descoberta OIDC | 60 minutos (configurável) | Atraso na detecção de rotação de chaves do IdP |
| Metadados do SAML IdP | 60 minutos (configurável) | Mesmo |
| Origens CORS permitidas | 60 minutos (configurável) | Novas origens levam até uma hora para propagar |

Esses caches são aceitáveis para uso em produção. Todas as durações são configuráveis através da seção de configuração `Cache`, consulte [Configuração](configuration). Se você precisar de propagação imediata, reinicie as instâncias afetadas.

## Limitação de taxa

Os endpoints propensos a abuso (registo por IP, redefinição de senha por e-mail de destino, SCIM por cliente, registo dinâmico de clientes por IP; consulte [Configuração → Limitação de Taxa](configuration#rate-limiting)) são protegidos por um limitador de taxa integrado.

Os limites são aplicados **em processo por nó** por trás do seam `IRateLimiter`, portanto com N instâncias o teto efetivo é N× o valor configurado. Isto é deliberado: o limitador é uma rede de segurança contra o abuso descontrolado de um único nó, e o limite global autoritativo pertence à borda (WAF / ingress / CDN), que vê todo o tráfego antes de ser balanceado.

## Clustering

Várias instâncias coordenam-se através de uma **eleição de líder** e de um **barramento de eventos entre nós**, ambos por trás de backends plugáveis:

- **Eleição de líder**: uma eleição baseada em concessão (`Cluster:LeaseTtlSeconds`, padrão 30s, renovada aproximadamente a metade desse intervalo). Exatamente um nó detém a concessão; a liderança é transferida automaticamente quando o líder morre. O trabalho restrito ao líder (atualmente a rotação da chave de assinatura, quando habilitada) é executado apenas no líder para evitar a geração concorrente de chaves.
- **Barramento de eventos**: notificações entre nós (por exemplo, invalidação de cache em hosts multi-tenant), sondadas a cada `Cluster:PollIntervalSeconds` (padrão 3s).

Cada instância gera um ID de nó aleatório de 12 caracteres hexadecimais na inicialização para se identificar; não é persistido.

### Backends

O **padrão é em processo**: um único nó é sempre o seu próprio líder, e os eventos são apenas locais, correto para uma instância com zero configuração. As implantações multi-nó substituem por um backend real através do callback `configureClustering` em `AddAuthagonal`:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS: leadership + event bus via DynamoDB (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` registam apenas o barramento de eventos, mantendo a concessão em processo (sempre líder); use-os em nós que devem receber eventos do cluster mas nunca devem disputar a liderança.

> **Nota:** com o padrão em processo em vários nós, *cada* nó acredita que é o líder. Isso é inofensivo para a maioria das cargas de trabalho, mas habilite um backend de concessão real antes de ativar `Auth:KeyRotationEnabled` em várias instâncias.

Consulte a página de [Configuração](configuration#cluster) para todas as configurações de cluster.

### Implantações multi-tenant

No modo multi-tenant (`AddAuthagonalCore()`), nenhum serviço em segundo plano é registado: `TokenCleanupService`, `GrantReconciliationService`, `SigningKeyRotationService` e os serviços de semeadura de configuração fazem todos parte da composição de tenant único `AddAuthagonal()`. O host gere-os por tenant.

## Partição quente do índice de nomes

A pesquisa por prefixo de nome no admin é suportada pelas tabelas de índice `UserFirstNames` / `UserLastNames`, que usam uma **única partição quente**. Em escala, isto limita o débito de escrita do índice a cerca de 2.000 ops/seg, o que pode tornar-se um estrangulamento na criação/atualização de utilizadores sob carga pesada. Se não expuser a pesquisa de nomes no admin, defina `Storage:NameIndexesEnabled = false` para evitar completamente essas gravações. Consulte [Configuração](configuration).

## Proxy confiável e endpoints internos

Ao executar múltiplas instâncias atrás de um balanceador de carga:

- **Cabeçalhos encaminhados**: a limitação de taxa e o bloqueio indexam pelo IP do cliente, resolvido a partir de `X-Forwarded-For`. Defina `ForwardedHeaders:KnownNetworks` para o CIDR do seu ingress / pod para que o IP do cliente não possa ser falsificado entre instâncias. `ForwardedHeaders:ForwardLimit` tem por padrão `1`. Consulte [Configuração](configuration#forwarded-headers-trusted-proxy).
- **Endpoints internos**: `/_internal/backchannel-logout` exige `Cluster:Secret` no cabeçalho `X-Cluster-Secret` (comparado em tempo constante). Sem ele, o endpoint não autoriza ninguém e responde 404 — o IP de origem não é tratado como credencial, porque loopback é o que um proxy inverso no mesmo host apresenta para cada pedido reencaminhado, e um intervalo privado é cada workload vizinha numa rede de cluster partilhada. `Cluster:AllowLoopbackWithoutSecret` é um opt-in apenas de desenvolvimento que readmite um par loopback antes do reencaminhamento. O produto entregue nunca chama esta rota (a difusão de sessão é in-process via `SessionTermination`), portanto só importa para uma difusão que você construa.

## Recomendações de escalabilidade

**Escalabilidade vertical**: aumente a CPU e a memória em uma única instância. Útil para lidar com mais requisições simultâneas por instância.

**Escalabilidade horizontal**: execute múltiplas instâncias atrás de um balanceador de carga. Sem necessidade de sessões persistentes ou caches compartilhados. Cada instância é totalmente independente.

**Escalar para zero**: o Authagonal suporta implantações com escala para zero (por exemplo, Azure Container Apps com `minReplicas: 0`). A primeira requisição após inatividade terá um início a frio de alguns segundos enquanto o runtime .NET inicializa e as chaves de assinatura são carregadas do armazenamento.
