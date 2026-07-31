---
layout: default
title: OAuth Scopes
locale: fr
---

# Scopes OAuth

Authagonal prend en charge à la fois les scopes OAuth/OIDC **intégrés** et les scopes **personnalisés** gérés à l'exécution. Les scopes personnalisés sont persistés, annoncés via le document de découverte et présentés sur l'écran de consentement aux côtés des scopes intégrés.

## Scopes intégrés

Ces scopes sont toujours disponibles et n'ont pas besoin d'être enregistrés :

| Scope | Rôle |
|---|---|
| `openid` | Requis pour initier un flux OIDC. Émet un ID token. |
| `profile` | Claims de profil standard (name, family_name, given_name, etc.) |
| `email` | Adresse e-mail et claim `email_verified` |
| `offline_access` | Émet un refresh token en plus de l'access token |

## Scopes personnalisés

Les scopes personnalisés sont gérés via l'API d'administration à `/api/v1/scopes`. Ils requièrent un access token JWT avec le scope `authagonal-admin` (configurable via `AdminApi:Scope`).

### Modèle Scope

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public string? Group { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> AllowedRoles { get; set; } = [];
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| Champ | Description |
|---|---|
| `Name` | L'identifiant du scope envoyé dans les requêtes de token (p. ex. `billing.read`) |
| `DisplayName` | Nom lisible affiché sur l'écran de consentement |
| `Description` | Description plus longue affichée sur l'écran de consentement |
| `Emphasize` | Si `true`, l'écran de consentement met ce scope en évidence comme sensible |
| `Group` | Titre de l'écran de consentement sous lequel classer ce scope. Présentation uniquement : cela n'affecte jamais ce qui est accordé |
| `Required` | Si `true`, l'utilisateur ne peut pas désélectionner ce scope lors du consentement |
| `ShowInDiscoveryDocument` | Si `true`, le scope apparaît dans `/.well-known/openid-configuration` sous `scopes_supported` |
| `AllowedRoles` | Rôles qu'un utilisateur doit détenir pour que ce scope lui soit accordé. Vide (par défaut), il reste non restreint : voir [Scopes restreints par rôle](#role-gated-scopes) |
| `UserClaims` | Claims ajoutés à l'access token lorsque ce scope est accordé |

### Scopes restreints par rôle {#role-gated-scopes}

Les `AllowedScopes` d'un client répondent à *cette application peut-elle demander ce scope* : une
question réglée avant que quiconque se soit connecté. `AllowedRoles` répond à l'autre moitié : *cette
personne peut-elle l'obtenir*. Les deux barrières s'appliquent, et aucune ne remplace l'autre.

```json
{
  "name": "staff-admin",
  "displayName": "Staff administration",
  "allowedRoles": ["staff", "super-admin"]
}
```

Pour un utilisateur ne détenant aucun des rôles listés, le scope est **retiré de l'octroi**, non
refusé : le client a demandé son ensemble complet et apprend, via le `scope` renvoyé dans la réponse
du token (RFC 6749 §3.3), qu'il en a reçu moins. C'est ce qui permet à une même application de servir
à la fois le personnel et tout le monde : la surface réservée au personnel est un scope parmi
d'autres, et seules les personnes qui y ont droit le reçoivent.

Une requête dont *tous* les scopes demandés sont retirés échoue avec `access_denied`, car il ne reste
rien pour quoi émettre un token.

La barrière s'applique partout où un token est émis pour un humain :

| Flux | Où elle s'exécute |
|---|---|
| Authorization code | À `/connect/authorize`, une fois l'utilisateur connu et **avant** le consentement : l'écran ne propose ainsi jamais une permission qui ne peut pas être accordée |
| Device code | À `/api/auth/device/approve`, le premier point de ce flux où le sujet est connu |
| Refresh | À chaque rotation, contre des rôles fraîchement résolus. C'est là que la révocation d'un rôle prend réellement effet, puisque l'octroi conserve ce qui a été approuvé à la connexion |
| Token exchange | Pas restreint séparément : un échange ne peut que réduire la portée à l'intérieur des scopes du subject token, il ne peut donc jamais en atteindre un que le sujet n'a pas reçu |

Les octrois client_credentials n'ont pas de sujet et restent délibérément intouchés : l'autorité d'un
client machine est son enregistrement.

Injecter un scope depuis la configuration peut ajouter ou modifier `AllowedRoles` mais ne peut pas
l'effacer (comme pour `UserClaims`, un champ omis conserve la valeur stockée). Pour retirer une
restriction, faites un `PUT` du scope avec un tableau explicitement vide.

## Endpoints d'administration

### Lister les scopes

```
GET /api/v1/scopes
```

Renvoie `{ "scopes": [ ... ] }`.

### Obtenir un scope

```
GET /api/v1/scopes/{name}
```

Renvoie le scope ou `404` s'il est introuvable.

### Créer un scope

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing — read-only",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

Renvoie `201 Created` avec le scope. Renvoie `409` si un scope portant le même nom existe déjà.

### Mettre à jour un scope

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing — read",
  "description": "View invoices",
  "emphasize": true
}
```

Seuls les champs fournis sont mis à jour ; les champs omis conservent leurs valeurs actuelles.

### Supprimer un scope

```
DELETE /api/v1/scopes/{name}
```

Renvoie `204 No Content` (`404` si le scope n'existe pas). Les tokens déjà émis qui incluent ce scope restent valides jusqu'à leur expiration ; révoquez-les explicitement via `/connect/revocation` si nécessaire.

## Document de découverte

Les scopes ayant `ShowInDiscoveryDocument = true` apparaissent sous `scopes_supported` dans `/.well-known/openid-configuration`. Les scopes intégrés sont toujours annoncés.

```json
{
  "scopes_supported": ["openid", "profile", "email", "offline_access", "billing.read"]
}
```

## Écran de consentement

Lorsqu'un client demande un scope qui ne figure pas dans sa liste d'exemption de consentement, la page de consentement liste chaque scope demandé par son `DisplayName` (avec repli sur `Name`), avec la `Description` en dessous. Les scopes ayant `Emphasize = true` reçoivent un traitement visuel distinct. Les scopes `Required` ne peuvent pas être désélectionnés.

Voir [Écran de consentement OAuth](index#features) pour le flux visible par l'utilisateur.

## Dynamic Client Registration

Les clients enregistrés via l'[enregistrement dynamique de client](client-registration) ne peuvent demander que des scopes soit intégrés, soit créés au préalable via l'API d'administration. Les scopes inconnus sont rejetés avec `invalid_scope`.
