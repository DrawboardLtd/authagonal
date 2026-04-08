---
layout: default
title: Escalabilidade
locale: pt
---

# Escalabilidade

Authagonal é projetado para escalar tanto verticalmente quanto horizontalmente sem configuração especial.

## Sem estado por design

Todos os estados persistentes são armazenados no Azure Table Storage. Não há estado em processo que exija sessões persistentes ou coordenação entre instâncias:

- **Chaves de assinatura** — carregadas do Table Storage, atualizadas a cada hora
- **Códigos de autorização e tokens de atualização** — armazenados no Table Storage com aplicação de uso único
- **Prevenção de replay SAML** — IDs de requisição são rastreados no Table Storage com exclusão atômica
- **OIDC state e verificadores PKCE** — armazenados no Table Storage
- **Configuração de clientes e provedores** — obtida por requisição do Table Storage

## Criptografia de cookies (Data Protection)

As chaves de Data Protection do ASP.NET Core são automaticamente persistidas no Azure Blob Storage ao usar uma string de conexão real do Azure Storage. Isso significa que cookies assinados por uma instância podem ser descriptografados por qualquer outra instância — sem necessidade de sessões persistentes.

Para desenvolvimento local com Azurite, as chaves de Data Protection utilizam o armazenamento padrão baseado em arquivos.

Você também pode especificar uma URI de blob explícita via configuração:

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

## Caches por instância

Um pequeno número de valores lidos com frequência e que mudam lentamente são armazenados em cache na memória por instância para reduzir as viagens de ida e volta ao Table Storage:

| Dados | Duração do cache | Impacto da obsolescência |
|---|---|---|
| Documentos de descoberta OIDC | 60 minutos | Atraso na detecção de rotação de chaves do IdP |
| Metadados do SAML IdP | 60 minutos | Mesmo |
| Origens CORS permitidas | 60 minutos | Novas origens levam até uma hora para propagar |

Esses caches são aceitáveis para uso em produção. Se você precisar de propagação imediata, reinicie as instâncias afetadas.

## Limitação de taxa

Os endpoints de registro são protegidos por um limitador de taxa distribuído integrado (5 registros por IP por hora). Ao executar múltiplas instâncias, as contagens de limitação de taxa são automaticamente compartilhadas entre todas as instâncias via um protocolo gossip — sem necessidade de coordenação externa.

### Como funciona

Cada instância mantém seus próprios contadores em memória usando um CRDT G-Counter. As instâncias se descobrem mutuamente via UDP multicast e trocam estado via HTTP a cada poucos segundos. A contagem consolidada entre todas as instâncias é usada para tomar decisões de limitação de taxa.

Isso significa que os limites de taxa são aplicados globalmente: se um cliente atinge 3 instâncias diferentes, todas as 3 sabem que o total é 3, e não 1 cada.

### Identidade do nó

Cada instância gera um ID de nó hexadecimal aleatório na inicialização (ex.: `a3f1b2`). Este ID identifica a instância nas mensagens de gossip e no estado de limitação de taxa. Não é persistido — um novo ID é gerado a cada reinicialização.

Um `ClusterLeaderService` executa em cada instância, elegendo um único líder entre os peers descobertos (o ID de nó mais baixo vence). A liderança é transferida automaticamente quando o líder morre. A eleição de líder está disponível para tarefas de coordenação ao nível do cluster que devem ser executadas em apenas um nó.

### Configuração do cluster

O clustering é **habilitado por padrão** com zero configuração. Instâncias na mesma rede se descobrem automaticamente via UDP multicast (`239.42.42.42:19847`).

Para ambientes onde multicast não está disponível (algumas VPCs em nuvem), configure uma URL interna com balanceamento de carga como fallback:

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

Para desabilitar o clustering completamente (limitação de taxa apenas local):

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

Consulte a página de [Configuração](configuration) para todas as configurações de cluster.

### Degradação graciosa

- **Nenhum par encontrado** — funciona como um limitador de taxa apenas local (cada instância aplica seu próprio limite)
- **Par inacessível** — o último estado conhecido daquele par ainda é utilizado; pares obsoletos são removidos após 30 segundos
- **Multicast indisponível** — a descoberta falha silenciosamente; o gossip recorre ao `InternalUrl` se configurado

### Implantações multi-tenant

No modo multi-tenant (`AddAuthagonalCore()`), serviços em segundo plano como `GrantReconciliationService` e `SigningKeyRotationService` não são registados — o host gere-os por tenant. Apenas o `TokenCleanupService` é executado incondicionalmente.

## Recomendações de escalabilidade

**Escalabilidade vertical** — aumente a CPU e a memória em uma única instância. Útil para lidar com mais requisições simultâneas por instância.

**Escalabilidade horizontal** — execute múltiplas instâncias atrás de um balanceador de carga. Sem necessidade de sessões persistentes ou caches compartilhados. Cada instância é totalmente independente.

**Escalar para zero** — Authagonal suporta implantações com escala para zero (por exemplo, Azure Container Apps com `minReplicas: 0`). A primeira requisição após inatividade terá um início a frio de alguns segundos enquanto o runtime .NET inicializa e as chaves de assinatura são carregadas do armazenamento.
