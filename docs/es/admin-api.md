---
layout: default
title: API de administracion
locale: es
---

# API de administracion

Los endpoints de administracion requieren un token de acceso JWT con el scope `authagonal-admin`.

Todos los endpoints estan bajo `/api/v1/`.

## Usuarios

### Obtener usuario

```
GET /api/v1/profile/{userId}
```

Devuelve los detalles del usuario, incluyendo los vinculos de inicio de sesion externo.

### Registrar usuario

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

Crea un usuario y envia un correo de verificacion. Devuelve `409` si el correo ya esta en uso.

### Actualizar usuario

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

Todos los campos son opcionales -- solo los campos proporcionados se actualizan. Cambiar `organizationId` desencadena:
- Rotacion del SecurityStamp (invalida todas las sesiones por cookie dentro de 30 minutos)
- Revocacion de todos los tokens de actualizacion

### Eliminar usuario

```
DELETE /api/v1/profile/{userId}
```

Elimina al usuario, revoca todos los otorgamientos y desaprovisiona de todas las aplicaciones posteriores (mejor esfuerzo).

### Confirmar correo electronico

```
POST /api/v1/profile/confirm-email?token={token}
```

### Enviar correo de verificacion

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Vincular identidad externa

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### Desvincular identidad externa

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

## Proveedores SSO

### Proveedores SAML

```
GET    /api/v1/sso/saml                    # Listar todos
GET    /api/v1/sso/saml/{connectionId}     # Obtener uno
POST   /api/v1/sso/saml                    # Crear
PUT    /api/v1/sso/saml/{connectionId}     # Actualizar
DELETE /api/v1/sso/saml/{connectionId}     # Eliminar
```

### Proveedores OIDC

```
GET    /api/v1/sso/oidc                    # Listar todos
GET    /api/v1/sso/oidc/{connectionId}     # Obtener uno
POST   /api/v1/sso/oidc                    # Crear
PUT    /api/v1/sso/oidc/{connectionId}     # Actualizar
DELETE /api/v1/sso/oidc/{connectionId}     # Eliminar
```

### Dominios SSO

```
GET    /api/v1/sso/domains                 # Listar todos
GET    /api/v1/sso/domains/{domain}        # Obtener uno
POST   /api/v1/sso/domains                 # Crear
DELETE /api/v1/sso/domains/{domain}        # Eliminar
```

## Tokens

### Suplantar usuario

```
POST /api/v1/tokens/impersonate
Content-Type: application/json

{
  "userId": "user-id",
  "clientId": "client-id",
  "scopes": ["openid", "profile"]
}
```

Emite tokens en nombre de un usuario sin requerir sus credenciales. Util para pruebas y soporte.
