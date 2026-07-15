---
layout: default
title: Documento técnico sobre copias de seguridad de Table Storage
locale: es
---

# Copia de seguridad de Azure Table Storage: un enfoque práctico

**Cómo Authagonal implementa copias de seguridad completas e incrementales para un almacén NoSQL sin esquema**

---

## El problema

Azure Table Storage es un almacén de clave-valor rentable y masivamente escalable, pero no ofrece ninguna función de copia de seguridad nativa. No hay instantáneas, ni restauración a un punto en el tiempo, ni botón de exportación. Si un despliegue defectuoso corrompe los datos, o un operador elimina accidentalmente una tabla, la recuperación depende por completo de lo que usted mismo haya construido.

Para una plataforma de identidad como Authagonal, donde las tablas contienen usuarios, credenciales, concesiones OAuth, claves de firma, configuraciones de SSO y estado de aprovisionamiento SCIM, lo que está en juego es mucho. Perder estos datos no solo rompe una aplicación; deja a las personas fuera.

Este documento describe la estrategia de copia de seguridad que usa Authagonal: cómo exporta los datos, cómo funcionan las copias de seguridad incrementales a pesar del modelo de consultas limitado de Table Storage, cómo se rastrean las eliminaciones y cómo las piezas se componen en una canalización de copia de seguridad lista para producción.

## Objetivos de diseño

1. **Copias de seguridad completas e incrementales.** Una copia de seguridad completa diaria está bien para despliegues pequeños, pero a escala, las incrementales cada hora mantienen corta la ventana de copia de seguridad y bajos los costes de almacenamiento.
2. **Ciclo de ida y vuelta fiel.** Cada propiedad de entidad (cadenas, enteros, booleanos, DateTimeOffsets, GUIDs, binarios) debe sobrevivir a un ciclo de copia de seguridad y restauración sin coerción de tipos ni pérdida de datos.
3. **Compatibilidad multiinquilino.** Authagonal usa prefijos de nombre de tabla para aislar a los inquilinos (por ejemplo, `acmecorpUsers`, `acmecorpClients`). La copia de seguridad y la restauración deben reconocer los prefijos para que una única cuenta de almacenamiento pueda alojar a muchos inquilinos con programaciones de copia de seguridad independientes.
4. **Almacenamiento intercambiable.** Las copias de seguridad deben funcionar hacia un sistema de archivos local durante el desarrollo y hacia el almacenamiento de blobs (o cualquier otro destino) en producción, sin cambiar la lógica central.
5. **Salida legible para humanos.** Cuando algo sale mal, un operador debería poder abrir un archivo de copia de seguridad en un editor de texto y ver lo que contiene.

## Arquitectura

El sistema de copia de seguridad está estructurado como una biblioteca .NET (`Authagonal.Backup`) con envoltorios CLI ligeros para las operaciones de copia de seguridad y restauración. La biblioteca está separada del servidor principal de Authagonal para que pueda usarse como una herramienta independiente, en un contenedor Docker o incrustada en un trabajo programado.

```
Authagonal.Backup (library)
  BackupService         -- orchestrates full/incremental export
  RestoreService        -- imports backup data into Table Storage
  MergeService          -- consolidates full + incrementals into one snapshot
  RollupService         -- merge + cleanup of old backups
  IBackupTarget         -- write abstraction (filesystem, blob, etc.)
  IBackupSource         -- read abstraction
  FileSystemBackupTarget/Source -- local filesystem implementation

tools/Authagonal.Backup     -- CLI entry point for backup
tools/Authagonal.Restore    -- CLI entry point for restore
```

### Abstracción de almacenamiento

Los servicios centrales nunca tocan el sistema de archivos directamente. Operan contra dos interfaces:

**IBackupTarget** proporciona cuatro operaciones: abrir un flujo de escritura para un archivo de copia de seguridad, escribir un manifiesto, obtener la última marca de agua (para la programación incremental) y establecer una nueva marca de agua.

**IBackupSource** proporciona el lado de lectura: leer un manifiesto, abrir un flujo de lectura, listar los ID de copia de seguridad cronológicamente, listar los archivos dentro de una copia de seguridad y eliminar una copia de seguridad.

Las implementaciones del sistema de archivos son sencillas (directorios con marca de tiempo con archivos JSONL dentro), pero la abstracción significa que cambiar a Azure Blob Storage o S3 requiere implementar solo estas dos interfaces.

## Copia de seguridad completa

Una copia de seguridad completa itera sobre cada tabla de Authagonal, consulta todas las entidades y las escribe en archivos JSONL (un objeto JSON por línea, un archivo por tabla).

