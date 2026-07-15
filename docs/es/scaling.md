---
layout: default
title: Escalabilidad
locale: es
---

# Escalabilidad

Authagonal esta disenado para escalar tanto vertical como horizontalmente sin configuracion especial.

## Sin estado por diseno

Todos los estados persistentes se almacenan en el almacen de tablas de respaldo -- Azure Table Storage, o DynamoDB en el backend de AWS. No hay estado en proceso que requiera sesiones persistentes o coordinacion entre instancias:

- **Claves de firma** — cargadas desde Table Storage, actualizadas cada hora
- **Codigos de autorizacion y tokens de actualizacion** — almacenados en Table Storage con aplicacion de uso unico
- **Prevencion de reproduccion SAML** — los IDs de solicitud se rastrean en Table Storage con eliminacion atomica
- **OIDC state y verificadores PKCE** — almacenados en Table Storage
- **Configuracion de clientes y proveedores** — obtenida por solicitud desde Table Storage

## Cifrado de cookies (Data Protection)

Las claves de Data Protection de ASP.NET Core se persisten automaticamente en Azure Blob Storage cuando se utiliza una cadena de conexion real de Azure Storage. Esto significa que las cookies firmadas por una instancia pueden ser descifradas por cualquier otra instancia — no se requieren sesiones persistentes.

Para el desarrollo local con Azurite, las claves de Data Protection recurren al almacenamiento predeterminado basado en archivos.

Tambien puede especificar una URI de blob explicita a traves de la configuracion (la ruta de identidad administrada, preferida en produccion):

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

