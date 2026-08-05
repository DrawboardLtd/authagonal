---
layout: default
title: Escalabilidad
locale: es
---

# Escalabilidad

Authagonal está diseñado para escalar tanto vertical como horizontalmente sin configuración especial.

## Sin estado por diseño

Todos los estados persistentes se almacenan en el almacén de tablas de respaldo: Azure Table Storage, o DynamoDB en el backend de AWS. No hay estado en proceso que requiera sesiones persistentes o coordinación entre instancias:

- **Claves de firma**: cargadas desde Table Storage, actualizadas cada hora
- **Códigos de autorización y tokens de actualización**: almacenados en Table Storage con aplicación de uso único
- **Prevención de reproducción SAML**: los IDs de solicitud se rastrean en Table Storage con eliminación atómica
- **OIDC state y verificadores PKCE**: almacenados en Table Storage
- **Configuración de clientes y proveedores**: obtenida por solicitud desde Table Storage

## Cifrado de cookies (Data Protection)

Las claves de Data Protection de ASP.NET Core se persisten automáticamente en Azure Blob Storage cuando se utiliza una cadena de conexión real de Azure Storage. Esto significa que las cookies firmadas por una instancia pueden ser descifradas por cualquier otra instancia, no se requieren sesiones persistentes.

Para el desarrollo local con Azurite, las claves de Data Protection recurren al almacenamiento predeterminado basado en archivos.

También puede especificar una URI de blob explícita a través de la configuración (la ruta de identidad administrada, preferida en producción):

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

