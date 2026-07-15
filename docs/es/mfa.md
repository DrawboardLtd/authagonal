---
layout: default
title: Autenticación multifactor
locale: es
---

# Autenticación multifactor (MFA)

Authagonal admite la autenticación multifactor. Hay tres métodos disponibles: TOTP (aplicaciones de autenticación), WebAuthn/llaves de acceso (llaves de hardware y biometría) y códigos de recuperación de un solo uso. Las llaves de acceso también pueden usarse para el [inicio de sesión sin contraseña](#passwordless-passkey-login).

Los inicios de sesión federados (SAML/OIDC) también están cubiertos: una aserción SAML u OIDC prueba el primer factor, no el segundo. Un usuario federado con MFA inscrito se enruta a través del mismo desafío MFA local que un inicio de sesión con contraseña, y una política `Required` fuerza la inscripción antes de emitir cualquier sesión. Solo cuando la MFA no está ni inscrita ni requerida, la federación es autónoma.

## Métodos admitidos

| Método | Descripción |
|---|---|
| **TOTP** | Contraseñas de un solo uso basadas en tiempo (RFC 6238): 6 dígitos, paso de 30 segundos, SHA-1, verificadas con una ventana de desfase de reloj de un paso. Funciona con cualquier aplicación de autenticación (Google Authenticator, Authy, 1Password, etc.). Un código que ya ha sido aceptado no puede reutilizarse dentro de su ventana de validez. |
| **WebAuthn / Llaves de acceso** | Llaves de seguridad de hardware FIDO2, biometría de plataforma (Touch ID, Windows Hello) y llaves de acceso sincronizadas. Los usuarios pueden registrar varias llaves de acceso, y las llaves de acceso pueden iniciar sesión sin contraseña. |
| **Códigos de recuperación** | 10 códigos de respaldo de un solo uso (formato `XXXX-XXXX`) para la recuperación de cuenta cuando otros métodos no están disponibles. Almacenados con hash y cifrados en reposo. |

## Política de MFA

La aplicación de MFA se configura **por cliente** mediante la propiedad `MfaPolicy` en `appsettings.json`:

| Valor | Comportamiento |
|---|---|
| `Disabled` (predeterminado) | No forzar el registro; la interfaz de configuración de autoservicio oculta la MFA cuando todos los clientes son `Disabled` |
| `Enabled` | Ofrecer el registro de MFA; no forzarlo |
| `Required` | Forzar el registro para los usuarios sin MFA |

Un usuario que tiene MFA registrado es **siempre desafiado al iniciar sesión, independientemente de la política del cliente**. La MFA es una propiedad del usuario y de su sesión, no del cliente solicitante, por lo que una solicitud enrutada a través de un cliente `Disabled` no puede usarse para omitir el segundo factor de un usuario registrado.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "MfaPolicy": "Enabled"
    },
    {
      "ClientId": "admin-portal",
      "MfaPolicy": "Required"
    }
  ]
}
```

El valor predeterminado es `Disabled`, por lo que los clientes existentes no se ven afectados hasta que opten por participar.

### Anulación por usuario

Implemente `IAuthHook.ResolveMfaPolicyAsync` para anular la política del cliente para usuarios específicos:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    // Exempt service accounts
    if (email.EndsWith("@service.internal"))
        return Task.FromResult(MfaPolicy.Disabled);

    return Task.FromResult(clientPolicy);
}
```

La política resuelta rige el registro (si se ofrece o se fuerza). No exime a un usuario ya registrado del desafío; los usuarios registrados siempre son desafiados.

Consulte [Extensibilidad](extensibility) para la documentación completa de hooks.

## Flujo de inicio de sesión

El flujo de inicio de sesión con MFA funciona de la siguiente manera:

1. El usuario envía correo electrónico y contraseña a `POST /api/auth/login`
2. El servidor verifica la contraseña y luego resuelve la política MFA efectiva
3. Según la política y el estado de registro del usuario:

| Política | ¿El usuario tiene MFA? | Resultado |
|---|---|---|
| Cualquiera | Sí | Devuelve `mfaRequired`: el usuario debe verificar |
| `Disabled` / `Enabled` | No | Cookie establecida, inicio de sesión completo |
| `Required` | No | Devuelve `mfaSetupRequired`: el usuario debe registrarse |

