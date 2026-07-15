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
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

`mfaAvailable` es `true` cuando la `MfaPolicy` del cliente es `Enabled` pero el usuario aun no se ha inscrito (la interfaz puede ofrecer la configuracion); en ese caso tambien se incluye un campo `clientId`.

**MFA requerido (200):** Si el usuario tiene MFA inscrito, **siempre** se le presenta el desafio, independientemente de la `MfaPolicy` del cliente que hace la peticion (MFA es una propiedad del usuario/sesion, no del cliente):

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
| `invalid_credentials` | 401 | Correo electronico o contrasena incorrectos. Deliberadamente identico para correos desconocidos (anti-enumeracion). |
| `locked_out` | 423 | Demasiados intentos fallidos. `retryAfter` (segundos) esta incluido. |
| `account_disabled` | 403 | La cuenta esta desactivada (solo se revela tras una contrasena correcta) |
| `email_not_confirmed` | 403 | Correo electronico aun no verificado (solo se revela tras una contrasena correcta) |
| `sso_required` | 409 | El dominio requiere SSO. `redirectUrl` apunta al inicio de sesion SSO. |
| `captcha_failed` | 400 | La verificacion de Turnstile fallo (solo cuando Turnstile esta configurado; las peticiones necesitan entonces un campo `turnstileToken`) |
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

Crea una nueva cuenta de usuario y envia un correo de verificacion. Devuelve `201 { "success": true, "userId": "..." }`. Campos opcionales: `locale` (etiqueta BCP-47 que se persiste en el usuario) y `customAttributes` (un mapa de cadenas).

El registro es deliberadamente **neutral ante la enumeracion**: si el correo ya esta registrado, la respuesta es el mismo `201` neutral (con un `userId` desechable) y en su lugar se envia al propietario real un aviso de inicio de sesion/restablecimiento. El registro tambien esta limitado por IP: `429 rate_limited` cuando se supera el limite (la ventana y el tope se configuran mediante `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes`).

### Confirmar correo electronico

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

Confirma la direccion de correo electronico del usuario usando el token del correo de verificacion. `GET` es el enlace clicable del correo: redirige a `/login?email_confirmed=1` (mas un parametro `continue_client` cuando el registro se origino en un flujo OAuth). `POST` es la ruta programatica y devuelve JSON (el token tambien puede proporcionarse en un cuerpo JSON como `{ "token": "..." }`); la respuesta incluye un `appLink` opcional (destino "continuar a la aplicacion").

### Proveedores

```
GET /api/auth/providers
```

Devuelve la lista de proveedores de identidad externos configurados (para renderizar botones SSO):

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "type": "oidc", "iconUrl": null, "loginUrl": "/oidc/google/login" }
  ],
  "turnstileSiteKey": null
}
```

Las conexiones con `AllowedDomains` configurados quedan **excluidas**: a esas se llega primero por correo mediante `/api/auth/sso-check` en lugar de un boton. `turnstileSiteKey` se establece cuando Cloudflare Turnstile esta configurado (la interfaz de inicio de sesion debe entonces enviar un `turnstileToken` con las peticiones de inicio de sesion/registro/contrasena).

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

### Aplicaciones

```
GET /api/auth/apps
```

Devuelve los enlaces a las aplicaciones del inquilino para el lanzador "volver a la aplicacion" de la pagina de cuenta: clientes habilitados que tienen una URI de inicio (`initiateLoginUri` tiene prioridad sobre `clientUri`). Cada entrada es `{ clientId, clientName, homeUri, logoUri, isDefault }`; exactamente una aplicacion se marca como predeterminada (el cliente marcado, o el unico cliente con una URI de inicio). Requiere autenticacion por cookie.

### Perfil (autoservicio)

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

El usuario autenticado lee/actualiza sus propios campos de perfil no sensibles: `firstName`, `lastName`, `companyName`, `phone`, `locale`. Los campos nulos quedan sin cambios; el correo electronico, la contrasena, los roles, el estado activo y la organizacion **no** son editables aqui. Ambos devuelven el perfil `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }`.

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

**Semantica de reintentos:** un codigo incorrecto **no** quema el desafio: el codigo se valida primero y el desafio se consume solo en caso de exito, por lo que el usuario puede reintentar con el mismo `challengeId` tras teclear mal un digito (`401 invalid_code` / `assertion_failed`). Cada desafio tolera **5 intentos fallidos**; el quinto fallo lo consume y devuelve `401 too_many_attempts`, forzando un nuevo inicio de sesion (esto acota la fuerza bruta de TOTP a 5 intentos por desafio). Los desafios tambien expiran (predeterminado 5 minutos, `Auth:MfaChallengeExpiryMinutes`); un `challengeId` expirado, desconocido o ya consumido devuelve `invalid_challenge`. Los codigos TOTP estan ademas protegidos contra reproduccion: se rechaza un codigo de un paso de tiempo ya utilizado.

### Estado de MFA

```
GET /api/auth/mfa/status
```

Devuelve los metodos MFA inscritos del usuario. Requiere autenticacion por cookie o encabezado `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