En el backend de AWS, pase un cliente S3 más un bucket a `AddAuthagonalAwsStorage` para persistir el conjunto de claves en S3: sin ello, el conjunto de claves queda en memoria y las cookies se rompen al reiniciar y entre nodos. Ver [Instalación → backend de AWS](installation#aws-backend).

## Caches por instancia

Un pequeño número de valores de lectura frecuente y cambio lento se almacenan en cache en memoria por instancia para reducir los viajes de ida y vuelta a Table Storage:

| Datos | Duración del cache | Impacto de la obsolescencia |
|---|---|---|
| Documentos de descubrimiento OIDC | 60 minutos (configurable) | Conciencia retrasada de la rotación de claves del IdP |
| Metadatos de SAML IdP | 60 minutos (configurable) | Igual |
| Orígenes CORS permitidos | 60 minutos (configurable) | Los nuevos orígenes tardan hasta una hora en propagarse |

Estos caches son aceptables para uso en producción. Todas las duraciones son configurables mediante la sección de configuración `Cache`; ver [Configuración](configuration). Si necesita propagación inmediata, reinicie las instancias afectadas.

## Limitación de velocidad

Los endpoints propensos a abuso (registro por IP, restablecimiento de contraseña por correo de destino, SCIM por cliente, registro dinámico de clientes por IP, ver [Configuración → Limitación de velocidad](configuration#rate-limiting)) están protegidos por un limitador de velocidad integrado.

Los límites se aplican **en proceso por nodo** detrás del seam `IRateLimiter`, por lo que con N instancias el techo efectivo es N× el valor configurado. Esto es deliberado: el limitador es una red de seguridad contra el abuso descontrolado de un solo nodo, y el límite global autoritativo pertenece al borde (WAF / ingress / CDN), que ve todo el tráfico antes de que se balancee.

## Clustering

Múltiples instancias se coordinan a través de una **elección de líder** y un **bus de eventos entre nodos**, ambos detrás de backends conectables:

- **Elección de líder**: una elección basada en arrendamiento (`Cluster:LeaseTtlSeconds`, predeterminado 30s, renovado aproximadamente a la mitad de ese intervalo). Exactamente un nodo mantiene el arrendamiento; el liderazgo se transfiere automáticamente cuando el líder muere. El trabajo restringido al líder, actualmente la rotación de claves de firma (cuando está habilitada), se ejecuta solo en el líder para evitar la generación concurrente de claves.
- **Bus de eventos**: notificaciones entre nodos (por ejemplo, invalidación de cache en hosts multi-tenant), consultadas cada `Cluster:PollIntervalSeconds` (predeterminado 3s).

Cada instancia genera un identificador de nodo aleatorio de 12 caracteres hexadecimales al inicio para identificarse; no se persiste.

### Backends

El **valor predeterminado es en proceso**: un solo nodo siempre es su propio líder, y los eventos son solo locales, correcto para una instancia sin configuración alguna. Los despliegues multi-nodo intercambian un backend real mediante el callback `configureClustering` en `AddAuthagonal`:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS: leadership + event bus via DynamoDB (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` registran solo el bus de eventos, manteniendo el arrendamiento en proceso (siempre líder): úselos en nodos que deben recibir eventos del cluster pero nunca deben competir por el liderazgo.

> **Nota:** con el valor predeterminado en proceso en múltiples nodos, *cada* nodo cree que es el líder. Eso es inofensivo para la mayoría de las cargas de trabajo, pero habilite un backend de arrendamiento real antes de activar `Auth:KeyRotationEnabled` en múltiples instancias.

Consulte la página de [Configuración](configuration#cluster) para todas las opciones del cluster.

### Despliegues multi-tenant

En el modo multi-tenant (`AddAuthagonalCore()`), no se registra ningún servicio en segundo plano: `TokenCleanupService`, `GrantReconciliationService`, `SigningKeyRotationService`, y los servicios de siembra de configuración son todos parte de la composición de un solo tenant `AddAuthagonal()`. El host los gestiona por tenant.

## Partición caliente del índice de nombres

La búsqueda de nombres por prefijo del administrador se respalda en las tablas de índice `UserFirstNames` / `UserLastNames`, que usan una **única partición caliente**. A escala, esto limita el rendimiento de escritura del índice a aproximadamente 2.000 operaciones/seg, lo que puede convertirse en un cuello de botella en la creación/actualización de usuarios bajo carga intensa. Si no expone la búsqueda de nombres del administrador, establezca `Storage:NameIndexesEnabled = false` para omitir estas escrituras por completo. Ver [Configuración](configuration).

## Proxy de confianza y endpoints internos

Al ejecutar múltiples instancias detrás de un balanceador de carga:

- **Encabezados reenviados**: la limitación de velocidad y el bloqueo se basan en la IP del cliente, resuelta desde `X-Forwarded-For`. Establezca `ForwardedHeaders:KnownNetworks` con el CIDR de su ingress / pod para que la IP del cliente no pueda suplantarse entre instancias. `ForwardedHeaders:ForwardLimit` tiene el valor predeterminado `1`. Ver [Configuración](configuration#forwarded-headers-trusted-proxy).
- **Endpoints internos**: `/_internal/backchannel-logout` requiere `Cluster:Secret` en el encabezado `X-Cluster-Secret` (comparado en tiempo constante). Sin él, el endpoint no autoriza a nadie y responde 404: la IP de origen no se trata como credencial, porque loopback es lo que presenta un proxy inverso en el mismo host para cada solicitud reenviada, y un rango privado es cada carga de trabajo vecina en una red de clúster compartida. `Cluster:AllowLoopbackWithoutSecret` es un opt-in solo para desarrollo que readmite un par loopback previo al reenvío. El producto entregado nunca llama a esta ruta (la difusión de sesión es en proceso vía `SessionTermination`), así que solo importa para una difusión que construya usted.

## Recomendaciones de escalabilidad

**Escalado vertical**: aumente la CPU y la memoria en una sola instancia. Útil para manejar más solicitudes concurrentes por instancia.

**Escalado horizontal**: ejecute múltiples instancias detrás de un balanceador de carga. No se requieren sesiones persistentes ni caches compartidos. Cada instancia es completamente independiente.

**Escalado a cero**: Authagonal soporta despliegues con escalado a cero (por ejemplo, Azure Container Apps con `minReplicas: 0`). La primera solicitud después de la inactividad tendrá un arranque en frío de unos segundos mientras el runtime de .NET se inicializa y las claves de firma se cargan desde el almacenamiento.
