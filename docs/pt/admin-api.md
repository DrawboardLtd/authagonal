---
layout: default
title: API de Administração
locale: pt
---

# API de Administração

Os endpoints de administração requerem um token de acesso JWT com o scope `authagonal-admin` (configurável via `AdminApi:Scope`).

Todos os endpoints estão sob `/api/v1/`.

## Utilizadores

### Obter Utilizador

```
GET /api/v1/profile/{userId}
```

Retorna detalhes do utilizador incluindo vínculos de login externo.

### Registar Utilizador

```
POST /api/v1/profile/
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Cria um utilizador e envia um e-mail de verificação. Retorna `409` se o e-mail já estiver em uso.

### Atualizar Utilizador

```
PUT /api/v1/profile/
Content-Type: application/json

{
  "userId": "user-id",
  "firstName": "Jane",
  "lastName": "Smith",
  "organizationId": "new-org-id"
}
```

Todos os campos são opcionais — apenas os campos fornecidos são atualizados. Alterar `organizationId` desencadeia:
- Rotação do SecurityStamp (invalida todas as sessões de cookie dentro de 30 minutos)
- Todos os refresh tokens revogados

### Eliminar Utilizador

```
DELETE /api/v1/profile/{userId}
```

Elimina o utilizador, revoga todas as concessões e desprovisiona de todas as aplicações downstream (melhor esforço).

### Confirmar E-mail

```
POST /api/v1/profile/confirm-email?token={token}
```

### Enviar E-mail de Verificação

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Vincular Identidade Externa

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### Desvincular Identidade Externa

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

## Gestão de MFA

### Obter Estado do MFA

```
GET /api/v1/profile/{userId}/mfa
```

Retorna o estado do MFA e os métodos inscritos de um utilizador.

### Redefinir Todo o MFA

```
DELETE /api/v1/profile/{userId}/mfa
```

Remove todas as credenciais MFA e define `MfaEnabled=false`. O utilizador precisará inscrever-se novamente se for obrigatório.

### Remover Credencial MFA Específica

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Remove uma credencial MFA específica (por exemplo, um autenticador perdido). Se o último método primário for removido, o MFA é desativado.

## Provedores SSO

### Provedores SAML

```
POST   /api/v1/saml/connections                    # Create
GET    /api/v1/saml/connections/{connectionId}     # Get one
PUT    /api/v1/saml/connections/{connectionId}     # Update
DELETE /api/v1/saml/connections/{connectionId}     # Delete
```

### Provedores OIDC

```
POST   /api/v1/oidc/connections                    # Create
GET    /api/v1/oidc/connections/{connectionId}     # Get one
DELETE /api/v1/oidc/connections/{connectionId}     # Delete
```

### Domínios SSO

```
GET    /api/v1/sso/domains                 # List all
```

## Clientes

Gira clientes OAuth em tempo de execução. Todas as rotas requerem a política `IdentityAdmin` (o scope de administração).

```
GET    /api/v1/clients              # List all clients
GET    /api/v1/clients/{clientId}   # Get one client
POST   /api/v1/clients              # Create a client
PUT    /api/v1/clients/{clientId}   # Update a client
DELETE /api/v1/clients/{clientId}   # Delete a client
```

### Criar / Atualizar Cliente

```
POST /api/v1/clients
Content-Type: application/json

{
  "clientId": "my-app",
  "clientName": "My Application",
  "allowedGrantTypes": ["authorization_code"],
  "redirectUris": ["https://app.example.com/callback"],
  "allowedScopes": ["openid", "profile", "email"]
}
```

`POST` retorna `409` se o cliente já existir. `PUT` atualiza um cliente existente (`404` se não encontrado); na atualização, apenas os scopes recém-adicionados são verificados quanto a escalonamento.

Notas:

- **Os hashes de segredo nunca são retornados.** `clientSecretHashes` é removido de todas as respostas (listar, obter, criar, atualizar). Na atualização, omitir `clientSecretHashes` preserva o segredo armazenado; fornecer novos hashes faz a sua rotação.
- **O scope de administração não pode ser concedido a um cliente.** Solicitar `AdminApi:Scope` (padrão `authagonal-admin`) em `allowedScopes` retorna `403 forbidden_scope` — nenhum cliente pode possuir o scope de administração, caso contrário um cliente `client_credentials` poderia emitir tokens de administração indefinidamente.
- Adicionar scopes que o chamador não está autorizado a conceder retorna `403`.

## Scopes

Gira scopes OAuth personalizados em tempo de execução. Consulte [Scopes OAuth](scopes) para o modelo de scope completo.

```
GET    /api/v1/scopes           # List all scopes
GET    /api/v1/scopes/{name}    # Get one scope
POST   /api/v1/scopes           # Create a scope
PUT    /api/v1/scopes/{name}    # Update a scope (only supplied fields change)
DELETE /api/v1/scopes/{name}    # Delete a scope
```

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing — read-only",
  "description": "View invoices and payment history",
  "userClaims": ["billing_plan"]
}
```