El proceso de copia de seguridad:

1. Generar un ID de copia de seguridad a partir de la marca de tiempo UTC actual (por ejemplo, `20260329-120000`).
2. Para cada una de las 20 tablas predeterminadas de Authagonal, consultar `QueryAsync<TableEntity>` del SDK de Azure Table Storage con un tamaño de página de 1.000.
3. Serializar cada entidad en un diccionario JSON plano que preserve todas las propiedades, incluidas las propiedades del sistema (`PartitionKey`, `RowKey`, `Timestamp`, `ETag`).
4. Escribir cada entidad serializada como una única línea en `{TableName}.jsonl` (o `{TableName}.jsonl.gz` si la compresión está habilitada).
5. Registrar los recuentos de entidades y las duraciones por tabla en un manifiesto (`_manifest.json`).
6. Actualizar el archivo de marca de agua `.lastbackup` con la hora de inicio de la copia de seguridad.

Las tablas que no existen en la cuenta de almacenamiento se omiten en silencio (el HTTP 404 se captura y se ignora). Las tablas transitorias como `SamlReplayCache` y `OidcStateStore` se excluyen de forma predeterminada, ya que su contenido es efímero.

### Formato de salida

```
backups/
  20260329-120000/
    Users.jsonl
    Clients.jsonl
    Grants.jsonl
    GrantsBySubject.jsonl
    ...
    _manifest.json
```

Una única línea en `Users.jsonl` se ve así:

```json
{"PartitionKey":"u_abc123","RowKey":"profile","Timestamp":"2026-03-28T09:14:22+00:00","ETag":"W/\"...\"","Email":"alice@example.com","DisplayName":"Alice","CreatedAt":"2025-11-01T00:00:00+00:00"}
```

Se eligió JSONL en lugar de CSV o un formato binario porque preserva la naturaleza heterogénea y sin esquema de las entidades de Table Storage (distintas entidades en la misma tabla pueden tener propiedades diferentes), es transmisible en flujo (no es necesario almacenar toda la tabla en memoria) y se puede inspeccionar directamente con herramientas estándar como `jq` o cualquier editor de texto.

### Compresión

Cuando se establece la opción `--gzip`, cada archivo JSONL se envuelve en un flujo GZip con `CompressionLevel.Optimal` antes de escribirlo. La extensión del archivo cambia a `.jsonl.gz`. La herramienta de restauración detecta automáticamente GZip inspeccionando los bytes mágicos (`0x1f 0x8b`) al inicio de cada archivo, por lo que no se necesita ninguna opción durante la restauración.

## Copia de seguridad incremental

### El truco del Timestamp

Azure Table Storage mantiene automáticamente una propiedad `Timestamp` en cada entidad, actualizada en cada inserción o reemplazo. Es una propiedad gestionada por el servidor: las aplicaciones no pueden establecerla. El sistema de copia de seguridad aprovecha esto filtrando las consultas por `Timestamp gt datetime'{watermark}'`, donde la marca de agua es la hora de inicio de la última copia de seguridad exitosa.

Esto significa que una copia de seguridad incremental solo descarga las entidades que se crearon o modificaron desde la ejecución anterior. Para un sistema con 500.000 entidades donde 200 cambiaron en la última hora, la copia de seguridad incremental transfiere 200 filas en lugar de 500.000.

La marca de agua se almacena en un archivo `.lastbackup` en el directorio raíz de la copia de seguridad. Si el archivo no existe (primera ejecución, o tras una limpieza manual), la copia de seguridad recurre a una exportación completa. Los ID de copia de seguridad incremental incluyen un sufijo `-incr` (por ejemplo, `20260329-180000-incr`) y el manifiesto registra `"mode": "incremental"` con el valor de marca de agua que se usó para el filtrado.

### Coste del filtro de Timestamp

Conviene ser honesto acerca de una limitación: `Timestamp` no está indexado. Azure Table Storage solo indexa `PartitionKey` y `RowKey`. Un filtro sobre `Timestamp gt datetime'...'` da como resultado un escaneo completo de la tabla: Azure lee cada entidad del lado del servidor y evalúa el predicado antes de devolver las coincidencias. El filtrado reduce la transferencia de datos (solo las entidades modificadas cruzan la red), pero no el coste de lectura del lado del servidor.

Más importante aún, el enfoque actual escanea **las 20 tablas** individualmente, incluso si solo una tabla tuvo cambios. Eso supone 20 escaneos completos de tabla por copia de seguridad incremental, independientemente de cuán pocas entidades hayan cambiado realmente.

