---
layout: default
title: Federacion OIDC
locale: es
---

# Federacion OIDC

Authagonal puede federar la autenticacion a proveedores de identidad OIDC externos (Google, Apple, Azure AD, etc.). Esto permite flujos de tipo "Iniciar sesion con Google" mientras Authagonal sigue siendo el servidor de autenticacion central.

## Como funciona

1. El usuario ingresa su correo electronico en la pagina de inicio de sesion
2. La SPA llama a `/api/auth/sso-check` -- si el dominio del correo esta vinculado a un proveedor OIDC, se requiere SSO
3. El usuario hace clic en "Continuar con SSO" y es redirigido al IdP externo
4. Despues de autenticarse, el IdP redirige a `/oidc/callback`
5. Authagonal valida el id_token, crea/vincula al usuario y establece una cookie de sesion

## Configuracion

### 1. Crear un proveedor OIDC

**Opcion A -- Configuracion (recomendado para configuraciones estaticas):**

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

Los proveedores se inyectan al inicio. El `ClientSecret` se protege mediante `ISecretProvider` (Key Vault cuando esta configurado, texto plano en caso contrario). Los mapeos de dominios SSO se registran automaticamente desde `AllowedDomains`.

**Opcion B -- API de administracion (para gestion en tiempo de ejecucion):**

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

Cuando se especifica `AllowedDomains` (en la configuracion o mediante la API de creacion), los mapeos de dominios SSO se registran automaticamente. Sin enrutamiento de dominio, los usuarios aun pueden ser dirigidos al inicio de sesion OIDC mediante `/oidc/{connectionId}/login`.

## Endpoints

| Endpoint | Descripcion |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...` | Inicia el inicio de sesion OIDC. Genera PKCE + state + nonce, redirige al endpoint de autorizacion del IdP. |
| `GET /oidc/callback` | Maneja la devolucion de llamada del IdP. Intercambia el codigo por tokens, valida el id_token, crea/inicia sesion del usuario. |

## Caracteristicas de seguridad

- **PKCE** -- code_challenge con S256 en cada solicitud de autorizacion
- **Validacion de nonce** -- el nonce se almacena en el state, se verifica en el id_token
- **Validacion de state** -- de un solo uso, almacenado en Azure Table Storage con expiracion
- **Validacion de firma del id_token** -- las claves se obtienen del endpoint JWKS del IdP
- **Respaldo a userinfo** -- si el id_token no contiene un email, se intenta el endpoint userinfo

## Especificaciones de Azure AD

Azure AD a veces devuelve los correos electronicos como un arreglo JSON en el claim `emails` (especialmente para B2C). Authagonal maneja esto verificando tanto el claim `email` como el arreglo `emails`.

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
