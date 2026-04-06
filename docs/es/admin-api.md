---
layout: default
title: API de administracion
locale: es
---

# API de administracion

Los endpoints de administracion requieren un token de acceso JWT con el scope `authagonal-admin` (configurable via `AdminApi:Scope`).

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

## Gestion de MFA

### Obtener estado de MFA

```
GET /api/v1/profile/{userId}/mfa
```

Devuelve el estado de MFA y los metodos inscritos de un usuario.

### Restablecer todo MFA

```
DELETE /api/v1/profile/{userId}/mfa
```

Elimina todas las credenciales MFA y establece `MfaEnabled=false`. El usuario debera volver a inscribirse si es requerido.

### Eliminar credencial MFA especifica

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Elimina una credencial MFA especifica (por ejemplo, un autenticador perdido). Si se elimina el ultimo metodo primario, MFA se desactiva.

## Proveedores SSO

### Proveedores SAML

```
POST   /api/v1/saml/connections                    # Crear
GET    /api/v1/saml/connections/{connectionId}     # Obtener uno
PUT    /api/v1/saml/connections/{connectionId}     # Actualizar
DELETE /api/v1/saml/connections/{connectionId}     # Eliminar
```

### Proveedores OIDC

```
POST   /api/v1/oidc/connections                    # Crear
GET    /api/v1/oidc/connections/{connectionId}     # Obtener uno
DELETE /api/v1/oidc/connections/{connectionId}     # Eliminar
```

### Dominios SSO

```
GET    /api/v1/sso/domains                 # Listar todos
```

## Roles

### Listar roles

```
GET /api/v1/roles
```

### Obtener rol

```
GET /api/v1/roles/{roleId}
```

### Crear rol

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### Actualizar rol

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### Eliminar rol

```
DELETE /api/v1/roles/{roleId}
```

### Asignar rol a usuario

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleId": "role-id"
}
```

### Desasignar rol de usuario

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleId": "role-id"
}
```

### Obtener roles de un usuario

```
GET /api/v1/roles/user/{userId}
```

## Tokens SCIM

### Generar token

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id"
}
```

Devuelve el token en texto plano una sola vez. Almacenelo de forma segura — no se puede recuperar de nuevo.

### Listar tokens

```
GET /api/v1/scim/tokens?clientId=client-id
```

Devuelve los metadatos del token (ID, fecha de creacion) sin el valor del token en texto plano.

### Revocar token

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## Tokens

### Suplantar usuario

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid,profile
```

Emite tokens en nombre de un usuario sin requerir sus credenciales. Util para pruebas y soporte. Los parametros se pasan como cadenas de consulta. El parametro opcional `refreshTokenLifetime` controla la validez del token de actualizacion.
