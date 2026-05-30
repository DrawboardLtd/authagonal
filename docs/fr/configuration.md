---
layout: default
title: Configuration
locale: fr
---

# Configuration

Authagonal est configure via `appsettings.json` ou des variables d'environnement. Les variables d'environnement utilisent `__` comme separateur de section (par exemple, `Storage__ConnectionString`).

## Parametres requis

Le stockage peut etre configure de deux manieres — fournissez **soit** `Storage:ConnectionString` **soit** `Storage:TableServiceUri` (la voie par identite geree, preferee en production).

| Parametre | Variable d'env | Description |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Chaine de connexion Azure Table Storage avec une cle de compte. Convient au dev / a Azurite. |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | Point de terminaison Table Storage par identite geree, par exemple `https://{account}.table.core.windows.net/`. Alternative a `Storage:ConnectionString` et **preferee en production** — s'authentifie via `DefaultAzureCredential`, de sorte qu'aucune cle d'acces n'aboutit jamais dans un secret. L'hote doit accorder a l'identite de charge de travail le role **Storage Table Data Contributor**. |
| `Issuer` | `Issuer` | L'URL publique de base de ce serveur (par exemple, `https://auth.example.com`) |

## Stockage

| Parametre | Variable d'env | Defaut | Description |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(aucun)* | Chaine de connexion avec cle de compte (voir Parametres requis). |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(aucun)* | URI Table Storage par identite geree (voir Parametres requis). A priorite sur `Storage:ConnectionString` lorsque les deux sont definis. |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | Indique s'il faut maintenir les tables d'index de recherche par prefixe `UserFirstNames` / `UserLastNames` qui sous-tendent la recherche par prefixe de nom dans l'administration. Definissez `false` sur les hotes qui n'exposent pas la recherche de noms dans l'administration pour eviter ces ecritures. **Note de mise a l'echelle :** ces index utilisent une partition chaude unique et plafonnent le debit a environ 2 000 ops/sec a grande echelle — desactivez-les si vous n'avez pas besoin de la recherche de noms. |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | URL de base vers laquelle le point d'acces `/connect/authorize` redirige pour la SPA de connexion (ecrans de connexion, d'elevation et de consentement). Definissez-la lorsque l'interface de connexion est servie depuis une origine differente de celle du serveur ; par defaut, le chemin relatif `/login` servi par la SPA integree. |

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
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Fenetre de grace optionnelle (en secondes) pour la reutilisation concurrente du jeton de rafraichissement. `0` (par defaut) maintient la posture stricte : toute reutilisation d'un jeton de rafraichissement deja consomme revoque tous les jetons de cet utilisateur+client. Definissez `> 0` pour traiter une reutilisation dans la fenetre comme une nouvelle tentative idempotente (re-livre les jetons successeurs) — utile pour les clients mobiles avec des coupures de connectivite. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Active le point d'acces d'enregistrement dynamique de client `POST /connect/register` (RFC 7591). Desactive par defaut car l'enregistrement ouvert peut etre abuse dans les deploiements multi-tenant. Voir [Enregistrement dynamique de client](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | Duree de vie de la cle de signature RSA avant rotation automatique |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Frequence de rechargement des cles de signature depuis le stockage |
| `Auth:KeyRotationEnabled` | `false` | Active la rotation automatique des cles de signature |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | Frequence de verification du besoin de rotation de la cle active |
| `Auth:KeyRotationLeadTimeDays` | `14` | Effectuer la rotation lorsque la cle active expire dans ce nombre de jours |
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
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
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
| `urn:ietf:params:oauth:grant-type:device_code` | Octroi d'autorisation d'appareil (RFC 8628) pour les appareils a saisie limitee |

### Utilisation du jeton de rafraichissement

| Valeur | Comportement |
|---|---|
| `OneTime` (par defaut) | Chaque rafraichissement emet un nouveau jeton de rafraichissement et invalide l'ancien. Par defaut (`Auth:RefreshTokenReuseGraceSeconds = 0`), toute reutilisation d'un jeton consomme revoque immediatement tous les jetons de cet utilisateur+client — il n'y a **aucune** fenetre de grace active par defaut. Definissez `Auth:RefreshTokenReuseGraceSeconds` sur une valeur positive pour activer une fenetre de tolerance aux nouvelles tentatives. |
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

Les secrets des clients OIDC en amont et les graines TOTP / MFA peuvent etre stockes dans Azure Key Vault plutot qu'en texte brut :

| Parametre | Description |
|---|---|
| `SecretProvider:VaultUri` | URI du Key Vault (par exemple, `https://my-vault.vault.azure.net/`). Si non defini, le fournisseur **en texte brut** est utilise et les secrets sont stockes tels quels dans Table Storage. |

Lorsqu'il est configure, les valeurs de secrets qui ressemblent a des references Key Vault sont resolues a l'execution. Utilise `DefaultAzureCredential` pour l'authentification.

> ⚠️ **Production : definissez `SecretProvider:VaultUri`.** Le fournisseur de secrets par defaut est **en texte brut**. Lorsque `SecretProvider:VaultUri` n'est pas defini, les secrets des clients OIDC en amont et les graines TOTP / MFA sont ecrits en clair dans Azure Table Storage — et apparaissent donc en clair dans toute [sauvegarde](backup-restore). Pour tout deploiement en production, configurez `SecretProvider:VaultUri` afin que ces secrets soient stockes dans Key Vault.

## API d'administration

| Parametre | Defaut | Description |
|---|---|---|
| `AdminApi:Enabled` | `true` | **Active par defaut.** Definissez sur `false` pour desactiver tous les points d'acces d'administration (ils ne seront pas enregistres). |
| `AdminApi:Scope` | `authagonal-admin` | Scope JWT requis pour acceder aux points d'acces d'administration. Modifiez-le pour correspondre a votre nom de scope existant (par exemple, `projects-identity-admin` pour les migrations IdentityServer). |

> ⚠️ **L'API d'administration est activee par defaut et est hautement privilegiee.** Le scope d'administration accorde la gestion complete et l'usurpation d'identite des utilisateurs — quiconque detient un jeton avec `AdminApi:Scope` peut emettre des jetons pour n'importe quel utilisateur, gerer les clients et lire/ecrire toute la configuration. Restreignez l'acces reseau aux points d'acces d'administration (les routes d'administration `/api/v1/*`) et controlez strictement a qui le scope d'administration peut etre emis. Par mesure de defense en profondeur, le scope est *reserve* : il ne peut jamais etre accorde a un client OAuth (voir [API d'administration](admin-api)) et ne peut pas etre emis via le point d'acces d'usurpation. Definissez `AdminApi:Enabled = false` entierement si l'API d'administration n'est pas utilisee.

