---
layout: default
title: SAML
locale: es
---

# SAML 2.0 SP

Authagonal incluye una implementación propia de proveedor de servicios SAML 2.0. Sin biblioteca SAML de terceros: construido sobre `System.Security.Cryptography.Xml.SignedXml` (parte de .NET).

## Alcance

- **SSO iniciado por el SP** (el usuario comienza en Authagonal, se redirige al IdP)
- **Binding HTTP-Redirect** para AuthnRequest (opcionalmente firmado, ver más abajo)
- **Binding HTTP-POST** para la respuesta (ACS)
- **Aserciones cifradas** (`EncryptedAssertion`) descifradas con un par de claves de SP por conexión
- **Cierre de sesión único (Single Logout)** (iniciado por el SP e iniciado por el IdP, bindings Redirect y POST)
- Azure AD / Entra ID es el objetivo principal, pero cualquier IdP compatible funciona (se manejan los nombres de atributo de Okta, OneLogin, Ping, Google Workspace, ADFS y Shibboleth)

### No soportado

- Binding Artifact
- Cifrado de aserciones AES-GCM (limitación de `EncryptedXml` de .NET; configure AES-CBC en el IdP, ver más abajo)

El SSO iniciado por el IdP está soportado **por conexión y desactivado por defecto**: establezca `allowUnsolicitedResponses: true` en la conexión para aceptarlo. Sin él, el ACS rechaza una Response sin `InResponseTo` y redirige con `error=saml_unsolicited`. Desactivado por defecto porque aceptar respuestas no solicitadas permite que cualquiera con una cuenta en el IdP inicie una sesión desde cualquier user-agent, y porque exigir la cookie de solicitud en la ruta iniciada por el SP no vale nada mientras la misma aserción pueda reproducirse sin `InResponseTo`. Cuando está activo, se omite la comprobación del ID de solicitud para las respuestas no solicitadas, pero el uso único del ID de aserción se sigue aplicando (ver Seguridad).

## Configuración de Azure AD

### 1. Crear un proveedor SAML

**Opción A: Configuración (recomendado para configuraciones estáticas)**

Agregue en `appsettings.json`:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "acme-azure",
      "ConnectionName": "Acme Corp Azure AD",
      "EntityId": "https://auth.example.com/saml/acme-azure",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
      "AllowedDomains": ["acme.com"]
    }
  ]
}
```

Los proveedores se inyectan al inicio. Los mapeos de dominios SSO se registran automáticamente desde `AllowedDomains`. Los proveedores inyectados desde la configuración requieren una URL en `MetadataLocation` y no obtienen un par de claves de SP (por lo que no hay AuthnRequests firmados, aserciones cifradas ni mensajes de cierre de sesión firmados); use la API de administración para esas funciones.

`EntityId` es **su ID de entidad de SP** (el identificador que registra en el IdP), no el ID de entidad del IdP.

**Opción B: API de administración (para gestión en tiempo de ejecución)**

```bash
curl -X POST https://auth.example.com/api/v1/saml/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Acme Corp Azure AD",
    "entityId": "https://auth.example.com/saml/acme-azure",
    "metadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
    "allowedDomains": ["acme.com"]
  }'
```

La API genera el `connectionId` (un GUID) y lo devuelve en la cabecera `Location` y en el cuerpo de la respuesta. Campos opcionales adicionales: `metadataXml` (metadatos pegados, ver más abajo), `nameIdFormat` (ver más abajo), `signAuthnRequests` (forzar AuthnRequests firmados), `iconUrl` (icono del botón de inicio de sesión), `disableJitProvisioning` (rechazar usuarios desconocidos en lugar de crearlos automáticamente), `allowUnsolicitedResponses` (aceptar el inicio de sesión iniciado por el IdP: desactivado por defecto, ver más arriba). Las conexiones creadas mediante la API también obtienen un par de claves de SP autogenerado (ver Par de claves de SP más abajo).

Las conexiones se gestionan mediante `POST` / `GET` / `PUT` / `DELETE` en `/api/v1/saml/connections[/{connectionId}]`. `PUT` es una actualización parcial: solo se modifican los campos suministrados en la petición.

### 2. Configurar Azure AD

1. En Azure AD, vaya a Aplicaciones empresariales, Nueva aplicación, Crear la suya propia
2. Configure el inicio de sesión único, SAML
3. **Identificador (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **URL de respuesta (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **URL de inicio de sesión:** `https://auth.example.com/saml/acme-azure/login`

### 3. Enrutamiento de dominio SSO

