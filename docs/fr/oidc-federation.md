---
layout: default
title: Fédération OIDC
locale: fr
---

# Fédération OIDC

Authagonal peut fédérer l'authentification vers des fournisseurs d'identité OIDC externes (Google, Apple, Azure AD, etc.). Cela permet des flux de type "Se connecter avec Google" tandis qu'Authagonal reste le serveur d'authentification central.

## Comment ça fonctionne

Il existe deux points d'entrée dans la fédération :

**Basé sur le domaine (connexion interactive) :**

1. L'utilisateur saisit son email sur la page de connexion
2. La SPA appelle `/api/auth/sso-check` : si le domaine de l'email est lié à un fournisseur OIDC, le SSO est requis
3. L'utilisateur clique sur "Continuer avec SSO" et est redirigé vers l'IdP externe
4. Après l'authentification, l'IdP redirige vers `/oidc/callback`
5. Authagonal valide l'id_token, crée/lie l'utilisateur et définit un cookie de session

**Guidé par le RP (`idp_hint`) :**

La relying party en aval peut router directement vers un IdP amont spécifique sans passer par l'étape email/domaine SSO. Ajoutez `idp_hint={connectionId}` à `/connect/authorize` :

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

Lorsque la requête n'est pas authentifiée, Authagonal redirige vers `/oidc/{connectionId}/login` en conservant l'URL `/authorize` d'origine comme `returnUrl`. Une fois la fédération terminée, l'utilisateur revient sur `/authorize` avec un cookie de session et le flux se poursuit normalement.

## Configuration

### 1. Créer un fournisseur OIDC

**Option A : Configuration (recommandée pour les configurations statiques) :**

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

