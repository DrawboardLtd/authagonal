---
layout: default
title: Solicitudes de autorización enviadas
locale: es
---

# Solicitudes de autorización enviadas (PAR)

[RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) permite que un cliente envíe por POST los parámetros de su solicitud de autorización directamente al servidor con la autenticación de cliente estándar y reciba un `request_uri` opaco y de vida corta para entregar al navegador. El navegador visita entonces `/connect/authorize?request_uri=...&client_id=...` en lugar de llevar cada parámetro en la URL.

Por qué usarlo:

- Los parámetros de autorización nunca aparecen en el historial del navegador, en los registros del servidor ni en los encabezados `Referer`.
- El servidor autentica al cliente en el momento del envío, por lo que se comprueba la integridad de los parámetros antes de que ocurra cualquier redirección.
- Los conjuntos de parámetros largos (solicitudes `claims` grandes, flujos con múltiples recursos) no rebasan los límites de longitud de la URL.

## Endpoint

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

La autenticación es la misma que la de `/connect/token`: HTTP Basic con `client_id`/`client_secret`, o credenciales codificadas en el formulario. Los clientes confidenciales deben autenticarse; los clientes públicos envían sin secreto. Los fallos de autenticación de cliente devuelven `401` (según la RFC 9126, a diferencia del endpoint de token, donde solo `invalid_client` es un 401).

El cuerpo del formulario lleva los mismos parámetros que normalmente irían en `/connect/authorize` (`response_type`, `redirect_uri`, `scope`, `state`, `code_challenge`, `code_challenge_method`, `nonce`, `resource`, etc.). El propio `request_uri` se rechaza: encadenar un PAR está prohibido por el §2.1 de la especificación. Si el cuerpo lleva un `client_id`, debe coincidir con el cliente autenticado.

### Respuesta

```
HTTP/1.1 201 Created
```
```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

El `request_uri` es de un solo uso. Se elimina del almacén una vez que la solicitud `/connect/authorize` correspondiente lo consume (o cuando expira la ventana de 90 segundos, lo que ocurra primero).

### Paso de autorización

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

Cuando `request_uri` está presente, todos los demás parámetros se toman de la carga útil enviada: se ignora cualquier otra cosa en la URL. El `client_id` de esta solicitud debe coincidir con el cliente que envió la carga útil.

## Requerir PAR por cliente

Establezca `RequirePushedAuthorizationRequests = true` en un cliente para rechazar sus solicitudes `/connect/authorize` simples. Cualquier intento de autorización que no sea PAR devuelve `invalid_request` con la descripción "This client requires requests to be pushed via /connect/par".

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

Esta es la postura recomendada para los clientes que manejan scopes sensibles: combinada con PKCE, elimina la barra de direcciones como superficie de ataque.

## Tiempo de vida y almacenamiento

El tiempo de vida del `request_uri` lo fija el servidor en 90 segundos, coincidiendo con el valor típico del IdP de referencia. Las cargas útiles enviadas se almacenan mediante el mismo `IGrantStore` que los códigos de autorización y los tokens de actualización, por lo que heredan automáticamente la estrategia de persistencia y replicación del host.

## Descubrimiento

El endpoint de PAR se anuncia a sí mismo en `.well-known/openid-configuration` como:

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```
