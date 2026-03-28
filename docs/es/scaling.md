---
layout: default
title: Escalabilidad
locale: es
---

# Escalabilidad

Authagonal esta disenado para escalar tanto vertical como horizontalmente sin configuracion especial.

## Sin estado por diseno

Todos los estados persistentes se almacenan en Azure Table Storage. No hay estado en proceso que requiera sesiones persistentes o coordinacion entre instancias:

- **Claves de firma** — cargadas desde Table Storage, actualizadas cada hora
- **Codigos de autorizacion y tokens de actualizacion** — almacenados en Table Storage con aplicacion de uso unico
- **Prevencion de reproduccion SAML** — los IDs de solicitud se rastrean en Table Storage con eliminacion atomica
- **OIDC state y verificadores PKCE** — almacenados en Table Storage
- **Configuracion de clientes y proveedores** — obtenida por solicitud desde Table Storage

## Cifrado de cookies (Data Protection)

Las claves de Data Protection de ASP.NET Core se persisten automaticamente en Azure Blob Storage cuando se utiliza una cadena de conexion real de Azure Storage. Esto significa que las cookies firmadas por una instancia pueden ser descifradas por cualquier otra instancia — no se requieren sesiones persistentes.

Para el desarrollo local con Azurite, las claves de Data Protection recurren al almacenamiento predeterminado basado en archivos.

Tambien puede especificar una URI de blob explicita a traves de la configuracion:

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

## Caches por instancia

Un pequeno numero de valores de lectura frecuente y cambio lento se almacenan en cache en memoria por instancia para reducir los viajes de ida y vuelta a Table Storage:

| Datos | Duracion del cache | Impacto de la obsolescencia |
|---|---|---|
| Documentos de descubrimiento OIDC | 60 minutos | Conciencia retrasada de la rotacion de claves del IdP |
| Metadatos de SAML IdP | 60 minutos | Igual |
| Origenes CORS permitidos | 60 minutos | Los nuevos origenes tardan hasta una hora en propagarse |

Estos caches son aceptables para uso en produccion. Si necesita propagacion inmediata, reinicie las instancias afectadas.

## Limitacion de velocidad

Authagonal no incluye limitacion de velocidad integrada. La limitacion de velocidad debe aplicarse en la capa de infraestructura (balanceador de carga, puerta de enlace API o proxy inverso) donde tiene una vista unificada de todo el trafico entre instancias.

## Recomendaciones de escalabilidad

**Escalado vertical** — aumente la CPU y la memoria en una sola instancia. Util para manejar mas solicitudes concurrentes por instancia.

**Escalado horizontal** — ejecute multiples instancias detras de un balanceador de carga. No se requieren sesiones persistentes ni caches compartidos. Cada instancia es completamente independiente.

**Escalado a cero** — Authagonal soporta despliegues con escalado a cero (por ejemplo, Azure Container Apps con `minReplicas: 0`). La primera solicitud despues de la inactividad tendra un arranque en frio de unos segundos mientras el runtime de .NET se inicializa y las claves de firma se cargan desde el almacenamiento.
