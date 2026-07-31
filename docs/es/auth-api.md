---
layout: default
title: API de autenticación
locale: es
---

# API de autenticación

Estos endpoints alimentan la SPA de inicio de sesión. Usan autenticación por cookie (`SameSite=Lax`, `HttpOnly`).

Si está construyendo una interfaz de inicio de sesión personalizada, estos son los endpoints que necesita implementar.

## Endpoints

### Inicio de sesión

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Éxito (200):** Establece una cookie de autenticación y devuelve:

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

`mfaAvailable` es `true` cuando la `MfaPolicy` del cliente es `Enabled` pero el usuario aún no se ha inscrito (la interfaz puede ofrecer la configuración); en ese caso también se incluye un campo `clientId`.

**MFA requerido (200):** Si el usuario tiene MFA inscrito, **siempre** se le presenta el desafío, independientemente de la `MfaPolicy` del cliente que hace la petición (MFA es una propiedad del usuario/sesión, no del cliente):

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

El cliente debe redirigir a una página de desafío MFA y llamar a `POST /api/auth/mfa/verify`.

**Configuración de MFA requerida (200):** Si `MfaPolicy` es `Required` y el usuario no tiene MFA inscrito:

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

El cliente debe redirigir a una página de configuración de MFA. El token de configuración autentica al usuario en los endpoints de configuración de MFA mediante el encabezado `X-MFA-Setup-Token`.

**Respuestas de error:**

| `error` | Estado | Descripción |
|---|---|---|
| `invalid_credentials` | 401 | Correo electrónico o contraseña incorrectos. Deliberadamente idéntico para correos desconocidos (anti-enumeración). |
| `locked_out` | 423 | Demasiados intentos fallidos. `retryAfter` (segundos) está incluido. |
| `account_disabled` | 403 | La cuenta está desactivada (solo se revela tras una contraseña correcta) |
| `email_not_confirmed` | 403 | Correo electrónico aún no verificado (solo se revela tras una contraseña correcta) |
| `sso_required` | 409 | El dominio requiere SSO. `redirectUrl` apunta al inicio de sesión SSO. |
| `captcha_failed` | 400 | La verificación de Turnstile falló (solo cuando Turnstile está configurado; las peticiones necesitan entonces un campo `turnstileToken`) |
| `email_required` | 400 | El campo de correo electrónico está vacío |
| `password_required` | 400 | El campo de contraseña está vacío |

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

Crea una nueva cuenta de usuario y envía un correo de verificación. Devuelve `201 { "success": true, "userId": "..." }`. Campos opcionales: `locale` (etiqueta BCP-47 que se persiste en el usuario) y `customAttributes` (un mapa de cadenas).

El registro es deliberadamente **neutral ante la enumeración**: si el correo ya está registrado, la respuesta es el mismo `201` neutral (con un `userId` desechable) y en su lugar se envía al propietario real un aviso de inicio de sesión/restablecimiento. El registro también está limitado por IP: `429 rate_limited` cuando se supera el límite (la ventana y el tope se configuran mediante `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes`).

### Confirmar correo electrónico

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

Confirma la dirección de correo electrónico del usuario usando el token del correo de verificación. `GET` es el enlace clicable del correo: redirige a `/login?email_confirmed=1` (más un parámetro `continue_client` cuando el registro se originó en un flujo OAuth). `POST` es la ruta programática y devuelve JSON (el token también puede proporcionarse en un cuerpo JSON como `{ "token": "..." }`); la respuesta incluye un `appLink` opcional (destino "continuar a la aplicación").

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

Las conexiones con `AllowedDomains` configurados quedan **excluidas**: a esas se llega primero por correo mediante `/api/auth/sso-check` en lugar de un botón. `turnstileSiteKey` se establece cuando Cloudflare Turnstile está configurado (la interfaz de inicio de sesión debe entonces enviar un `turnstileToken` con las peticiones de inicio de sesión/registro/contraseña).

### Cierre de sesión

```
POST /api/auth/logout
```

Borra la cookie de autenticación. Devuelve `200 { success: true }`.

### Contraseña olvidada

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

Siempre devuelve `200` (anti-enumeración). Si el usuario existe, se envía un correo de restablecimiento.

### Restablecer contraseña

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | Descripción |
|---|---|
| `weak_password` | No cumple con los requisitos de robustez |
| `invalid_token` | El token está mal formado |
| `token_expired` | El token ha expirado (validez predeterminada de 60 minutos, configurable mediante `Auth:PasswordResetExpiryMinutes`) |

### Sesión

```
GET /api/auth/session
```

Devuelve la información de la sesión actual si está autenticado:

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

Devuelve `401` si no está autenticado.

### Aplicaciones

```
GET /api/auth/apps
```

