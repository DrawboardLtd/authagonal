---
layout: default
title: Table Storage Backup Whitepaper
locale: fr
---

# Sauvegarder Azure Table Storage : une approche pratique

**Comment Authagonal implémente la sauvegarde complète et incrémentale pour un magasin NoSQL sans schéma**

---

## Le problème

Azure Table Storage est un magasin clé-valeur économique et massivement extensible, mais il n'offre aucune fonctionnalité de sauvegarde native. Il n'y a pas d'instantanés, pas de restauration à un instant précis, pas de bouton d'export. Si un mauvais déploiement corrompt des données, ou si un opérateur supprime accidentellement une table, la récupération dépend entièrement de ce que vous avez construit vous-même.

Pour une plateforme d'identité comme Authagonal (où les tables contiennent les utilisateurs, les identifiants, les OAuth grants, les clés de signature, les configurations SSO et l'état de provisionnement SCIM), les enjeux sont élevés. Perdre ces données ne casse pas seulement une application ; cela empêche les gens d'accéder à leur compte.

Ce document décrit la stratégie de sauvegarde utilisée par Authagonal : comment elle exporte les données, comment les sauvegardes incrémentales fonctionnent malgré le modèle de requête limité de Table Storage, comment les suppressions sont suivies, et comment les pièces se composent en un pipeline de sauvegarde prêt pour la production.

## Objectifs de conception

1. **Sauvegardes complètes et incrémentales.** Une sauvegarde complète quotidienne convient aux petits déploiements, mais à grande échelle, des incréments horaires maintiennent la fenêtre de sauvegarde courte et les coûts de stockage bas.
2. **Aller-retour fidèle.** Chaque propriété d'entité (chaînes, entiers, booléens, DateTimeOffsets, GUIDs, binaire) doit survivre à un cycle de sauvegarde/restauration sans coercition de type ni perte de données.
3. **Prise en charge multi-tenant.** Authagonal utilise des préfixes de noms de table pour isoler les tenants (par exemple `acmecorpUsers`, `acmecorpClients`). La sauvegarde et la restauration doivent tenir compte du préfixe afin qu'un seul compte de stockage puisse héberger de nombreux tenants avec des calendriers de sauvegarde indépendants.
4. **Stockage enfichable.** Les sauvegardes doivent fonctionner vers un système de fichiers local en développement et vers le stockage blob (ou toute autre cible) en production, sans changer la logique centrale.
5. **Sortie lisible par un humain.** Lorsque quelque chose tourne mal, un opérateur doit pouvoir ouvrir un fichier de sauvegarde dans un éditeur de texte et voir ce qu'il contient.

## Architecture

Le système de sauvegarde est structuré comme une bibliothèque .NET (`Authagonal.Backup`) avec de fines surcouches CLI pour les opérations de sauvegarde et de restauration. La bibliothèque est séparée du serveur Authagonal principal afin de pouvoir être utilisée comme outil autonome, dans un conteneur Docker, ou intégrée dans une tâche planifiée.

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

### Abstraction du stockage

Les services centraux ne touchent jamais directement au système de fichiers. Ils opèrent contre deux interfaces :

**IBackupTarget** fournit quatre opérations : ouvrir un flux inscriptible pour un fichier de sauvegarde, écrire un manifeste, obtenir le dernier watermark (pour la planification incrémentale) et définir un nouveau watermark.

**IBackupSource** fournit le côté lecture : lire un manifeste, ouvrir un flux lisible, lister les identifiants de sauvegarde par ordre chronologique, lister les fichiers d'une sauvegarde et supprimer une sauvegarde.

Les implémentations sur système de fichiers sont simples (des répertoires horodatés contenant des fichiers JSONL), mais l'abstraction signifie que passer à Azure Blob Storage ou S3 ne requiert d'implémenter que ces deux interfaces.

## Sauvegarde complète

Une sauvegarde complète parcourt chaque table Authagonal, interroge toutes les entités et les écrit dans des fichiers JSONL (un objet JSON par ligne, un fichier par table).

Le processus de sauvegarde :

1. Générer un identifiant de sauvegarde à partir de l'horodatage UTC actuel (par exemple `20260329-120000`).
2. Pour chacune des 20 tables Authagonal par défaut, interroger `QueryAsync<TableEntity>` du SDK Azure Table Storage avec une taille de page de 1 000.
3. Sérialiser chaque entité en un dictionnaire JSON plat préservant toutes les propriétés, y compris les propriétés système (`PartitionKey`, `RowKey`, `Timestamp`, `ETag`).
4. Écrire chaque entité sérialisée sous forme d'une seule ligne dans `{TableName}.jsonl` (ou `{TableName}.jsonl.gz` si la compression est activée).
5. Enregistrer le nombre d'entités et les durées par table dans un manifeste (`_manifest.json`).
6. Mettre à jour le fichier watermark `.lastbackup` avec l'heure de début de la sauvegarde.

