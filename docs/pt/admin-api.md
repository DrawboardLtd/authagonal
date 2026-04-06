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
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid,profile
```

Emite tokens em nome de um utilizador sem exigir as suas credenciais. Útil para testes e suporte. Os parâmetros são passados como query strings. O parâmetro opcional `refreshTokenLifetime` controla a validade do refresh token.
