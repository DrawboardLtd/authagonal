---
layout: default
title: SAML
locale: es
---

# SAML 2.0 SP

Authagonal incluye una implementacion propia de proveedor de servicios SAML 2.0. Sin biblioteca SAML de terceros: construido sobre `System.Security.Cryptography.Xml.SignedXml` (parte de .NET).

## Alcance

- **SSO iniciado por el SP** (el usuario comienza en Authagonal, se redirige al IdP)
- **Binding HTTP-Redirect** para AuthnRequest (opcionalmente firmado, ver mas abajo)
- **Binding HTTP-POST** para la respuesta (ACS)
- **Aserciones cifradas** (`EncryptedAssertion`) descifradas con un par de claves de SP por conexion
- **Cierre de sesion unico (Single Logout)** (iniciado por el SP e iniciado por el IdP, bindings Redirect y POST)
- Azure AD / Entra ID es el objetivo principal, pero cualquier IdP compatible funciona (se manejan los nombres de atributo de Okta, OneLogin, Ping, Google Workspace, ADFS y Shibboleth)

### No soportado

- Binding Artifact
- Cifrado de aserciones AES-GCM (limitacion de `EncryptedXml` de .NET; configure AES-CBC en el IdP, ver mas abajo)

El SSO iniciado por el IdP esta soportado. El endpoint ACS maneja respuestas sin `InResponseTo` (la comprobacion del ID de solicitud se omite para las respuestas no solicitadas, pero el uso unico del ID de asercion se sigue aplicando, ver Seguridad).

## Configuracion de Azure AD

### 1. Crear un proveedor SAML

**Opcion A: Configuracion (recomendado para configuraciones estaticas)**

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

Los proveedores se inyectan al inicio. Los mapeos de dominios SSO se registran automaticamente desde `AllowedDomains`. Los proveedores inyectados desde la configuracion requieren una URL en `MetadataLocation` y no obtienen un par de claves de SP (por lo que no hay AuthnRequests firmados, aserciones cifradas ni mensajes de cierre de sesion firmados); use la API de administracion para esas funciones.

`EntityId` es **su ID de entidad de SP** (el identificador que registra en el IdP), no el ID de entidad del IdP.

**Opcion B: API de administracion (para gestion en tiempo de ejecucion)**

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

La API genera el `connectionId` (un GUID) y lo devuelve en la cabecera `Location` y en el cuerpo de la respuesta. Campos opcionales adicionales: `metadataXml` (metadatos pegados, ver mas abajo), `nameIdFormat` (ver mas abajo), `signAuthnRequests` (forzar AuthnRequests firmados), `iconUrl` (icono del boton de inicio de sesion), `disableJitProvisioning` (rechazar usuarios desconocidos en lugar de crearlos automaticamente). Las conexiones creadas mediante la API tambien obtienen un par de claves de SP autogenerado (ver Par de claves de SP mas abajo).

Las conexiones se gestionan mediante `POST` / `GET` / `PUT` / `DELETE` en `/api/v1/saml/connections[/{connectionId}]`. `PUT` es una actualizacion parcial: solo se modifican los campos suministrados en la peticion.

### 2. Configurar Azure AD

