---
layout: default
title: Backup & Restore
locale: fr
---

# Sauvegarde et restauration

Authagonal fournit deux outils CLI pour sauvegarder et restaurer les données Azure Table Storage. Les deux sont des applications console .NET situées dans le répertoire `tools/`, et les deux sont de fines surcouches du package NuGet `Authagonal.Backup`. Les hôtes qui ont besoin de sauvegardes planifiées, multi-tenant ou vers autre chose que le système de fichiers peuvent utiliser la bibliothèque directement (voir [Utiliser la bibliothèque](#utiliser-la-bibliothèque)).

## Sauvegarde

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### Options

| Option | Description |
|---|---|
| `--connection-string <conn>` | Chaîne de connexion Azure Table Storage (ou définir la variable d'environnement `STORAGE_CONNECTION_STRING`) |
| `--output <dir>` | Répertoire de sortie (par défaut : `./backups`) |
| `--incremental` | Sauvegarder uniquement les entités modifiées depuis la dernière sauvegarde |
| `--tables <t1,t2,...>` | Liste de tables séparées par des virgules (par défaut : toutes les tables Authagonal) |
| `--prefix <prefix>` | Préfixe de nom de table (pour le stockage multi-tenant) |
| `--gzip` | Compresser les fichiers de sauvegarde avec gzip (`.jsonl.gz`) |
| `--dry-run` | Afficher ce qui serait sauvegardé sans écrire |

### Format de sortie

Chaque sauvegarde crée un répertoire horodaté :

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

Chaque fichier `.jsonl` contient un objet JSON par ligne (un par entité de table). Avec `--gzip`, les fichiers sont compressés en `.jsonl.gz`. Le fichier `_manifest.json` enregistre l'identifiant de la sauvegarde, l'horodatage, le mode (`full` ou `incremental`), la compression, la borne (watermark) incrémentale, le nombre d'entités par table, le nombre de tombstones, quelles tables (le cas échéant) ont été lues via le change-log (`ChangeLogTables`, null signifiant une couverture par scan complet), et les empreintes SHA-256 des fichiers pour la vérification d'intégrité.

Les sauvegardes incrémentales écrivent aussi un fichier `_tombstones.jsonl(.gz)` qui enregistre les suppressions depuis la borne : une ligne par ligne supprimée avec `Table`, `PartitionKey`, `RowKey` et `DeletedAt`. La restauration les rejoue afin que les lignes supprimées ne soient pas ressuscitées (voir [Relecture des tombstones](#relecture-des-tombstones)).

Les valeurs d'entité font un aller-retour exact : chaque ligne sauvegardée porte un marqueur de format `"@v"` et une annotation explicite `"{column}@odata.type"` (`Edm.Guid`, `Edm.DateTime`, `Edm.Binary`, `Edm.Int64`, `Edm.Double`) pour chaque colonne que JSON ne peut pas représenter sans ambiguïté, de sorte que la restauration réécrit les types d'origine plutôt que des valeurs converties en chaîne ou ré-inférées.

### Vérification d'intégrité

Chaque manifeste de sauvegarde inclut un dictionnaire `FileHashes` associant les noms de fichiers à leurs empreintes SHA-256. Lors de la restauration, l'intégrité de chaque fichier est vérifiée par rapport à ces empreintes avant l'écriture de la moindre de ses données ; un fichier qui échoue à la vérification, ou un fichier de données absent du manifeste, interrompt la restauration avec une erreur. Les sauvegardes écrites avant l'existence du hachage d'intégrité (pas de `FileHashes` dans le manifeste) ne peuvent pas être vérifiées et sont restaurées avec un avertissement bien visible à la place. La vérification peut être désactivée par programmation via `RestoreOptions.VerifyIntegrity` (par défaut `true`).

### Sauvegardes incrémentales

Passez `--incremental` pour ne sauvegarder que les entités modifiées depuis la dernière sauvegarde réussie. L'outil utilise la propriété intégrée `Timestamp` d'Azure Table Storage pour le filtrage et suit la valeur maximale (high-water mark) dans un fichier `.lastbackup` situé dans le répertoire de sortie.

Si aucun fichier `.lastbackup` n'existe, la première exécution incrémentale effectue une sauvegarde complète.

Chaque filtre `Timestamp` incrémental soustrait une petite marge de sécurité (`BackupDefaults.WatermarkSkewMargin`, 5 minutes) avant de filtrer. La borne provient de l'horloge de l'appelant tandis que les horodatages des lignes sont apposés par le service de stockage : une mutation validée à l'intérieur du décalage d'horloge serait sinon manquée par cette exécution et par toutes les suivantes. Relire la marge coûte quelques lignes dupliquées par exécution, que la sémantique upsert de la restauration dédoublonne.

### Tables par défaut

L'outil de sauvegarde inclut toutes les tables Authagonal par défaut (`BackupDefaults.Tables`) :

`Users`, `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `ScimGroupRoleMappings`, `Roles`, `Scopes`, `ProvisioningApps`

Les tables transitoires (`SamlReplayCache`, `OidcStateStore`, `RevokedTokens`) sont exclues par défaut puisque leurs entrées sont bornées par la durée de vie des tokens ; incluez-les explicitement avec `--tables` si nécessaire. La table de change-log `Tombstones` est gérée séparément par le moteur de sauvegarde et ne doit pas être listée.

### Les clés de signature sont exclues par défaut

La table `SigningKeys` figure dans la liste de tables par défaut mais est **filtrée des sauvegardes par défaut** (`BackupOptions.IncludeSigningKeys`, par défaut `false` ; la CLI ne l'active jamais). Pour les hôtes utilisant la source de clés locale (stockée en table), cette table contient la **clé privée** de signature JWT, et l'écrire dans un fichier de sauvegarde en texte brut permettrait à quiconque lit la sauvegarde de forger des tokens. (Les hôtes qui signent via HashiCorp Vault Transit ne conservent aucune clé privée dans la table, donc cette préoccupation ne les concerne pas.)

> ⚠️ N'activez `BackupOptions.IncludeSigningKeys` que lorsque la cible de sauvegarde est elle-même chiffrée au repos et à accès contrôlé. Il en va de même pour le reste de la sauvegarde : avec le fournisseur de secrets **en texte brut** par défaut, les sauvegardes contiennent aussi les secrets des clients OIDC en amont et les graines TOTP / MFA en clair. Voir [Configuration → Fournisseur de secrets](configuration#secret-provider).

## Restauration

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### Options

| Option | Description |
|---|---|
| `--connection-string <conn>` | Chaîne de connexion Azure Table Storage (ou définir la variable d'environnement `STORAGE_CONNECTION_STRING`) |
| `--input <dir>` | Répertoire de sauvegarde à partir duquel restaurer |
| `--mode <mode>` | Mode de restauration : `upsert` (par défaut), `merge` ou `clean` |
| `--tables <t1,t2,...>` | Liste de tables à restaurer séparées par des virgules (par défaut : tous les fichiers `.jsonl`/`.jsonl.gz` dans la sauvegarde) |
| `--prefix <prefix>` | Préfixe de nom de table (pour le stockage multi-tenant) |
| `--dry-run` | Afficher ce qui serait restauré sans écrire |

### Modes de restauration

| Mode | Comportement |
|---|---|
| `upsert` | Insérer ou remplacer chaque entité. Les données existantes sont écrasées. |
| `merge` | Insérer ou fusionner. Les propriétés existantes absentes de la sauvegarde sont conservées. |
| `clean` | Supprimer toutes les données existantes dans chaque table avant la restauration. |

Les fichiers de sauvegarde compressés avec gzip (`.jsonl.gz`) sont détectés et décompressés automatiquement ; aucun indicateur supplémentaire n'est nécessaire.

### Relecture des tombstones

Après les fichiers de données, la restauration applique le fichier `_tombstones` de la sauvegarde : chaque clé enregistrée est supprimée des tables restaurées (`RestoreOptions.ApplyTombstones`, par défaut `true`). Les suppressions d'un incrément font autant partie de son état que ses upserts ; les ignorer ressusciterait des lignes supprimées, y compris celles effacées au titre du RGPD, lors de la restauration d'une séquence complète plus incréments. Les sauvegardes complètes ne portent aucun fichier de tombstones. Lors de la restauration d'une sauvegarde complète suivie d'incréments, appliquez-les du plus ancien au plus récent afin qu'une recréation ultérieure vienne après une suppression antérieure. L'empreinte du fichier de tombstones est vérifiée par rapport au manifeste, comme les fichiers de données.

### Aller-retour exact des types

Les lignes écrites avec le marqueur de format `"@v"` portent des annotations de type EDM explicites, de sorte que la restauration reconstruit les types de colonnes d'origine exacts (`Int64`, `Guid`, `Binary`, `DateTime`, `Double`) ; une chaîne non annotée est restaurée comme une chaîne. Les anciens fichiers de sauvegarde sans le marqueur retombent sur une inférence basée sur la forme, conservée uniquement pour que les anciennes sauvegardes restent restaurables (l'inférence peut mal typer des colonnes de type chaîne ayant la forme d'un GUID ou d'une date).

### Codes de sortie

| Code | Signification |
|---|---|
| `0` | Succès |
| `1` | Erreur (arguments manquants, entrée invalide) |
| `2` | Succès partiel (certaines entités ont eu des erreurs) |

## Utiliser la bibliothèque

Le package NuGet `Authagonal.Backup` expose les mêmes opérations par programmation, pour des services d'arrière-plan ou une orchestration personnalisée :

| Type | Rôle |
|---|---|
| `BackupService` | Exécute une sauvegarde complète ou incrémentale sur un `TableServiceClient`, en écrivant vers un `IBackupTarget` |
| `RestoreService` | Vérifie les empreintes et réécrit une sauvegarde dans Table Storage |
| `MergeService` | Diffuse une sauvegarde complète plus les incréments (et leurs tombstones) en une seule vue de l'état courant |
| `RollupService` | Replie les incréments dans une nouvelle sauvegarde complète, en supprimant éventuellement les entrées |
| `BackupOptions` / `RestoreOptions` | Configuration par exécution |
| `BackupDefaults` | Liste de tables par défaut et préréglages de change-log |
| `IBackupSource` / `IBackupTarget` | Abstractions de stockage ; `FileSystemBackupSource` / `FileSystemBackupTarget` sont les implémentations intégrées. Implémentez `IBackupTarget` pour écrire vers le stockage blob ou ailleurs. |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### Incréments pilotés par le change-log

Azure Table Storage n'indexe que `PartitionKey` et `RowKey`, donc une sauvegarde incrémentale filtrée sur `Timestamp` reste un scan complet de chaque table. Pour éviter cela, les stores d'Authagonal enregistrent chaque mutation dans un change-log via le seam `IChangeWriter` (`Authagonal.Core`), implémenté pour Azure par `TableChangeWriter` (`Authagonal.AzureProvider`). C'est une seule table physique, toujours nommée `Tombstones` : PK = le nom logique de la table, RK = `"{pk}|{rk}"`, une colonne `Op` valant `"U"` (upsert) ou `"D"` (delete), et des colonnes `OrigPK`/`OrigRK` faisant autorité (un `|` à l'intérieur du PartitionKey d'origine rend ambigu le découpage du RowKey composite, donc le lecteur de sauvegarde se fie aux colonnes et ne retombe sur le découpage que pour les lignes héritées). Chaque clé ne contient qu'une ligne (upsert-replace), donc la dernière opération d'une fenêtre de sauvegarde l'emporte.

Lorsque le chemin de change-log est activé, une sauvegarde incrémentale énumère les entrées de change-log `Op = "U"` d'une table depuis la borne et effectue une lecture ponctuelle de chaque ligne active au lieu de scanner la table. La fonctionnalité est **optionnelle et désactivée par défaut** : `BackupOptions.ChangeLoggedTables` null ou vide signifie que chaque table reste sur le chemin de scan, donc le mécanisme est livré inerte jusqu'à un basculement délibéré (un déploiement ne peut pas manquer silencieusement des lignes modifiées par du code antérieur à la capture). Deux préréglages :

| Préréglage | Contenu |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | Les tables dont les écritures sont entièrement capturées par le change-log |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | Le même ensemble plus `Users`. Les écritures d'état de connexion des utilisateurs ne sont délibérément pas capturées (chemin critique, faible valeur), donc ce préréglage n'est **sûr que si vous exécutez aussi le filet de sécurité par scan complet ci-dessous** |

La propriété `ChangeLogTables` du manifeste liste les tables qu'une exécution a lues via le change-log ; null ou vide signifie que l'exécution avait une couverture par scan complet (une sauvegarde complète, un incrément par scan simple, ou un scan de filet de sécurité).

### Filet de sécurité par scan complet

Parce que la capture par change-log peut manquer des écritures (champs d'état de connexion, écrivains hors store, pods exécutant du code antérieur à la capture pendant un déploiement), associez les incréments par change-log à un re-scan complet périodique. Réglez `BackupOptions.WatermarkOverride` sur l'horodatage du dernier scan à couverture complète et laissez `ChangeLoggedTables` non défini pour cette exécution : l'incrément filtre alors sur `Timestamp` sur toute la fenêtre depuis ce scan, récupérant tout ce que le change-log n'a jamais capturé. Un filet de sécurité quotidien aux côtés d'incréments par change-log horaires est une cadence raisonnable. Les suppressions sont la seule classe de mutation sans auto-réparation (un scan de lignes actives ne peut pas voir une ligne qui a disparu), ce qui explique pourquoi les stores écrivent le tombstone de suppression **avant** de supprimer la ligne de données.

Tous les filtres incrémentaux, filet de sécurité compris, soustraient `BackupDefaults.WatermarkSkewMargin` (5 minutes) de la borne ; les appelants qui purgent le change-log après une sauvegarde doivent borner la purge par la même marge, faute de quoi ils supprimeraient des lignes dont l'exécution suivante a encore besoin.

### Rollups

`RollupService.RollupAsync` fusionne une sauvegarde complète et ses incréments en une nouvelle sauvegarde complète ; `RollupAndCleanAsync` supprime en plus les entrées ensuite. Le paramètre optionnel `newBackupId` nomme le résultat (null dérive un identifiant horodaté) ; un instantané spécialement conservé (par exemple un rollup hebdomadaire) doit passer son identifiant ici, puisque la rétention par identifiant liste des identifiants de sauvegarde physiques, pas des manifestes.

Pendant une fusion, les tombstones s'appliquent avec un ordonnancement par horodatage : une suppression ne retire une ligne capturée que lorsque le `Timestamp` de la ligne ne postdate pas le `DeletedAt` du tombstone. Une clé supprimée tôt dans la fenêtre et recréée plus tard possède à la fois un tombstone et une capture active, et la ligne recréée survit au rollup. Les anciens tombstones sans `DeletedAt` suppriment inconditionnellement.

## Docker

L'outil de sauvegarde livre un Dockerfile (`tools/Authagonal.Backup/Dockerfile`) pour une exécution en CI ou sans installer le SDK .NET :

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

L'outil de restauration n'a pas d'image ; exécutez-le avec le SDK .NET (`dotnet run --project tools/Authagonal.Restore`).

## Planification des sauvegardes

Pour une utilisation en production, exécutez l'outil de sauvegarde selon un calendrier (par exemple, sauvegarde complète quotidienne + incrémentale horaire) :

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

Les hôtes qui embarquent la bibliothèque exécutent typiquement des incréments horaires avec le chemin de change-log activé, un filet de sécurité par scan complet quotidien, et des rollups périodiques pour borner la chaîne d'incréments.
