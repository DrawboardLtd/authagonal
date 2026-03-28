---
layout: default
title: SAML
locale: es
---

# SAML 2.0 SP

Authagonal incluye una implementacion propia de proveedor de servicios SAML 2.0. Sin biblioteca SAML de terceros -- construido sobre `System.Security.Cryptography.Xml.SignedXml` (parte de .NET).

## Alcance

- **SSO iniciado por el SP** (el usuario comienza en Authagonal, se redirige al IdP)
- **Binding HTTP-Redirect** para AuthnRequest
- **Binding HTTP-POST** para la respuesta (ACS)
- Azure AD es el objetivo principal, pero cualquier IdP compatible funciona

### No soportado

- SSO iniciado por el IdP
- Cierre de sesion SAML (usar expiracion de sesion)
- Cifrado de asercion (no publicar un certificado de cifrado)
- Binding Artifact

## Configuracion de Azure AD

### 1. Crear un proveedor SAML

**Opcion A -- Configuracion (recomendado para configuraciones estaticas):**

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

Los proveedores se inyectan al inicio. Los mapeos de dominios SSO se registran automaticamente desde `AllowedDomains`.

**Opcion B -- API de administracion (para gestion en tiempo de ejecucion):**

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

### 2. Configurar Azure AD

1. En Azure AD, vaya a Aplicaciones empresariales, Nueva aplicacion, Crear la suya propia
2. Configure el inicio de sesion unico, SAML
3. **Identificador (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **URL de respuesta (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **URL de inicio de sesion:** `https://auth.example.com/saml/acme-azure/login`

### 3. Enrutamiento de dominio SSO

Cuando se especifica `AllowedDomains` (en la configuracion o mediante la API de creacion), los mapeos de dominios SSO se registran automaticamente. Cuando un usuario ingresa `user@acme.com` en la pagina de inicio de sesion, la SPA detecta que se requiere SSO y muestra "Continuar con SSO".

Tambien puede gestionar dominios en tiempo de ejecucion mediante la API de administracion -- ver [API de administracion](admin-api).

## Endpoints

| Endpoint | Descripcion |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...` | Inicia el SSO iniciado por el SP. Construye una AuthnRequest y redirige al IdP. |
| `POST /saml/{connectionId}/acs` | Servicio consumidor de aserciones. Recibe la respuesta SAML, la valida, crea/inicia sesion del usuario. |
| `GET /saml/{connectionId}/metadata` | XML de metadatos SP para configurar el IdP. |

## Compatibilidad con Azure AD

| Comportamiento de Azure AD | Manejo |
|---|---|
| Firma solo la asercion (predeterminado) | Valida la firma en el elemento Assertion |
| Firma solo la respuesta | Valida la firma en el elemento Response |
| Firma ambas | Valida ambas firmas |
| SHA-256 (predeterminado) | Soporta SHA-256 y SHA-1 |
| NameID: emailAddress | Extraccion directa del email |
| NameID: persistent (opaco) | Recurre al claim de email desde los atributos |
| NameID: transient, unspecified | Recurre al claim de email desde los atributos |

## Mapeo de claims

Los claims de Azure AD (formato URI completo) se mapean a nombres simples:

| URI de claim Azure AD | Mapeado a |
|---|---|
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress` | `email` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname` | `firstName` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname` | `lastName` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name` | `name` (UPN) |
| `http://schemas.microsoft.com/identity/claims/objectidentifier` | `oid` |
| `http://schemas.microsoft.com/identity/claims/tenantid` | `tenantId` |
| `http://schemas.microsoft.com/identity/claims/displayname` | `displayName` |

## Seguridad

- **Prevencion de reutilizacion:** InResponseTo se valida contra un identificador de solicitud almacenado. Cada identificador es de un solo uso.
- **Tolerancia de reloj:** Tolerancia de 5 minutos en NotBefore/NotOnOrAfter
- **Prevencion de ataques de envoltura:** La validacion de firma usa la resolucion de referencia correcta
- **Prevencion de redireccion abierta:** RelayState (returnUrl) debe ser una ruta relativa a la raiz (que comience con `/`, sin esquema ni host)
