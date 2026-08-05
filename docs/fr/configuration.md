---
layout: default
title: Configuration
locale: fr
---

# Configuration

Authagonal est configuré via `appsettings.json` ou des variables d'environnement. Les variables d'environnement utilisent `__` comme séparateur de section (par exemple, `Storage__ConnectionString`).

## Paramètres requis

Le stockage peut être configuré de deux manières : fournissez **soit** `Storage:ConnectionString` **soit** `Storage:TableServiceUri` (la voie par identité gérée, préférée en production).

| Paramètre | Variable d'env | Description |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Chaîne de connexion Azure Table Storage avec une clé de compte. Convient au dev / à Azurite. |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | Point de terminaison Table Storage par identité gérée, par exemple `https://{account}.table.core.windows.net/`. Alternative à `Storage:ConnectionString` et **préférée en production** : s'authentifie via `DefaultAzureCredential`, de sorte qu'aucune clé d'accès n'aboutit jamais dans un secret. L'hôte doit accorder à l'identité de charge de travail le rôle **Storage Table Data Contributor**. |
| `Issuer` | `Issuer` | L'URL publique de base de ce serveur (par exemple, `https://auth.example.com`) |

## Stockage

| Paramètre | Variable d'env | Défaut | Description |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(aucun)* | Chaîne de connexion avec clé de compte (voir Paramètres requis). |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(aucun)* | URI Table Storage par identité gérée (voir Paramètres requis). A priorité sur `Storage:ConnectionString` lorsque les deux sont définis. |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | Indique s'il faut maintenir les tables d'index de recherche par préfixe `UserFirstNames` / `UserLastNames` qui sous-tendent la recherche par préfixe de nom dans l'administration. Définissez `false` sur les hôtes qui n'exposent pas la recherche de noms dans l'administration pour éviter ces écritures. **Note de mise à l'échelle :** ces index utilisent une partition chaude unique et plafonnent le débit à environ 2 000 ops/sec à grande échelle : désactivez-les si vous n'avez pas besoin de la recherche de noms. |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | URL de base vers laquelle le point d'accès `/connect/authorize` redirige pour la SPA de connexion (écrans de connexion, d'élévation et de consentement). Définissez-la lorsque l'interface de connexion est servie depuis une origine différente de celle du serveur ; par défaut, le chemin relatif `/login` servi par la SPA intégrée. |

## Authentification