Les fournisseurs sont injectés au démarrage. Les champs injectables sont exactement ceux affichés : `ConnectionId`, `ConnectionName`, `MetadataLocation`, `ClientId`, `ClientSecret`, `RedirectUrl`, `AllowedDomains`. Le `ClientSecret` est protégé via `ISecretProvider` (Key Vault lorsqu'il est configuré, texte brut sinon). Les mappages de domaines SSO sont enregistrés automatiquement à partir d'`AllowedDomains`.

Le modèle de connexion porte des comportements optionnels supplémentaires : `PassthroughParams` (définissable via la création par l'API d'administration), ainsi que `SessionExpClaim` et `DisableJitProvisioning` (champs au niveau du store, définis via `IOidcProviderStore` depuis le code d'hébergement). Voir [Propagation des Scopes et des Claims](#propagation-des-scopes-et-des-claims) et [Plafond de durée de vie de la session](#plafond-de-durée-de-vie-de-la-session) ci-dessous.

**Option B : API d'administration (pour la gestion à l'exécution) :**

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

Lorsque `AllowedDomains` est spécifié (dans la configuration ou via l'API de création), les mappages de domaines SSO sont enregistrés automatiquement. Sans routage de domaine, les utilisateurs peuvent toujours être dirigés vers la connexion OIDC via `/oidc/{connectionId}/login`.

## Points d'accès

| Point d'accès | Description |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...` | Initie la connexion OIDC. Génère PKCE + state + nonce, dérive le Scope amont et les paramètres relayés à partir de `returnUrl`, redirige vers le point d'accès d'autorisation de l'IdP. |
| `GET /oidc/callback` | Gère le rappel de l'IdP. Échange le code contre des Tokens, valide l'id_token, capture chaque Claim non protocolaire sur le cookie sous la forme `federated:*`, crée/connecte l'utilisateur. |

## Propagation des Scopes et des Claims

L'ensemble des Scopes demandés par le RP en aval sur `/connect/authorize` est transmis à l'IdP amont, **filtré à l'ensemble OIDC standard** : `openid`, `profile`, `email`, `address`, `phone`, avec `openid` toujours inclus. Tout ce que le RP a demandé d'autre (Scopes d'API personnalisés, `offline_access`, etc.) est écarté avant l'appel amont : un IdP strict comme Google renvoie `invalid_scope` sur des valeurs inconnues, et l'amont n'a besoin que d'identifier l'utilisateur (les propres Scopes du RP sont honorés sur les Tokens émis par Authagonal, pas sur ceux de l'amont). Quels que soient les Claims que le Scope de l'IdP amont place sur l'id_token, ils reviennent à Authagonal, sont stockés sur le ticket du cookie sous forme de Claims `federated:<name>`, et se propagent dans `OidcSubject.FederationClaims` lors du prochain passage par `/connect/authorize`. À partir de là, `ProtocolTokenService` les réémet sur les Tokens émis par Authagonal, filtrés par la même liste blanche `Scope.UserClaims` qui filtre `CustomAttributes`. Les valeurs de fédération l'emportent en cas de collision de clés.

Effet net : pas de liste d'autorisation de Claims à préserver par connexion. Chaque Claim non protocolaire que l'amont place sur l'id_token est capturé ; lesquels atteignent les Tokens en aval est contrôlé par le `UserClaims` du Scope en aval : déclarez le Claim là et la valeur se propage.

`FederationClaims` survit aux rotations de refresh distinctement de `CustomAttributes`, de sorte que le contexte de fédération par session (par exemple un Token de lien de partage capturé lors de l'autorisation d'origine) reste intact tandis que les attributs par utilisateur sont toujours relus à neuf depuis le user store.

## Paramètres de requête relayés

`OidcProviderConfig.PassthroughParams` est une liste blanche, par connexion, de clés de requête qui se propagent depuis la requête `/authorize` d'origine vers l'URL d'autorisation de l'IdP amont. L'ensemble standard (`scope`, `state`, `nonce`, PKCE) est toujours transmis ; ceci concerne des valeurs supplémentaires spécifiées par le RP, comme un identifiant à usage unique dont l'amont a besoin pour s'authentifier (par exemple `link_token` pour les IdP de liens de partage).

Lorsqu'une clé est sur la liste blanche, Authagonal extrait sa valeur de la requête `/authorize` d'origine (transportée via `returnUrl`) et l'ajoute à l'URL amont. Tout ce qui n'est pas sur la liste blanche est écarté silencieusement.

## Plafond de durée de vie de la session

`OidcProviderConfig.SessionExpClaim` est le nom optionnel d'un Claim de l'id_token (secondes Unix) dont la valeur plafonne la durée de vie de la session locale. Lorsqu'il est présent, la valeur amont se propage sous forme de `session_max_exp` sur le ticket du cookie et dans le code d'autorisation émis ; les Tokens d'accès / id / refresh sont bornés afin qu'aucun Token, y compris ceux issus de rotations, ne survive à la session amont. Utile lorsque l'IdP amont impose des bornes de session plus courtes qu'Authagonal ne le ferait par défaut.

## Fonctionnalités de sécurité

- **PKCE** : code_challenge avec S256 sur chaque requête d'autorisation
- **Validation du nonce** : le nonce est stocké avec le state, doit être présent dans l'id_token et correspondre
- **Validation du state** : à usage unique (consommé de manière atomique via `IOidcStateStore`, persisté avec expiration) **et lié au navigateur** : un cookie `SameSite=Lax` limité à `/oidc` est défini à la connexion et doit correspondre au `state` du rappel, de sorte qu'un attaquant ne peut pas terminer un flux de fédération qu'il a initié puis livrer l'URL de rappel à une victime (login CSRF)
- **Validation de la signature de l'id_token** : les clés sont récupérées depuis le point d'accès JWKS de l'IdP ; l'émetteur, l'audience et la durée de vie sont validés
- **Repli sur userinfo** : si l'id_token ne contient pas d'email, le point d'accès userinfo est essayé. Le `sub` de userinfo doit correspondre au `sub` de l'id_token (OIDC Core 5.3.2), sinon la réponse est ignorée
- **Liaison d'identité stable** : un utilisateur qui revient est résolu par fournisseur + `sub`, jamais par email seul. Rattacher une identité fédérée à un compte local **préexistant** par email exige que les `AllowedDomains` de la connexion couvrent le domaine de cet email, l'attestation explicite de l'administrateur que l'IdP le possède. Un `email_verified` affirmé en amont n'est *pas* suffisant pour s'emparer d'un compte existant
- **Application du domaine** : lorsque `AllowedDomains` est défini, la connexion ne peut affirmer que des identités au sein de ces domaines (`access_denied` sinon)
- **Désactivation du JIT** : `DisableJitProvisioning` rejette les utilisateurs inconnus au lieu de les créer automatiquement
- **Protection contre les redirections ouvertes** : `returnUrl` doit être un chemin relatif de même site ; les formes relatives au protocole (`//`) et avec barre oblique inverse sont rejetées
- **La MFA locale s'applique toujours** : la fédération ne prouve que le premier facteur. Un utilisateur inscrit à la MFA (ou dont la politique client exige la MFA) est dirigé vers les pages locales de challenge/configuration MFA après le rappel au lieu d'être connecté directement ; ce n'est qu'alors que la session porte le marqueur MFA

## Spécificités Azure AD

Azure AD renvoie parfois les emails sous forme de tableau JSON dans le Claim `emails` (en particulier pour B2C). Authagonal gère cela en vérifiant à la fois le Claim `email` et le tableau `emails`.

## Fournisseurs pris en charge

Tout fournisseur compatible OIDC qui prend en charge :
- Le flux Authorization Code
- PKCE (S256)
- Le document de découverte (`.well-known/openid-configuration`)

Testé avec :
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C