Les tables qui n'existent pas dans le compte de stockage sont ignorées silencieusement (l'HTTP 404 est intercepté et ignoré). Les tables transitoires comme `SamlReplayCache` et `OidcStateStore` sont exclues par défaut car leur contenu est éphémère.

### Format de sortie

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

Une seule ligne dans `Users.jsonl` ressemble à :

```json
{"PartitionKey":"u_abc123","RowKey":"profile","Timestamp":"2026-03-28T09:14:22+00:00","ETag":"W/\"...\"","Email":"alice@example.com","DisplayName":"Alice","CreatedAt":"2025-11-01T00:00:00+00:00"}
```

JSONL a été choisi plutôt que CSV ou un format binaire car il préserve la nature sans schéma et hétérogène des entités Table Storage (des entités différentes dans la même table peuvent avoir des propriétés différentes), est diffusable en flux (pas besoin de mettre toute la table en mémoire tampon), et est directement inspectable avec des outils standard comme `jq` ou n'importe quel éditeur de texte.

### Compression

Lorsque l'option `--gzip` est activée, chaque fichier JSONL est encapsulé dans un flux GZip à `CompressionLevel.Optimal` avant l'écriture. L'extension du fichier devient `.jsonl.gz`. L'outil de restauration détecte automatiquement GZip en inspectant les octets magiques (`0x1f 0x8b`) au début de chaque fichier, aucune option n'est donc nécessaire lors de la restauration.

## Sauvegarde incrémentale

### L'astuce du Timestamp

Azure Table Storage maintient automatiquement une propriété `Timestamp` sur chaque entité, mise à jour à chaque insertion ou remplacement. C'est une propriété gérée par le serveur : les applications ne peuvent pas la définir. Le système de sauvegarde l'exploite en filtrant les requêtes sur `Timestamp gt datetime'{watermark}'`, où le watermark est l'heure de début de la dernière sauvegarde réussie.

Cela signifie qu'une sauvegarde incrémentale ne télécharge que les entités créées ou modifiées depuis l'exécution précédente. Pour un système comptant 500 000 entités dont 200 ont changé au cours de la dernière heure, la sauvegarde incrémentale transfère 200 lignes au lieu de 500 000.

Le watermark est stocké dans un fichier `.lastbackup` dans le répertoire racine des sauvegardes. Si le fichier n'existe pas (première exécution, ou après un nettoyage manuel), la sauvegarde retombe sur un export complet. Les identifiants de sauvegarde incrémentale incluent un suffixe `-incr` (par exemple `20260329-180000-incr`) et le manifeste enregistre `"mode": "incremental"` avec la valeur de watermark utilisée pour le filtrage.

### Coût du filtre Timestamp

Il vaut la peine d'être honnête sur une limitation : `Timestamp` n'est pas indexé. Azure Table Storage n'indexe que `PartitionKey` et `RowKey`. Un filtre sur `Timestamp gt datetime'...'` entraîne un balayage complet de la table : Azure lit chaque entité côté serveur et évalue le prédicat avant de renvoyer les correspondances. Le filtrage réduit le transfert de données (seules les entités modifiées passent sur le réseau), mais pas le coût de lecture côté serveur.

Plus important encore, l'approche actuelle balaie **les 20 tables** individuellement, même si une seule table a changé. Cela représente 20 balayages complets de tables par sauvegarde incrémentale, quel que soit le faible nombre d'entités réellement modifiées.

