---
layout: default
title: Provisionnement SCIM 2.0
locale: fr
---

# Provisionnement SCIM 2.0

Authagonal prend en charge SCIM 2.0 (System for Cross-domain Identity Management) pour le provisionnement automatique des utilisateurs depuis des fournisseurs d'identité d'entreprise tels que Microsoft Entra ID, Okta et OneLogin.

## Vue d'ensemble

SCIM est un protocole de provisionnement entrant : votre fournisseur d'identité pousse les modifications d'utilisateurs et de groupes vers Authagonal. Il est complémentaire au provisionnement sortant TCC (Try-Confirm-Cancel) existant, qui pousse les utilisateurs vers les applications en aval.

**Opérations prises en charge :**
- CRUD des utilisateurs (création, lecture, mise à jour, suppression via désactivation logique)
- CRUD des groupes avec gestion des membres
- Filtrage (la grammaire de filtres complète de la RFC 7644 §3.4.2.2)
- Pagination : basée sur un curseur pour les listes d'utilisateurs (`cursor`/`nextCursor`), `startIndex` et `count` pour les groupes
- PATCH pour les mises à jour partielles (y compris la désactivation `active=false`)
- Mappage groupe-rôle résolu lors de l'émission du token

**Non pris en charge :** opérations en masse, tri, ETags, gestion des mots de passe via SCIM.

Toutes les ressources sont limitées au client SCIM qui les a provisionnées : un utilisateur ou un groupe créé par le client d'un token SCIM est invisible (404) pour tout autre client SCIM.

## Générer un token SCIM

Les endpoints SCIM sont authentifiés avec des Bearer tokens statiques. Générez des tokens via l'API d'administration :

```http
POST /api/v1/scim/tokens
Authorization: Bearer {admin-token}
Content-Type: application/json

{
  "clientId": "your-client-id",
  "description": "Entra ID SCIM token",
  "expiresInDays": 365
}
```

La réponse inclut le token brut **une seule fois**. Il est stocké sous forme de hash SHA-256 et ne peut pas être récupéré ultérieurement, alors stockez-le en lieu sûr :

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "description": "Entra ID SCIM token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z"
}
```

Omettez `expiresInDays` (ou passez `0`) pour un token sans expiration.

### Lister les tokens

```http
GET /api/v1/scim/tokens?clientId=your-client-id
Authorization: Bearer {admin-token}
```

### Révoquer un token

```http
DELETE /api/v1/scim/tokens/{tokenId}?clientId=your-client-id
Authorization: Bearer {admin-token}
```

## Configurer votre fournisseur d'identité

### URL du tenant

```
https://your-authagonal-instance/scim/v2
```

### Authentification

Utilisez le **OAuth Bearer Token** avec le token généré ci-dessus.

### Microsoft Entra ID

1. Dans le portail Azure, allez dans **Enterprise Applications** > votre application > **Provisioning**
2. Réglez le mode de provisionnement sur **Automatic**
3. Saisissez l'URL du tenant : `https://your-instance/scim/v2`
4. Saisissez le Secret Token : le token brut de l'étape de génération
5. Cliquez sur **Test Connection** pour vérifier
6. Configurez les mappages d'attributs (voir ci-dessous)

### Okta

1. Dans la console d'administration Okta, allez dans **Applications** > votre application > **Provisioning**
2. Activez le **connecteur SCIM**
3. Réglez l'URL de base : `https://your-instance/scim/v2`
4. Réglez le mode d'authentification sur **HTTP Header**
5. Saisissez le Bearer token

### OneLogin

1. Dans l'administration OneLogin, allez dans **Applications** > votre application > **Provisioning**
2. Activez le provisionnement
3. Réglez l'URL de base SCIM : `https://your-instance/scim/v2`
4. Réglez le SCIM Bearer Token

## Endpoints SCIM