En los volúmenes típicos de datos de identidad de Authagonal (decenas de miles de entidades, no millones), esto es perfectamente aceptable: los escaneos son rápidos, las lecturas son baratas ($0.00036 por cada 10.000 transacciones) y la operación es de solo lectura, sin impacto en el tráfico en vivo. La sección sobre [escalar más allá de los escaneos por timestamp](#scaling-beyond-timestamp-scans) analiza cómo podría evolucionar esto.

### El problema de las eliminaciones

El filtro de `Timestamp` captura con elegancia las inserciones y las actualizaciones, pero no puede capturar las eliminaciones. Una entidad eliminada simplemente desaparece: no hay ningún `Timestamp` por el que filtrar, ni ninguna lápida (tombstone) que Table Storage deje por sí mismo.

Authagonal resuelve esto con un seguimiento de tombstones a nivel de aplicación.

## Seguimiento de tombstones

Cada almacén de datos en Authagonal (usuarios, clientes, concesiones, claves de firma, dominios de SSO, proveedores SAML/OIDC, credenciales MFA, recursos SCIM, roles) acepta una dependencia opcional `ITombstoneWriter`. Cuando un almacén elimina una entidad, escribe un registro de tombstone en una tabla `Tombstones` dedicada:

| Columna | Valor |
|---|---|
| `PartitionKey` | Nombre lógico de la tabla (por ejemplo, `"Users"`) |
| `RowKey` | `"{originalPartitionKey}\|{originalRowKey}"` |
| `DeletedAt` | Marca de tiempo UTC de la eliminación |

Es un canal lateral ligero y de tipo mayormente incremental (append). La escritura de tombstone es un simple upsert, agrupado hasta el límite de transacción de 100 entidades de Azure para operaciones masivas.

Durante una copia de seguridad incremental, después de exportar las entidades modificadas de cada tabla, el servicio de copia de seguridad consulta la tabla `Tombstones` en busca de registros con `Timestamp > watermark`. Estos se escriben en un archivo `_tombstones.jsonl` aparte en el directorio de copia de seguridad, con un formato normalizado:

```json
{"Table":"Users","PartitionKey":"u_abc123","RowKey":"profile","DeletedAt":"2026-03-29T14:30:00+00:00"}
```

Esto significa que una copia de seguridad incremental captura una imagen completa de lo que cambió: las entidades añadidas o modificadas (de los archivos JSONL por tabla) y las entidades eliminadas (del archivo de tombstones).

## Fusión y rollup

Con el tiempo, un directorio de copia de seguridad acumula una copia de seguridad completa y muchas incrementales. Para restaurar al estado actual, habría que aplicarlas todas en orden. El **MergeService** las consolida en una única copia de seguridad completa.

El algoritmo de fusión:

1. Cargar el conjunto de entidades de la copia de seguridad completa de una tabla a la vez (para acotar el uso de memoria).
2. Superponer cada incremental encima en orden cronológico: los valores más nuevos sobrescriben a los más antiguos, con clave `(PartitionKey, RowKey)`.
3. Aplicar los tombstones: por cada tupla `(Table, PartitionKey, RowKey)` en los archivos de tombstone, eliminar la entidad del conjunto fusionado.
4. Escribir el conjunto de entidades resultante como una nueva copia de seguridad completa.

El **RollupService** envuelve esto con una limpieza: tras una fusión exitosa, elimina la copia de seguridad completa antigua y todas las incrementales que se plegaron en ella. Esto evita que el uso de almacenamiento crezca sin límite.

Una programación de producción típica podría verse así:

- **Cada hora:** copia de seguridad incremental
- **Diariamente (2:00):** copia de seguridad completa
- **Semanalmente:** rollup (fusionar las incrementales diarias y horarias de la semana anterior, eliminar los originales)

## Restauración

La herramienta de restauración lee un directorio de copia de seguridad y vuelve a escribir las entidades en Azure Table Storage. Admite tres modos:

**Upsert** (predeterminado): cada entidad se inserta o se reemplaza. Las entidades existentes con la misma clave se sobrescriben. Es el modo más seguro para la recuperación ante desastres.

**Merge**: cada entidad se inserta o se fusiona. Las propiedades presentes en la copia de seguridad sobrescriben las propiedades correspondientes de la entidad existente, pero las propiedades que existen en la tabla en vivo y no en la copia de seguridad se conservan. Útil para restauraciones parciales.

**Clean**: todas las entidades existentes en cada tabla de destino se eliminan antes de restaurar. Esto produce una réplica exacta del estado de la copia de seguridad, a costa de un escaneo completo de la tabla (potencialmente lento) para eliminar los datos existentes.

### Fidelidad de tipos

Un desafío clave al hacer el ciclo de ida y vuelta de los datos de Table Storage a través de JSON es preservar los tipos de las propiedades. Table Storage admite de forma nativa cadenas, enteros (Int32/Int64), dobles, booleanos, DateTimeOffset, Guid y binarios. JSON no tiene representación nativa para la mayoría de estos.

El servicio de restauración usa heurísticas para recuperar los tipos a partir de su representación JSON en cadena:

- **DateTimeOffset**: las cadenas que tienen entre 19 y 35 caracteres, comienzan con un dígito y se analizan como ISO 8601 se restauran como `DateTimeOffset`.
- **Guid**: las cadenas que tienen exactamente 36 caracteres y se analizan como un GUID se restauran como `Guid`.
- **Números**: los números JSON se prueban como `Int32`, luego `Int64`, luego `double`, en ese orden.
- **Booleanos y nulos**: se mapean directamente.

Este enfoque heurístico cubre los patrones de datos reales de Authagonal sin requerir un registro de esquemas ni anotaciones de tipo en el formato de copia de seguridad.

### Manejo de errores

Las operaciones de restauración son tolerantes a fallos a nivel de entidad. Si una entidad individual no se puede escribir (por ejemplo, debido a un error transitorio de Azure), el recuento de errores se incrementa, pero la restauración continúa. El resultado final informa de los recuentos de éxito y de error por tabla, y el proceso finaliza con el código `2` para un éxito parcial, distinto de `0` (éxito total) y `1` (error fatal).

## Multiinquilino

Authagonal admite despliegues multiinquilino donde las tablas de cada inquilino llevan prefijo (por ejemplo, `acmecorpUsers`, `contosoclients`). Tanto la copia de seguridad como la restauración aceptan una opción `--prefix` que se antepone a los nombres lógicos de las tablas al comunicarse con Azure Table Storage.

Esto significa:
- Una copia de seguridad con `--prefix acmecorp` lee de `acmecorpUsers`, `acmecorpClients`, etc., pero escribe archivos con los nombres `Users.jsonl`, `Clients.jsonl` (nombres lógicos).
- Una restauración con `--prefix contoso` lee `Users.jsonl` y escribe en `contosoUsers`.

Esto facilita clonar los datos de un inquilino, migrar entre entornos o restaurar un inquilino sin afectar a los demás.

## Manifiesto

Cada copia de seguridad incluye un archivo `_manifest.json` que registra:

- **BackupId**: identificador con marca de tiempo (por ejemplo, `20260329-120000` o `20260329-180000-incr`)
- **Mode**: `"full"` o `"incremental"`
- **BackupTimestamp**: cuándo comenzó la copia de seguridad (UTC)
- **Watermark**: para las incrementales, la marca de tiempo de corte usada para el filtrado
- **Compressed**: si los archivos están comprimidos con GZip
- **Tables**: un diccionario de nombres de tabla a recuentos de entidades y duraciones
- **TombstoneCount**: número de registros de tombstone (solo incremental)
- **TotalEntities**: recuento agregado de entidades en todas las tablas
- **DurationSeconds**: tiempo real transcurrido de la ejecución de la copia de seguridad
- **FileHashes**: hashes SHA-256 de cada archivo de copia de seguridad para la verificación de integridad

El manifiesto sirve tanto como un panel operativo (¿qué tamaño tenía la copia de seguridad? ¿cuánto tardó? ¿qué tablas son las más grandes?) como una red de seguridad (la verificación de hash durante la restauración detecta archivos corruptos o manipulados).

## Características operativas

**La velocidad de copia de seguridad** está limitada por el rendimiento de consultas de Azure Table Storage, que suele ser de 5.000 a 10.000 entidades por segundo por tabla. Una copia de seguridad completa de 100.000 entidades repartidas en 20 tablas se completa en menos de un minuto. Las copias de seguridad incrementales de unos cientos de entidades modificadas terminan en segundos.

**El uso de memoria** es mínimo. El servicio de copia de seguridad transmite las entidades directamente al disco: nunca carga una tabla entera en memoria. El servicio de fusión procesa una tabla a la vez, cargando solo el conjunto de entidades de esa tabla. Para tablas muy grandes (millones de entidades), la huella de memoria de la fusión es proporcional a la tabla individual más grande.

**La política de reintentos** está configurada con retroceso exponencial: 5 reintentos, empezando en 500 ms, con un tope de 30 segundos. Esto cubre la limitación transitoria (throttling) que Table Storage aplica bajo una carga elevada.

**El modo de ejecución en seco** (`--dry-run`) enumera las entidades sin escribir ningún archivo, útil para validar la conectividad y estimar el tamaño de la copia de seguridad antes de comprometerse a una ejecución completa.

## Escalar más allá de los escaneos por timestamp

El enfoque basado en `Timestamp` es pragmático a escala moderada, pero su coste es proporcional al tamaño total de los datos, no al número de cambios. A medida que las tablas crecen, 20 escaneos completos de tabla por copia de seguridad incremental resultan cada vez más derrochadores. La evolución natural es una **tabla de registro de cambios unificada**.

La idea clave es que el mecanismo de tombstones ya demuestra este patrón para las eliminaciones. La tabla `Tombstones` es un índice único, compacto y entre tablas: cada eliminación en las 20 tablas de datos se registra en un solo lugar, consultable por marca de tiempo. Extender esto para cubrir todas las mutaciones (inserciones, actualizaciones y eliminaciones) eliminaría por completo la necesidad de escanear las tablas de datos.

### Diseño del registro de cambios

Una tabla de registro de cambios con claves de partición agrupadas por tiempo se vería así:

| PartitionKey | RowKey | Propiedades |
|---|---|---|
| `2026-03-29T18` | `Users\|u_abc123\|profile` | `Op = "upsert"` |
| `2026-03-29T18` | `Clients\|c_456\|config` | `Op = "upsert"` |
| `2026-03-29T18` | `Users\|u_xyz789\|profile` | `Op = "delete"` |

La clave de partición es un contenedor de una hora, por lo que encontrar todos los cambios desde la última copia de seguridad se convierte en un conjunto de **consultas puntuales por clave de partición**, la operación más rápida que admite Table Storage. El servicio de copia de seguridad haría lo siguiente:

1. Consultar el registro de cambios en busca de todas las particiones de contenedor horario desde la marca de agua. Es una operación indexada, no un escaneo.
2. Por cada entrada `upsert`, obtener la entidad actual de la tabla de datos por su `PartitionKey`/`RowKey` exacto: también una lectura puntual indexada.
3. Por cada entrada `delete`, registrar el tombstone directamente desde el registro de cambios. No hace falta una tabla de tombstones aparte.

Esto hace que el coste de la copia de seguridad sea proporcional al número de cambios, no al tamaño total de los datos. Una sola consulta contra una tabla de índice compacta reemplaza 20 escaneos completos de tabla. También unifica el mecanismo de tombstones: el registro de cambios captura las creaciones, actualizaciones y eliminaciones de manera uniforme, por lo que la tabla `Tombstones` aparte se vuelve redundante.

### Por qué todavía no

La contrapartida es la sobrecarga en la ruta de escritura. Cada mutación en cada almacén necesitaría una escritura adicional en la tabla de registro de cambios. La infraestructura ya está casi toda ahí: el `ITombstoneWriter` ya se inyecta en cada almacén y se invoca en cada eliminación. Ampliarlo a un `IChangeTracker` que se dispare también en los upserts es una refactorización sencilla.

Pero "sencilla" no es "gratis". Añade latencia a cada operación de cara al usuario (una escritura adicional en Table Storage), aumenta las transacciones de almacenamiento e introduce una nueva preocupación de consistencia (¿qué pasa si la escritura de datos tiene éxito pero la escritura en el registro de cambios falla?). A los volúmenes actuales, los 20 escaneos filtrados por timestamp se completan en segundos y cuestan fracciones de céntimo. El registro de cambios sería la decisión correcta si las tablas crecieran hasta millones de entidades, pero por ahora, gana el enfoque más simple.

## Resumen

El enfoque es deliberadamente simple. En lugar de construir una compleja canalización de captura de datos de cambios o depender de características específicas de Azure que quizá no existan para Table Storage, Authagonal usa el único fragmento de metadatos que Azure *sí* garantiza (el `Timestamp` gestionado por el servidor) combinado con un seguimiento de tombstones a nivel de aplicación para las eliminaciones.

El resultado es un sistema de copia de seguridad que:

- Produce archivos JSONL portátiles y legibles para humanos
- Admite modos completo e incremental con gestión automática de la marca de agua
- Captura correctamente las creaciones, las actualizaciones *y* las eliminaciones
- Maneja el prefijado de tablas multiinquilino de forma transparente
- Se compone limpiamente (fusión, rollup, restauración selectiva)
- Se ejecuta como una herramienta independiente sin dependencia del servidor de Authagonal

La abstracción de almacenamiento significa que la misma lógica puede apuntar a un disco local, a Azure Blob Storage, a S3 o a cualquier otro destino. El formato es lo bastante simple como para que, incluso sin la herramienta de restauración, un operador pudiera reconstruir los datos con `jq` y la CLI de Azure.