`offered` es `false` cuando la `MfaPolicy` de todos los clientes es `Disabled`: el inquilino tiene MFA desactivado, por lo que la interfaz de configuracion puede ocultarse. Las entradas de codigos de recuperacion llevan ademas `isConsumed`.

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

La inscripcion de passkey requiere **primero una credencial TOTP confirmada** (`400 totp_required_first`): las passkeys son una comodidad por dispositivo superpuesta a un factor base portatil, por lo que una cuenta nunca puede acabar solo con passkey y bloqueada a un dispositivo. Los usuarios cuyo dominio de correo esta enrutado por SSO no pueden inscribir una passkey local (`400 sso_managed`): eludiria el IdP del inquilino. Un ID de credencial ya registrado para otro usuario se rechaza con `409 credential_already_registered`.

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

Elimina una credencial MFA especifica. Si se elimina el ultimo metodo primario, MFA se desactiva para el usuario. Requiere una sesion de cookie real: un token de configuracion se rechaza con `403 session_required` (los tokens de configuracion existen solo para anadir un primer factor, nunca para degradar MFA).

### Inicio de sesion con passkey sin contrasena

```
POST /api/auth/mfa/passwordless/begin
-> { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
-> { "userId": "...", "email": "...", "name": "..." }
```

Inicio de sesion con credencial descubrible (passkey residente) sin contexto de usuario previo: `begin` emite un desafio de asercion con una lista `allowCredentials` vacia, y `complete` resuelve al usuario **a partir de** la passkey elegida, verifica la asercion e inicia su sesion (la sesion lleva el marcador de MFA: una passkey es autenticacion fuerte resistente al phishing). Si el dominio de correo del usuario resuelto esta enrutado por SSO, el inicio de sesion se rechaza con `409 sso_required` + `redirectUrl` para que una passkey local no pueda esquivar un IdP forzado.

## Autorizacion de dispositivo (RFC 8628)

### Solicitar codigo de dispositivo

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

Devuelve un codigo de dispositivo, un codigo de usuario y una URI de verificacion:

```json
{
  "device_code": "abc123...",
  "user_code": "ABCD-EFGH",
  "verification_uri": "https://auth.example.com/device",
  "verification_uri_complete": "https://auth.example.com/device?user_code=ABCD-EFGH",
  "expires_in": 300,
  "interval": 5
}
```

`expires_in` proviene del `DeviceCodeLifetimeSeconds` del cliente (predeterminado 300). El dispositivo muestra la `verification_uri` y el `user_code` al usuario, luego sondea el endpoint de token con el `device_code`, no mas rapido que cada `interval` segundos, o el endpoint de token responde `slow_down` (RFC 8628 §3.5). Mientras el usuario no haya aprobado, el endpoint de token devuelve `authorization_pending`. El usuario visita la URI de verificacion, inicia sesion e introduce el codigo de usuario para aprobar.