### Desafío MFA

Cuando se devuelve `mfaRequired`, la respuesta de inicio de sesión incluye un `challengeId`, los `methods` disponibles del usuario y (cuando el usuario tiene llaves de acceso) las opciones de aserción `webAuthn`. El cliente redirige a una página de desafío MFA donde el usuario verifica con uno de sus métodos registrados mediante `POST /api/auth/mfa/verify`:

```json
{
  "challengeId": "...",
  "method": "totp",
  "code": "123456"
}
```

`method` es `totp`, `recovery` o `webauthn` (WebAuthn envía una `assertion` en lugar de un `code`).

Los desafíos expiran después de 5 minutos (configurable mediante `Auth:MfaChallengeExpiryMinutes`) y se consumen tras una verificación exitosa.

#### Presupuesto de reintentos

Un código incorrecto no quema el desafío. El punto de conexión de verificación valida el código primero y consume el desafío solo en caso de éxito, por lo que un dígito TOTP mal tecleado puede simplemente reintentarse contra el mismo `challengeId`. Los intentos fallidos devuelven `invalid_code` (o `assertion_failed` para WebAuthn) con un 401 e incrementan un contador acotado en el desafío; el quinto intento incorrecto consume el desafío y devuelve `too_many_attempts`, forzando un nuevo inicio de sesión. Esto aplica a los tres métodos y acota la fuerza bruta de TOTP a 5 intentos por desafío.

Un desafío ausente, expirado o ya consumido devuelve `invalid_challenge`.

### Inicios de sesión federados

Tras una aserción SAML u OIDC exitosa, el servidor resuelve la misma política MFA efectiva. Un usuario con MFA registrado es redirigido a la página alojada de desafío MFA (con un `challengeId`) en lugar de recibir una sesión; un usuario sin MFA bajo una política `Required` es redirigido a la página de configuración de MFA (con un `setupToken`). La sesión solo se marca como autenticada por MFA una vez que se completa la verificación.

### Registro forzado

Cuando se devuelve `mfaSetupRequired`, la respuesta incluye un `setupToken`. Este token autentica al usuario en los puntos de conexión de configuración de MFA (mediante el encabezado `X-MFA-Setup-Token`) para que pueda registrar un método antes de obtener una sesión de cookie. Los tokens de configuración expiran después de 15 minutos (configurable mediante `Auth:MfaSetupTokenExpiryMinutes`).

## Registrar MFA

Los usuarios registran MFA a través de los puntos de conexión de configuración de autoservicio. Estos requieren una sesión de cookie autenticada o un token de configuración.

### Configuración de TOTP

1. Llamar a `POST /api/auth/mfa/totp/setup` — devuelve un código QR (`data:image/png;base64,...`), una clave manual (`manualKey` en Base32 para entrada manual) y un token de configuración
2. El usuario escanea el código QR con su aplicación de autenticación
3. El usuario introduce el código de 6 dígitos para confirmar: `POST /api/auth/mfa/totp/confirm`

### Configuración de WebAuthn / Llave de acceso

1. Llamar a `POST /api/auth/mfa/webauthn/setup`: devuelve un `setupToken` y `PublicKeyCredentialCreationOptions`
2. El cliente llama a `navigator.credentials.create()` con las opciones
3. Enviar la respuesta de atestación a `POST /api/auth/mfa/webauthn/confirm`

El registro de llaves de acceso requiere primero una credencial TOTP confirmada (`totp_required_first`). Las llaves de acceso son una comodidad por dispositivo superpuesta a un factor base portátil, por lo que cada cuenta conserva un factor independiente del dispositivo y una política `Required` no puede satisfacerse solo con una llave de acceso.

Los usuarios pueden registrar varias llaves de acceso (una por dispositivo). Un ID de credencial ya registrado para un usuario distinto se rechaza (`credential_already_registered`), y los usuarios cuyo dominio de correo se enruta a un IdP externo mediante SSO forzado no pueden registrar una llave de acceso local (`sso_managed`), ya que eludiría al IdP y su desaprovisionamiento.

### Códigos de recuperación

Llamar a `POST /api/auth/mfa/recovery/generate` para generar 10 códigos de un solo uso. Primero debe registrarse al menos un método principal (TOTP o WebAuthn).

