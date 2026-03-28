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

Authagonal não inclui limitação de taxa integrada. A limitação de taxa deve ser aplicada na camada de infraestrutura (balanceador de carga, API gateway ou proxy reverso) onde há uma visão unificada de todo o tráfego entre as instâncias.

## Recomendações de escalabilidade

**Escalabilidade vertical** — aumente a CPU e a memória em uma única instância. Útil para lidar com mais requisições simultâneas por instância.

**Escalabilidade horizontal** — execute múltiplas instâncias atrás de um balanceador de carga. Sem necessidade de sessões persistentes ou caches compartilhados. Cada instância é totalmente independente.

**Escalar para zero** — Authagonal suporta implantações com escala para zero (por exemplo, Azure Container Apps com `minReplicas: 0`). A primeira requisição após inatividade terá um início a frio de alguns segundos enquanto o runtime .NET inicializa e as chaves de assinatura são carregadas do armazenamento.
