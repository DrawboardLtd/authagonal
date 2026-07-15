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
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
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
| `Required` | Si `true`, l'utilisateur ne peut pas désélectionner ce scope lors du consentement |
| `ShowInDiscoveryDocument` | Si `true`, le scope apparaît dans `/.well-known/openid-configuration` sous `scopes_supported` |
| `UserClaims` | Claims ajoutés à l'access token lorsque ce scope est accordé |

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