Aux volumes de données d'identité typiques d'Authagonal (des dizaines de milliers d'entités, pas des millions), c'est parfaitement acceptable : les balayages sont rapides, les lectures sont bon marché (0,00036 $ pour 10 000 transactions), et l'opération est en lecture seule sans impact sur le trafic en direct. La section [au-delà des balayages par Timestamp](#au-delà-des-balayages-par-timestamp) explique comment cela pourrait évoluer.

### Le problème de la suppression

Le filtre `Timestamp` capture élégamment les insertions et les mises à jour, mais il ne peut pas capturer les suppressions. Une entité supprimée disparaît simplement : il n'y a pas de `Timestamp` sur lequel filtrer, pas de tombstone laissé par Table Storage lui-même.

Authagonal résout cela avec un suivi de tombstones au niveau applicatif.

## Suivi des tombstones

Chaque magasin de données dans Authagonal (utilisateurs, clients, grants, clés de signature, domaines SSO, fournisseurs SAML/OIDC, identifiants MFA, ressources SCIM, rôles) accepte une dépendance `ITombstoneWriter` optionnelle. Lorsqu'un magasin supprime une entité, il écrit un enregistrement de tombstone dans une table `Tombstones` dédiée :

| Colonne | Valeur |
|---|---|
| `PartitionKey` | Nom de table logique (par exemple `"Users"`) |
| `RowKey` | `"{originalPartitionKey}\|{originalRowKey}"` |
| `DeletedAt` | Horodatage UTC de la suppression |

C'est un canal latéral léger, principalement en ajout. L'écriture de tombstone est un simple upsert, regroupé jusqu'à la limite de transaction de 100 entités d'Azure pour les opérations en masse.

Pendant une sauvegarde incrémentale, après avoir exporté les entités modifiées de chaque table, le service de sauvegarde interroge la table `Tombstones` pour les enregistrements ayant `Timestamp > watermark`. Ceux-ci sont écrits dans un fichier `_tombstones.jsonl` séparé dans le répertoire de sauvegarde, avec un format normalisé :

```json
{"Table":"Users","PartitionKey":"u_abc123","RowKey":"profile","DeletedAt":"2026-03-29T14:30:00+00:00"}
```

Cela signifie qu'une sauvegarde incrémentale capture une image complète de ce qui a changé : les entités ajoutées/modifiées (à partir des fichiers JSONL par table) et les entités supprimées (à partir du fichier de tombstones).

## Fusion et rollup

Au fil du temps, un répertoire de sauvegarde accumule une sauvegarde complète et de nombreux incréments. Pour restaurer l'état actuel, il faudrait tous les appliquer dans l'ordre. Le **MergeService** les consolide en une seule sauvegarde complète.

L'algorithme de fusion :

1. Charger l'ensemble d'entités de la sauvegarde complète, une table à la fois (pour borner l'utilisation de la mémoire).
2. Superposer chaque incrément par-dessus dans l'ordre chronologique : les valeurs plus récentes écrasent les plus anciennes, indexées par `(PartitionKey, RowKey)`.
3. Appliquer les tombstones : pour chaque tuple `(Table, PartitionKey, RowKey)` des fichiers de tombstones, retirer l'entité de l'ensemble fusionné.
4. Écrire l'ensemble d'entités résultant comme une nouvelle sauvegarde complète.

Le **RollupService** enveloppe cela d'un nettoyage : après une fusion réussie, il supprime l'ancienne sauvegarde complète et tous les incréments qui y ont été intégrés. Cela empêche l'utilisation du stockage de croître sans limite.

Un calendrier de production typique pourrait ressembler à ceci :

- **Toutes les heures :** sauvegarde incrémentale
- **Quotidien (2 h) :** sauvegarde complète
- **Hebdomadaire :** rollup (fusionner les sauvegardes quotidiennes + les incréments horaires de la semaine précédente, supprimer les originaux)

## Restauration

L'outil de restauration lit un répertoire de sauvegarde et réécrit les entités dans Azure Table Storage. Il prend en charge trois modes :

**Upsert** (par défaut) : chaque entité est insérée ou remplacée. Les entités existantes ayant la même clé sont écrasées. C'est le mode le plus sûr pour la reprise après sinistre.

**Merge** : chaque entité est insérée ou fusionnée. Les propriétés présentes dans la sauvegarde écrasent les propriétés correspondantes de l'entité existante, mais les propriétés qui existent dans la table en direct et pas dans la sauvegarde sont préservées. Utile pour les restaurations partielles.

**Clean** : toutes les entités existantes de chaque table cible sont supprimées avant la restauration. Cela produit une réplique exacte de l'état de la sauvegarde, au prix d'un balayage complet de la table (potentiellement lent) pour supprimer les données existantes.

### Fidélité des types

Un défi majeur dans l'aller-retour des données Table Storage via JSON est la préservation des types de propriétés. Table Storage prend nativement en charge les chaînes, les entiers (Int32/Int64), les doubles, les booléens, DateTimeOffset, Guid et binaire. JSON n'a pas de représentation native pour la plupart d'entre eux.

Le service de restauration utilise des heuristiques pour récupérer les types à partir de leur représentation JSON sous forme de chaîne :

- **DateTimeOffset** : les chaînes de 19 à 35 caractères, commençant par un chiffre et analysables en ISO 8601, sont restaurées en tant que `DateTimeOffset`.
- **Guid** : les chaînes d'exactement 36 caractères analysables en tant que GUID sont restaurées en tant que `Guid`.
- **Nombres** : les nombres JSON sont essayés en tant que `Int32`, puis `Int64`, puis `double`, dans cet ordre.
- **Booléens et nulls** : correspondent directement.

Cette approche heuristique couvre les schémas de données réels d'Authagonal sans nécessiter de registre de schéma ni d'annotations de type dans le format de sauvegarde.

### Gestion des erreurs

Les opérations de restauration sont tolérantes aux pannes au niveau de l'entité. Si une entité individuelle échoue à l'écriture (par exemple à cause d'une erreur Azure transitoire), le compteur d'erreurs est incrémenté mais la restauration continue. Le résultat final rapporte le nombre de succès et d'erreurs par table, et le processus se termine avec le code `2` en cas de succès partiel, distinct de `0` (succès total) et `1` (erreur fatale).

## Multi-tenancy

Authagonal prend en charge les déploiements multi-tenant où les tables de chaque tenant sont préfixées (par exemple `acmecorpUsers`, `contosoclients`). La sauvegarde et la restauration acceptent toutes deux une option `--prefix` qui est ajoutée en préfixe aux noms de table logiques lors de la communication avec Azure Table Storage.

Cela signifie :
- Une sauvegarde avec `--prefix acmecorp` lit depuis `acmecorpUsers`, `acmecorpClients`, etc., mais écrit des fichiers nommés `Users.jsonl`, `Clients.jsonl` (noms logiques).
- Une restauration avec `--prefix contoso` lit `Users.jsonl` et écrit dans `contosoUsers`.

Cela rend simple le clonage des données d'un tenant, la migration entre environnements, ou la restauration d'un tenant sans affecter les autres.

## Manifeste

Chaque sauvegarde inclut un fichier `_manifest.json` qui enregistre :

- **BackupId** : identifiant horodaté (par exemple `20260329-120000` ou `20260329-180000-incr`)
- **Mode** : `"full"` ou `"incremental"`
- **BackupTimestamp** : quand la sauvegarde a commencé (UTC)
- **Watermark** : pour les incréments, l'horodatage de coupure utilisé pour le filtrage
- **Compressed** : si les fichiers sont compressés en GZip
- **Tables** : un dictionnaire associant les noms de table au nombre d'entités et aux durées
- **TombstoneCount** : nombre d'enregistrements de tombstone (incrémental uniquement)
- **TotalEntities** : nombre total d'entités sur l'ensemble des tables
- **DurationSeconds** : temps d'horloge de l'exécution de la sauvegarde
- **FileHashes** : hashes SHA-256 de chaque fichier de sauvegarde pour la vérification d'intégrité

Le manifeste sert à la fois de tableau de bord opérationnel (quelle était la taille de la sauvegarde ? combien de temps a-t-elle pris ? quelles tables sont les plus grandes ?) et de filet de sécurité (la vérification des hashes pendant la restauration détecte les fichiers corrompus ou altérés).

## Caractéristiques opérationnelles

**La vitesse de sauvegarde** est bornée par le débit de requête d'Azure Table Storage, qui est généralement de 5 000 à 10 000 entités par seconde par table. Une sauvegarde complète de 100 000 entités réparties sur 20 tables s'achève en moins d'une minute. Les sauvegardes incrémentales de quelques centaines d'entités modifiées se terminent en quelques secondes.

**L'utilisation de la mémoire** est minimale. Le service de sauvegarde diffuse les entités directement sur le disque : il ne charge jamais une table entière en mémoire. Le service de fusion traite une table à la fois, ne chargeant que l'ensemble d'entités de cette table. Pour les très grandes tables (des millions d'entités), l'empreinte mémoire de la fusion est proportionnelle à la plus grande table.

**La politique de nouvelle tentative** est configurée avec un backoff exponentiel : 5 tentatives, commençant à 500 ms, plafonnées à 30 secondes. Cela couvre la limitation transitoire que Table Storage applique en cas de forte charge.

**Le mode simulation** (`--dry-run`) énumère les entités sans écrire aucun fichier, utile pour valider la connectivité et estimer la taille de la sauvegarde avant de s'engager dans une exécution complète.

## Au-delà des balayages par Timestamp

L'approche basée sur `Timestamp` est pragmatique à échelle modérée, mais son coût est proportionnel à la taille totale des données, pas au nombre de changements. À mesure que les tables grossissent, 20 balayages complets de tables par sauvegarde incrémentale deviennent de plus en plus dispendieux. L'évolution naturelle est une **table de journal de changements unifiée**.

L'idée clé est que le mécanisme de tombstone valide déjà ce schéma pour les suppressions. La table `Tombstones` est un index unique, compact et inter-tables : chaque suppression sur l'ensemble des 20 tables de données est enregistrée en un seul endroit, interrogeable par timestamp. Étendre cela pour couvrir toutes les mutations (insertions, mises à jour et suppressions) éliminerait entièrement le besoin de balayer les tables de données.

### Conception du journal de changements

Une table de journal de changements avec des partition keys regroupées par tranche de temps ressemblerait à ceci :

| PartitionKey | RowKey | Propriétés |
|---|---|---|
| `2026-03-29T18` | `Users\|u_abc123\|profile` | `Op = "upsert"` |
| `2026-03-29T18` | `Clients\|c_456\|config` | `Op = "upsert"` |
| `2026-03-29T18` | `Users\|u_xyz789\|profile` | `Op = "delete"` |

La partition key est une tranche horaire, de sorte que trouver tous les changements depuis la dernière sauvegarde devient un ensemble de **requêtes ponctuelles par partition key**, l'opération la plus rapide que Table Storage prend en charge. Le service de sauvegarde :

1. Interrogerait le journal de changements pour toutes les partitions de tranches horaires depuis le watermark. C'est une opération indexée, pas un balayage.
2. Pour chaque entrée `upsert`, récupérerait l'entité actuelle de la table de données par ses `PartitionKey`/`RowKey` exacts, également une lecture ponctuelle indexée.
3. Pour chaque entrée `delete`, enregistrerait le tombstone directement à partir du journal de changements. Pas besoin de table de tombstones séparée.

Cela rend le coût de la sauvegarde proportionnel au nombre de changements, pas à la taille totale des données. Une seule requête sur une table d'index compacte remplace 20 balayages complets de tables. Cela unifie également le mécanisme de tombstone : le journal de changements capture les créations, les mises à jour et les suppressions de manière uniforme, de sorte que la table `Tombstones` séparée devient redondante.

### Pourquoi pas encore

Le compromis est le surcoût sur le chemin d'écriture. Chaque mutation dans chaque magasin nécessiterait une écriture supplémentaire dans la table de journal de changements. La plomberie est en grande partie déjà là : l'`ITombstoneWriter` est déjà injecté dans chaque magasin et appelé à chaque suppression. L'élargir en un `IChangeTracker` qui se déclenche aussi sur les upserts est un refactoring simple.

Mais « simple » ne veut pas dire « gratuit ». Cela ajoute de la latence à chaque opération visible par l'utilisateur (une écriture Table Storage supplémentaire), augmente les transactions de stockage et introduit une nouvelle préoccupation de cohérence (que se passe-t-il si l'écriture des données réussit mais que l'écriture du journal de changements échoue ?). Aux volumes actuels, les 20 balayages filtrés par timestamp s'achèvent en quelques secondes et coûtent des fractions de centime. Le journal de changements serait le bon choix si les tables atteignaient des millions d'entités, mais pour l'instant, l'approche la plus simple l'emporte.

## Résumé

L'approche est délibérément simple. Plutôt que de construire un pipeline complexe de capture de données de changement (CDC) ou de s'appuyer sur des fonctionnalités spécifiques à Azure qui pourraient ne pas exister pour Table Storage, Authagonal utilise la seule métadonnée qu'Azure *garantit* réellement, le `Timestamp` géré par le serveur, combinée à un suivi de tombstones au niveau applicatif pour les suppressions.

Le résultat est un système de sauvegarde qui :

- Produit des fichiers JSONL portables et lisibles par un humain
- Prend en charge les modes complet et incrémental avec une gestion automatique du watermark
- Capture correctement les créations, les mises à jour *et* les suppressions
- Gère de manière transparente le préfixage de tables multi-tenant
- Se compose proprement (fusion, rollup, restauration sélective)
- S'exécute comme un outil autonome sans dépendance au serveur Authagonal

L'abstraction du stockage signifie que la même logique peut cibler le disque local, Azure Blob Storage, S3, ou toute autre destination. Le format est assez simple pour que, même sans l'outil de restauration, un opérateur puisse reconstruire les données avec `jq` et l'Azure CLI.
