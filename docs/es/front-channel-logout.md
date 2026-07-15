---
layout: default
title: Cierre de sesión por canal frontal
locale: es
---

# Cierre de sesión por canal frontal

Authagonal implementa **OpenID Connect Front-Channel Logout 1.0**, un mecanismo de cierre de sesión impulsado por el navegador que complementa el [cierre de sesión por canal trasero](index#features). Mientras que el cierre de sesión por canal trasero es un POST de servidor a servidor, el cierre de sesión por canal frontal renderiza la URL de cierre de sesión de cada parte confiante en un iframe oculto, de modo que la sesión de navegador de cada aplicación (cookies, almacenamiento local) se limpia desde dentro del navegador del usuario.

## Cuándo usar cada uno

| Aspecto | Canal trasero | Canal frontal |
|---|---|---|
| Sesiones del lado del servidor | ✅ | ❌ |
| Cookies del navegador / almacenamiento local | ❌ | ✅ |
| Funciona cuando el navegador del usuario está sin conexión | ✅ | ❌ |
| Sobrevive a errores de red (reintento) | ✅ | ❌ (un único intento de mejor esfuerzo) |

La mayoría de las aplicaciones se benefician de configurar **ambos**. El canal trasero garantiza que se notifique al servidor; el canal frontal limpia el navegador.

## Configuración del cliente

Añada una URI de cierre de sesión por canal frontal al registro `OAuthClient`:

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| Campo | Descripción |
|---|---|
| `FrontChannelLogoutUri` | El endpoint de cierre de sesión del cliente visible para el navegador |
| `FrontChannelLogoutSessionRequired` | Si es `true` (predeterminado), la URL se llama con los parámetros de consulta `iss` y `sid` para que el cliente pueda correlacionar el cierre de sesión con la sesión específica |

## Cómo funciona

Cuando el navegador visita `/connect/endsession`:

1. El servidor encuentra todos los clientes con los que el usuario tiene concesiones actualmente.
2. Para cada cliente con una `FrontChannelLogoutUri`, el servidor construye una URL, añadiendo `iss=<issuer>` (y `sid=<session_id>`, cuando la sesión tiene uno) si `FrontChannelLogoutSessionRequired` es `true`.
3. El servidor cierra la sesión del usuario en el cookie del servidor de autorización, desencadena en segundo plano las notificaciones de cierre de sesión por canal trasero y devuelve una página HTML que contiene un `<iframe>` oculto por cada URL de cierre de sesión de cliente:
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
4. Tras un periodo de gracia de 2 segundos, el navegador se redirige a `post_logout_redirect_uri`, que se respeta solo cuando la solicitud también lleva un `id_token_hint` que identifica al cliente y la URI está en las `PostLogoutRedirectUris` registradas de ese cliente (un parámetro `state`, si se proporciona, se añade a la redirección). De lo contrario, se muestra una confirmación de "sesión cerrada".

## Controlador de cierre de sesión del lado del cliente

Cada parte confiante debe implementar la URL a la que hace referencia `FrontChannelLogoutUri`. Un controlador mínimo:

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. Verifique que `iss` coincida con el servidor de autorización esperado.
2. Si se proporciona `sid`, confirme que coincide con el ID de sesión del cookie de sesión.
3. Borre la sesión local (cookies, sesión del lado del servidor, almacenamiento de la SPA).
4. Responda con `200 OK` y un cuerpo vacío (o una página diminuta); la respuesta nunca es visible para el usuario.

```csharp
app.MapGet("/oidc/frontchannel", (HttpContext ctx) =>
{
    var iss = ctx.Request.Query["iss"].ToString();
    var sid = ctx.Request.Query["sid"].ToString();
    // Validate iss/sid, then clear local session
    ctx.SignOutAsync();
    return Results.Ok();
});
```

## Documento de descubrimiento

El cierre de sesión por canal frontal se anuncia en `/.well-known/openid-configuration`:

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## Registro dinámico de clientes

Los clientes registrados mediante el [registro dinámico de clientes](client-registration) pueden incluir:

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

## Limitaciones

- **Mejor esfuerzo**: los iframes se cargan una sola vez. Si un error de red o una extensión del navegador los bloquea, no hay reintento. Combínelo con el cierre de sesión por canal trasero para mayor fiabilidad.
- **Cookies de terceros**: algunos navegadores bloquean las cookies en iframes entre sitios de forma predeterminada. Si su RP depende de cookies de origen propio, confirme que el controlador de cierre de sesión no dependa del envío de cookies.
- **Tiempo de espera**: la página espera ~2 segundos antes de redirigir o confirmar. Los controladores de cierre de sesión de RP pesados podrían no completarse a tiempo.

## Relacionado

- [Registro dinámico de clientes](client-registration): parámetros de canal frontal en la solicitud de registro
- [Scopes de OAuth](scopes): el consentimiento con reconocimiento de scopes complementa el flujo de cierre de sesión
