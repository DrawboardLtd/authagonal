---
layout: default
title: Federación OIDC
locale: es
---

# Federación OIDC

Authagonal puede federar la autenticación a proveedores de identidad OIDC externos (Google, Apple, Azure AD, etc.). Esto permite flujos de tipo "Iniciar sesión con Google" mientras Authagonal sigue siendo el servidor de autenticación central.

## Cómo funciona

Hay dos rutas de entrada a la federación:

**Basada en dominio (inicio de sesión interactivo):**

1. El usuario ingresa su correo electrónico en la página de inicio de sesión
2. La SPA llama a `/api/auth/sso-check`: si el dominio del correo está vinculado a un proveedor OIDC, se requiere SSO
3. El usuario hace clic en "Continuar con SSO" → es redirigido al IdP externo
4. Después de autenticarse, el IdP redirige de vuelta a `/oidc/callback`
5. Authagonal valida el id_token, crea/vincula al usuario y establece una cookie de sesión

**Sugerida por el RP (`idp_hint`):**

La parte confiante posterior puede enrutar directamente a un IdP upstream específico sin pasar por el paso de correo/dominio SSO. Agregue `idp_hint={connectionId}` a `/connect/authorize`:

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

Cuando la solicitud no está autenticada, Authagonal redirige a `/oidc/{connectionId}/login` con la URL original de `/authorize` preservada como `returnUrl`. Una vez completada la federación, el usuario vuelve a `/authorize` con una cookie de sesión y el flujo continúa normalmente.

## Configuración

### 1. Crear un proveedor OIDC

**Opción A, configuración (recomendada para configuraciones estáticas):**

Agregue en `appsettings.json`:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

Los proveedores se inyectan al inicio. Los campos inyectables son exactamente los mostrados: `ConnectionId`, `ConnectionName`, `MetadataLocation`, `ClientId`, `ClientSecret`, `RedirectUrl`, `AllowedDomains`. El `ClientSecret` se protege mediante `ISecretProvider` (Key Vault cuando está configurado, texto plano en caso contrario). Los mapeos de dominios SSO se registran automáticamente desde `AllowedDomains`.

El modelo de conexión incorpora comportamiento opcional adicional: `PassthroughParams` (configurable mediante la creación en la API de administración), más `SessionExpClaim` y `DisableJitProvisioning` (campos a nivel de almacén, establecidos mediante `IOidcProviderStore` desde el código de alojamiento). Ver [Flujo de scopes y claims](#scope-and-claim-flow-through) y [Límite de duración de sesión](#session-lifetime-cap) más abajo.

**Opción B, API de administración (para gestión en tiempo de ejecución):**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"]
  }'
```

### 2. Enrutamiento de dominio SSO

Cuando se especifica `AllowedDomains` (en la configuración o mediante la API de creación), los mapeos de dominios SSO se registran automáticamente. Sin enrutamiento de dominio, los usuarios aún pueden ser dirigidos al inicio de sesión OIDC mediante `/oidc/{connectionId}/login`.

## Endpoints

| Endpoint | Descripción |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...` | Inicia el inicio de sesión OIDC. Genera PKCE + state + nonce, redirige al endpoint de autorización del IdP. |
| `GET /oidc/callback` | Maneja la devolución de llamada del IdP. Intercambia el código por tokens, valida el id_token, crea/inicia sesión del usuario. |

## Scope and claim flow-through

El conjunto de scopes solicitado por el RP posterior en `/connect/authorize` se reenvía al IdP upstream, **filtrado al conjunto OIDC estándar** (`openid`, `profile`, `email`, `address`, `phone`), con `openid` siempre incluido. Cualquier otra cosa que el RP haya solicitado (scopes de API personalizados, `offline_access`, ...) se descarta antes de la llamada upstream: un IdP estricto como Google devuelve `invalid_scope` ante valores desconocidos, y el upstream solo necesita identificar al usuario. Los scopes propios del RP se respetan en los tokens emitidos por Authagonal, no en los del upstream. Cualesquiera claims que el IdP upstream limite por scope en el id_token vuelven a Authagonal, se guardan en el ticket de la cookie como claims `federated:<name>` y viajan a `OidcSubject.FederationClaims` en el siguiente recorrido de `/connect/authorize`. Desde ahí `ProtocolTokenService` los reemite en los tokens emitidos por Authagonal, restringidos por la misma lista blanca `Scope.UserClaims` que restringe `CustomAttributes`. Los valores de federación ganan en caso de colisión de claves.