## Consentement

Le consentement par client peut etre active avec la propriete `RequireConsent` :

| Valeur | Comportement |
|---|---|
| `false` (par defaut) | L'autorisation se poursuit immediatement apres l'authentification |
| `true` | Un ecran de consentement listant les scopes demandes est presente a l'utilisateur. Le consentement est conserve pendant 5 ans et n'est redemande que lorsque de nouveaux scopes sont demandes. |

Les utilisateurs peuvent consulter et revoquer leurs autorisations de consentement via `GET /consent/grants` et `DELETE /consent/grants/{clientId}`.

## Deconnexion par canal arriere

Enregistrez un `BackChannelLogoutUri` sur un client pour recevoir les notifications OIDC Back-Channel Logout 1.0. Lorsqu'un utilisateur se deconnecte, Authagonal envoie un jeton de deconnexion signe (JWT) a l'URI enregistree de chaque client.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback"
    }
  ]
}
```

## Email

Par defaut, Authagonal utilise un service d'email no-op qui ignore silencieusement tous les emails. Pour activer l'envoi d'emails, enregistrez une implementation de `IEmailService` avant d'appeler `AddAuthagonal()`.

Le service integre `EmailService` utilise [Resend](https://resend.com). Pour l'utiliser, enregistrez-le explicitement :

```csharp
services.AddSingleton<IEmailService, EmailService>();
services.AddAuthagonal(configuration);
```

| Parametre | Description |
|---|---|
| `Email:ResendApiKey` | Cle API Resend pour l'envoi d'emails |
| `Email:SenderEmail` | Adresse email de l'expediteur |
| `Email:SenderName` | Nom d'affichage de l'expediteur (par defaut : `"Authagonal"`) |

Les emails aux adresses `@example.com` sont ignores silencieusement (utile pour les tests).

## Cluster

Les instances Authagonal forment automatiquement un cluster pour partager l'etat de limitation de debit. Le clustering est active par defaut sans aucune configuration.

| Parametre | Variable d'env | Defaut | Description |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Interrupteur principal du clustering. Definir a `false` pour une limitation de debit locale uniquement. |
| `Cluster:MulticastGroup` | `Cluster__MulticastGroup` | `239.42.42.42` | Groupe multicast UDP pour la decouverte des pairs |
| `Cluster:MulticastPort` | `Cluster__MulticastPort` | `19847` | Port multicast UDP pour la decouverte des pairs |
| `Cluster:InternalUrl` | `Cluster__InternalUrl` | *(aucun)* | URL de repli avec repartition de charge pour le gossip lorsque le multicast est indisponible |
| `Cluster:Secret` | `Cluster__Secret` | *(aucun)* | Secret partage requis sur les points d'acces internes uniquement (`/_internal/cluster/gossip` et `/_internal/backchannel-logout`). Lorsqu'il est defini, les appelants doivent le presenter dans l'en-tete `X-Cluster-Secret` (compare en temps constant). Lorsqu'il est **non defini**, ces points d'acces ne sont accessibles que depuis des IP source de boucle locale / privees (RFC 1918 / lien-local / ULA) — une requete externe portant une IP publique est rejetee. Recommande des que `InternalUrl` achemine le gossip via un equilibreur de charge. |
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

## En-tetes transferes (proxy de confiance)

Authagonal indexe la limitation de debit et le verrouillage de compte sur l'IP du client, et n'emet HSTS que sur les requetes HTTPS. Derriere un reverse proxy / ingress, l'IP reelle du client et le schema arrivent dans les en-tetes `X-Forwarded-For` / `X-Forwarded-Proto`. Ces parametres controlent **quels sauts de proxy sont de confiance** pour definir ces valeurs, afin qu'un appelant ne puisse pas usurper `X-Forwarded-For` pour falsifier l'IP du client.

| Parametre | Variable d'env | Defaut | Description |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | Nombre de sauts de proxy a honorer depuis la droite de la chaine `X-Forwarded-For`. La valeur par defaut de `1` ne fait confiance qu'au seul saut que votre ingress ajoute et ignore tout ce qui se trouve plus a gauche dans la chaine. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (tableau) | *(vide)* | Plages CIDR (tableau de chaines, par exemple `"10.0.0.0/8"`) autorisees a definir les en-tetes transferes. **Garantie la plus forte :** definissez-la sur le CIDR de votre ingress / de vos pods afin que seul ce reseau puisse definir l'IP du client. |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0` (tableau) | *(vide)* | Adresses IP de proxy individuelles (tableau de chaines) autorisees a definir les en-tetes transferes. A utiliser en complement ou a la place de `KnownNetworks`. |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