### Aprobar dispositivo

```
POST /api/auth/device/approve
Content-Type: application/json

{
  "userCode": "ABCD-EFGH"
}
```

Requiere autenticacion por cookie. Aprueba el codigo de dispositivo para el usuario actual. El dispositivo puede entonces intercambiar el codigo de dispositivo por tokens mediante el endpoint de token usando el tipo de concesion `urn:ietf:params:oauth:grant-type:device_code`.

## Introspeccion de tokens (RFC 7662)

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

token=eyJhbGci...
```

O con credenciales codificadas en el formulario:

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJhbGci...&client_id=my-app&client_secret=secret
```

Devuelve los metadatos del token:

```json
{
  "active": true,
  "sub": "user-id",
  "client_id": "my-app",
  "scope": "openid profile",
  "iss": "https://auth.example.com",
  "exp": 1234567890,
  "iat": 1234567890,
  "token_type": "Bearer"
}
```

Los tokens inactivos o invalidos devuelven `{ "active": false }`. Admite tanto tokens de acceso JWT como tokens de actualizacion opacos.

## Endpoints de consentimiento

### Informacion de consentimiento

```
GET /consent/info?client_id=my-app&scope=openid%20profile%20email
```

Devuelve los detalles del cliente y los scopes solicitados para la pagina de consentimiento (`scope` es `openid` de forma predeterminada cuando se omite):

```json
{
  "clientId": "my-app",
  "clientName": "My Application",
  "description": null,
  "clientUri": null,
  "logoUri": null,
  "scopes": ["openid", "profile", "email"]
}
```

Devuelve `404 client_not_found` para un cliente desconocido.

### Enviar consentimiento

```
POST /consent
Content-Type: application/json

{
  "clientId": "my-app",
  "decision": "allow",
  "scopes": ["openid", "profile", "email"],
  "returnUrl": "/connect/authorize?..."
}
```

Registra la decision de consentimiento del usuario (requiere autenticacion por cookie) y devuelve `{ "redirect": "..." }` para que la SPA navegue a el. Al permitir, los scopes concedidos se persisten (filtrados a los `AllowedScopes` del cliente: un cuerpo manipulado no puede registrar scopes que el cliente no podia solicitar) y la redireccion apunta de vuelta al flujo de autorizacion. Con `"decision": "deny"`, la redireccion apunta al `redirect_uri` del cliente con un error `access_denied`.

### Listar concesiones

```
GET /consent/grants
```

Devuelve todas las aplicaciones que el usuario ha autorizado:

```json
[
  {
    "clientId": "my-app",
    "clientName": "My Application",
    "scopes": ["openid", "profile", "email"],
    "consentedAt": "2026-04-09T12:00:00Z"
  }
]
```

### Revocar concesion

```
DELETE /consent/grants/{clientId}
```

Revoca el consentimiento para una aplicacion especifica. Se le pedira al usuario que vuelva a consentir en su proximo inicio de sesion.

## Construir una interfaz de inicio de sesion personalizada

La SPA predeterminada (`login-app/`) es una implementacion de esta API. Para construir la suya:

1. Sirva su interfaz en las rutas `/login`, `/forgot-password`, `/reset-password`
2. El endpoint de autorizacion redirige a los usuarios no autenticados a `/login?returnUrl={encoded-authorize-url}`
3. Despues de un inicio de sesion exitoso (cookie establecida), redirija al usuario al `returnUrl`
4. Los enlaces de restablecimiento de contrasena usan `{Issuer}/login/reset-password?p={token}` (la SPA de inicio de sesion se monta bajo `/login`)

Su interfaz debe servirse desde el **mismo origen** que la API porque:
- La autenticacion por cookie usa `SameSite=Lax` + `HttpOnly`
- El endpoint de autorizacion redirige a `/login` (relativo)
- Los enlaces de restablecimiento usan `{Issuer}/login/reset-password`