Cuando se especifica `AllowedDomains` (en la configuración o mediante la API de creación), los mapeos de dominios SSO se registran automáticamente. Cuando un usuario ingresa `user@acme.com` en la página de inicio de sesión, la SPA detecta que se requiere SSO y muestra "Continuar con SSO". Un dominio solo puede asignarse a una conexión; la API rechaza un dominio ya reclamado por una conexión distinta.

También puede gestionar dominios en tiempo de ejecución mediante la API de administración; ver [API de administración](admin-api).

## Metadatos XML pegados

Algunos IdP no publican una URL de metadatos (Google Workspace), o su endpoint de metadatos es inaccesible desde el SP (ADFS en red privada). Para esos casos, pegue el documento de metadatos en su lugar: suministre `metadataXml` en la creación/actualización. Debe proporcionarse exactamente uno entre `metadataLocation` o `metadataXml`; suministrar uno en una actualización borra el otro.

Los metadatos pegados se validan en el momento de guardar y se **condensan** (`SamlMetadataParser.Condense`) a un `EntityDescriptor` mínimo y canónico que contiene exactamente lo que consume el SP: entityID, certificados de firma, el endpoint SSO, el endpoint SLO si está presente y la marca `WantAuthnRequestsSigned`. Los documentos de proveedores pueden superar los 100KB (el `FederationMetadata.xml` de ADFS), por encima del límite de 64KB de una propiedad de Azure Table, mientras que las partes que usa el SP ocupan unos pocos KB. Los pegados que no se pueden analizar se rechazan con un 400; el documento debe contener un `IDPSSODescriptor` con un certificado de firma y un `SingleSignOnService`.

## Formato de NameID

El campo `nameIdFormat` controla el Format de `NameIDPolicy` solicitado en la AuthnRequest:

