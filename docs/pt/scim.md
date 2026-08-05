---
layout: default
title: Provisionamento SCIM 2.0
locale: pt
---

# Provisionamento SCIM 2.0

O Authagonal suporta SCIM 2.0 (System for Cross-domain Identity Management) para o provisionamento automático de utilizadores a partir de provedores de identidade empresariais como Microsoft Entra ID, Okta e OneLogin.

## Visão Geral

O SCIM é um protocolo de provisionamento de entrada: o seu provedor de identidade envia alterações de utilizadores e grupos para o Authagonal. Isto é complementar ao provisionamento de saída TCC (Try-Confirm-Cancel) existente, que envia utilizadores para aplicações downstream.

**Operações suportadas:**
- CRUD de utilizadores (criar, ler, atualizar, eliminar via desativação suave)
- CRUD de grupos com gestão de membros
- Filtragem (a gramática de filtros completa da RFC 7644 §3.4.2.2)
- Paginação: baseada em cursor para listagens de utilizadores (`cursor`/`nextCursor`), `startIndex` e `count` para grupos
- PATCH para atualizações parciais (incluindo desativação `active=false`)
- Mapeamento de grupo para role resolvido na emissão de token

**Não suportado:** operações em lote, ordenação, ETags, gestão de senhas via SCIM.

Todos os recursos têm o âmbito limitado ao cliente SCIM que os provisionou: um utilizador ou grupo criado pelo cliente de um token SCIM é invisível (404) para todos os outros clientes SCIM.

## Gerando um Token SCIM

Os endpoints SCIM são autenticados com tokens Bearer estáticos. Gere tokens via a API de administração:

```http
POST /api/v1/scim/tokens
Authorization: Bearer {admin-token}
Content-Type: application/json

{
  "clientId": "your-client-id",
  "description": "Entra ID SCIM token",
  "expiresInDays": 365
}
```

A resposta inclui o token em bruto **uma vez**. Ele é armazenado como um hash SHA-256 e não pode ser recuperado depois, portanto armazene-o de forma segura:

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "description": "Entra ID SCIM token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z"
}
```

Omita `expiresInDays` (ou passe `0`) para um token que não expira.

### Listar tokens

```http
GET /api/v1/scim/tokens?clientId=your-client-id
Authorization: Bearer {admin-token}
```

### Revogar um token

```http
DELETE /api/v1/scim/tokens/{tokenId}?clientId=your-client-id
Authorization: Bearer {admin-token}
```

## Configurando o Seu Provedor de Identidade

### URL do Tenant

```
https://your-authagonal-instance/scim/v2
```

### Autenticação

Use **OAuth Bearer Token** com o token gerado acima.

### Microsoft Entra ID

1. No portal do Azure, vá a **Enterprise Applications** > a sua aplicação > **Provisioning**
2. Defina o Provisioning Mode como **Automatic**
3. Introduza o Tenant URL: `https://your-instance/scim/v2`
4. Introduza o Secret Token: o token em bruto do passo de geração
5. Clique em **Test Connection** para verificar
6. Configure os mapeamentos de atributos (ver abaixo)

### Okta

1. No console de administração do Okta, vá a **Applications** > a sua aplicação > **Provisioning**
2. Habilite o **SCIM connector**
3. Defina o Base URL: `https://your-instance/scim/v2`
4. Defina o Authentication Mode: **HTTP Header**
5. Introduza o token Bearer

### OneLogin

1. Na administração do OneLogin, vá a **Applications** > a sua aplicação > **Provisioning**
2. Habilite o provisionamento
3. Defina o SCIM Base URL: `https://your-instance/scim/v2`
4. Defina o SCIM Bearer Token

## Endpoints SCIM

