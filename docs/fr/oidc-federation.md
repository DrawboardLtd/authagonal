---
layout: default
title: Federation OIDC
locale: fr
---

# Federation OIDC

Authagonal peut federer l'authentification vers des fournisseurs d'identite OIDC externes (Google, Apple, Azure AD, etc.). Cela permet des flux de type "Se connecter avec Google" tandis qu'Authagonal reste le serveur d'authentification central.

## Comment ca fonctionne

1. L'utilisateur entre son email sur la page de connexion
2. La SPA appelle `/api/auth/sso-check` -- si le domaine de l'email est lie a un fournisseur OIDC, le SSO est requis
3. L'utilisateur clique sur "Continuer avec SSO" et est redirige vers l'IdP externe
4. Apres l'authentification, l'IdP redirige vers `/oidc/callback`
5. Authagonal valide l'id_token, cree/lie l'utilisateur et definit un cookie de session

## Configuration

### 1. Creer un fournisseur OIDC

**Option A -- Configuration (recommande pour les configurations statiques) :**

Ajoutez dans `appsettings.json` :

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

Les fournisseurs sont injectes au demarrage. Le `ClientSecret` est protege via `ISecretProvider` (Key Vault lorsqu'il est configure, texte brut sinon). Les mappages de domaines SSO sont enregistres automatiquement a partir de `AllowedDomains`.

**Option B -- API d'administration (pour la gestion a l'execution) :**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"]
  }'
```

### 2. Routage de domaine SSO

Lorsque `AllowedDomains` est specifie (dans la configuration ou via l'API de creation), les mappages de domaines SSO sont enregistres automatiquement. Sans routage de domaine, les utilisateurs peuvent toujours etre diriges vers la connexion OIDC via `/oidc/{connectionId}/login`.

## Points d'acces

| Point d'acces | Description |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...` | Initie la connexion OIDC. Genere PKCE + state + nonce, redirige vers le point d'acces d'autorisation de l'IdP. |
| `GET /oidc/callback` | Gere le rappel de l'IdP. Echange le code contre des jetons, valide l'id_token, cree/connecte l'utilisateur. |

## Fonctionnalites de securite

- **PKCE** -- code_challenge avec S256 sur chaque requete d'autorisation
- **Validation du nonce** -- le nonce est stocke dans le state, verifie dans l'id_token
- **Validation du state** -- a usage unique, stocke dans Azure Table Storage avec expiration
- **Validation de la signature de l'id_token** -- les cles sont recuperees depuis le point d'acces JWKS de l'IdP
- **Repli sur userinfo** -- si l'id_token ne contient pas d'email, le point d'acces userinfo est essaye

## Specificites Azure AD

Azure AD renvoie parfois les emails sous forme de tableau JSON dans le claim `emails` (en particulier pour B2C). Authagonal gere cela en verifiant a la fois le claim `email` et le tableau `emails`.

## Fournisseurs pris en charge

Tout fournisseur compatible OIDC qui prend en charge :
- Le flux Authorization Code
- PKCE (S256)
- Le document de decouverte (`.well-known/openid-configuration`)

Teste avec :
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C
