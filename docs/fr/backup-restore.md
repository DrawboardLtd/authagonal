---
layout: default
title: Backup & Restore
---

# Sauvegarde et restauration

Authagonal fournit deux outils CLI pour sauvegarder et restaurer les donnees Azure Table Storage. Les deux sont des applications console .NET situees dans le repertoire `tools/`.

## Sauvegarde

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### Options

| Option | Description |
|---|---|
| `--connection-string <conn>` | Chaine de connexion Azure Table Storage (ou definir la variable d'environnement `STORAGE_CONNECTION_STRING`) |
| `--output <dir>` | Repertoire de sortie (par defaut : `./backups`) |
| `--incremental` | Sauvegarder uniquement les entites modifiees depuis la derniere sauvegarde |
| `--tables <t1,t2,...>` | Liste de tables separees par des virgules (par defaut : toutes les tables Authagonal) |
| `--gzip` | Compresser les fichiers de sauvegarde avec gzip (`.jsonl.gz`) |
| `--dry-run` | Afficher ce qui serait sauvegarde sans ecrire |

### Format de sortie

Chaque sauvegarde cree un repertoire horodate :

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

Chaque fichier `.jsonl` contient un objet JSON par ligne (un par entite de table). Avec `--gzip`, les fichiers sont compresses en `.jsonl.gz`. Le fichier `_manifest.json` enregistre l'horodatage de la sauvegarde, le mode, la compression et le nombre d'entites.

### Sauvegardes incrementales

Passez `--incremental` pour ne sauvegarder que les entites modifiees depuis la derniere sauvegarde reussie. L'outil utilise la propriete integree `Timestamp` d'Azure Table Storage pour le filtrage et suit la valeur maximale dans un fichier `.lastbackup` dans le repertoire de sortie.

Si aucun fichier `.lastbackup` n'existe, la premiere execution incrementale effectue une sauvegarde complete.

### Tables par defaut

L'outil de sauvegarde inclut toutes les tables Authagonal par defaut :

`Users`, `UserEmails`, `UserLogins`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`

Les tables transitoires (`SamlReplayCache`, `OidcStateStore`) sont exclues par defaut — incluez-les explicitement avec `--tables` si necessaire.

## Restauration

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### Options

| Option | Description |
|---|---|
| `--connection-string <conn>` | Chaine de connexion Azure Table Storage (ou definir la variable d'environnement `STORAGE_CONNECTION_STRING`) |
| `--input <dir>` | Repertoire de sauvegarde a partir duquel restaurer |
| `--mode <mode>` | Mode de restauration : `upsert` (par defaut), `merge` ou `clean` |
| `--tables <t1,t2,...>` | Liste de tables a restaurer separees par des virgules (par defaut : tous les fichiers `.jsonl`/`.jsonl.gz` dans la sauvegarde) |
| `--dry-run` | Afficher ce qui serait restaure sans ecrire |

### Modes de restauration

| Mode | Comportement |
|---|---|
| `upsert` | Inserer ou remplacer chaque entite. Les donnees existantes sont ecrasees. |
| `merge` | Inserer ou fusionner. Les proprietes existantes absentes de la sauvegarde sont conservees. |
| `clean` | Supprimer toutes les donnees existantes dans chaque table avant la restauration. |

Les fichiers de sauvegarde compresses avec gzip (`.jsonl.gz`) sont detectes et decompresses automatiquement — aucun indicateur supplementaire n'est necessaire.

### Codes de sortie

| Code | Signification |
|---|---|
| `0` | Succes |
| `1` | Erreur (arguments manquants, entree invalide) |
| `2` | Succes partiel (certaines entites ont eu des erreurs) |

## Docker

Les deux outils disposent d'images Docker pour une execution en CI ou sans installer le SDK .NET :

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

## Planification des sauvegardes

Pour une utilisation en production, executez l'outil de sauvegarde selon un calendrier (par exemple, sauvegarde complete quotidienne + incrementale toutes les heures) :

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```