| Método | Caminho | Descrição |
|--------|------|-------------|
| GET | `/scim/v2/Users` | Listar/filtrar utilizadores |
| GET | `/scim/v2/Users/{id}` | Obter um utilizador |
| POST | `/scim/v2/Users` | Criar um utilizador |
| PUT | `/scim/v2/Users/{id}` | Substituir um utilizador |
| PATCH | `/scim/v2/Users/{id}` | Atualização parcial |
| DELETE | `/scim/v2/Users/{id}` | Tombstone (desativa; um GET posterior é 404) |
| GET | `/scim/v2/Groups` | Listar/filtrar grupos |
| GET | `/scim/v2/Groups/{id}` | Obter um grupo |
| POST | `/scim/v2/Groups` | Criar um grupo |
| PUT | `/scim/v2/Groups/{id}` | Substituir um grupo |
| PATCH | `/scim/v2/Groups/{id}` | Adicionar/remover membros |
| DELETE | `/scim/v2/Groups/{id}` | Eliminar um grupo |
| GET | `/scim/v2/ServiceProviderConfig` | Capacidades |
| GET | `/scim/v2/Schemas` | Definições de esquema |
| GET | `/scim/v2/ResourceTypes` | Tipos de recurso |

Cada endpoint também é mapeado sem o segmento `/v2` (por exemplo, `/scim/Users`) para provedores de identidade que anexam o seu próprio caminho. Os endpoints de descoberta (`ServiceProviderConfig`, `Schemas`, `ResourceTypes`, e as URLs base nuas `/scim/` e `/scim/v2/`, que retornam o ServiceProviderConfig) são anónimos; tudo o resto requer um token Bearer SCIM.

Os endpoints de utilizador têm limitação de taxa de 200 requisições por minuto por cliente SCIM; as requisições em excesso recebem um erro SCIM com estado `429`.

## Mapeamento de Atributos

### Atributos de utilizador

| Atributo SCIM | Campo do Authagonal |
|---------------|------------------|
| `userName` | `Email` |
| `name.givenName` | `FirstName` |
| `name.familyName` | `LastName` |
| `displayName` | `FirstName LastName` |
| `emails[type eq "work"].value` | `Email` |
| `active` | `IsActive` |
| `externalId` | `ExternalId` |
| `preferredLanguage` (falling back to `locale`) | `Locale` |

### Atributos de grupo

| Atributo SCIM | Campo do Authagonal |
|---------------|------------------|
| `displayName` | `DisplayName` |
| `externalId` | `ExternalId` |
| `members` | `MemberUserIds` |

## Detalhes de Comportamento

### Criação de utilizador
- Os utilizadores provisionados por SCIM são criados com `EmailConfirmed = true` (apenas SSO, sem senha).
- O campo `ScimProvisionedByClientId` regista qual cliente SCIM criou o utilizador.
- Se o cliente tiver `ProvisioningApps` configuradas, o provisionamento TCC é disparado automaticamente. Se o provisionamento rejeitar o utilizador, a criação SCIM é revertida e a resposta é um `400` SCIM com `scimType: invalidValue` e uma mensagem fixa (o texto da aplicação a jusante não é repassado ao cliente SCIM, deliberadamente).
- Criar um utilizador cujo `userName` ou `externalId` já exista retorna um conflito SCIM `409`. As alterações de e-mail via PUT ou PATCH são verificadas quanto a conflitos da mesma forma.

### Desativação de utilizador
- `DELETE /scim/v2/Users/{id}` cria um **tombstone**: desativa o utilizador, mantém o registo local e marca `ScimDeletedAt`. Um `GET /scim/v2/Users/{id}` subsequente devolve **404**, como a RFC 7644 §3.6 exige ("o service provider TEM DE devolver 404 para todas as operações associadas ao recurso previamente eliminado"). Não confirme um desprovisionamento relendo o recurso e esperando `active: false` — a leitura é um 404, e esse é o caso de sucesso.
- O registo é mantido em vez de apagado para que uma recontratação possa ser recriada: o tombstone liberta o `userName`/`externalId` de que um novo recurso precisa, enquanto a conta local, o seu histórico de auditoria e as suas pertenças a grupos sobrevivem.
- `PATCH` com `active = false` também desativa o utilizador.
- Utilizadores desativados não podem iniciar sessão via senha, SAML ou OIDC.
- Todas as concessões (refresh tokens, sessões) são revogadas após a desativação.
- O desprovisionamento de aplicações downstream é disparado apenas por `DELETE`; uma desativação por `PATCH` revoga as concessões mas deixa as aplicações downstream intactas.

