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

Los endpoints de registro estan protegidos por un limitador de velocidad distribuido integrado (5 registros por IP por hora). Al ejecutar multiples instancias, los conteos de limite de velocidad se comparten automaticamente entre todas las instancias a traves de un protocolo de difusion (gossip) — no se requiere coordinacion externa.

### Como funciona

Cada instancia mantiene sus propios contadores en memoria utilizando un CRDT G-Counter. Las instancias se descubren entre si a traves de UDP multicast e intercambian estado por HTTP cada pocos segundos. El conteo consolidado de todas las instancias se utiliza para tomar decisiones de limitacion de velocidad.

Esto significa que los limites de velocidad se aplican globalmente: si un cliente accede a 3 instancias diferentes, las 3 saben que el total es 3, no 1 cada una.

### Identidad de nodo

Cada instancia genera un identificador de nodo hexadecimal aleatorio al inicio (por ejemplo, `a3f1b2`). Este identificador identifica la instancia en los mensajes de gossip y el estado de limites de velocidad. No se persiste — se genera uno nuevo en cada reinicio.

Un `ClusterLeaderService` se ejecuta en cada instancia, eligiendo un unico lider entre los pares descubiertos (el identificador de nodo mas bajo gana). El liderazgo se transfiere automaticamente cuando el lider muere. La eleccion de lider esta disponible para tareas de coordinacion a nivel de cluster que solo deben ejecutarse en un nodo.

### Configuracion del cluster

El clustering esta **habilitado por defecto** sin necesidad de configuracion. Las instancias en la misma red se descubren automaticamente a traves de UDP multicast (`239.42.42.42:19847`).

Para entornos donde el multicast no esta disponible (algunas VPCs en la nube), configure una URL interna con balanceo de carga como alternativa:

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

Para deshabilitar el clustering por completo (limitacion de velocidad solo local):

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

Consulte la pagina de [Configuracion](configuration) para todas las opciones del cluster.

### Degradacion elegante

- **Sin pares encontrados** — funciona como un limitador de velocidad solo local (cada instancia aplica su propio limite)
- **Par inaccesible** — el ultimo estado conocido de ese par se sigue utilizando; los pares obsoletos se eliminan despues de 30 segundos
- **Multicast no disponible** — el descubrimiento falla silenciosamente; el protocolo de difusion recurre a `InternalUrl` si esta configurado

## Recomendaciones de escalabilidad

**Escalado vertical** — aumente la CPU y la memoria en una sola instancia. Util para manejar mas solicitudes concurrentes por instancia.

**Escalado horizontal** — ejecute multiples instancias detras de un balanceador de carga. No se requieren sesiones persistentes ni caches compartidos. Cada instancia es completamente independiente.

**Escalado a cero** — Authagonal soporta despliegues con escalado a cero (por ejemplo, Azure Container Apps con `minReplicas: 0`). La primera solicitud despues de la inactividad tendra un arranque en frio de unos segundos mientras el runtime de .NET se inicializa y las claves de firma se cargan desde el almacenamiento.
