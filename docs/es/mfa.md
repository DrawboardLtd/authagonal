---
layout: default
title: Autenticación multifactor
locale: es
---

# Autenticación multifactor (MFA)

Authagonal admite la autenticación multifactor para inicios de sesión basados en contraseña. Hay tres métodos disponibles: TOTP (aplicaciones de autenticación), WebAuthn/llaves de acceso (llaves de hardware y biometría) y códigos de recuperación de un solo uso.

Los inicios de sesión federados (SAML/OIDC) omiten la MFA — el proveedor de identidad externo gestiona la autenticación de segundo factor.

## Métodos admitidos

| Método | Descripción |
|---|---|
| **TOTP** | Contraseñas de un solo uso basadas en tiempo (RFC 6238). Funciona con cualquier aplicación de autenticación — Google Authenticator, Authy, 1Password, etc. |
| **WebAuthn / Llaves de acceso** | Llaves de seguridad de hardware FIDO2, biometría de plataforma (Touch ID, Windows Hello) y llaves de acceso sincronizadas. |
| **Códigos de recuperación** | 10 códigos de respaldo de un solo uso (formato `XXXX-XXXX`) para la recuperación de cuenta cuando otros métodos no están disponibles. |

## Política de MFA

La aplicación de MFA se configura **por cliente** mediante la propiedad `MfaPolicy` en `appsettings.json`:

| Valor | Comportamiento |
|---|---|
| `Disabled` (predeterminado) | Sin desafío MFA, incluso si el usuario tiene MFA registrado |
| `Enabled` | Desafiar a los usuarios que tienen MFA registrado; no forzar el registro |
| `Required` | Desafiar a los usuarios registrados; forzar el registro para los usuarios sin MFA |

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

Consulte [Extensibilidad](extensibility) para la documentación completa de hooks.

## Flujo de inicio de sesión

El flujo de inicio de sesión con MFA funciona de la siguiente manera:

1. El usuario envía correo electrónico y contraseña a `POST /api/auth/login`
2. El servidor verifica la contraseña y luego resuelve la política MFA efectiva
3. Según la política y el estado de registro del usuario:

| Política | ¿El usuario tiene MFA? | Resultado |
|---|---|---|
| `Disabled` | — | Cookie establecida, inicio de sesión completo |
| `Enabled` | No | Cookie establecida, inicio de sesión completo |
| `Enabled` | Sí | Devuelve `mfaRequired` — el usuario debe verificar |
| `Required` | No | Devuelve `mfaSetupRequired` — el usuario debe registrarse |
| `Required` | Sí | Devuelve `mfaRequired` — el usuario debe verificar |

### Desafío MFA

Cuando se devuelve `mfaRequired`, la respuesta de inicio de sesión incluye un `challengeId` y los métodos disponibles del usuario. El cliente redirige a una página de desafío MFA donde el usuario verifica con uno de sus métodos registrados mediante `POST /api/auth/mfa/verify`.

Los desafíos expiran después de 5 minutos y son de un solo uso.

### Registro forzado

Cuando se devuelve `mfaSetupRequired`, la respuesta incluye un `setupToken`. Este token autentica al usuario en los puntos de conexión de configuración de MFA (mediante el encabezado `X-MFA-Setup-Token`) para que pueda registrar un método antes de obtener una sesión de cookie.

## Registrar MFA

Los usuarios registran MFA a través de los puntos de conexión de configuración de autoservicio. Estos requieren una sesión de cookie autenticada o un token de configuración.

### Configuración de TOTP

1. Llamar a `POST /api/auth/mfa/totp/setup` — devuelve un código QR (`data:image/svg+xml;base64,...`) y un token de configuración
2. El usuario escanea el código QR con su aplicación de autenticación
3. El usuario introduce el código de 6 dígitos para confirmar: `POST /api/auth/mfa/totp/confirm`

### Configuración de WebAuthn / Llave de acceso

1. Llamar a `POST /api/auth/mfa/webauthn/setup` — devuelve `PublicKeyCredentialCreationOptions`
2. El cliente llama a `navigator.credentials.create()` con las opciones
3. Enviar la respuesta de atestación a `POST /api/auth/mfa/webauthn/confirm`

### Códigos de recuperación

Llamar a `POST /api/auth/mfa/recovery/generate` para generar 10 códigos de un solo uso. Primero debe registrarse al menos un método principal (TOTP o WebAuthn).

La regeneración de códigos reemplaza todos los códigos de recuperación existentes. Cada código solo puede usarse una vez.

## Gestionar MFA

### Autoservicio del usuario

- `GET /api/auth/mfa/status` — ver los métodos registrados
- `DELETE /api/auth/mfa/credentials/{id}` — eliminar una credencial específica

Si se elimina el último método principal, la MFA se deshabilita para el usuario.

### API de administración

Los administradores pueden gestionar la MFA para cualquier usuario a través de la [API de administración](admin-api):

- `GET /api/v1/profile/{userId}/mfa` — ver el estado de MFA de un usuario
- `DELETE /api/v1/profile/{userId}/mfa` — restablecer toda la MFA (para usuarios bloqueados)
- `DELETE /api/v1/profile/{userId}/mfa/{id}` — eliminar una credencial específica

### Hook de auditoría

Implemente `IAuthHook.OnMfaVerifiedAsync` para registrar eventos de MFA:

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

## Interfaz de inicio de sesión personalizada

Si está creando una interfaz de inicio de sesión personalizada, gestione estas respuestas de `POST /api/auth/login`:

1. **Inicio de sesión normal** — `{ userId, email, name }` con cookie establecida. Redirigir a `returnUrl`.
2. **MFA requerida** — `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Mostrar el formulario de desafío MFA.
3. **Configuración de MFA requerida** — `{ mfaSetupRequired: true, setupToken }`. Mostrar el flujo de registro de MFA.

Consulte la [API de autenticación](auth-api) para la referencia completa de puntos de conexión.