> ⚠️ **Proxy terminant le TLS requis.** Authagonal doit s'executer derriere un reverse proxy terminant le TLS. Le cookie de session utilise `SecurePolicy = SameAsRequest` et HSTS (`Strict-Transport-Security`) n'est emis que sur les requetes HTTPS ; le proxy doit donc transferer `X-Forwarded-Proto: https` pour que les cookies soient marques `Secure` et que HSTS soit envoye. Configurez `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` sur votre proxy de confiance afin que le schema et l'IP du client ne puissent pas etre usurpes.

## Limitation de debit

Limites de debit integrees par IP appliquees a toutes les instances via le protocole de gossip du cluster :

| Point d'acces | Limite | Fenetre |
|---|---|---|
| `POST /api/auth/register` | 5 inscriptions | 1 heure |

Lorsque le clustering est active, ces limites sont consolidees sur toutes les instances. Lorsqu'il est desactive, chaque instance applique sa propre limite independamment.

## CORS

CORS est configure dynamiquement. Les origines de tous les `AllowedCorsOrigins` des clients enregistres sont automatiquement autorisees, avec un cache de 60 minutes.

## HashiCorp Vault Transit

Authagonal peut signer les JWT en utilisant le moteur de secrets Transit de HashiCorp Vault. Les cles privees ne quittent jamais Vault — seule l'operation de signature est deleguee a distance. Les cles publiques sont mises en cache localement pour la verification.

Ceci se configure par programmation lors de l'hebergement en tant que bibliotheque. Voir [Extensibilite](extensibility) pour plus de details.

## Exemple complet

```json
{
  "Storage": {
    "TableServiceUri": "https://myaccount.table.core.windows.net/",
    "NameIndexesEnabled": true
  },
  "Issuer": "https://auth.example.com",
  "LoginAppUrl": "/login",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "RefreshTokenReuseGraceSeconds": 0,
    "DynamicClientRegistrationEnabled": false,
    "SigningKeyLifetimeDays": 90
  },
  "SecretProvider": {
    "VaultUri": "https://my-vault.vault.azure.net/"
  },
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"]
  },
  "Cluster": {
    "Enabled": true,
    "Secret": "shared-secret-here"
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
    "ResendApiKey": "re_xxx",
    "SenderEmail": "noreply@example.com",
    "SenderName": "Example Auth"
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
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