La regeneración de códigos reemplaza todos los códigos de recuperación existentes. Cada código solo puede usarse una vez; un código canjeado se marca como consumido y ya no se acepta.

Los códigos nunca se almacenan en texto plano: cada código se somete a hash, y el hash se cifra adicionalmente en reposo con el proveedor de secretos del inquilino, de modo que un volcado del almacenamiento produce texto cifrado en lugar de un hash susceptible de fuerza bruta sin conexión.

## Inicio de sesión sin contraseña con llave de acceso

Las llaves de acceso no son solo un segundo factor: un usuario con una llave de acceso registrada puede iniciar sesión sin contraseña.

1. `POST /api/auth/mfa/passwordless/begin` devuelve un `challengeId` y `options` de aserción para credenciales detectables, de modo que el autenticador ofrece cualquier llave de acceso residente para el sitio
2. El cliente llama a `navigator.credentials.get()` con las opciones
3. `POST /api/auth/mfa/passwordless/complete` con `{ challengeId, assertion }`: el servidor resuelve al usuario a partir de la propia llave de acceso y lo inicia sesión

La página de inicio de sesión alojada conecta esto al campo de correo electrónico mediante mediación condicional (autocompletado de llave de acceso): cuando el navegador lo admite, una llave de acceso disponible se ofrece como sugerencia de autocompletado sin ninguna interfaz adicional.

Una llave de acceso es autenticación fuerte resistente al phishing, por lo que la sesión resultante lleva el marcador de MFA y no vuelve a ser desafiada. Si el dominio de correo del usuario se enruta a un IdP externo mediante SSO forzado, el inicio de sesión sin contraseña se rechaza con una respuesta 409 `sso_required` que incluye la URL de redirección de SSO, de modo que una llave de acceso local no pueda eludir al IdP.

## Gestionar MFA

### Autoservicio del usuario

- `GET /api/auth/mfa/status`: ver los métodos registrados (también informa si algún cliente ofrece MFA)
- `DELETE /api/auth/mfa/credentials/{id}` — eliminar una credencial específica

Eliminar una credencial requiere una sesión autenticada real; un token de configuración solo autoriza agregar un primer factor y obtiene `session_required` aquí, por lo que un token de configuración filtrado no puede degradar la MFA de un usuario.

Si se elimina el último método principal, la MFA se deshabilita para el usuario.

### API de administración

Los administradores pueden gestionar la MFA para cualquier usuario a través de la [API de administración](admin-api):

- `GET /api/v1/profile/{userId}/mfa` — ver el estado de MFA de un usuario
- `DELETE /api/v1/profile/{userId}/mfa` — restablecer toda la MFA (para usuarios bloqueados)
- `DELETE /api/v1/profile/{userId}/mfa/{id}` — eliminar una credencial específica

### Hooks de auditoría

Implemente `IAuthHook.OnMfaVerifiedAsync` para registrar eventos de MFA:

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

El ciclo de vida completo de MFA es enganchable: `OnMfaVerifyFailedAsync` (un intento de verificación fallido), `OnMfaEnrolledAsync` (un método confirmado), `OnMfaCredentialRemovedAsync` (una credencial eliminada, con un indicador de si eso deshabilitó la MFA) y `OnRecoveryCodesRegeneratedAsync`.

## Interfaz de inicio de sesión personalizada

Si está creando una interfaz de inicio de sesión personalizada, gestione estas respuestas de `POST /api/auth/login`:

1. **Inicio de sesión normal** — `{ userId, email, name }` con cookie establecida. Redirigir a `returnUrl`.
2. **MFA requerida** — `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Mostrar el formulario de desafío MFA.
3. **Configuración de MFA requerida** — `{ mfaSetupRequired: true, setupToken }`. Mostrar el flujo de registro de MFA.

Al gestionar errores de `POST /api/auth/mfa/verify`: `invalid_code` y `assertion_failed` son reintentables contra el mismo `challengeId` (hasta el presupuesto de intentos); `too_many_attempts` e `invalid_challenge` son terminales, por lo que debe devolver al usuario al formulario de inicio de sesión.

Consulte la [API de autenticación](auth-api) para la referencia completa de puntos de conexión.
