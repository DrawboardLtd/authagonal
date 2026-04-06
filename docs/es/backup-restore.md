---
layout: default
title: Backup & Restore
---

# Copia de seguridad y restauracion

Authagonal proporciona dos herramientas CLI para realizar copias de seguridad y restaurar datos de Azure Table Storage. Ambas son aplicaciones de consola .NET ubicadas en el directorio `tools/`.

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
    _manifest.json
```

Cada archivo `.jsonl` contiene un objeto JSON por linea (uno por entidad de tabla). Con `--gzip`, los archivos se comprimen como `.jsonl.gz`. El archivo `_manifest.json` registra la marca de tiempo de la copia de seguridad, el modo, la compresion y el recuento de entidades.

### Copias de seguridad incrementales

Pase `--incremental` para respaldar solo las entidades modificadas desde la ultima copia de seguridad exitosa. La herramienta utiliza la propiedad integrada `Timestamp` de Azure Table Storage para el filtrado y registra la marca de agua alta en un archivo `.lastbackup` en el directorio de salida.

Si no existe un archivo `.lastbackup`, la primera ejecucion incremental realiza una copia de seguridad completa.

### Tablas predeterminadas

La herramienta de copia de seguridad incluye todas las tablas de Authagonal de forma predeterminada:

`Users`, `UserEmails`, `UserLogins`, `UserExternalIds`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `Roles`

Las tablas transitorias (`SamlReplayCache`, `OidcStateStore`) se excluyen de forma predeterminada; incluyalas explicitamente con `--tables` si es necesario.

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
| `--dry-run` | Mostrar lo que se restauraria sin escribir |

### Modos de restauracion

| Modo | Comportamiento |
|---|---|
| `upsert` | Insertar o reemplazar cada entidad. Los datos existentes se sobrescriben. |
| `merge` | Insertar o fusionar. Las propiedades existentes que no estan en la copia de seguridad se conservan. |
| `clean` | Eliminar todos los datos existentes en cada tabla antes de restaurar. |

Los archivos de copia de seguridad comprimidos con gzip (`.jsonl.gz`) se detectan y descomprimen automaticamente — no se necesitan indicadores adicionales.

### Codigos de salida

| Codigo | Significado |
|---|---|
| `0` | Exito |
| `1` | Error (argumentos faltantes, entrada invalida) |
| `2` | Exito parcial (algunas entidades tuvieron errores) |

## Docker

Ambas herramientas tienen imagenes Docker para ejecutarse en CI o sin instalar el SDK de .NET:

```bash
# Backup
docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  drawboardci/authagonal-backup --output /backups

# Restore
docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  drawboardci/authagonal-restore --input /backups/20260329-120000
```

## Programacion de copias de seguridad

Para uso en produccion, ejecute la herramienta de copia de seguridad de forma programada (por ejemplo, completa diaria + incremental cada hora):

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```