| Méthode | Chemin | Description |
|--------|------|-------------|
| GET | `/scim/v2/Users` | Lister/filtrer les utilisateurs |
| GET | `/scim/v2/Users/{id}` | Obtenir un utilisateur |
| POST | `/scim/v2/Users` | Créer un utilisateur |
| PUT | `/scim/v2/Users/{id}` | Remplacer un utilisateur |
| PATCH | `/scim/v2/Users/{id}` | Mise à jour partielle |
| DELETE | `/scim/v2/Users/{id}` | Tombstone (désactive ; un GET ultérieur est un 404) |
| GET | `/scim/v2/Groups` | Lister/filtrer les groupes |
| GET | `/scim/v2/Groups/{id}` | Obtenir un groupe |
| POST | `/scim/v2/Groups` | Créer un groupe |
| PUT | `/scim/v2/Groups/{id}` | Remplacer un groupe |
| PATCH | `/scim/v2/Groups/{id}` | Ajouter/retirer des membres |
| DELETE | `/scim/v2/Groups/{id}` | Supprimer un groupe |
| GET | `/scim/v2/ServiceProviderConfig` | Capacités |
| GET | `/scim/v2/Schemas` | Définitions de schéma |
| GET | `/scim/v2/ResourceTypes` | Types de ressources |

Chaque endpoint est également mappé sans le segment `/v2` (par exemple `/scim/Users`) pour les fournisseurs d'identité qui ajoutent leur propre chemin. Les endpoints de découverte (`ServiceProviderConfig`, `Schemas`, `ResourceTypes`, ainsi que les URL de base nues `/scim/` et `/scim/v2/`, qui renvoient le ServiceProviderConfig) sont anonymes ; tout le reste requiert un SCIM Bearer token.

Les endpoints utilisateur et groupe sont limités à 200 requêtes par minute par client SCIM ; les requêtes excédentaires reçoivent une erreur SCIM avec le statut `429`.

## Mappage des attributs

### Attributs utilisateur

| Attribut SCIM | Champ Authagonal |
|---------------|------------------|
| `userName` | `Email` |
| `name.givenName` | `FirstName` |
| `name.familyName` | `LastName` |
| `displayName` | `FirstName LastName` |
| `emails[type eq "work"].value` | `Email` |
| `active` | `IsActive` |
| `externalId` | `ExternalId` |
| `preferredLanguage` (falling back to `locale`) | `Locale` |

### Attributs de groupe

| Attribut SCIM | Champ Authagonal |
|---------------|------------------|
| `displayName` | `DisplayName` |
| `externalId` | `ExternalId` |
| `members` | `MemberUserIds` |

## Détails du comportement

### Création d'utilisateur
- Les utilisateurs provisionnés via SCIM sont créés avec `EmailConfirmed = true` (SSO uniquement, sans mot de passe).
- Le champ `ScimProvisionedByClientId` suit quel client SCIM a créé l'utilisateur.
- Si le client a des `ProvisioningApps` configurées, le provisionnement TCC est déclenché automatiquement. Si le provisionnement rejette l'utilisateur, la création SCIM est annulée et la réponse est un `400` SCIM avec `scimType: invalidValue` et un message fixe (le texte de l'application en aval n'est délibérément pas répercuté vers le client SCIM).
- Créer un utilisateur dont le `userName` ou l'`externalId` existe déjà renvoie un conflit SCIM `409`. Les changements d'e-mail via PUT ou PATCH sont contrôlés pour conflit de la même manière.

### Désactivation d'utilisateur
- `DELETE /scim/v2/Users/{id}` pose un **tombstone** : l'utilisateur est désactivé, l'enregistrement local est conservé et `ScimDeletedAt` est horodaté. Un `GET /scim/v2/Users/{id}` ultérieur renvoie **404**, comme l'exige la RFC 7644 §3.6 (« le fournisseur de service DOIT renvoyer un 404 pour toutes les opérations associées à la ressource précédemment supprimée »). Ne confirmez donc pas un déprovisionnement en relisant la ressource et en attendant `active: false` : la lecture est un 404, et c'est le cas de succès.
- L'enregistrement est conservé plutôt qu'effacé pour qu'un réembauche puisse être recréé : le tombstone libère le `userName`/`externalId` dont une nouvelle ressource a besoin, tandis que le compte local, son historique d'audit et ses appartenances aux groupes subsistent.
- Un `PATCH` avec `active = false` désactive également l'utilisateur.
- Les utilisateurs désactivés ne peuvent pas se connecter par mot de passe, SAML ou OIDC.
- Tous les grants (refresh tokens, sessions) sont révoqués lors de la désactivation.
- Le déprovisionnement des applications en aval n'est déclenché que par `DELETE` ; une désactivation par `PATCH` révoque les grants mais laisse les applications en aval intactes.

### Filtrage
Expressions de filtre prises en charge :
- `userName eq "user@example.com"`
- `externalId eq "12345"`
- `displayName co "John"`