1. En Azure AD, vaya a Aplicaciones empresariales, Nueva aplicacion, Crear la suya propia
2. Configure el inicio de sesion unico, SAML
3. **Identificador (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **URL de respuesta (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **URL de inicio de sesion:** `https://auth.example.com/saml/acme-azure/login`

### 3. Enrutamiento de dominio SSO

Cuando se especifica `AllowedDomains` (en la configuracion o mediante la API de creacion), los mapeos de dominios SSO se registran automaticamente. Cuando un usuario ingresa `user@acme.com` en la pagina de inicio de sesion, la SPA detecta que se requiere SSO y muestra "Continuar con SSO". Un dominio solo puede asignarse a una conexion; la API rechaza un dominio ya reclamado por una conexion distinta.

Tambien puede gestionar dominios en tiempo de ejecucion mediante la API de administracion; ver [API de administracion](admin-api).

## Metadatos XML pegados

Algunos IdP no publican una URL de metadatos (Google Workspace), o su endpoint de metadatos es inaccesible desde el SP (ADFS en red privada). Para esos casos, pegue el documento de metadatos en su lugar: suministre `metadataXml` en la creacion/actualizacion. Debe proporcionarse exactamente uno entre `metadataLocation` o `metadataXml`; suministrar uno en una actualizacion borra el otro.

Los metadatos pegados se validan en el momento de guardar y se **condensan** (`SamlMetadataParser.Condense`) a un `EntityDescriptor` minimo y canonico que contiene exactamente lo que consume el SP: entityID, certificados de firma, el endpoint SSO, el endpoint SLO si esta presente y la marca `WantAuthnRequestsSigned`. Los documentos de proveedores pueden superar los 100KB (el `FederationMetadata.xml` de ADFS), por encima del limite de 64KB de una propiedad de Azure Table, mientras que las partes que usa el SP ocupan unos pocos KB. Los pegados que no se pueden analizar se rechazan con un 400; el documento debe contener un `IDPSSODescriptor` con un certificado de firma y un `SingleSignOnService`.

## Formato de NameID

El campo `nameIdFormat` controla el Format de `NameIDPolicy` solicitado en la AuthnRequest:

| Valor | Comportamiento |
|---|---|
| omitido / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress` (el valor predeterminado historico) |
| `"none"` | Omite por completo el elemento `NameIDPolicy`. El ajuste seguro para ADFS: ADFS falla todo el inicio de sesion (MSIS7070) cuando sus reglas de claims no emiten el formato solicitado. |
| cualquier otro valor | Se envia tal cual como el Format URN (debe comenzar con `urn:`) |

En una actualizacion, `""` restablece el valor predeterminado emailAddress. Los metadatos del SP anuncian el formato solicitado por la conexion (y omiten `NameIDFormat` cuando se establece en `"none"`).

## Endpoints

| Endpoint | Descripcion |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | Inicia el SSO iniciado por el SP. Construye una AuthnRequest (firmada cuando corresponde) y redirige al IdP. `loginHint` se pasa como `login_hint` para los IdP que lo respetan (Entra, Google). |
| `POST /saml/{connectionId}/acs` | Servicio consumidor de aserciones. Recibe la respuesta SAML, la valida, crea/inicia sesion del usuario. |
| `GET /saml/{connectionId}/metadata` | XML de metadatos SP para configurar el IdP. |
| `GET /saml/{connectionId}/logout?returnUrl=...` | Cierre de sesion unico iniciado por el SP. Finaliza la sesion local y luego envia una LogoutRequest al IdP cuando este soporta SLO. |
| `GET/POST /saml/{connectionId}/slo` | Endpoint de cierre de sesion unico. Recibe LogoutRequests iniciadas por el IdP (binding Redirect o POST) y el tramo de LogoutResponse del SLO iniciado por el SP. |

La URL de retorno posterior al inicio de sesion se transporta del lado del servidor en la AuthnRequest almacenada (indexada por el ID de solicitud), no en RelayState: la especificacion SAML limita RelayState a 80 bytes y algunos IdP lo truncan. RelayState solo se consulta para los flujos iniciados por el IdP.

## Par de claves de SP y aserciones cifradas

Cada conexion creada mediante la API obtiene un par de claves de SP autogenerado: un certificado RSA de 2048 bits autofirmado (validez de 10 anos), almacenado como PKCS#12 y protegido en reposo por el proveedor de secretos del host. Es exclusivo del servidor y la API nunca lo devuelve. El par de claves habilita:

- **AuthnRequests firmados** (firma de los parametros `SigAlg`/`Signature` en la query del binding redirect). La firma se activa automaticamente cuando los metadatos del IdP declaran `WantAuthnRequestsSigned`, o siempre cuando la conexion establece `signAuthnRequests: true`.
- **Descifrado de aserciones cifradas.** Cuando los metadatos del SP anuncian un certificado de cifrado, ADFS empieza a cifrar las aserciones de forma predeterminada; el ACS las descifra con la clave privada del SP y hace pasar la asercion descifrada por el mismo pipeline de firma/condiciones que una en texto plano. Soportado: transporte de clave RSA-OAEP (SHA-1/SHA-256) y RSA-1.5; cifrado de datos AES-128/192/256-CBC y 3DES. **AES-GCM no esta soportado** (limitacion de `EncryptedXml` de .NET) y produce un error claro; configure el IdP para usar AES-CBC.
- **Mensajes de cierre de sesion firmados** (LogoutRequest/LogoutResponse en el binding redirect).

Los metadatos del SP publican el certificado como `KeyDescriptor` tanto de `signing` como de `encryption`, y establecen `AuthnRequestsSigned="true"` cuando la conexion fuerza la firma.

## Cierre de sesion unico (Single Logout)

El ACS registra la sesion SAML en la cookie de autenticacion (claims `saml_connection`, `saml_name_id`, `saml_name_id_format`, `saml_session_index`) para que el cierre de sesion pueda vincularse de vuelta a la sesion del IdP.

- **Iniciado por el SP:** `GET /saml/{connectionId}/logout` siempre finaliza primero la sesion local de la cookie (el usuario pidio cerrar sesion; el SLO del IdP es de mejor esfuerzo). Si la sesion del navegador provino de esta conexion y los metadatos del IdP anuncian un `SingleLogoutService`, se envia una LogoutRequest (NameID + SessionIndex, firmada cuando el SP tiene clave) mediante el binding redirect; la LogoutResponse del IdP vuelve a `/slo`, que lleva al usuario a la `returnUrl` almacenada. Los IdP sin endpoint SLO (Google) solo reciben el cierre de sesion local.
- **Iniciado por el IdP:** el IdP envia una LogoutRequest a `/saml/{connectionId}/slo` (GET redirect o binding POST). Las solicitudes firmadas se validan contra los certificados de los metadatos del IdP. **Las LogoutRequests sin firmar solo se respetan cuando la propia sesion del navegador pertenece a esta conexion**, de modo que un atacante no autenticado no puede cerrar la sesion de nadie salvo la suya. Se devuelve una LogoutResponse firmada cuando el IdP tiene un endpoint SLO. Solo por canal frontal: el mensaje llega en el navegador del usuario, por lo que finalizar la sesion de la cookie cierra la sesion exactamente de ese navegador.

## Almacenamiento en cache de metadatos y rotacion de certificados

- Los metadatos del IdP obtenidos de `MetadataLocation` se almacenan en cache en memoria durante 60 minutos (configurable mediante `Cache:SamlMetadataCacheMinutes`), indexados por la URL de metadatos (no por el ID de conexion, de modo que no es posible ninguna confusion de cache entre inquilinos).
- Los metadatos pegados se almacenan en cache direccionados por contenido (hash del XML) y nunca se vuelven a obtener.
- **Reobtencion ante fallo de firma:** un fallo de validacion de firma justo despues de una rotacion de certificado del IdP significa que los metadatos en cache estan obsoletos. Ante ese fallo exacto, la entrada de cache se desaloja y los metadatos se vuelven a obtener una vez, luego se reintenta la validacion, con un enfriamiento de 5 minutos por ubicacion de metadatos para que una asercion basura no pueda martillear el endpoint de metadatos del IdP. Sin esto, una rotacion de certificado haria fallar los inicios de sesion hasta que expirara el TTL de la cache. (Solo para metadatos obtenidos por URL; los metadatos pegados no tienen nada que reobtener.)

## Compatibilidad con Azure AD

| Comportamiento de Azure AD | Manejo |
|---|---|
| Firma solo la asercion (predeterminado) | Valida la firma en el elemento Assertion |
| Firma solo la respuesta | Valida la firma en el elemento Response |
| Firma ambas | Valida ambas firmas |
| SHA-256 (predeterminado) | Soporta SHA-256 y SHA-1 |
| NameID: emailAddress | Extraccion directa del email |
| NameID: persistent (opaco) | Recurre al claim de email desde los atributos |
| NameID: unspecified | Recurre al claim de email desde los atributos |
| NameID: transient | Rota en cada inicio de sesion, por lo que nunca se usa como clave federada. En su lugar se usa el atributo de object-id estable del IdP; si no se afirma ninguno, el inicio de sesion se rechaza con un error accionable (configure un NameID persistent o emailAddress, o afirme un atributo de object-id). |

## Mapeo de atributos

Los atributos se indexan sin distinguir mayusculas de minusculas tanto bajo su `Name` como bajo su `FriendlyName` (Okta y Shibboleth emiten Names de OID con FriendlyNames legibles; coincidir con cualquiera de ellos es lo que hace funcionar el mapeo de proveedores). Cada campo prueba una lista de alias en orden; el primer alias es la URI de claim de Microsoft, de modo que el comportamiento de Entra/ADFS no cambia, y el resto cubre los nombres friendly y de OID que Okta, OneLogin, Ping, Google y Shibboleth emiten de forma predeterminada:

| Campo | Nombres de atributo aceptados |
|---|---|
| email | `.../claims/emailaddress`, `email`, `mail`, `emailaddress`, `urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`, `givenName`, `given_name`, `firstName`, `first_name`, `urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`, `sn`, `surname`, `lastName`, `last_name`, `familyName`, `family_name`, `urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`, `displayName`, `urn:oid:2.16.840.1.113730.3.1.241`, `cn`, `urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`, `objectGUID`, `user.objectid` |
| groups | `.../claims/groups`, `groups`, `memberOf`, `.../claims/role`, `urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

(`.../claims/...` abrevia la URI completa `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` o `http://schemas.microsoft.com/ws/2008/06/identity/claims/...`.)

Prioridad de resolucion del email: atributo de email explicito (cualquier alias) → NameID cuando su formato es emailAddress → el claim `name` si contiene `@` → rechazar (se requiere un email).

**Los grupos son multivaluados:** se captura cada elemento `AttributeValue` (uno por pertenencia a grupo), no solo el primero.

## Aprovisionamiento JIT

Los usuarios desconocidos se crean automaticamente en el primer inicio de sesion (email, nombre y apellido desde la asercion, email marcado como confirmado) y se vinculan a la conexion por su identidad federada estable (`saml:{connectionId}` + NameID, o el object-id para los NameID transient). Establezca `disableJitProvisioning: true` para rechazar usuarios desconocidos en su lugar. Los usuarios recurrentes se emparejan primero por el vinculo federado, nunca solo por email; una cuenta local existente se adjunta por email unicamente cuando los `AllowedDomains` de la conexion cubren el dominio de ese email (la declaracion explicita del administrador de que este IdP posee el dominio), lo que evita el secuestro de cuentas mediante un IdP malicioso.

## Seguridad

- **Prevencion de reutilizacion:** para los flujos iniciados por el SP, `InResponseTo` se valida contra un ID de solicitud almacenado (de un solo uso). De forma independiente, el ID de cada asercion aceptada se almacena y se aplica de un solo uso, lo que tambien cubre las respuestas iniciadas por el IdP y las respuestas cuyo `InResponseTo` fue eliminado (el ID de asercion vive dentro de la asercion firmada, por lo que no puede alterarse sin romper la firma).
- **Tolerancia de reloj:** Tolerancia de 5 minutos en NotBefore/NotOnOrAfter
- **Prevencion de ataques de envoltura:** la URI de Reference de la firma debe coincidir con el ID del elemento firmado
- **Prevencion de redireccion abierta:** la URL de retorno posterior al inicio de sesion debe ser una ruta relativa a la raiz (que comience con `/`, sin `//`, sin barras invertidas, ya que los navegadores tratan `\` como `/`)
- **Verificacion de dominio:** cuando `AllowedDomains` esta configurado, las aserciones para emails fuera de esos dominios se rechazan, de modo que una conexion no puede afirmar el dominio de otra ni el email de un usuario local
- **MFA:** la federacion prueba solo el primer factor. Si la politica efectiva del usuario requiere MFA, el inicio de sesion se enruta a traves del desafio/configuracion de MFA local en lugar de emitir una sesion completamente autenticada.