Retorna `201` na criação (`409` se o scope já existir), o JSON do scope em obter/atualizar, e `204` ao eliminar.

## Aplicações de Provisionamento

Gira os alvos de provisionamento downstream em tempo de execução. Todas as rotas requerem a política `IdentityAdmin`.

```
GET    /api/v1/provisioning/apps               # List apps (also returns the configured limit)
POST   /api/v1/provisioning/apps               # Create an app
PUT    /api/v1/provisioning/apps/{appId}       # Update an app
DELETE /api/v1/provisioning/apps/{appId}       # Delete an app
POST   /api/v1/provisioning/apps/{appId}/test  # Send a test /try call to the app's callback
```

### Criar / Atualizar Aplicação de Provisionamento

```
POST /api/v1/provisioning/apps
Content-Type: application/json

{
  "name": "Backend",
  "callbackUrl": "https://api.example.com/provisioning",
  "apiKey": "secret-api-key",
  "tryTimeoutSeconds": 30
}
```

- `name` e `callbackUrl` são obrigatórios; `callbackUrl` deve ser uma URL `http(s)` absoluta.
- `tryTimeoutSeconds` é limitado ao intervalo 5–300.
- **A chave de API nunca é retornada.** As respostas expõem `hasApiKey` (um booleano) em vez da própria chave. Na atualização, omitir `apiKey` deixa-a inalterada, uma string vazia limpa-a e um valor substitui-a.
- A criação está sujeita a uma quota por implantação configurável (`IProvisioningAppQuota`); excedê-la retorna `400 provisioning_app_limit`. A resposta da listagem inclui o `limit` atual.

### Testar uma Aplicação de Provisionamento

```
POST /api/v1/provisioning/apps/{appId}/test
```

Envia um `POST {callbackUrl}/try` sintético com um payload de exemplo (e a chave de API da aplicação como bearer token, se definida) e retorna `{ success, statusCode, body }` para que possa verificar a conectividade a partir da interface de administração.

## Roles

### Listar Roles

```
GET /api/v1/roles
```

### Obter Role

```
GET /api/v1/roles/{roleId}
```

### Criar Role

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### Atualizar Role

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### Eliminar Role

```
DELETE /api/v1/roles/{roleId}
```

### Atribuir Role a Utilizador

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleId": "role-id"
}
```

### Remover Role de Utilizador

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleId": "role-id"
}
```

### Obter Roles do Utilizador

```
GET /api/v1/roles/user/{userId}
```

## Tokens SCIM

### Gerar Token

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id"
}
```

Retorna o token bruto uma vez. Armazene-o de forma segura — não pode ser recuperado novamente.

### Listar Tokens

```
GET /api/v1/scim/tokens?clientId=client-id
```

Retorna metadados dos tokens (ID, data de criação) sem o valor bruto do token.

### Revogar Token

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## Tokens

### Impersonar Utilizador

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

Emite tokens (access, refresh e — quando `openid` é solicitado — id token) em nome de um utilizador sem exigir as suas credenciais. Útil para testes e suporte. Os parâmetros são passados como query strings.

| Parâmetro de query | Obrigatório | Descrição |
|---|---|---|
| `clientId` | Sim | O cliente para o qual os tokens são emitidos. Os tempos de vida dos tokens vêm da configuração deste cliente. |
| `userId` | Sim | O utilizador a impersonar. |
| `scopes` | Não | Lista de scopes **separados por espaço** (codifique os espaços na URL). Por padrão usa os `AllowedScopes` do cliente quando omitido. |

Restrições:

- Os scopes estão limitados aos `AllowedScopes` do cliente — solicitar qualquer scope que o próprio cliente não poderia solicitar retorna `400 invalid_scope`.
- O scope de administração (`AdminApi:Scope`, padrão `authagonal-admin`) **não pode** ser emitido através deste endpoint; solicitá-lo retorna `403 forbidden_scope`. Isto impede que um token de administração (possivelmente com tempo limitado) emita um token de acesso/refresh de administração de longa duração.

A resposta é uma resposta de token padrão com `access_token`, `refresh_token`, opcionalmente `id_token`, `expires_in` e o `scope` concedido (separado por espaço).
