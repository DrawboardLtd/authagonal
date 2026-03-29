---
layout: default
title: Configuration
locale: fr
---

# Configuration

Authagonal est configure via `appsettings.json` ou des variables d'environnement. Les variables d'environnement utilisent `__` comme separateur de section (par exemple, `Storage__ConnectionString`).

## Parametres requis

| Parametre | Variable d'env | Description |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Chaine de connexion Azure Table Storage |
| `Issuer` | `Issuer` | L'URL publique de base de ce serveur (par exemple, `https://auth.example.com`) |

## Authentification

| Parametre | Defaut | Description |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Duree de vie de la session par cookie (glissante) |

## Clients

Les clients sont definis dans le tableau `Clients` et injectes au demarrage. Chaque client peut avoir :

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "ClientName": "My Application",
      "ClientSecretHashes": ["sha256-hash-here"],
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email", "custom-scope"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "AlwaysIncludeUserClaimsInIdToken": false,
      "AccessTokenLifetimeSeconds": 1800,
      "IdentityTokenLifetimeSeconds": 300,
      "AuthorizationCodeLifetimeSeconds": 300,
      "AbsoluteRefreshTokenLifetimeSeconds": 2592000,
      "SlidingRefreshTokenLifetimeSeconds": 1296000,
      "RefreshTokenUsage": "OneTime",
      "ProvisioningApps": ["my-backend"]
    }
  ]
}
```

### Types d'octroi

| Type d'octroi | Cas d'utilisation |
|---|---|
| `authorization_code` | Connexion interactive de l'utilisateur (applications web, SPA, mobile) |
| `client_credentials` | Communication service a service |
| `refresh_token` | Renouvellement de jeton (necessite `AllowOfflineAccess: true`) |

### Utilisation du jeton de rafraichissement

| Valeur | Comportement |
|---|---|
| `OneTime` (par defaut) | Chaque rafraichissement emet un nouveau jeton de rafraichissement. L'ancien est invalide avec une fenetre de grace de 60 secondes pour les requetes concurrentes. La reutilisation apres la fenetre de grace revoque tous les jetons pour cet utilisateur+client. |
| `ReUse` | Le meme jeton de rafraichissement est reutilise jusqu'a expiration. |

### Applications de provisionnement

Le tableau `ProvisioningApps` reference les identifiants d'applications definis dans la section de configuration `ProvisioningApps`. Lorsqu'un utilisateur s'autorise via ce client, il est provisionne dans ces applications via TCC. Voir [Provisionnement](provisioning) pour plus de details.

## Applications de provisionnement

Definissez les applications en aval dans lesquelles les utilisateurs doivent etre provisionnes :

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-api-key"
    },
    "analytics": {
      "CallbackUrl": "https://analytics.example.com/provisioning",
      "ApiKey": "another-key"
    }
  }
}
```

Voir [Provisionnement](provisioning) pour la specification complete du protocole TCC.

## Politique de mot de passe

Personnalisez les exigences de robustesse des mots de passe :

```json
{
  "PasswordPolicy": {
    "MinLength": 10,
    "MinUniqueChars": 3,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": false
  }
}
```

| Propriete | Defaut | Description |
|---|---|---|
| `MinLength` | `8` | Longueur minimale du mot de passe |
| `MinUniqueChars` | `2` | Nombre minimum de caracteres distincts |
| `RequireUppercase` | `true` | Exiger au moins une lettre majuscule |
| `RequireLowercase` | `true` | Exiger au moins une lettre minuscule |
| `RequireDigit` | `true` | Exiger au moins un chiffre |
| `RequireSpecialChar` | `true` | Exiger au moins un caractere non alphanumerique |

La politique est appliquee lors de la reinitialisation du mot de passe et de l'inscription d'un utilisateur par l'administrateur. L'interface de connexion recupere la politique active depuis `GET /api/auth/password-policy` pour afficher les exigences dynamiquement.

## Fournisseurs SAML