En el backend de AWS, pase un cliente S3 mas un bucket a `AddAuthagonalAwsStorage` para persistir el conjunto de claves en S3 -- sin ello el conjunto de claves queda en memoria y las cookies se rompen al reiniciar y entre nodos. Ver [Instalacion → backend de AWS](installation#aws-backend).

## Caches por instancia

Un pequeno numero de valores de lectura frecuente y cambio lento se almacenan en cache en memoria por instancia para reducir los viajes de ida y vuelta a Table Storage:

| Datos | Duracion del cache | Impacto de la obsolescencia |
|---|---|---|
| Documentos de descubrimiento OIDC | 60 minutos (configurable) | Conciencia retrasada de la rotacion de claves del IdP |
| Metadatos de SAML IdP | 60 minutos (configurable) | Igual |
| Origenes CORS permitidos | 60 minutos (configurable) | Los nuevos origenes tardan hasta una hora en propagarse |

Estos caches son aceptables para uso en produccion. Todas las duraciones son configurables mediante la seccion de configuracion `Cache`; ver [Configuracion](configuration). Si necesita propagacion inmediata, reinicie las instancias afectadas.

## Limitacion de velocidad

Los endpoints propensos a abuso (registro por IP, restablecimiento de contrasena por correo de destino, SCIM por cliente, registro dinamico de clientes por IP -- ver [Configuracion → Limitacion de velocidad](configuration#rate-limiting)) estan protegidos por un limitador de velocidad integrado.

Los limites se aplican **en proceso por nodo** detras del seam `IRateLimiter`, por lo que con N instancias el techo efectivo es N× el valor configurado. Esto es deliberado: el limitador es una red de seguridad contra el abuso descontrolado de un solo nodo, y el limite global autoritativo pertenece al borde (WAF / ingress / CDN), que ve todo el trafico antes de que se balancee.

## Clustering

Multiples instancias se coordinan a traves de una **eleccion de lider** y un **bus de eventos entre nodos**, ambos detras de backends conectables:

- **Eleccion de lider** -- una eleccion basada en arrendamiento (`Cluster:LeaseTtlSeconds`, predeterminado 30s, renovado aproximadamente a la mitad de ese intervalo). Exactamente un nodo mantiene el arrendamiento; el liderazgo se transfiere automaticamente cuando el lider muere. El trabajo restringido al lider -- actualmente la rotacion de claves de firma (cuando esta habilitada) -- se ejecuta solo en el lider para evitar la generacion concurrente de claves.
- **Bus de eventos** -- notificaciones entre nodos (por ejemplo, invalidacion de cache en hosts multi-tenant), consultadas cada `Cluster:PollIntervalSeconds` (predeterminado 3s).

Cada instancia genera un identificador de nodo aleatorio de 12 caracteres hexadecimales al inicio para identificarse; no se persiste.

### Backends

El **valor predeterminado es en proceso**: un solo nodo siempre es su propio lider, y los eventos son solo locales -- correcto para una instancia sin configuracion alguna. Los despliegues multi-nodo intercambian un backend real mediante el callback `configureClustering` en `AddAuthagonal`:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS: leadership + event bus via DynamoDB (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` registran solo el bus de eventos, manteniendo el arrendamiento en proceso (siempre lider) -- uselos en nodos que deben recibir eventos del cluster pero nunca deben competir por el liderazgo.

> **Nota:** con el valor predeterminado en proceso en multiples nodos, *cada* nodo cree que es el lider. Eso es inofensivo para la mayoria de las cargas de trabajo, pero habilite un backend de arrendamiento real antes de activar `Auth:KeyRotationEnabled` en multiples instancias.

Consulte la pagina de [Configuracion](configuration#cluster) para todas las opciones del cluster.

### Despliegues multi-tenant

En el modo multi-tenant (`AddAuthagonalCore()`), no se registra ningun servicio en segundo plano -- `TokenCleanupService`, `GrantReconciliationService`, `SigningKeyRotationService`, y los servicios de siembra de configuracion son todos parte de la composicion de un solo tenant `AddAuthagonal()`. El host los gestiona por tenant.

## Particion caliente del indice de nombres

La busqueda de nombres por prefijo del administrador se respalda en las tablas de indice `UserFirstNames` / `UserLastNames`, que usan una **unica particion caliente**. A escala, esto limita el rendimiento de escritura del indice a aproximadamente 2.000 operaciones/seg, lo que puede convertirse en un cuello de botella en la creacion/actualizacion de usuarios bajo carga intensa. Si no expone la busqueda de nombres del administrador, establezca `Storage:NameIndexesEnabled = false` para omitir estas escrituras por completo. Ver [Configuracion](configuration).

## Proxy de confianza y endpoints internos

Al ejecutar multiples instancias detras de un balanceador de carga:

- **Encabezados reenviados** — la limitacion de velocidad y el bloqueo se basan en la IP del cliente, resuelta desde `X-Forwarded-For`. Establezca `ForwardedHeaders:KnownNetworks` con el CIDR de su ingress / pod para que la IP del cliente no pueda suplantarse entre instancias. `ForwardedHeaders:ForwardLimit` tiene el valor predeterminado `1`. Ver [Configuracion](configuration#forwarded-headers-trusted-proxy).
- **Endpoints internos** -- `/_internal/backchannel-logout` esta protegido por IP de origen (solo loopback / privada) a menos que se establezca `Cluster:Secret`, en cuyo caso los llamadores deben presentar el secreto en el encabezado `X-Cluster-Secret` (comparado en tiempo constante). Establezca el secreto siempre que el trafico interno se enrute a traves de cualquier cosa que reescriba la IP de origen.

## Recomendaciones de escalabilidad

**Escalado vertical** — aumente la CPU y la memoria en una sola instancia. Util para manejar mas solicitudes concurrentes por instancia.

**Escalado horizontal** — ejecute multiples instancias detras de un balanceador de carga. No se requieren sesiones persistentes ni caches compartidos. Cada instancia es completamente independiente.

**Escalado a cero** — Authagonal soporta despliegues con escalado a cero (por ejemplo, Azure Container Apps con `minReplicas: 0`). La primera solicitud despues de la inactividad tendra un arranque en frio de unos segundos mientras el runtime de .NET se inicializa y las claves de firma se cargan desde el almacenamiento.
