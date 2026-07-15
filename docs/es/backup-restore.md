---
layout: default
title: Backup & Restore
---

# Copia de seguridad y restauracion

Authagonal proporciona dos herramientas CLI para realizar copias de seguridad y restaurar datos de Azure Table Storage. Ambas son aplicaciones de consola .NET ubicadas en el directorio `tools/`, y ambas son envoltorios ligeros sobre el paquete NuGet `Authagonal.Backup`. Los hosts que necesitan copias de seguridad programadas, multiinquilino o fuera del sistema de archivos pueden usar la biblioteca directamente (ver [Usar la biblioteca](#usar-la-biblioteca)).

## Copia de seguridad

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### Opciones

| Option | Descripcion |
|---|---|
| `--connection-string <conn>` | Cadena de conexion de Azure Table Storage (o establecer la variable de entorno `STORAGE_CONNECTION_STRING`) |
| `--output <dir>` | Directorio de salida (predeterminado: `./backups`) |
| `--incremental` | Solo respaldar entidades modificadas desde la ultima copia de seguridad |
| `--tables <t1,t2,...>` | Lista de tablas separadas por comas (predeterminado: todas las tablas de Authagonal) |
| `--prefix <prefix>` | Prefijo de nombre de tabla (para almacenamiento multiinquilino) |
| `--gzip` | Comprimir archivos de copia de seguridad con gzip (`.jsonl.gz`) |
| `--dry-run` | Mostrar lo que se respaldaria sin escribir |

### Formato de salida

Cada copia de seguridad crea un directorio con marca de tiempo:

```
backups/
  20260329-120000/          (full backup)
    Users.jsonl
    Clients.jsonl
    Grants.jsonl
    ...
    _manifest.json
  20260329-180000-incr/     (incremental, compressed)
    Users.jsonl.gz
    _tombstones.jsonl.gz
    _manifest.json
```

Cada archivo `.jsonl` contiene un objeto JSON por linea (uno por entidad de tabla). Con `--gzip`, los archivos se comprimen como `.jsonl.gz`. El archivo `_manifest.json` registra el id de la copia de seguridad, la marca de tiempo, el modo (`full` o `incremental`), la compresion, la marca de agua incremental, el recuento de entidades por tabla, el recuento de tombstones, que tablas (si las hay) se leyeron mediante el change-log (`ChangeLogTables`, null significa cobertura de escaneo completo) y los hashes SHA-256 de los archivos para la verificacion de integridad.

Las copias de seguridad incrementales tambien escriben un archivo `_tombstones.jsonl(.gz)` que registra las eliminaciones desde la marca de agua: una linea por cada fila eliminada con `Table`, `PartitionKey`, `RowKey` y `DeletedAt`. La restauracion las reproduce para que las filas eliminadas no vuelvan a aparecer (ver [Reproduccion de tombstones](#reproduccion-de-tombstones)).

Los valores de las entidades hacen un round-trip exacto: cada fila respaldada lleva un marcador de formato `"@v"` y una anotacion explicita `"{column}@odata.type"` (`Edm.Guid`, `Edm.DateTime`, `Edm.Binary`, `Edm.Int64`, `Edm.Double`) para cada columna que JSON no puede representar sin ambiguedad, de modo que la restauracion vuelve a escribir los tipos originales en lugar de valores convertidos en cadena o reinferidos.

### Verificacion de integridad

Cada manifiesto de copia de seguridad incluye un diccionario `FileHashes` que asigna los nombres de archivo a sus hashes SHA-256. Durante la restauracion, la integridad de cada archivo se verifica contra estos hashes antes de escribir cualquiera de sus datos; un archivo que no supera la comprobacion, o un archivo de datos ausente del manifiesto, aborta la restauracion con un error. Las copias de seguridad escritas antes de que existiera el hashing de integridad (sin `FileHashes` en el manifiesto) no se pueden verificar y en su lugar se restauran con una advertencia destacada. La verificacion se puede deshabilitar mediante programacion con `RestoreOptions.VerifyIntegrity` (predeterminado `true`).

### Copias de seguridad incrementales

Pase `--incremental` para respaldar solo las entidades modificadas desde la ultima copia de seguridad exitosa. La herramienta utiliza la propiedad integrada `Timestamp` de Azure Table Storage para el filtrado y registra la marca de agua alta en un archivo `.lastbackup` en el directorio de salida.

Si no existe un archivo `.lastbackup`, la primera ejecucion incremental realiza una copia de seguridad completa.

Cada filtro `Timestamp` incremental resta un pequeno margen de seguridad (`BackupDefaults.WatermarkSkewMargin`, 5 minutos) antes de filtrar. La marca de agua proviene del reloj del llamador mientras que las marcas de tiempo de las filas las estampa el servicio de almacenamiento, por lo que una mutacion que se confirme dentro del desfase de reloj de otro modo se perderia en esta ejecucion y en todas las posteriores. Volver a leer el margen cuesta unas pocas filas duplicadas por ejecucion, que la semantica de upsert de la restauracion elimina.

### Tablas predeterminadas

La herramienta de copia de seguridad incluye todas las tablas de Authagonal de forma predeterminada (`BackupDefaults.Tables`):

`Users`, `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `ScimGroupRoleMappings`, `Roles`, `Scopes`, `ProvisioningApps`

Las tablas transitorias (`SamlReplayCache`, `OidcStateStore`, `RevokedTokens`) se excluyen de forma predeterminada, ya que sus entradas estan limitadas por la vida util de los tokens; incluyalas explicitamente con `--tables` si es necesario. La tabla de change-log `Tombstones` la gestiona por separado el motor de copia de seguridad y no debe incluirse en la lista.

### Las claves de firma se excluyen de forma predeterminada

La tabla `SigningKeys` esta en la lista de tablas predeterminada pero **se filtra de las copias de seguridad de forma predeterminada** (`BackupOptions.IncludeSigningKeys`, `false` por defecto; la CLI nunca la habilita). Para hosts que usan la fuente de claves local (almacenada en tabla), esta tabla contiene la **clave privada** de firma de los JWT, y escribirla en un archivo de copia de seguridad en texto plano permitiria que cualquiera que lea la copia de seguridad falsifique tokens. (Los hosts que firman mediante HashiCorp Vault Transit no guardan ninguna clave privada en la tabla, por lo que esta consideracion no les aplica.)

> ⚠️ Opte por incluirla mediante `BackupOptions.IncludeSigningKeys` solo cuando el destino de la copia de seguridad este cifrado en reposo y tenga control de acceso. Lo mismo aplica al resto de la copia de seguridad: con el proveedor de secretos de **texto plano** predeterminado, las copias de seguridad tambien contienen secretos de clientes OIDC upstream y semillas TOTP / MFA en texto claro. Ver [Configuracion → Proveedor de secretos](configuration#secret-provider).

## Restauracion

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### Opciones

| Option | Descripcion |
|---|---|
| `--connection-string <conn>` | Cadena de conexion de Azure Table Storage (o establecer la variable de entorno `STORAGE_CONNECTION_STRING`) |
| `--input <dir>` | Directorio de copia de seguridad desde el cual restaurar |
| `--mode <mode>` | Modo de restauracion: `upsert` (predeterminado), `merge` o `clean` |
| `--tables <t1,t2,...>` | Lista de tablas a restaurar separadas por comas (predeterminado: todos los archivos `.jsonl`/`.jsonl.gz` en la copia de seguridad) |
| `--prefix <prefix>` | Prefijo de nombre de tabla (para almacenamiento multiinquilino) |
| `--dry-run` | Mostrar lo que se restauraria sin escribir |

### Modos de restauracion

| Modo | Comportamiento |
|---|---|
| `upsert` | Insertar o reemplazar cada entidad. Los datos existentes se sobrescriben. |
| `merge` | Insertar o fusionar. Las propiedades existentes que no estan en la copia de seguridad se conservan. |
| `clean` | Eliminar todos los datos existentes en cada tabla antes de restaurar. |

Los archivos de copia de seguridad comprimidos con gzip (`.jsonl.gz`) se detectan y descomprimen automaticamente; no se necesitan indicadores adicionales.

### Reproduccion de tombstones

Despues de los archivos de datos, la restauracion aplica el archivo `_tombstones` de la copia de seguridad: cada clave registrada se elimina de las tablas restauradas (`RestoreOptions.ApplyTombstones`, predeterminado `true`). Las eliminaciones de una incremental son parte de su estado tanto como sus upserts; omitirlas resucitaria filas eliminadas, incluidas las borradas por RGPD, al restaurar una secuencia de completa mas incrementales. Las copias de seguridad completas no llevan archivo de tombstones. Al restaurar una copia de seguridad completa seguida de incrementales, apliquelas de la mas antigua a la mas reciente para que una recreacion posterior quede despues de una eliminacion anterior. El hash del archivo de tombstones se verifica contra el manifiesto igual que los archivos de datos.

### Round-trip de tipos exacto

Las filas escritas con el marcador de formato `"@v"` llevan anotaciones de tipo EDM explicitas, de modo que la restauracion reconstruye los tipos de columna originales exactos (`Int64`, `Guid`, `Binary`, `DateTime`, `Double`); una cadena sin anotacion se restaura como cadena. Los archivos de copia de seguridad heredados sin el marcador recurren a la inferencia basada en la forma, conservada solo para que las copias de seguridad antiguas sigan siendo restaurables (la inferencia puede asignar un tipo incorrecto a columnas de cadena con forma de GUID o de fecha).

### Codigos de salida

| Codigo | Significado |
|---|---|
| `0` | Exito |
| `1` | Error (argumentos faltantes, entrada invalida) |
| `2` | Exito parcial (algunas entidades tuvieron errores) |

## Usar la biblioteca

El paquete NuGet `Authagonal.Backup` expone las mismas operaciones mediante programacion, para servicios en segundo plano u orquestacion personalizada:

| Tipo | Proposito |
|---|---|
| `BackupService` | Ejecuta una copia de seguridad completa o incremental contra un `TableServiceClient`, escribiendo en un `IBackupTarget` |
| `RestoreService` | Verifica los hashes y vuelve a escribir una copia de seguridad en Table Storage |
| `MergeService` | Transmite una copia de seguridad completa mas incrementales (y sus tombstones) en una unica vista de estado actual |
| `RollupService` | Consolida los incrementales en una nueva copia de seguridad completa, eliminando opcionalmente las entradas |
| `BackupOptions` / `RestoreOptions` | Configuracion por ejecucion |
| `BackupDefaults` | Lista de tablas predeterminada y ajustes preestablecidos del change-log |
| `IBackupSource` / `IBackupTarget` | Abstracciones de almacenamiento; `FileSystemBackupSource` / `FileSystemBackupTarget` son las implementaciones integradas. Implemente `IBackupTarget` para escribir en blob storage o en otro destino. |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### Incrementales impulsados por el change-log

Azure Table Storage solo indexa `PartitionKey` y `RowKey`, por lo que una copia de seguridad incremental filtrada por `Timestamp` sigue siendo un escaneo completo de cada tabla. Para evitarlo, los stores de Authagonal registran cada mutacion en un change-log mediante el seam `IChangeWriter` (`Authagonal.Core`), implementado para Azure por `TableChangeWriter` (`Authagonal.AzureProvider`). Es una unica tabla fisica, todavia llamada `Tombstones`: PK = el nombre logico de la tabla, RK = `"{pk}|{rk}"`, una columna `Op` de `"U"` (upsert) o `"D"` (delete), y columnas autoritativas `OrigPK`/`OrigRK` (un `|` dentro del PartitionKey original hace ambigua la division del RowKey compuesto, por lo que el lector de la copia de seguridad confia en las columnas y solo recurre a la division para las filas heredadas). Cada clave conserva una fila (upsert-replace), de modo que la ultima operacion en una ventana de copia de seguridad gana.

Con la ruta de change-log habilitada, una copia de seguridad incremental enumera las entradas de change-log con `Op = "U"` de una tabla desde la marca de agua y lee por punto cada fila viva en lugar de escanear la tabla. La funcion es **opcional y esta desactivada de forma predeterminada**: `BackupOptions.ChangeLoggedTables` nulo o vacio significa que cada tabla permanece en la ruta de escaneo, por lo que el mecanismo se entrega inerte hasta un cambio deliberado (un despliegue no puede omitir silenciosamente filas modificadas por codigo anterior a la captura). Dos ajustes preestablecidos:

| Ajuste preestablecido | Contenido |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | Las tablas cuyas escrituras se capturan por completo en el change-log |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | El mismo conjunto mas `Users`. Las escrituras de estado de inicio de sesion de Users deliberadamente no se capturan (ruta caliente, bajo valor), por lo que este ajuste preestablecido **solo es seguro cuando tambien ejecuta el backstop de escaneo completo de abajo** |

La propiedad `ChangeLogTables` del manifiesto lista que tablas leyo una ejecucion mediante el change-log; nulo o vacio significa que la ejecucion tuvo cobertura de escaneo completo (una copia de seguridad completa, una incremental de escaneo simple o un escaneo de backstop).

### Backstop de escaneo completo

Dado que la captura del change-log puede omitir escrituras (campos de estado de inicio de sesion, escritores ajenos al store, pods ejecutando codigo anterior a la captura durante un despliegue), combine las incrementales de change-log con un re-escaneo completo periodico. Establezca `BackupOptions.WatermarkOverride` en la marca de tiempo del ultimo escaneo de cobertura completa y deje `ChangeLoggedTables` sin definir para esa ejecucion: la incremental filtra entonces por `Timestamp` en toda la ventana desde ese escaneo, recogiendo cualquier cosa que el change-log nunca haya capturado. Un backstop diario junto a incrementales de change-log cada hora es una cadencia razonable. Las eliminaciones son la unica clase de mutacion sin autocorreccion (un escaneo de filas vivas no puede ver una fila que ya no existe), razon por la cual los stores escriben el tombstone de eliminacion **antes** de eliminar la fila de datos.

Todos los filtros incrementales, incluido el backstop, restan `BackupDefaults.WatermarkSkewMargin` (5 minutos) de la marca de agua; los llamadores que purgan el change-log despues de una copia de seguridad deben acotar la purga por el mismo margen o eliminaran filas que la siguiente ejecucion todavia necesita.

### Rollups

`RollupService.RollupAsync` fusiona una copia de seguridad completa y sus incrementales en una nueva copia de seguridad completa; `RollupAndCleanAsync` ademas elimina las entradas despues. El parametro opcional `newBackupId` nombra el resultado (nulo deriva un id con marca de tiempo); una instantanea retenida de forma especial (por ejemplo un rollup semanal) debe pasar su id aqui, ya que la retencion basada en id lista los ids fisicos de copia de seguridad, no los manifiestos.

Durante una fusion, los tombstones se aplican con ordenacion por marca de tiempo: una eliminacion quita una fila capturada solo cuando la marca de tiempo `Timestamp` de la fila no es posterior al `DeletedAt` del tombstone. Una clave eliminada al principio de la ventana y recreada mas tarde tiene tanto un tombstone como una captura viva, y la fila recreada sobrevive al rollup. Los tombstones heredados sin `DeletedAt` eliminan de forma incondicional.

## Docker

La herramienta de copia de seguridad incluye un Dockerfile (`tools/Authagonal.Backup/Dockerfile`) para ejecutarse en CI o sin instalar el SDK de .NET:

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

La herramienta de restauracion no tiene imagen; ejecutela con el SDK de .NET (`dotnet run --project tools/Authagonal.Restore`).

## Programacion de copias de seguridad

Para uso en produccion, ejecute la herramienta de copia de seguridad de forma programada (por ejemplo, completa diaria + incremental cada hora):

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

Los hosts que integran la biblioteca suelen ejecutar incrementales cada hora con la ruta de change-log activada, un backstop de escaneo completo diario y rollups periodicos para acotar la cadena de incrementales.