| Valor | Comportamiento |
|---|---|
| omitido / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress` (el valor predeterminado histórico) |
| `"none"` | Omite por completo el elemento `NameIDPolicy`. El ajuste seguro para ADFS: ADFS falla todo el inicio de sesión (MSIS7070) cuando sus reglas de claims no emiten el formato solicitado. |
| cualquier otro valor | Se envía tal cual como el Format URN (debe comenzar con `urn:`) |

En una actualización, `""` restablece el valor predeterminado emailAddress. Los metadatos del SP anuncian el formato solicitado por la conexión (y omiten `NameIDFormat` cuando se establece en `"none"`).

## Endpoints

| Endpoint | Descripción |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | Inicia el SSO iniciado por el SP. Construye una AuthnRequest (firmada cuando corresponde) y redirige al IdP. `loginHint` se pasa como `login_hint` para los IdP que lo respetan (Entra, Google). |
| `POST /saml/{connectionId}/acs` | Servicio consumidor de aserciones. Recibe la respuesta SAML, la valida, crea/inicia sesión del usuario. |
| `GET /saml/{connectionId}/metadata` | XML de metadatos SP para configurar el IdP. |
| `GET /saml/{connectionId}/logout?returnUrl=...` | Cierre de sesión único iniciado por el SP. Finaliza la sesión local y luego envía una LogoutRequest al IdP cuando este soporta SLO. |
| `GET/POST /saml/{connectionId}/slo` | Endpoint de cierre de sesión único. Recibe LogoutRequests iniciadas por el IdP (binding Redirect o POST) y el tramo de LogoutResponse del SLO iniciado por el SP. |

La URL de retorno posterior al inicio de sesión se transporta del lado del servidor en la AuthnRequest almacenada (indexada por el ID de solicitud), no en RelayState: la especificación SAML limita RelayState a 80 bytes y algunos IdP lo truncan. RelayState solo se consulta para los flujos iniciados por el IdP.

## Par de claves de SP y aserciones cifradas

Cada conexión creada mediante la API obtiene un par de claves de SP autogenerado: un certificado RSA de 2048 bits autofirmado (validez de 10 años), almacenado como PKCS#12 y protegido en reposo por el proveedor de secretos del host. Es exclusivo del servidor y la API nunca lo devuelve. El par de claves habilita:

- **AuthnRequests firmados** (firma de los parámetros `SigAlg`/`Signature` en la query del binding redirect). La firma se activa automáticamente cuando los metadatos del IdP declaran `WantAuthnRequestsSigned`, o siempre cuando la conexión establece `signAuthnRequests: true`.
- **Descifrado de aserciones cifradas.** Cuando los metadatos del SP anuncian un certificado de cifrado, ADFS empieza a cifrar las aserciones de forma predeterminada; el ACS las descifra con la clave privada del SP y hace pasar la aserción descifrada por el mismo pipeline de firma/condiciones que una en texto plano. Soportado: transporte de clave RSA-OAEP (SHA-1/SHA-256); cifrado de datos AES-128/192/256-CBC y 3DES. **El transporte de clave RSA-1.5 se rechaza** —el desempaquetado PKCS#1 v1.5 es un oráculo de Bleichenbacher/ROBOT— y **AES-GCM no está soportado** (limitación de `EncryptedXml` de .NET). Configure el IdP para RSA-OAEP y AES-CBC. Ambos fallos devuelven el mismo mensaje constante («Could not decrypt the assertion.»), de forma deliberada: nombrar el algoritmo o la etapa que falló es precisamente lo que construye el oráculo, así que diagnostique desde la configuración del IdP y no desde el error.
- **Mensajes de cierre de sesión firmados** (LogoutRequest/LogoutResponse en el binding redirect).

Los metadatos del SP publican el certificado como `KeyDescriptor` tanto de `signing` como de `encryption`, y establecen `AuthnRequestsSigned="true"` cuando la conexión fuerza la firma.

## Cierre de sesión único (Single Logout)

El ACS registra la sesión SAML en la cookie de autenticación (claims `saml_connection`, `saml_name_id`, `saml_name_id_format`, `saml_session_index`) para que el cierre de sesión pueda vincularse de vuelta a la sesión del IdP.

- **Iniciado por el SP:** `GET /saml/{connectionId}/logout` siempre finaliza primero la sesión local de la cookie (el usuario pidió cerrar sesión; el SLO del IdP es de mejor esfuerzo). Si la sesión del navegador provino de esta conexión y los metadatos del IdP anuncian un `SingleLogoutService`, se envía una LogoutRequest (NameID + SessionIndex, firmada cuando el SP tiene clave) mediante el binding redirect; la LogoutResponse del IdP vuelve a `/slo`, que lleva al usuario a la `returnUrl` almacenada. Los IdP sin endpoint SLO (Google) solo reciben el cierre de sesión local.
- **Iniciado por el IdP:** el IdP envía una LogoutRequest a `/saml/{connectionId}/slo` (GET redirect o binding POST). Las solicitudes firmadas se validan contra los certificados de los metadatos del IdP. **Una LogoutRequest sin firmar o no verificable se rechaza con un 400** antes de consultar ninguna sesión. No hay un respaldo limitado a la sesión: una página de terceros que navegue el navegador de la *víctima* hasta aquí aporta la sesión de la víctima, no la del atacante, así que limitarlo a la sesión actual no habría restringido a quién se puede cerrar la sesión. Profiles §4.4.3.1 exige de todos modos que el IdP firme una LogoutRequest en el binding Redirect o POST, y los metadatos de la conexión ya aportan los certificados, por lo que rechazar una sin firmar no le cuesta nada a un IdP conforme. Se devuelve una LogoutResponse firmada cuando el IdP tiene un endpoint SLO. Solo por canal frontal: el mensaje llega en el navegador del usuario, por lo que finalizar la sesión de la cookie cierra la sesión exactamente de ese navegador.

## Almacenamiento en cache de metadatos y rotación de certificados

- Los metadatos del IdP obtenidos de `MetadataLocation` se almacenan en cache en memoria durante 60 minutos (configurable mediante `Cache:SamlMetadataCacheMinutes`), indexados por la URL de metadatos (no por el ID de conexión, de modo que no es posible ninguna confusión de cache entre inquilinos).
- Los metadatos pegados se almacenan en cache direccionados por contenido (hash del XML) y nunca se vuelven a obtener.
- **Reobtención ante fallo de firma:** un fallo de validación de firma justo después de una rotación de certificado del IdP significa que los metadatos en cache están obsoletos. Ante ese fallo exacto, la entrada de cache se desaloja y los metadatos se vuelven a obtener una vez, luego se reintenta la validación, con un enfriamiento de 5 minutos por ubicación de metadatos para que una aserción basura no pueda martillear el endpoint de metadatos del IdP. Sin esto, una rotación de certificado haría fallar los inicios de sesión hasta que expirara el TTL de la cache. (Solo para metadatos obtenidos por URL; los metadatos pegados no tienen nada que reobtener.)

## Compatibilidad con Azure AD

| Comportamiento de Azure AD | Manejo |
|---|---|
| Firma solo la aserción (predeterminado) | Valida la firma en el elemento Assertion |
| Firma solo la respuesta | Valida la firma en el elemento Response |
| Firma ambas | Valida ambas firmas |
| SHA-256 (predeterminado) | Soporta SHA-256 y SHA-1 |
| NameID: emailAddress | Extracción directa del email |
| NameID: persistent (opaco) | Recurre al claim de email desde los atributos |
| NameID: unspecified | Recurre al claim de email desde los atributos |
| NameID: transient | Rota en cada inicio de sesión, por lo que nunca se usa como clave federada. En su lugar se usa el atributo de object-id estable del IdP; si no se afirma ninguno, el inicio de sesión se rechaza con un error accionable (configure un NameID persistent o emailAddress, o afirme un atributo de object-id). |

## Mapeo de atributos

Los atributos se indexan sin distinguir mayúsculas de minúsculas tanto bajo su `Name` como bajo su `FriendlyName` (Okta y Shibboleth emiten Names de OID con FriendlyNames legibles; coincidir con cualquiera de ellos es lo que hace funcionar el mapeo de proveedores). Cada campo prueba una lista de alias en orden; el primer alias es la URI de claim de Microsoft, de modo que el comportamiento de Entra/ADFS no cambia, y el resto cubre los nombres friendly y de OID que Okta, OneLogin, Ping, Google y Shibboleth emiten de forma predeterminada:

| Campo | Nombres de atributo aceptados |
|---|---|
| email | `.../claims/emailaddress`, `email`, `mail`, `emailaddress`, `urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`, `givenName`, `given_name`, `firstName`, `first_name`, `urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`, `sn`, `surname`, `lastName`, `last_name`, `familyName`, `family_name`, `urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`, `displayName`, `urn:oid:2.16.840.1.113730.3.1.241`, `cn`, `urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`, `objectGUID`, `user.objectid` |
| groups | `.../claims/groups`, `groups`, `memberOf`, `.../claims/role`, `urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