### Filtragem
Expressões de filtro suportadas:
- `userName eq "user@example.com"`
- `externalId eq "12345"`
- `displayName co "John"`

Apenas filtros de atributo único são suportados. Expressões booleanas complexas (`and`, `or`) não são suportadas.

Os filtros `eq` em `userName` e `externalId` (as pesquisas que o Entra e o Okta emitem antes de cada criação ou atualização) são resolvidos via pesquisas pontuais indexadas em vez de uma varredura de listagem, portanto permanecem rápidos com qualquer número de utilizadores. Outros filtros (`co`, ou filtros em `displayName`) são aplicados enquanto se pagina pelos utilizadores do cliente.

### Paginação
As listagens de utilizadores usam **paginação por cursor**. Cada página de `GET /scim/v2/Users` retorna uma propriedade `nextCursor` na resposta da lista; passe-a de volta como `?cursor=` para obter a próxima página. Quando `nextCursor` está ausente, a listagem está completa. O tamanho da página é controlado por `count` (padrão 100, máximo 200).

Pedir `startIndex` maior que 1 no endpoint de Users retorna um erro `400` que o direciona para a paginação por cursor; a paginação por offset além da primeira página não é oferecida. `totalResults` é **omitido** enquanto `nextCursor` estiver presente, e traz o total exato apenas na última página — deliberadamente não reporta o tamanho da página devolvida, porque um cliente que confunde as duas coisas lê o diretório de forma incompleta e em silêncio. Conduza o ciclo por `nextCursor`, não por `totalResults`, e trate um `totalResults` ausente como "ainda desconhecido", não como zero.

As listagens de grupos ainda usam a paginação por offset `startIndex`/`count`.

### Associação a grupos via PATCH
`PATCH /scim/v2/Groups/{id}` aceita os formatos de associação que os principais provedores de identidade realmente enviam:

- **Adicionar membros:** `op: "add"` com `path: "members"` e um array de valores de objetos `{ "value": "user-id" }`. Duplicados são ignorados.
- **Substituir membros:** `op: "replace"` com `path: "members"` substitui toda a associação pelo array fornecido.
- **Remover um membro específico (array de valores):** `op: "remove"` com `path: "members"` e um array de valores dos ids de membro a remover (o formato que o Entra ID envia).
- **Remover um membro específico (filtro de path):** `op: "remove"` com `path: 'members[value eq "user-id"]'`, com o id carregado no filtro de path sem valor (o formato que o Okta envia para desprovisionamento).
- **Remover todos os membros:** `op: "remove"` com `path: "members"` e sem valor limpa o grupo.

### Mapeamento de grupo para role
A associação a um grupo SCIM pode conceder roles de aplicação. Os mapeamentos são uma linha por par (grupo, role), e um grupo pode conceder vários roles. São resolvidos na **emissão de token**: os roles efetivos de um utilizador são os seus roles atribuídos diretamente mais os roles de cada grupo mapeado a que pertence, portanto adicionar ou remover um membro de grupo tem efeito no próximo token sem tocar no registo do utilizador. Um store de mapeamento vazio é um no-op.

Os mapeamentos são persistidos via o `IScimGroupRoleMappingStore` (implementado pelos provedores de armazenamento Azure e AWS; caso contrário, um padrão em memória é registado) e são geridos pela superfície de administração da aplicação hospedeira, não via a própria API SCIM.

Opcionalmente, um cliente com `IncludeGroupsInTokens` habilitado também recebe os nomes de exibição dos grupos SCIM do utilizador como uma claim `groups` nos tokens emitidos.

## Limitações Conhecidas

- **Sem operações em lote:** utilizadores e grupos devem ser provisionados individualmente.
- **Sem ordenação:** as listagens de utilizadores retornam a ordem de armazenamento sob paginação por cursor; as listagens de grupos são ordenadas por data de criação.
- **Sem gestão de senhas:** os utilizadores provisionados por SCIM autenticam-se apenas via SSO.
- **Tombstone, não apagamento:** o `DELETE` desativa e marca um tombstone (um `GET` posterior é um 404, conforme a RFC 7644 §3.6) em vez de remover permanentemente o registo local. Para apagamento, use a API de administração.
