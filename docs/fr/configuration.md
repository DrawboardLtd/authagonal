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
| `Auth:MaxFailedAttempts` | `5` | Tentatives de connexion echouees avant le verrouillage du compte |
| `Auth:LockoutDurationMinutes` | `10` | Duree du verrouillage du compte apres le nombre maximal de tentatives echouees |
| `Auth:MaxRegistrationsPerIp` | `5` | Nombre maximal d'inscriptions par adresse IP dans la fenetre |
| `Auth:RegistrationWindowMinutes` | `60` | Fenetre de limitation du debit d'inscription |
| `Auth:EmailVerificationExpiryHours` | `24` | Duree de vie du lien de verification d'email |
| `Auth:PasswordResetExpiryMinutes` | `60` | Duree de vie du lien de reinitialisation du mot de passe |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Duree de vie du jeton de verification MFA |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Duree de vie du jeton de configuration MFA (pour l'inscription forcee) |
| `Auth:Pbkdf2Iterations` | `100000` | Nombre d'iterations PBKDF2 pour le hachage du mot de passe |
| `Auth:RefreshTokenReuseGraceSeconds` | `60` | Fenetre de grace pour la reutilisation concurrente du jeton de rafraichissement |
| `Auth:SigningKeyLifetimeDays` | `90` | Duree de vie de la cle de signature RSA avant rotation automatique |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Frequence de rechargement des cles de signature depuis le stockage |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Intervalle entre les verifications du tampon de securite du cookie |
| `DataProtection:BlobUri` | *(aucun)* | URI Azure Blob pour persister les cles de protection des donnees entre les instances |

## Cache et delais d'attente

| Parametre | Defaut | Description |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | Duree de mise en cache des origines CORS autorisees |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | Duree de mise en cache du document de decouverte OIDC |
| `Cache:SamlMetadataCacheMinutes` | `60` | Duree de mise en cache des metadonnees SAML de l'IdP |
| `Cache:OidcStateLifetimeMinutes` | `10` | Duree de vie du parametre state d'autorisation OIDC |
| `Cache:SamlReplayLifetimeMinutes` | `10` | Duree de vie de l'ID AuthnRequest SAML (prevention de rejeu) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Delai d'attente de la verification de sante de Table Storage |

## Services d'arriere-plan

| Parametre | Defaut | Description |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | Delai initial avant le premier nettoyage des jetons expires |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | Intervalle de nettoyage des jetons expires |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | Delai initial avant la premiere reconciliation des autorisations |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | Intervalle de reconciliation des autorisations |

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
      "MfaPolicy": "Enabled",
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

## Politique MFA

L'authentification multifacteur est appliquee par client via la propriete `MfaPolicy` :

| Valeur | Comportement |
|---|---|
| `Disabled` (par defaut) | Pas de verification MFA, meme si l'utilisateur a inscrit le MFA |
| `Enabled` | Verifie les utilisateurs ayant inscrit le MFA ; ne force pas l'inscription |
| `Required` | Verifie les utilisateurs inscrits ; force l'inscription pour les utilisateurs sans MFA |

```json
{
  "Clients": [
    {
      "ClientId": "secure-app",
      "MfaPolicy": "Required"
    }
  ]
}
```

Lorsque `MfaPolicy` est `Required` et que l'utilisateur n'a pas inscrit le MFA, la connexion renvoie `{ mfaSetupRequired: true, setupToken: "..." }`. Le jeton de configuration authentifie l'utilisateur aupres des points d'acces de configuration MFA (via l'en-tete `X-MFA-Setup-Token`) afin qu'il puisse s'inscrire avant d'obtenir une session par cookie.

Les connexions federees (SAML/OIDC) ignorent le MFA -- le fournisseur d'identite externe le gere.

### Surcharge IAuthHook

La methode `IAuthHook.ResolveMfaPolicyAsync` peut surcharger la politique du client par utilisateur :

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Forcer le MFA pour les administrateurs independamment du parametre client
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    return Task.FromResult(clientPolicy);
}
```

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
| `Email:SenderEmail` | Adresse email de l'expediteur |
| `Email:SenderName` | Nom d'affichage de l'expediteur |
| `Email:VerificationTemplateId` | Identifiant de modele dynamique SendGrid pour la verification d'email |
| `Email:PasswordResetTemplateId` | Identifiant de modele dynamique SendGrid pour la reinitialisation du mot de passe |

Les emails aux adresses `@example.com` sont ignores silencieusement (utile pour les tests).

## Cluster

Les instances Authagonal forment automatiquement un cluster pour partager l'etat de limitation de debit. Le clustering est active par defaut sans aucune configuration.

| Parametre | Variable d'env | Defaut | Description |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Interrupteur principal du clustering. Definir a `false` pour une limitation de debit locale uniquement. |
| `Cluster:MulticastGroup` | `Cluster__MulticastGroup` | `239.42.42.42` | Groupe multicast UDP pour la decouverte des pairs |
| `Cluster:MulticastPort` | `Cluster__MulticastPort` | `19847` | Port multicast UDP pour la decouverte des pairs |
| `Cluster:InternalUrl` | `Cluster__InternalUrl` | *(aucun)* | URL de repli avec repartition de charge pour le gossip lorsque le multicast est indisponible |
| `Cluster:Secret` | `Cluster__Secret` | *(aucun)* | Secret partage pour l'authentification du point d'acces gossip (recommande lorsque `InternalUrl` est defini) |
| `Cluster:GossipIntervalSeconds` | `Cluster__GossipIntervalSeconds` | `5` | Frequence d'echange de l'etat de limitation de debit entre les instances |
| `Cluster:DiscoveryIntervalSeconds` | `Cluster__DiscoveryIntervalSeconds` | `10` | Frequence a laquelle les instances s'annoncent via multicast |
| `Cluster:PeerStaleAfterSeconds` | `Cluster__PeerStaleAfterSeconds` | `30` | Supprimer les pairs dont on n'a pas eu de nouvelles apres ce nombre de secondes |

**Zero-config (par defaut) :** Les instances se decouvrent mutuellement via multicast UDP. Fonctionne dans Kubernetes, Docker Compose ou tout reseau partage.

**Multicast desactive (par exemple, certains VPC cloud) :**

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

**Clustering entierement desactive :**

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

Voir [Mise a l'echelle](scaling) pour plus de details sur le fonctionnement de la limitation de debit distribuee.

## Limitation de debit

Limites de debit integrees par IP appliquees a toutes les instances via le protocole de gossip du cluster :

| Point d'acces | Limite | Fenetre |
|---|---|---|
| `POST /api/auth/register` | 5 inscriptions | 1 heure |

Lorsque le clustering est active, ces limites sont consolidees sur toutes les instances. Lorsqu'il est desactive, chaque instance applique sa propre limite independamment.

## CORS

CORS est configure dynamiquement. Les origines de tous les `AllowedCorsOrigins` des clients enregistres sont automatiquement autorisees, avec un cache de 60 minutes.

## Exemple complet

```json
{
  "Storage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..."
  },
  "Issuer": "https://auth.example.com",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "SigningKeyLifetimeDays": 90
  },
  "Cluster": {
    "Enabled": true
  },
  "AdminApi": {
    "Enabled": true,
    "Scope": "authagonal-admin"
  },
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
    "SenderEmail": "noreply@example.com",
    "SenderName": "Example Auth",
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
      "MfaPolicy": "Enabled",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
