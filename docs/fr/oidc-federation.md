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

Les fournisseurs sont injectes au demarrage. Les champs injectables sont exactement ceux affiches : `ConnectionId`, `ConnectionName`, `MetadataLocation`, `ClientId`, `ClientSecret`, `RedirectUrl`, `AllowedDomains`. Le `ClientSecret` est protege via `ISecretProvider` (Key Vault lorsqu'il est configure, texte brut sinon). Les mappages de domaines SSO sont enregistres automatiquement a partir de `AllowedDomains`.

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
- **Validation du nonce** -- le nonce est stocke avec le state, doit etre present dans l'id_token et correspondre
- **Validation du state** -- a usage unique (consomme de maniere atomique via `IOidcStateStore`, persiste avec expiration) **et lie au navigateur** : un cookie `SameSite=Lax` limite a `/oidc` est defini a la connexion et doit correspondre au `state` du rappel, de sorte qu'un attaquant ne peut pas terminer un flux de federation qu'il a initie puis livrer l'URL de rappel a une victime (login CSRF)
- **Validation de la signature de l'id_token** -- les cles sont recuperees depuis le point d'acces JWKS de l'IdP ; l'emetteur, l'audience et la duree de vie sont valides
- **Repli sur userinfo** -- si l'id_token ne contient pas d'email, le point d'acces userinfo est essaye. Le `sub` de userinfo doit correspondre au `sub` de l'id_token (OIDC Core 5.3.2), sinon la reponse est ignoree
- **Liaison d'identite stable** -- un utilisateur qui revient est resolu par fournisseur + `sub`, jamais par email seul. Rattacher une identite federee a un compte local **preexistant** par email exige que les `AllowedDomains` de la connexion couvrent le domaine de cet email, l'attestation explicite de l'administrateur que l'IdP le possede. Un `email_verified` affirme en amont n'est *pas* suffisant pour s'emparer d'un compte existant
- **Application du domaine** -- lorsque `AllowedDomains` est defini, la connexion ne peut affirmer que des identites au sein de ces domaines (`access_denied` sinon)
- **Desactivation du JIT** -- `DisableJitProvisioning` rejette les utilisateurs inconnus au lieu de les creer automatiquement
- **Protection contre les redirections ouvertes** -- `returnUrl` doit etre un chemin relatif de meme site ; les formes relatives au protocole (`//`) et avec barre oblique inverse sont rejetees
- **La MFA locale s'applique toujours** -- la federation ne prouve que le premier facteur. Un utilisateur inscrit a la MFA (ou dont la politique client exige la MFA) est dirige vers les pages locales de defi/configuration MFA apres le rappel au lieu d'etre connecte directement ; ce n'est qu'alors que la session porte le marqueur MFA

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
