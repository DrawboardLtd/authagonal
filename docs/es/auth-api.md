---
layout: default
title: API de autenticacion
locale: es
---

# API de autenticacion

Estos endpoints alimentan la SPA de inicio de sesion. Usan autenticacion por cookie (`SameSite=Lax`, `HttpOnly`).

Si esta construyendo una interfaz de inicio de sesion personalizada, estos son los endpoints que necesita implementar.

## Endpoints

### Inicio de sesion

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Exito (200):** Establece una cookie de autenticacion y devuelve:

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

**MFA requerido (200):** Si el usuario tiene MFA inscrito y la `MfaPolicy` del cliente es `Enabled` o `Required`:

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

El cliente debe redirigir a una pagina de desafio MFA y llamar a `POST /api/auth/mfa/verify`.

**Configuracion de MFA requerida (200):** Si `MfaPolicy` es `Required` y el usuario no tiene MFA inscrito:

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

El cliente debe redirigir a una pagina de configuracion de MFA. El token de configuracion autentica al usuario en los endpoints de configuracion de MFA mediante el encabezado `X-MFA-Setup-Token`.

**Respuestas de error:**

| `error` | Estado | Descripcion |
|---|---|---|
| `invalid_credentials` | 401 | Correo electronico o contrasena incorrectos |
| `locked_out` | 423 | Demasiados intentos fallidos. `retryAfter` (segundos) esta incluido. |
| `email_not_confirmed` | 403 | Correo electronico aun no verificado |
| `sso_required` | 409 | El dominio requiere SSO. `redirectUrl` apunta al inicio de sesion SSO. |
| `email_required` | 400 | El campo de correo electronico esta vacio |
| `password_required` | 400 | El campo de contrasena esta vacio |

### Registro

```
POST /api/auth/register
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Crea una nueva cuenta de usuario y envia un correo de verificacion. Devuelve `409` si el correo ya esta registrado.

### Confirmar correo electronico

```
POST /api/auth/confirm-email?token={token}
```

Confirma la direccion de correo electronico del usuario usando el token del correo de verificacion.

### Proveedores

```
GET /api/auth/providers
```

Devuelve la lista de proveedores de identidad externos configurados (para renderizar botones SSO):

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "loginUrl": "/oidc/google/login" }
  ]
}
```

### Cierre de sesion

```
POST /api/auth/logout
```

Borra la cookie de autenticacion. Devuelve `200 { success: true }`.

### Contrasena olvidada

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

Siempre devuelve `200` (anti-enumeracion). Si el usuario existe, se envia un correo de restablecimiento.

### Restablecer contrasena

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | Descripcion |
|---|---|
| `weak_password` | No cumple con los requisitos de robustez |
| `invalid_token` | El token esta mal formado |
| `token_expired` | El token ha expirado (validez predeterminada de 60 minutos, configurable mediante `Auth:PasswordResetExpiryMinutes`) |

### Sesion

```
GET /api/auth/session
```

Devuelve la informacion de la sesion actual si esta autenticado:

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

Devuelve `401` si no esta autenticado.

### Verificacion SSO

```
GET /api/auth/sso-check?email=user@acme.com
```

Verifica si el dominio del correo electronico requiere SSO:

```json
{
  "ssoRequired": true,
  "providerType": "saml",
  "connectionId": "acme-azure",
  "redirectUrl": "/saml/acme-azure/login"
}
```

Si no se requiere SSO:

```json
{
  "ssoRequired": false
}
```

### Politica de contrasenas

```
GET /api/auth/password-policy
```

Devuelve los requisitos de contrasena del servidor (configurados mediante `PasswordPolicy` en los ajustes):

```json
{
  "rules": [
    { "rule": "minLength", "value": 8, "label": "At least 8 characters" },
    { "rule": "uppercase", "value": null, "label": "Uppercase letter" },
    { "rule": "lowercase", "value": null, "label": "Lowercase letter" },
    { "rule": "digit", "value": null, "label": "Number" },
    { "rule": "specialChar", "value": null, "label": "Special character" }
  ]
}
```

La interfaz de inicio de sesion predeterminada obtiene este endpoint en la pagina de restablecimiento de contrasena para mostrar los requisitos dinamicamente.

## Requisitos de contrasena predeterminados

Con la configuracion predeterminada, las contrasenas deben cumplir todos estos requisitos:

- Al menos 8 caracteres
- Al menos una letra mayuscula
- Al menos una letra minuscula
- Al menos un digito
- Al menos un caracter no alfanumerico
- Al menos 2 caracteres distintos

Estos pueden personalizarse mediante la seccion de configuracion `PasswordPolicy` -- ver [Configuracion](configuration).

## Endpoints de MFA

### Verificar MFA

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

Verifica un desafio MFA. En caso de exito, establece la cookie de autenticacion y devuelve la informacion del usuario.

**Metodos:**

| `method` | Campos requeridos | Descripcion |
|---|---|---|
| `totp` | `code` (6 digitos) | Contrasena de un solo uso basada en tiempo desde una aplicacion de autenticacion |
| `webauthn` | `assertion` (cadena JSON) | Respuesta de asercion WebAuthn de `navigator.credentials.get()` |
| `recovery` | `code` (`XXXX-XXXX`) | Codigo de recuperacion de un solo uso (se consume al usarse) |

### Estado de MFA

```
GET /api/auth/mfa/status
```

Devuelve los metodos MFA inscritos del usuario. Requiere autenticacion por cookie o encabezado `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

### Configuracion de TOTP

```
POST /api/auth/mfa/totp/setup
-> { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
-> { "success": true }
```

### Configuracion de WebAuthn / Passkey

```
POST /api/auth/mfa/webauthn/setup
-> { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
-> { "success": true, "credentialId": "..." }
```

### Codigos de recuperacion

```
POST /api/auth/mfa/recovery/generate
-> { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Genera 10 codigos de recuperacion de un solo uso. Requiere que al menos un metodo primario (TOTP o WebAuthn) este inscrito. Regenerar reemplaza todos los codigos de recuperacion existentes.

### Eliminar credencial MFA

```
DELETE /api/auth/mfa/credentials/{credentialId}
-> { "success": true }
```

Elimina una credencial MFA especifica. Si se elimina el ultimo metodo primario, MFA se desactiva para el usuario.

## Construir una interfaz de inicio de sesion personalizada

La SPA predeterminada (`login-app/`) es una implementacion de esta API. Para construir la suya:

1. Sirva su interfaz en las rutas `/login`, `/forgot-password`, `/reset-password`
2. El endpoint de autorizacion redirige a los usuarios no autenticados a `/login?returnUrl={encoded-authorize-url}`
3. Despues de un inicio de sesion exitoso (cookie establecida), redirija al usuario al `returnUrl`
4. Los enlaces de restablecimiento de contrasena usan `{Issuer}/reset-password?p={token}`

Su interfaz debe servirse desde el **mismo origen** que la API porque:
- La autenticacion por cookie usa `SameSite=Lax` + `HttpOnly`
- El endpoint de autorizacion redirige a `/login` (relativo)
- Los enlaces de restablecimiento usan `{Issuer}/reset-password`