| Paramètre | Défaut | Description |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Durée de vie de la session par cookie (glissante) |
| `Authentication:AllowInsecureCookie` | `false` | Let the session cookie be sent over plain http (`SameAsRequest` instead of `Always`). **Development only** — see the English documentation. |
| `Authentication:CookieDomain` | *(unset)* | Scope the session cookie to a parent domain. **Costs the `__Host-` prefix and its origin binding** — see the English documentation. |
| `Auth:AllowInsecureHttp` | `false` | Laisse les points d'accès OAuth (`/connect/*`) répondre à des requêtes http en clair. **Développement uniquement.** La RFC 6749 §3.1/§3.2 exige TLS aux points d'accès d'autorisation et de token : par défaut, une requête non-https vers l'un d'eux est refusée avec `invalid_request`. Le schéma est évalué *après* le traitement des en-têtes transférés, si bien qu'un proxy qui termine le TLS et transfère `X-Forwarded-Proto: https` franchit la barrière sans activer cette option — à condition que ce proxy soit déclaré dans `ForwardedHeaders:KnownNetworks` / `KnownProxies` ; sans cette déclaration, l'en-tête est ignoré. Seul un déploiement réellement en clair (le `docker-compose.yml` fourni, la démo de serveur personnalisé) en a besoin, et le serveur journalise un avertissement au démarrage tant qu'elle est active. Propagée à `AuthagonalProtocolOptions.AllowInsecureHttp`, elle régit donc aussi les points d'accès appartenant à `Authagonal.Protocol` (voir [Extensibilité](extensibility#embedding-authagonalprotocol-alone)). |
| `Auth:MaxFailedAttempts` | `5` | Tentatives de connexion échouées avant le verrouillage du compte |
| `Auth:LockoutDurationMinutes` | `10` | Durée du verrouillage du compte après le nombre maximal de tentatives échouées |
| `Auth:MaxRegistrationsPerIp` | `5` | Nombre maximal d'inscriptions par adresse IP dans la fenêtre |
| `Auth:RegistrationWindowMinutes` | `60` | Fenêtre de limitation du débit d'inscription |
| `Auth:MaxPasswordResetsPerEmail` | `3` | Nombre maximal d'emails de réinitialisation de mot de passe par adresse cible dans la fenêtre (indexé sur l'email, pas sur l'IP de l'appelant, de sorte qu'une adresse ne peut pas être bombardée d'emails) |
| `Auth:PasswordResetWindowMinutes` | `60` | Fenêtre de limitation du débit de réinitialisation de mot de passe |
| `Auth:AutoConfirmEmailDomains` | *(vide)* | Domaines d'email (tableau de chaînes) dont les inscriptions en libre-service sont auto-confirmées : ils ignorent l'email de vérification. Vide (par défaut) signifie que chaque inscription doit être vérifiée. Destiné uniquement au dev/test ; ne listez jamais un domaine capable de recevoir du courrier réel. |
| `Auth:EmailVerificationExpiryHours` | `24` | Durée de vie du lien de vérification d'email |
| `Auth:PasswordResetExpiryMinutes` | `60` | Durée de vie du lien de réinitialisation du mot de passe |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Durée de vie du jeton de vérification MFA |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Durée de vie du jeton de configuration MFA (pour l'inscription forcée) |
| `Auth:Pbkdf2Iterations` | `100000` | Nombre d'itérations PBKDF2 pour le hachage du mot de passe |
| `Auth:FailedLoginMinimumMilliseconds` | `250` | Plancher de temps horloge auquel une connexion échouée est maintenue avant de renvoyer `invalid_credentials`, mesuré depuis le début de la requête. Ferme l'oracle temporel d'énumération d'utilisateurs : un compte inexistant est vérifié contre un hachage factice au format PBKDF2 natif, mais un compte réel peut encore porter un hachage bcrypt ou ASP.NET Identity V3 importé à un coût différent ; égaliser le travail est donc impossible et c'est le temps écoulé qui est imposé. Relevez-le au-dessus du hachage le plus lent que détient le déploiement, par exemple si vous avez importé du bcrypt au-delà du coût 11 ou augmenté `Pbkdf2Iterations` bien au-delà de la valeur par défaut : un avertissement unique est journalisé la première fois qu'une connexion échouée dépasse le plancher. `0` désactive le remplissage et rouvre l'oracle. |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Fenêtre de grâce optionnelle (en secondes) pour la réutilisation concurrente du jeton de rafraîchissement. `0` (par défaut) maintient la posture stricte : toute réutilisation d'un jeton de rafraîchissement déjà consommé révoque tous les jetons de cet utilisateur+client. Définissez `> 0` pour traiter une réutilisation dans la fenêtre comme une nouvelle tentative idempotente (re-livre les jetons successeurs), utile pour les clients mobiles avec des coupures de connectivité. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Active le point d'accès d'enregistrement dynamique de client `POST /connect/register` (RFC 7591). Désactivé par défaut car l'enregistrement ouvert peut être abusé dans les déploiements multi-tenant. Voir [Enregistrement dynamique de client](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | Durée de vie de la clé de signature RSA avant rotation automatique |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Fréquence de rechargement des clés de signature depuis le stockage |
| `Auth:KeyRotationEnabled` | `false` | Active la rotation automatique des clés de signature |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | Fréquence de vérification du besoin de rotation de la clé active |
| `Auth:KeyRotationLeadTimeDays` | `14` | Effectuer la rotation lorsque la clé active expire dans ce nombre de jours |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Intervalle entre les vérifications du tampon de sécurité du cookie |

## Protection des données (Data Protection)

Les clés ASP.NET Core Data Protection (qui chiffrent le cookie de session) doivent être partagées entre les instances, voir [Mise à l'échelle](scaling#cookie-encryption-data-protection). Options de persistance, par ordre de priorité :

| Paramètre | Défaut | Description |
|---|---|---|
| `DataProtection:BlobUri` | *(aucun)* | URI Azure Blob explicite pour le trousseau de clés (par exemple `https://{account}.blob.core.windows.net/dataprotection/keys.xml`). S'authentifie via `DefaultAzureCredential`, le chemin de production préféré aux côtés de `Storage:TableServiceUri`. |
| *(repli)* | — | Lorsque `DataProtection:BlobUri` n'est pas défini et que `Storage:ConnectionString` pointe vers un compte de stockage réel (pas Azurite), les clés sont persistées automatiquement dans un conteneur `dataprotection` de ce compte. Avec Azurite, les clés retombent sur le magasin par défaut basé sur des fichiers. |

Sur le backend AWS, passez un client S3 + un bucket à `AddAuthagonalAwsStorage` pour persister le trousseau de clés dans S3, voir [Installation → backend AWS](installation#aws-backend).

## Cache et délais d'attente

| Paramètre | Défaut | Description |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | Durée de mise en cache des origines CORS autorisées |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | Durée de mise en cache du document de découverte OIDC |
| `Cache:SamlMetadataCacheMinutes` | `60` | Durée de mise en cache des métadonnées SAML de l'IdP |
| `Cache:OidcStateLifetimeMinutes` | `10` | Durée de vie du paramètre state d'autorisation OIDC |
| `Cache:SamlReplayLifetimeMinutes` | `10` | Durée de vie de l'ID AuthnRequest SAML (prévention de rejeu) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Délai d'attente de la vérification de santé de Table Storage |

## Services d'arrière-plan

| Paramètre | Défaut | Description |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | Délai initial avant le premier nettoyage des jetons expirés |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | Intervalle de nettoyage des jetons expirés |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | Délai initial avant la première réconciliation des autorisations |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | Intervalle de réconciliation des autorisations |

## Rôles

Les rôles sont définis dans le tableau `Roles` et injectés au démarrage, au même titre que les
clients, les scopes et les fournisseurs. Les injecter compte surtout lorsqu'un scope est restreint
par [`AllowedRoles`](scopes#role-gated-scopes) : un scope restreint à un rôle que rien ne crée est
restreint pour tout le monde, y compris l'opérateur qui l'a configuré, et il échoue en silence : le
scope n'est tout simplement jamais accordé.

```json
{
  "Roles": [
    {
      "Name": "staff-admin",
      "Description": "Internal staff console",
      "Members": [ "ada@example.com", "grace@example.com" ]
    }
  ]
}
```

| Champ | Description |
|---|---|
| `Name` | Le nom du rôle, tel qu'utilisé dans `Scope.AllowedRoles` et dans le claim `roles` du jeton |
| `Description` | Lisible par un humain ; mise à jour aux démarrages suivants lorsque l'injection en indique une |
| `Members` | Emails placés dans le rôle à chaque démarrage. Une adresse sans utilisateur existant est ignorée avec un avertissement et réessayée au démarrage suivant : le démarrage ne dépend jamais d'un compte que personne n'a créé |

L'injection est **additive et idempotente**. Elle ne supprime jamais un rôle ni ne révoque une
appartenance : la configuration n'est pas la source de vérité de qui détient quoi, de sorte qu'un
rôle accordé via l'API d'administration survit au redémarrage suivant.

## Clients

Les clients sont définis dans le tableau `Clients` et injectés au démarrage. Chaque client peut avoir :

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
| `client_credentials` | Communication service à service |
| `refresh_token` | Renouvellement de jeton (nécessite `AllowOfflineAccess: true`) |
| `urn:ietf:params:oauth:grant-type:device_code` | Octroi d'autorisation d'appareil (RFC 8628) pour les appareils à saisie limitée |

### Utilisation du jeton de rafraîchissement

| Valeur | Comportement |
|---|---|
| `OneTime` (par défaut) | Chaque rafraîchissement émet un nouveau jeton de rafraîchissement et invalide l'ancien. Par défaut (`Auth:RefreshTokenReuseGraceSeconds = 0`), toute réutilisation d'un jeton consommé révoque immédiatement tous les jetons de cet utilisateur+client : il n'y a **aucune** fenêtre de grâce active par défaut. Définissez `Auth:RefreshTokenReuseGraceSeconds` sur une valeur positive pour activer une fenêtre de tolérance aux nouvelles tentatives. |
| `ReUse` | Le même jeton de rafraîchissement est réutilisé jusqu'à expiration. |

### Applications de provisionnement

Le tableau `ProvisioningApps` référence les identifiants d'applications définis dans la section de configuration `ProvisioningApps`. Lorsqu'un utilisateur s'autorise via ce client, il est provisionné dans ces applications via TCC. Voir [Provisionnement](provisioning) pour plus de détails.

## Applications de provisionnement

Définissez les applications en aval dans lesquelles les utilisateurs doivent être provisionnés :

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

Voir [Provisionnement](provisioning) pour la spécification complète du protocole TCC.

## Politique MFA

L'authentification multifacteur est appliquée par client via la propriété `MfaPolicy` :

| Valeur | Comportement |
|---|---|
| `Disabled` (par défaut) | Pas de vérification MFA, même si l'utilisateur a inscrit le MFA |
| `Enabled` | Vérifie les utilisateurs ayant inscrit le MFA ; ne force pas l'inscription |
| `Required` | Vérifie les utilisateurs inscrits ; force l'inscription pour les utilisateurs sans MFA |

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

Lorsque `MfaPolicy` est `Required` et que l'utilisateur n'a pas inscrit le MFA, la connexion renvoie `{ mfaSetupRequired: true, setupToken: "..." }`. Le jeton de configuration authentifie l'utilisateur auprès des points d'accès de configuration MFA (via l'en-tête `X-MFA-Setup-Token`) afin qu'il puisse s'inscrire avant d'obtenir une session par cookie.

Les connexions fédérées (SAML/OIDC) respectent également la politique MFA : un utilisateur ayant inscrit le MFA est dirigé vers le défi MFA après que l'IdP externe l'a authentifié, et `Required` force l'inscription pour les utilisateurs fédérés sans MFA.

### Surcharge IAuthHook

La méthode `IAuthHook.ResolveMfaPolicyAsync` peut surcharger la politique du client par utilisateur :

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
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

| Propriété | Défaut | Description |
|---|---|---|
| `MinLength` | `8` | Longueur minimale du mot de passe |
| `MinUniqueChars` | `2` | Nombre minimum de caractères distincts |
| `RequireUppercase` | `true` | Exiger au moins une lettre majuscule |
| `RequireLowercase` | `true` | Exiger au moins une lettre minuscule |
| `RequireDigit` | `true` | Exiger au moins un chiffre |
| `RequireSpecialChar` | `true` | Exiger au moins un caractère non alphanumérique |

La politique est appliquée lors de la réinitialisation du mot de passe et de l'inscription d'un utilisateur par l'administrateur. L'interface de connexion récupère la politique active depuis `GET /api/auth/password-policy` pour afficher les exigences dynamiquement.

## Fournisseurs SAML

Définissez les fournisseurs d'identité SAML dans la configuration. Ceux-ci sont injectés au démarrage :

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

| Propriété | Requis | Description |
|---|---|---|
| `ConnectionId` | Oui | Identifiant stable (utilisé dans les URLs comme `/saml/{connectionId}/login`) |
| `ConnectionName` | Non | Nom d'affichage (par défaut : ConnectionId) |
| `EntityId` | Oui | Identifiant d'entité SP **de ce serveur** : l'identifiant que vous enregistrez auprès de l'IdP, pas l'identifiant d'entité propre à l'IdP |
| `MetadataLocation` | Oui | URL vers le XML de métadonnées SAML de l'IdP |
| `AllowedDomains` | Non | Domaines de messagerie acheminés vers ce fournisseur via SSO |

## Fournisseurs OIDC

Définissez les fournisseurs d'identité OIDC dans la configuration. Ceux-ci sont injectés au démarrage :

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

| Propriété | Requis | Description |
|---|---|---|
| `ConnectionId` | Oui | Identifiant stable (utilisé dans les URLs comme `/oidc/{connectionId}/login`) |
| `ConnectionName` | Non | Nom d'affichage (par défaut : ConnectionId) |
| `MetadataLocation` | Oui | URL vers le document de découverte OpenID Connect de l'IdP |
| `ClientId` | Oui | Identifiant client OAuth2 enregistré auprès de l'IdP |
| `ClientSecret` | Oui | Secret client OAuth2 (protégé via `ISecretProvider` au démarrage) |
| `RedirectUrl` | Non | **Ignoré.** L'URI de redirection est dérivée par requête sous la forme `{Issuer}/oidc/callback` — enregistrez *celle-ci* auprès de l'IdP. Une valeur ici n'a aucun effet et est journalisée comme ignorée. |
| `AllowedDomains` | Non | Domaines de messagerie acheminés vers ce fournisseur via SSO |

> **Remarque :** Les fournisseurs peuvent également être gérés à l'exécution via l'[API d'administration](admin-api). Les fournisseurs configurés sont mis à jour (upsert) à chaque démarrage, donc les modifications de configuration prennent effet au redémarrage.

## Fournisseur de secrets

Les secrets des clients OIDC en amont et les graines TOTP / MFA peuvent être stockés dans Azure Key Vault plutôt qu'en texte brut :

| Paramètre | Description |
|---|---|
| `SecretProvider:VaultUri` | URI du Key Vault (par exemple, `https://my-vault.vault.azure.net/`). Si non défini, le fournisseur **en texte brut** est utilisé et les secrets sont stockés tels quels dans Table Storage. |

| `SecretProvider:RequireVaultReferences` | `false` par défaut. Lorsqu'il vaut `true`, une référence stockée sans préfixe de vault (`kv:` pour Key Vault, `sm:` pour AWS Secrets Manager) est une **erreur** au lieu d'être honorée comme une valeur en texte brut. Activez-le une fois la migration vers le vault terminée. |

Lorsqu'il est configuré, les valeurs de secrets qui ressemblent à des références Key Vault sont résolues à l'exécution. Utilise `DefaultAzureCredential` pour l'authentification.

### Migrer vers un vault, et refermer la porte ensuite

Les deux fournisseurs adossés à un vault renvoient telle quelle une référence sans préfixe, la traitant comme une valeur en texte brut écrite avant que le déploiement ne dispose d'un vault. C'est ce qui permet de migrer un système en fonctionnement secret par secret plutôt que d'un seul coup, mais laissée ouverte, cette voie est un chemin de dégradation permanent : tout ce qui peut écrire une seule colonne de configuration (une migration à moitié faite, un chemin d'administration qui stocke une valeur brute là où une référence est attendue, un attaquant ayant accès au stockage mais pas au vault) remplace un secret protégé par le vault par une valeur de son choix, et cela se vérifie parfaitement, car pour une référence sans préfixe la référence *est* la valeur.

Activez `SecretProvider:RequireVaultReferences` une fois la migration terminée. Résoudre une référence sans préfixe lève alors une exception au lieu de renvoyer discrètement du texte clair. L'activer alors que le fournisseur résolu est celui en texte brut est refusé au démarrage, car cette combinaison n'a aucun état fonctionnel : toute référence écrite par le fournisseur en texte brut est sans préfixe.

Le serveur journalise également un avertissement au démarrage chaque fois qu'un hôte hors développement se retrouve avec le fournisseur en texte brut.

> ⚠️ **Production : définissez `SecretProvider:VaultUri`.** Le fournisseur de secrets par défaut est **en texte brut**. Lorsque `SecretProvider:VaultUri` n'est pas défini, les secrets des clients OIDC en amont et les graines TOTP / MFA sont écrits en clair dans Azure Table Storage, et apparaissent donc en clair dans toute [sauvegarde](backup-restore). Pour tout déploiement en production, configurez `SecretProvider:VaultUri` afin que ces secrets soient stockés dans Key Vault.

## API d'administration

| Paramètre | Défaut | Description |
|---|---|---|
| `AdminApi:Enabled` | `true` | **Activé par défaut.** Définissez sur `false` pour désactiver tous les points d'accès d'administration (ils ne seront pas enregistrés). |
| `AdminApi:Scope` | `authagonal-admin` | Scope JWT requis pour accéder aux points d'accès d'administration. Modifiez-le pour correspondre à votre nom de scope existant (par exemple, `projects-identity-admin` pour les migrations IdentityServer). |

> ⚠️ **L'API d'administration est activée par défaut et est hautement privilégiée.** Le scope d'administration accorde la gestion complète et l'usurpation d'identité des utilisateurs : quiconque détient un jeton avec `AdminApi:Scope` peut émettre des jetons pour n'importe quel utilisateur, gérer les clients et lire/écrire toute la configuration. Restreignez l'accès réseau aux points d'accès d'administration (les routes d'administration `/api/v1/*`) et contrôlez strictement à qui le scope d'administration peut être émis. Par mesure de défense en profondeur, le scope est *réservé* : il ne peut jamais être accordé à un client OAuth (voir [API d'administration](admin-api)) et ne peut pas être émis via le point d'accès d'usurpation. Définissez `AdminApi:Enabled = false` entièrement si l'API d'administration n'est pas utilisée.

## Consentement

Le consentement par client peut être activé avec la propriété `RequireConsent` :

| Valeur | Comportement |
|---|---|
| `false` (par défaut) | L'autorisation se poursuit immédiatement après l'authentification |
| `true` | Un écran de consentement listant les scopes demandés est présenté à l'utilisateur. Le consentement est conservé pendant 5 ans et n'est redemandé que lorsque de nouveaux scopes sont demandés. |

Les utilisateurs peuvent consulter et révoquer leurs autorisations de consentement via `GET /consent/grants` et `DELETE /consent/grants/{clientId}`.

## Déconnexion par canal arrière

Enregistrez un `BackChannelLogoutUri` sur un client pour recevoir les notifications OIDC Back-Channel Logout 1.0. Lorsqu'un utilisateur se déconnecte, Authagonal envoie un jeton de déconnexion signé (JWT) à l'URI enregistrée de chaque client.

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

L'expéditeur d'email intégré utilise [Resend](https://resend.com) et **s'active automatiquement** lorsque `Email:ResendApiKey` est configuré, sans enregistrement de service. Pour utiliser un autre fournisseur, enregistrez votre propre implémentation de `IEmailService` avant d'appeler `AddAuthagonal()` (elle a la priorité quelles que soient les clés `Email:*`).

| Paramètre | Description |
|---|---|
| `Email:ResendApiKey` | Clé API Resend. Lorsqu'elle est définie, l'expéditeur Resend intégré est utilisé. |
| `Email:SenderEmail` | Adresse email de l'expéditeur |
| `Email:SenderName` | Nom d'affichage de l'expéditeur (par défaut : `"Authagonal"`) |

> ⚠️ **Sans aucun expéditeur d'email, l'auto-inscription est cassée.** Lorsque `Email:ResendApiKey` n'est pas défini et qu'aucun `IEmailService` personnalisé n'est enregistré, un service no-op ignore silencieusement tout le courrier : les emails de vérification et de réinitialisation de mot de passe n'arrivent jamais, et comme la connexion exige un email confirmé par défaut, les utilisateurs auto-inscrits ne peuvent jamais se connecter. `UseAuthagonal` journalise un avertissement au démarrage dans cet état. Échappatoire pour le dev/test : `Auth:AutoConfirmEmailDomains` auto-confirme les inscriptions pour les domaines listés.

Les emails aux adresses `@example.com` sont ignorés silencieusement (utile pour les tests).

## Cluster

La couche de clustering fournit l'**élection d'un leader** (afin que les tâches réservées au leader, comme la rotation des clés de signature, s'exécutent sur exactement un nœud) et un **bus d'événements inter-nœuds**, derrière des backends interchangeables. Le défaut est en-processus : un nœud unique est toujours son propre leader, le bon réglage pour un nœud unique et le développement local, sans aucune configuration.

| Paramètre | Variable d'env | Défaut | Description |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Interrupteur principal. Lorsque `false`, le nœud s'exécute en autonome (toujours leader, bus d'événements en-processus). |
| `Cluster:Secret` | `Cluster__Secret` | *(aucun)* | Secret partagé requis sur le point d'accès interne uniquement `/_internal/backchannel-logout`. Lorsqu'il est défini, les appelants doivent le présenter dans l'en-tête `X-Cluster-Secret` (comparé en temps constant). Lorsqu'il n'est **pas défini, le point d'accès n'autorise personne** et répond 404 : une adresse source n'est pas un credential, et la boucle locale est précisément ce qu'un proxy inverse sur le même hôte présente pour chaque requête qu'il transfère, y compris celles venues d'internet. |
| `Cluster:AllowLoopbackWithoutSecret` | `Cluster__AllowLoopbackWithoutSecret` | `false` | Opt-in de développement : sans `Cluster:Secret`, accepter un appelant dont l'**adresse de pair avant transfert** est la boucle locale. Les plages privées restent refusées : dans un réseau de cluster partagé, cela ferait confiance à chaque charge de travail voisine. Ne l'activez pas sur un hôte derrière un proxy inverse. |
| `Cluster:RunLeaderElection` | `Cluster__RunLeaderElection` | `true` | Si ce nœud exécute la boucle de renouvellement du bail et peut donc devenir leader. `false` rejoint toujours le cluster et consomme le bus d'événements ; il ne se dispute simplement jamais le bail — pour un nœud qui doit recevoir les événements du cluster mais ne jamais détenir le leadership. |
| `Cluster:LeaseTtlSeconds` | `Cluster__LeaseTtlSeconds` | `30` | Durée du bail de leadership. Renouvelé à environ la moitié de cet intervalle. |
| `Cluster:PollIntervalSeconds` | `Cluster__PollIntervalSeconds` | `3` | Fréquence à laquelle le backend du bus d'événements interroge les messages publiés par les autres nœuds. |

**Les déploiements multi-nœuds** remplacent le backend par un backend réel via le rappel `configureClustering` sur `AddAuthagonal` / `AddAuthagonalCore` :

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS equivalent (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// Self-hosted PostgreSQL (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` enregistrent uniquement le bus d'événements, en conservant le bail en-processus, pour les nœuds qui doivent recevoir les événements du cluster mais ne doivent jamais se disputer le leadership.

Voir [Mise à l'échelle](scaling) pour le comportement du leadership et du bus d'événements entre les instances.

## En-têtes transférés (proxy de confiance)

Authagonal indexe la limitation de débit et le verrouillage de compte sur l'IP du client, et n'émet HSTS que sur les requêtes HTTPS. Derrière un reverse proxy / ingress, l'IP réelle du client et le schéma arrivent dans les en-têtes `X-Forwarded-For` / `X-Forwarded-Proto`. Ces paramètres contrôlent **quels sauts de proxy sont de confiance** pour définir ces valeurs, afin qu'un appelant ne puisse pas usurper `X-Forwarded-For` pour falsifier l'IP du client.

| Paramètre | Variable d'env | Défaut | Description |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | Nombre de sauts de proxy à honorer depuis la droite de la chaîne `X-Forwarded-For`. La valeur par défaut de `1` ne fait confiance qu'au seul saut que votre ingress ajoute et ignore tout ce qui se trouve plus à gauche dans la chaîne. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (tableau) | *(vide)* | Plages CIDR (tableau de chaînes, par exemple `"10.0.0.0/8"`) autorisées à définir les en-têtes transférés. Définissez-la sur le CIDR de votre proxy / ingress / pods. C'est cette déclaration qui permet à `X-Forwarded-Proto` d'être pris en compte — voir ci-dessous. |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0` (tableau) | *(vide)* | Adresses IP de proxy individuelles (tableau de chaînes) autorisées à définir les en-têtes transférés. À utiliser en complément ou à la place de `KnownNetworks`. |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

### Les deux en-têtes ne bénéficient pas de la même confiance

`X-Forwarded-For` ajuste l'**IP du client** : la clé dont dépendent la limitation de débit, le verrouillage de compte et la garde `/_internal`. Si rien n'est déclaré, Authagonal l'accepte depuis la boucle locale et les plages RFC1918, et journalise un avertissement. C'est une valeur par défaut au mieux, et elle vaut mieux que le comportement du framework avec un ensemble de confiance vide, qui consiste à accepter l'en-tête de *n'importe quel* appelant.

`X-Forwarded-Proto` change le **schéma**, et le schéma décide si `/connect/*` répond tout court (RFC 6749 §3.1/§3.2), si les cookies sont marqués `Secure`, et si les URL absolues générées sont en https. Il n'est accepté **que** depuis un proxy que vous avez déclaré dans `KnownNetworks` / `KnownProxies`. Une adresse privée n'est pas une déclaration : Authagonal est livré comme bibliothèque et ne peut pas voir le réseau sur lequel il a été déployé, si bien que « le pair porte une adresse privée » est une supposition sur la topologie. Sur un LAN à plat, un VPC partagé ou un bridge de conteneurs partagé, chaque charge de travail voisine se trouve dans ces plages et pourrait affirmer `https` pour une requête arrivée en clair.

**Si votre proxy n'a pas d'adresse fixe** — un ingress Kubernetes, un répartiteur de charge tournant, une plateforme qui ne vous donnera pas le CIDR du saut — déclarez tout pair comme proxy :

```json
{
  "ForwardedHeaders": {
    "KnownNetworks": ["0.0.0.0/0", "::/0"]
  }
}
```

C'est sûr précisément quand rien d'autre que le proxy ne peut atteindre le processus, ce qui est l'hypothèse sur laquelle un tel déploiement repose déjà. L'écrire la place là où elle peut être relue, plutôt que de laisser la bibliothèque la deviner. Si d'autres charges de travail *peuvent* atteindre Kestrel directement, elles pourront avec ce réglage usurper le schéma et l'IP du client : épinglez alors le CIDR réel.

> ⚠️ **Proxy terminant le TLS requis, et il doit être déclaré.** Authagonal doit s'exécuter derrière un reverse proxy terminant le TLS (ou terminer le TLS lui-même). HSTS (`Strict-Transport-Security`) n'est émis que sur les requêtes HTTPS, et les points d'accès OAuth refusent catégoriquement les requêtes en clair sauf si `Auth:AllowInsecureHttp` est activé — le proxy doit donc transférer `X-Forwarded-Proto: https` **et** être nommé dans `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` pour que HSTS soit envoyé et que `/connect/*` réponde tout court. Ne rien déclarer est l'échec de mise à niveau le plus courant : l'en-tête arrive, rien n'est habilité à l'appliquer, et chaque requête `/connect/*` répond 400 sur un déploiement qui est pourtant bel et bien en TLS. Le journal de démarrage le dit, et le corps du refus aussi.

## Limitation de débit

Les limites de débit intégrées protègent les points d'accès exposés aux abus :

| Point d'accès | Limite | Fenêtre | Indexé sur |
|---|---|---|---|
| `POST /api/auth/register` | 5 (`Auth:MaxRegistrationsPerIp`) | 1 heure (`Auth:RegistrationWindowMinutes`) | IP du client |
| `POST /api/auth/forgot-password` | 3 (`Auth:MaxPasswordResetsPerEmail`) | 1 heure (`Auth:PasswordResetWindowMinutes`) | Email cible |
| `POST /connect/register` (lorsqu'activé) | 10 | 1 heure | IP du client |
| Points d'accès SCIM | 200 | 1 minute | Client SCIM |

Les limites sont appliquées **en-processus par nœud** (derrière le seam `IRateLimiter`), donc avec N instances le plafond effectif est de N fois la valeur configurée. Traitez-les comme un filet de sécurité et appliquez la limite globale faisant autorité en périphérie (WAF / ingress / CDN). Voir [Mise à l'échelle](scaling#rate-limiting).

## CORS

CORS est configuré dynamiquement et **délimité par chemin** : la description précédente en une ligne (« les origines de tous les clients enregistrés sont automatiquement autorisées ») décrivait bien plus que ce que fait réellement le fournisseur.

- **Les origines enregistrées sur un client** (`AllowedCorsOrigins`) ne sont honorées que sous `/connect/` et `/.well-known/`. Elles n'ouvrent **pas** `/api/auth/`, `/api/v1/` ni `/scim/`. Un client désactivé n'apporte rien, et une origine mal formée est écartée.
- **Les credentials ne sont jamais autorisés** sous `/api/auth/`, `/api/v1/`, `/scim/`, `/consent` ou `/approvals`, pour aucune origine — configurée par l'opérateur ou enregistrée par un client. Un client navigateur qui appelle ces chemins avec `credentials: 'include'` depuis une autre origine échouera quelle que soit la configuration ; utilisez un backend-for-frontend (voir le paquet `@authagonal/bff`) plutôt que des appels cross-origin avec credentials.
- Les policies résolues sont mises en cache pendant 60 minutes.

Une origine ajoutée aux `AllowedCorsOrigins` d'un client fait donc fonctionner `/connect/*` et ne fait pas fonctionner `/api/v1/*`. C'est délibéré : ces chemins portent le cookie de session et la surface d'administration.

## HashiCorp Vault Transit

Authagonal peut signer les JWT en utilisant le moteur de secrets Transit de HashiCorp Vault. Les clés privées ne quittent jamais Vault : seule l'opération de signature est déléguée à distance. Les clés publiques sont mises en cache localement pour la vérification.

Ceci se configure par programmation lors de l'hébergement en tant que bibliothèque. Voir [Extensibilité](extensibility) pour plus de détails.

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
