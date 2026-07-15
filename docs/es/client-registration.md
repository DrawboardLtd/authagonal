---
layout: default
title: Registro dinámico de clientes
locale: es
---

# Registro dinámico de clientes

Authagonal implementa el **registro dinámico de clientes de OAuth 2.0** ([RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)), lo que permite que las aplicaciones cliente se registren a sí mismas en tiempo de ejecución sin intervención del administrador.

## Habilitar el endpoint

El registro dinámico está **deshabilitado de forma predeterminada**. Actívelo mediante la configuración:

```json
{
  "Auth": {
    "DynamicClientRegistrationEnabled": true
  }
}
```

O establezca `Auth__DynamicClientRegistrationEnabled=true` como una variable de entorno.

Cuando está habilitado, el documento de descubrimiento anuncia el endpoint:

```
GET /.well-known/openid-configuration
```
```json
{
  "registration_endpoint": "https://auth.example.com/connect/register"
}
```

## Registrar un cliente

```
POST /connect/register
Content-Type: application/json

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access",
  "audiences": ["https://api.myapp.example.com"],
  "allowed_cors_origins": ["https://myapp.example.com"],
  "backchannel_logout_uri": "https://myapp.example.com/oidc/backchannel",
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

### Respuesta

```
HTTP/1.1 201 Created
Content-Type: application/json

{
  "client_id": "a1b2c3d4e5f6...",
  "client_secret": "xkCd2_base64url...",
  "client_id_issued_at": 1745000000,
  "client_secret_expires_at": 0,
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email offline_access",
  "token_endpoint_auth_method": "client_secret_basic"
}
```

El `client_secret` se devuelve **una sola vez** y no puede recuperarse más tarde. Almacénelo de forma segura.

## Parámetros de la solicitud

| Parámetro | Requerido | Notas |
|---|---|---|
| `client_name` | no | Por defecto es el `client_id` generado si se omite |
| `redirect_uris` | condicional | Requerido cuando `grant_types` contiene `authorization_code`. Deben ser URIs absolutas; los esquemas `javascript:`/`data:`/`vbscript:`/`file:` se rechazan (los esquemas personalizados nativos para enlaces profundos móviles son válidos). |
| `post_logout_redirect_uris` | no | Destinos de redirección válidos tras el cierre de sesión |
| `grant_types` | no | Por defecto es `["authorization_code"]`. **Solo `authorization_code` y `refresh_token` son registrables**: `client_credentials`, `implicit`, el de dispositivo y cualquier otro tipo de concesión se rechazan con `invalid_client_metadata`, de modo que el registro abierto nunca puede crear un cliente de máquina a máquina. `refresh_token` se añade automáticamente si se solicita `offline_access`. |
| `token_endpoint_auth_method` | no | `client_secret_basic` (predeterminado), `client_secret_post`, o `none` para clientes públicos |
| `scope` | no | Scopes separados por espacios: todos deben ser integrados o estar previamente registrados (ver [Scopes](scopes)). El scope administrativo (`AdminApi:Scope`, predeterminado `authagonal-admin`) nunca puede registrarse. |
| `audiences` | no | Valores `aud` de JWT añadidos a los tokens de acceso |
| `allowed_cors_origins` | no | Orígenes autorizados a llamar al endpoint de token desde un navegador |
| `backchannel_logout_uri` | no | Habilita el [cierre de sesión por canal trasero](index#features) |
| `frontchannel_logout_uri` | no | Habilita el [cierre de sesión por canal frontal](front-channel-logout) |
| `frontchannel_logout_session_required` | no | Por defecto es `true`; cuando es `true`, la URL de cierre de sesión lleva los parámetros `iss` y `sid` |

## Valores predeterminados e invariantes

- **PKCE requerido**: `RequirePkce` siempre es `true` para los clientes registrados dinámicamente.
- **Clientes públicos**: `token_endpoint_auth_method: "none"` produce un cliente sin secreto. PKCE sigue siendo obligatorio.
- **Acceso sin conexión**: solicitar el scope `offline_access` añade implícitamente `refresh_token` a `grant_types`.

## Respuestas de error

| HTTP | `error` | Causa |
|---|---|---|
| `400` | `invalid_redirect_uri` | Una de las `redirect_uris` no es una URI absoluta válida, o usa un pseudoesquema script/data/file |
| `400` | `invalid_client_metadata` | Se solicitó un tipo de concesión no registrable, o faltan las `redirect_uris` para un tipo de concesión que las requiere |
| `400` | `invalid_scope` | Un scope solicitado no es ni integrado ni registrado |
| `403` | `invalid_scope` | Se solicitó el scope administrativo: nunca puede otorgarse mediante el registro |
| `403` | `not_supported` | El registro dinámico de clientes no está habilitado |
| `429` | `rate_limited` | Demasiados registros desde esta IP (10 por hora) |

## Consideraciones de seguridad

El endpoint de registro **no está autenticado**, pero está restringido por diseño:

- **Con límite de velocidad**: 10 registros por IP en una ventana móvil de una hora (`429 rate_limited`), de modo que el almacén de clientes no puede inundarse.
- **Tipos de concesión restringidos**: solo `authorization_code` + `refresh_token`; un cliente registrado siempre requiere un flujo mediado por el usuario y nunca puede actuar como un cliente de máquina a máquina.
- **Scope de administración reservado**: el scope `authagonal-admin` (o el valor que tenga `AdminApi:Scope`) se rechaza, de modo que el registro nunca puede producir un cliente que alcance la [API de administración](admin-api).
- **PKCE siempre obligatorio** en los clientes registrados.

Para un control más estricto (tokens de acceso iniciales, mTLS, software statements), anteponga su propio middleware o un `IAuthHook` al endpoint. Considere deshabilitar el registro dinámico por completo y gestionar los clientes mediante la API de administración en entornos donde el registro de autoservicio no sea un requisito.
