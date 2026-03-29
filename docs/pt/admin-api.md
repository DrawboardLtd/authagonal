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

## Provedores SSO

### Provedores SAML

```
GET    /api/v1/sso/saml                    # List all
GET    /api/v1/sso/saml/{connectionId}     # Get one
POST   /api/v1/sso/saml                    # Create
PUT    /api/v1/sso/saml/{connectionId}     # Update
DELETE /api/v1/sso/saml/{connectionId}     # Delete
```

### Provedores OIDC

```
GET    /api/v1/sso/oidc                    # List all
GET    /api/v1/sso/oidc/{connectionId}     # Get one
POST   /api/v1/sso/oidc                    # Create
PUT    /api/v1/sso/oidc/{connectionId}     # Update
DELETE /api/v1/sso/oidc/{connectionId}     # Delete
```

### Domínios SSO

```
GET    /api/v1/sso/domains                 # List all
GET    /api/v1/sso/domains/{domain}        # Get one
POST   /api/v1/sso/domains                 # Create
DELETE /api/v1/sso/domains/{domain}        # Delete
```

## Tokens

### Impersonar Utilizador

```
POST /api/v1/tokens/impersonate
Content-Type: application/json

{
  "userId": "user-id",
  "clientId": "client-id",
  "scopes": ["openid", "profile"]
}
```

Emite tokens em nome de um utilizador sem exigir as suas credenciais. Útil para testes e suporte.
