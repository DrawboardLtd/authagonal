---
layout: default
title: Dynamic Client Registration
locale: fr
---

# Enregistrement dynamique de clients

Authagonal implémente l'**enregistrement dynamique de clients OAuth 2.0** ([RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)), ce qui permet aux applications clientes de s'enregistrer elles-mêmes à l'exécution sans intervention d'un administrateur.

## Activer l'endpoint

L'enregistrement dynamique est **désactivé par défaut**. Activez-le via la configuration :

```json
{
  "Auth": {
    "DynamicClientRegistrationEnabled": true
  }
}
```

Ou définissez `Auth__DynamicClientRegistrationEnabled=true` comme variable d'environnement.

Lorsqu'il est activé, le document de découverte annonce l'endpoint :

```
GET /.well-known/openid-configuration
```
```json
{
  "registration_endpoint": "https://auth.example.com/connect/register"
}
```

## Enregistrer un client

```
POST /connect/register
Content-Type: application/json

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access",
  "audiences": ["https://api.myapp.example.com"],
  "allowed_cors_origins": ["https://myapp.example.com"],
  "backchannel_logout_uri": "https://myapp.example.com/oidc/backchannel",
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

### Réponse

```
HTTP/1.1 201 Created
Content-Type: application/json

{
  "client_id": "a1b2c3d4e5f6...",
  "client_secret": "xkCd2_base64url...",
  "client_id_issued_at": 1745000000,
  "client_secret_expires_at": 0,
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email offline_access",
  "token_endpoint_auth_method": "client_secret_basic"
}
```

Le `client_secret` est renvoyé **une seule fois** et ne peut pas être récupéré ultérieurement. Stockez-le en lieu sûr.

## Paramètres de la requête

| Paramètre | Requis | Notes |
|---|---|---|
| `client_name` | non | Par défaut, le `client_id` généré si omis |
| `redirect_uris` | conditionnel | Requis lorsque `grant_types` contient `authorization_code`. Doivent être des URI absolus ; les schémas `javascript:`/`data:`/`vbscript:`/`file:` sont rejetés (les schémas personnalisés natifs pour les liens profonds mobiles conviennent). |
| `post_logout_redirect_uris` | non | Cibles de redirection valides après la déconnexion |
| `grant_types` | non | Par défaut `["authorization_code"]`. **Seuls `authorization_code` et `refresh_token` sont enregistrables** : `client_credentials`, `implicit`, device et tout autre type de grant sont rejetés avec `invalid_client_metadata`, de sorte que l'enregistrement ouvert ne peut jamais créer un client machine à machine. `refresh_token` est ajouté automatiquement si `offline_access` est demandé. |
| `token_endpoint_auth_method` | non | `client_secret_basic` (par défaut), `client_secret_post`, ou `none` pour les clients publics |
| `scope` | non | Scopes séparés par des espaces : ils doivent tous être intégrés ou préalablement enregistrés (voir [Scopes](scopes)). Le scope administratif (`AdminApi:Scope`, par défaut `authagonal-admin`) ne peut jamais être enregistré. |
| `audiences` | non | Valeurs `aud` du JWT ajoutées aux access tokens |
| `allowed_cors_origins` | non | Origines autorisées à appeler le token endpoint depuis un navigateur |
| `backchannel_logout_uri` | non | Active la [déconnexion Back-Channel](index#features) |
| `frontchannel_logout_uri` | non | Active la [déconnexion Front-Channel](front-channel-logout) |
| `frontchannel_logout_session_required` | non | Par défaut `true` ; lorsque `true`, l'URL de déconnexion transporte les paramètres `iss` et `sid` |

## Valeurs par défaut et invariants

- **PKCE requis** : `RequirePkce` est toujours `true` pour les clients enregistrés dynamiquement.
- **Clients publics** : `token_endpoint_auth_method: "none"` produit un client sans secret. Le PKCE reste requis.
- **Accès hors ligne** : demander le scope `offline_access` ajoute implicitement `refresh_token` aux `grant_types`.

## Réponses d'erreur

| HTTP | `error` | Cause |
|---|---|---|
| `400` | `invalid_redirect_uri` | L'un des `redirect_uris` n'est pas un URI absolu valide, ou utilise un pseudo-schéma script/data/file |
| `400` | `invalid_client_metadata` | Un type de grant non enregistrable a été demandé, ou `redirect_uris` est absent pour un type de grant qui l'exige |
| `400` | `invalid_scope` | Un scope demandé n'est ni intégré ni enregistré |
| `403` | `invalid_scope` | Le scope administratif a été demandé : il ne peut jamais être accordé via l'enregistrement |
| `403` | `not_supported` | L'enregistrement dynamique de clients n'est pas activé |
| `429` | `rate_limited` | Trop d'enregistrements depuis cette IP (10 par heure) |

## Considérations de sécurité

L'endpoint d'enregistrement est **non authentifié**, mais contraint par conception :

- **Limitation du débit** : 10 enregistrements par IP par heure glissante (`429 rate_limited`), de sorte que le magasin de clients ne peut pas être submergé.
- **Types de grant restreints** : uniquement `authorization_code` + `refresh_token` ; un client enregistré requiert toujours un flux médiatisé par un utilisateur et ne peut jamais agir comme un client machine à machine.
- **Scope admin réservé** : le scope `authagonal-admin` (ou quelle que soit la valeur de `AdminApi:Scope`) est refusé, de sorte que l'enregistrement ne peut jamais produire un client qui atteint l'[API d'administration](admin-api).
- **PKCE toujours requis** sur les clients enregistrés.

Pour un contrôle plus strict (initial access tokens, mTLS, software statements), placez votre propre middleware ou un `IAuthHook` devant l'endpoint. Envisagez de désactiver entièrement l'enregistrement dynamique et de gérer les clients via l'API d'administration dans les environnements où l'enregistrement en libre-service n'est pas nécessaire.