Efecto neto: no hay una lista de permitidos de claims a preservar por conexión. Cada claim no protocolario que el upstream ponga en el id_token se captura; cuáles de ellos llegan a los tokens posteriores lo controla el `UserClaims` del scope posterior: declare el claim ahí y el valor fluye.

`FederationClaims` sobrevive a las rotaciones de refresco de forma distinta a `CustomAttributes`, por lo que el contexto de federación por sesión (por ejemplo, un token de enlace compartido capturado en el authorize original) se mantiene intacto mientras que los atributos por usuario se vuelven a leer frescos del almacén de usuarios.

## Passthrough query parameters

`OidcProviderConfig.PassthroughParams` es una lista blanca por conexión de claves de consulta que fluyen desde la solicitud original de `/authorize` a la URL de autorización del IdP upstream. El conjunto estándar (`scope`, `state`, `nonce`, PKCE) siempre se reenvía; esto es para valores adicionales especificados por el RP, como una credencial de un solo uso que el upstream necesita para autenticar (por ejemplo, `link_token` para IdPs de enlace compartido).

Cuando una clave está en la lista blanca, Authagonal toma su valor de la consulta original de `/authorize` (transportada mediante `returnUrl`) y la adjunta a la URL upstream. Cualquier cosa que no esté en la lista blanca se descarta en silencio.

## Session lifetime cap

`OidcProviderConfig.SessionExpClaim` es el nombre opcional de un claim del id_token (segundos Unix) cuyo valor limita la duración de la sesión local. Cuando está presente, el valor upstream viaja como `session_max_exp` en el ticket de la cookie y hacia el código de autorización emitido; los tokens de acceso / id / refresco se acotan de modo que ningún token, incluidos los acuñados a partir de rotaciones, sobreviva a la sesión upstream. Útil cuando el IdP upstream impone límites de sesión más cortos de los que Authagonal aplicaría de forma predeterminada.

## Características de seguridad

- **PKCE**: code_challenge con S256 en cada solicitud de autorización
- **Validación de nonce**: el nonce se almacena junto con el state, debe estar presente en el id_token y coincidir
- **Validación de state**: de un solo uso (consumido atómicamente mediante `IOidcStateStore`, persistido con expiración) **y vinculado al navegador**: se establece una cookie `SameSite=Lax` con alcance a `/oidc` al iniciar sesión, que debe coincidir con el `state` en la devolución de llamada, de modo que un atacante no pueda completar un flujo de federación que inició y entregar la URL de devolución de llamada a una víctima (CSRF de inicio de sesión)
- **Validación de firma del id_token**: las claves se obtienen del endpoint JWKS del IdP; se validan el emisor, la audiencia y la vigencia
- **Respaldo a userinfo**: si el id_token no contiene un email, se intenta el endpoint userinfo. El `sub` de userinfo debe coincidir con el `sub` del id_token (OIDC Core 5.3.2); de lo contrario, la respuesta se ignora
- **Vinculación de identidad estable**: un usuario que regresa se resuelve por proveedor + `sub`, nunca solo por email. Adjuntar una identidad federada a una cuenta local **preexistente** por email requiere que el `AllowedDomains` de la conexión cubra el dominio de ese email: el aval explícito del administrador de que el IdP es su propietario. Un `email_verified` afirmado por el upstream *no* es suficiente para apoderarse de una cuenta existente
- **Aplicación de dominio**: cuando se establece `AllowedDomains`, la conexión solo puede afirmar identidades dentro de esos dominios (`access_denied` en caso contrario)
- **Exclusión de JIT**: `DisableJitProvisioning` rechaza a los usuarios desconocidos en lugar de crearlos automáticamente
- **Guarda de redirección abierta**: `returnUrl` debe ser una ruta relativa del mismo sitio; se rechazan las formas relativas al protocolo (`//`) y con barra invertida
- **La MFA local sigue aplicando**: la federación prueba solo el primer factor. Un usuario que tiene MFA inscrito (o cuya política de cliente requiere MFA) se enruta a través de las páginas locales de desafío/configuración de MFA después de la devolución de llamada, en lugar de iniciar sesión directamente; solo entonces la sesión lleva el marcador de MFA

## Especificaciones de Azure AD

Azure AD a veces devuelve los correos electrónicos como un arreglo JSON en el claim `emails` (especialmente para B2C). Authagonal maneja esto verificando tanto el claim `email` como el arreglo `emails`.

## Proveedores soportados

Cualquier proveedor compatible con OIDC que soporte:
- Flujo de Authorization Code
- PKCE (S256)
- Documento de descubrimiento (`.well-known/openid-configuration`)

Probado con:
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C