Definissez les fournisseurs d'identite SAML dans la configuration. Ceux-ci sont injectes au demarrage :

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com", "example.org"]
    }
  ]
}
```

| Propriete | Requis | Description |
|---|---|---|
| `ConnectionId` | Oui | Identifiant stable (utilise dans les URLs comme `/saml/{connectionId}/login`) |
| `ConnectionName` | Non | Nom d'affichage (par defaut : ConnectionId) |
| `EntityId` | Oui | Identifiant d'entite du fournisseur de services SAML |
| `MetadataLocation` | Oui | URL vers le XML de metadonnees SAML de l'IdP |
| `AllowedDomains` | Non | Domaines de messagerie achemines vers ce fournisseur via SSO |

## Fournisseurs OIDC

Definissez les fournisseurs d'identite OIDC dans la configuration. Ceux-ci sont injectes au demarrage :

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

| Propriete | Requis | Description |
|---|---|---|
| `ConnectionId` | Oui | Identifiant stable (utilise dans les URLs comme `/oidc/{connectionId}/login`) |
| `ConnectionName` | Non | Nom d'affichage (par defaut : ConnectionId) |
| `MetadataLocation` | Oui | URL vers le document de decouverte OpenID Connect de l'IdP |
| `ClientId` | Oui | Identifiant client OAuth2 enregistre aupres de l'IdP |
| `ClientSecret` | Oui | Secret client OAuth2 (protege via `ISecretProvider` au demarrage) |
| `RedirectUrl` | Oui | URI de redirection OAuth2 enregistree aupres de l'IdP |
| `AllowedDomains` | Non | Domaines de messagerie achemines vers ce fournisseur via SSO |

> **Remarque :** Les fournisseurs peuvent egalement etre geres a l'execution via l'[API d'administration](admin-api). Les fournisseurs configures sont mis a jour (upsert) a chaque demarrage, donc les modifications de configuration prennent effet au redemarrage.

## Fournisseur de secrets

Les secrets des clients et des fournisseurs OIDC peuvent optionnellement etre stockes dans Azure Key Vault :

| Parametre | Description |
|---|---|
| `SecretProvider:VaultUri` | URI du Key Vault (par exemple, `https://my-vault.vault.azure.net/`). Si non defini, les secrets sont traites en texte brut. |

Lorsqu'il est configure, les valeurs de secrets qui ressemblent a des references Key Vault sont resolues a l'execution. Utilise `DefaultAzureCredential` pour l'authentification.

## Email

Par defaut, Authagonal utilise un service d'email no-op qui ignore silencieusement tous les emails. Pour activer l'envoi d'emails, enregistrez une implementation de `IEmailService` avant d'appeler `AddAuthagonal()`. Le service integre `EmailService` utilise SendGrid.

| Parametre | Description |
|---|---|
| `Email:SendGridApiKey` | Cle API SendGrid pour l'envoi d'emails |
| `Email:FromAddress` | Adresse email de l'expediteur |
| `Email:FromName` | Nom d'affichage de l'expediteur |
| `Email:VerificationTemplateId` | Identifiant de modele dynamique SendGrid pour la verification d'email |
| `Email:PasswordResetTemplateId` | Identifiant de modele dynamique SendGrid pour la reinitialisation du mot de passe |

Les emails aux adresses `@example.com` sont ignores silencieusement (utile pour les tests).

## Limitation de debit

Limites de debit integrees par IP :

| Groupe de points d'acces | Limite | Fenetre |
|---|---|---|
| Points d'acces d'authentification (connexion, SSO) | 20 requetes | 1 minute |
| Point d'acces de jeton | 30 requetes | 1 minute |

## CORS

CORS est configure dynamiquement. Les origines de tous les `AllowedCorsOrigins` des clients enregistres sont automatiquement autorisees, avec un cache de 60 minutes.

## Exemple complet

```json
{
  "Storage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..."
  },
  "Issuer": "https://auth.example.com",
  "Authentication": {
    "CookieLifetimeHours": 48
  },
  "PasswordPolicy": {
    "MinLength": 8,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": true
  },
  "Email": {
    "SendGridApiKey": "SG.xxx",
    "FromAddress": "noreply@example.com",
    "FromName": "Example Auth",
    "VerificationTemplateId": "d-xxx",
    "PasswordResetTemplateId": "d-yyy"
  },
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com"]
    }
  ],
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "...",
      "ClientSecret": "...",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["gmail.com"]
    }
  ],
  "ProvisioningApps": {
    "backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret"
    }
  },
  "Clients": [
    {
      "ClientId": "web",
      "ClientName": "Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