Devuelve los enlaces a las aplicaciones del inquilino para el lanzador "volver a la aplicación" de la página de cuenta: clientes habilitados que tienen una URI de inicio (`initiateLoginUri` tiene prioridad sobre `clientUri`). Cada entrada es `{ clientId, clientName, homeUri, logoUri, isDefault }`; exactamente una aplicación se marca como predeterminada (el cliente marcado, o el único cliente con una URI de inicio). Requiere autenticación por cookie.

### Perfil (autoservicio)

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

El usuario autenticado lee/actualiza sus propios campos de perfil no sensibles: `firstName`, `lastName`, `companyName`, `phone`, `locale`. Los campos nulos quedan sin cambios; el correo electrónico, la contraseña, los roles, el estado activo y la organización **no** son editables aquí. Ambos devuelven el perfil `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }`.

### Verificación SSO

```
GET /api/auth/sso-check?email=user@acme.com
```

Verifica si el dominio del correo electrónico requiere SSO:

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

### Política de contraseñas

```
GET /api/auth/password-policy
```

Devuelve los requisitos de contraseña del servidor (configurados mediante `PasswordPolicy` en los ajustes):

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

La interfaz de inicio de sesión predeterminada obtiene este endpoint en la página de restablecimiento de contraseña para mostrar los requisitos dinámicamente.

## Requisitos de contraseña predeterminados

Con la configuración predeterminada, las contraseñas deben cumplir todos estos requisitos:

- Al menos 8 caracteres
- Al menos una letra mayúscula
- Al menos una letra minúscula
- Al menos un dígito
- Al menos un carácter no alfanumérico
- Al menos 2 caracteres distintos

Estos pueden personalizarse mediante la sección de configuración `PasswordPolicy`, ver [Configuración](configuration).

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

Verifica un desafío MFA. En caso de éxito, establece la cookie de autenticación y devuelve la información del usuario.

**Métodos:**

| `method` | Campos requeridos | Descripción |
|---|---|---|
| `totp` | `code` (6 dígitos) | Contraseña de un solo uso basada en tiempo desde una aplicación de autenticación |
| `webauthn` | `assertion` (cadena JSON) | Respuesta de aserción WebAuthn de `navigator.credentials.get()` |
| `recovery` | `code` (`XXXX-XXXX`) | Código de recuperación de un solo uso (se consume al usarse) |

**Semántica de reintentos:** un código incorrecto **no** quema el desafío: el código se valida primero y el desafío se consume solo en caso de éxito, por lo que el usuario puede reintentar con el mismo `challengeId` tras teclear mal un dígito (`401 invalid_code` / `assertion_failed`). Cada desafío tolera **5 intentos fallidos**; el quinto fallo lo consume y devuelve `401 too_many_attempts`, forzando un nuevo inicio de sesión (esto acota la fuerza bruta de TOTP a 5 intentos por desafío). Los desafíos también expiran (predeterminado 5 minutos, `Auth:MfaChallengeExpiryMinutes`); un `challengeId` expirado, desconocido o ya consumido devuelve `invalid_challenge`. Los códigos TOTP están además protegidos contra reproducción: se rechaza un código de un paso de tiempo ya utilizado.

### Estado de MFA

```
GET /api/auth/mfa/status
```

Devuelve los métodos MFA inscritos del usuario. Requiere autenticación por cookie o encabezado `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

`offered` es `false` cuando la `MfaPolicy` de todos los clientes es `Disabled`: el inquilino tiene MFA desactivado, por lo que la interfaz de configuración puede ocultarse. Las entradas de códigos de recuperación llevan además `isConsumed`.

### Configuración de TOTP

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
→ { "success": true }
```

### Configuración de WebAuthn / Passkey

```
POST /api/auth/mfa/webauthn/setup
→ { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
→ { "success": true, "credentialId": "..." }
```

La inscripción de passkey requiere **primero una credencial TOTP confirmada** (`400 totp_required_first`): las passkeys son una comodidad por dispositivo superpuesta a un factor base portátil, por lo que una cuenta nunca puede acabar solo con passkey y bloqueada a un dispositivo. Los usuarios cuyo dominio de correo está enrutado por SSO no pueden inscribir una passkey local (`400 sso_managed`): eludiría el IdP del inquilino. Un ID de credencial ya registrado para otro usuario se rechaza con `409 credential_already_registered`.

### Códigos de recuperación

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Genera 10 códigos de recuperación de un solo uso. Requiere que al menos un método primario (TOTP o WebAuthn) esté inscrito. Regenerar reemplaza todos los códigos de recuperación existentes.

### Eliminar credencial MFA

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

Elimina una credencial MFA específica. Si se elimina el último método primario, MFA se desactiva para el usuario. Requiere una sesión de cookie real: un token de configuración se rechaza con `403 session_required` (los tokens de configuración existen solo para añadir un primer factor, nunca para degradar MFA).