Seuls les filtres à attribut unique sont pris en charge. Les expressions booléennes complexes (`and`, `or`) ne sont pas prises en charge.

Les filtres `eq` sur `userName` et `externalId` (les recherches qu'Entra et Okta émettent avant chaque création ou mise à jour) sont résolus par des recherches ponctuelles indexées plutôt que par un balayage de liste, de sorte qu'ils restent rapides quel que soit le nombre d'utilisateurs. Les autres filtres (`co`, ou les filtres sur `displayName`) sont appliqués lors du parcours paginé des utilisateurs du client.

### Pagination
Les listes d'utilisateurs utilisent la **pagination par curseur**. Chaque page de `GET /scim/v2/Users` renvoie une propriété `nextCursor` dans la réponse de liste ; renvoyez-la sous la forme `?cursor=` pour récupérer la page suivante. Lorsque `nextCursor` est absent, la liste est complète. La taille de page est contrôlée par `count` (par défaut 100, maximum 200).

Demander un `startIndex` supérieur à 1 sur l'endpoint Users renvoie une erreur `400` qui vous oriente vers la pagination par curseur ; la pagination par décalage au-delà de la première page n'est pas proposée. `totalResults` est **omis** tant que `nextCursor` est présent, et ne porte le total exact que sur la dernière page : il ne rapporte délibérément pas la taille de la page renvoyée, car un client qui confond les deux lit l'annuaire de façon incomplète et silencieuse. Pilotez la boucle avec `nextCursor`, pas avec `totalResults`, et traitez un `totalResults` absent comme "encore inconnu", pas comme zéro.

Les listes de groupes utilisent toujours la pagination par décalage `startIndex`/`count`.

### Appartenance aux groupes via PATCH
`PATCH /scim/v2/Groups/{id}` accepte les formes d'appartenance que les principaux fournisseurs d'identité envoient réellement :

- **Ajouter des membres :** `op: "add"` avec `path: "members"` et un tableau de valeurs d'objets `{ "value": "user-id" }`. Les doublons sont ignorés.
- **Remplacer les membres :** `op: "replace"` avec `path: "members"` remplace l'appartenance entière par le tableau fourni.
- **Retirer un membre spécifique (tableau de valeurs) :** `op: "remove"` avec `path: "members"` et un tableau de valeurs des ids de membres à retirer (la forme qu'envoie Entra ID).
- **Retirer un membre spécifique (filtre de chemin) :** `op: "remove"` avec `path: 'members[value eq "user-id"]'`, l'id étant porté dans le filtre de chemin sans valeur (la forme qu'Okta envoie pour le déprovisionnement).
- **Retirer tous les membres :** `op: "remove"` avec `path: "members"` et sans valeur vide le groupe.

### Mappage groupe-rôle
L'appartenance à un groupe SCIM peut accorder des rôles d'application. Les mappages comportent une ligne par paire (groupe, rôle), et un groupe peut accorder plusieurs rôles. Ils sont résolus lors de l'**émission du token** : les rôles effectifs d'un utilisateur sont ses rôles directement assignés plus les rôles de chaque groupe mappé auquel il appartient, de sorte qu'ajouter ou retirer un membre de groupe prend effet sur le token suivant sans toucher à l'enregistrement de l'utilisateur. Un magasin de mappages vide est sans effet.

Les mappages sont persistés via l'`IScimGroupRoleMappingStore` (implémenté par les fournisseurs de stockage Azure et AWS ; un défaut en mémoire est enregistré sinon) et sont gérés par la surface d'administration de l'application hôte, et non via l'API SCIM elle-même.

En option, un client avec `IncludeGroupsInTokens` activé reçoit également les noms d'affichage des groupes SCIM de l'utilisateur sous forme de claim `groups` dans les tokens émis.

## Limitations connues

- **Aucune opération en masse :** les utilisateurs et les groupes doivent être provisionnés individuellement.
- **Aucun tri :** les listes d'utilisateurs renvoient l'ordre de stockage sous pagination par curseur ; les listes de groupes sont triées par date de création.
- **Aucune gestion des mots de passe :** les utilisateurs provisionnés via SCIM s'authentifient via SSO uniquement.
- **Tombstone, pas effacement :** `DELETE` désactive et pose un tombstone (un `GET` ultérieur est un 404, conformément à la RFC 7644 §3.6) au lieu de supprimer définitivement l'enregistrement local. Pour l'effacement, utilisez l'API d'administration.