(`.../claims/...` abrevia la URI completa `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` o `http://schemas.microsoft.com/ws/2008/06/identity/claims/...`.)

Prioridad de resolución del email: atributo de email explícito (cualquier alias) → NameID cuando su formato es emailAddress → el claim `name` si contiene `@` → rechazar (se requiere un email).

**Los grupos son multivaluados:** se captura cada elemento `AttributeValue` (uno por pertenencia a grupo), no solo el primero.

## Aprovisionamiento JIT

Los usuarios desconocidos se crean automáticamente en el primer inicio de sesión (email, nombre y apellido desde la aserción, email marcado como confirmado) y se vinculan a la conexión por su identidad federada estable (`saml:{connectionId}` + NameID, o el object-id para los NameID transient). Establezca `disableJitProvisioning: true` para rechazar usuarios desconocidos en su lugar. Los usuarios recurrentes se emparejan primero por el vínculo federado, nunca solo por email; una cuenta local existente se adjunta por email únicamente cuando los `AllowedDomains` de la conexión cubren el dominio de ese email (la declaración explícita del administrador de que este IdP posee el dominio), lo que evita el secuestro de cuentas mediante un IdP malicioso.

## Seguridad

- **Prevención de reutilización:** para los flujos iniciados por el SP, `InResponseTo` se valida contra un ID de solicitud almacenado (de un solo uso). De forma independiente, el ID de cada aserción aceptada se almacena y se aplica de un solo uso, lo que también cubre las respuestas iniciadas por el IdP y las respuestas cuyo `InResponseTo` fue eliminado (el ID de aserción vive dentro de la aserción firmada, por lo que no puede alterarse sin romper la firma).
- **Tolerancia de reloj:** Tolerancia de 5 minutos en NotBefore/NotOnOrAfter
- **Prevención de ataques de envoltura:** la URI de Reference de la firma debe coincidir con el ID del elemento firmado
- **Prevención de redirección abierta:** la URL de retorno posterior al inicio de sesión debe ser una ruta relativa a la raíz (que comience con `/`, sin `//`, sin barras invertidas, ya que los navegadores tratan `\` como `/`)
- **Verificación de dominio:** cuando `AllowedDomains` está configurado, las aserciones para emails fuera de esos dominios se rechazan, de modo que una conexión no puede afirmar el dominio de otra ni el email de un usuario local
- **MFA:** la federación prueba solo el primer factor. Si la política efectiva del usuario requiere MFA, el inicio de sesión se enruta a través del desafío/configuración de MFA local en lugar de emitir una sesión completamente autenticada.