### Inicio de sesión con passkey sin contraseña

```
POST /api/auth/mfa/passwordless/begin
→ { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
→ { "userId": "...", "email": "...", "name": "..." }
```

Inicio de sesión con credencial descubrible (passkey residente) sin contexto de usuario previo: `begin` emite un desafío de aserción con una lista `allowCredentials` vacía, y `complete` resuelve al usuario **a partir de** la passkey elegida, verifica la aserción e inicia su sesión (la sesión lleva el marcador de MFA: una passkey es autenticación fuerte resistente al phishing). Si el dominio de correo del usuario resuelto está enrutado por SSO, el inicio de sesión se rechaza con `409 sso_required` + `redirectUrl` para que una passkey local no pueda esquivar un IdP forzado.

## Autorización de dispositivo (RFC 8628)

### Solicitar código de dispositivo

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

Devuelve un código de dispositivo, un código de usuario y una URI de verificación:

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

`expires_in` proviene del `DeviceCodeLifetimeSeconds` del cliente (predeterminado 300). El dispositivo muestra la `verification_uri` y el `user_code` al usuario, luego sondea el endpoint de token con el `device_code`, no más rápido que cada `interval` segundos, o el endpoint de token responde `slow_down` (RFC 8628 §3.5). Mientras el usuario no haya aprobado, el endpoint de token devuelve `authorization_pending`. El usuario visita la URI de verificación, inicia sesión e introduce el código de usuario para aprobar.

### Aprobar dispositivo

```
POST /api/auth/device/approve
Content-Type: application/json

{
  "userCode": "ABCD-EFGH"
}
```

Requiere autenticación por cookie. Aprueba el código de dispositivo para el usuario actual. El dispositivo puede entonces intercambiar el código de dispositivo por tokens mediante el endpoint de token usando el tipo de concesión `urn:ietf:params:oauth:grant-type:device_code`.

El código enviado se normaliza según RFC 8628 §6.1 antes de la búsqueda: se pasa a mayúsculas y se descarta todo carácter fuera del alfabeto de 31 caracteres del código. `ABCD-EFGH`, `abcd-efgh`, `ABCDEFGH`, `ABCD EFGH` y un pegado que convirtió el guion en una raya son todos el mismo código. El guion existe solo para que el código sea más fácil de leer en voz alta. La entrada está limitada a diez intentos por minuto y por sujeto (RFC 8628 §5.1); el undécimo devuelve `429`. Ese contador es por nodo con el limitador en proceso predeterminado, así que un despliegue con varias réplicas debería imponer además el límite en el borde.

## Introspección de tokens (RFC 7662)

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

Los tokens inactivos o inválidos devuelven `{ "active": false }`. Admite tanto tokens de acceso JWT como tokens de actualización opacos.

## Endpoints de consentimiento

### Información de consentimiento

```
GET /consent/info?client_id=my-app&scope=openid%20profile%20email
```

Devuelve los detalles del cliente y los scopes solicitados para la página de consentimiento (`scope` es `openid` de forma predeterminada cuando se omite):

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

Registra la decisión de consentimiento del usuario (requiere autenticación por cookie) y devuelve `{ "redirect": "..." }` para que la SPA navegue a él. Al permitir, los scopes concedidos se persisten (filtrados a los `AllowedScopes` del cliente: un cuerpo manipulado no puede registrar scopes que el cliente no podía solicitar) y la redirección apunta de vuelta al flujo de autorización. Con `"decision": "deny"`, la redirección apunta al `redirect_uri` del cliente con un error `access_denied`.

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

### Revocar concesión

```
DELETE /consent/grants/{clientId}
```

Revoca el consentimiento para una aplicación específica. Se le pedirá al usuario que vuelva a consentir en su próximo inicio de sesión.

## Construir una interfaz de inicio de sesión personalizada

La SPA predeterminada (`login-app/`) es una implementación de esta API. Para construir la suya:

1. Sirva su interfaz en las rutas `/login`, `/forgot-password`, `/reset-password`
2. El endpoint de autorización redirige a los usuarios no autenticados a `/login?returnUrl={encoded-authorize-url}`
3. Después de un inicio de sesión exitoso (cookie establecida), redirija al usuario al `returnUrl`
4. Los enlaces de restablecimiento de contraseña usan `{Issuer}/login/reset-password?p={token}` (la SPA de inicio de sesión se monta bajo `/login`)

Su interfaz debe servirse desde el **mismo origen** que la API porque:
- La autenticación por cookie usa `SameSite=Lax` + `HttpOnly`
- El endpoint de autorización redirige a `/login` (relativo)
- Los enlaces de restablecimiento usan `{Issuer}/login/reset-password`
