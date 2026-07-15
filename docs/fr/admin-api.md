---
layout: default
title: API d'administration
locale: fr
---

# API d'administration

Les points d'accès d'administration nécessitent un jeton d'accès JWT avec le scope `authagonal-admin` (configurable via `AdminApi:Scope`).

Tous les points d'accès sont sous `/api/v1/`.

## Amorçage du premier jeton d'administration

Chaque point d'accès `/api/v1/*` exige un jeton bearer portant le scope d'administration, mais l'API d'administration elle-même (ainsi que l'[enregistrement dynamique de clients](client-registration)) **refuse de créer ou de mettre à jour tout client détenant ce scope** (`403 forbidden_scope`) : un client créé à l'exécution ne peut donc jamais s'élever au rang d'administrateur. La seule façon d'émettre un jeton d'administration est un **client amorcé par la configuration** : les entrées de la section de configuration `Clients:` sont insérées ou mises à jour au démarrage par `ClientSeedService`, et la configuration est de confiance (la protection contre le scope interdit ne s'applique qu'aux API d'exécution).

Amorcez un client `client_credentials` avec le scope d'administration dans `appsettings.json` (ou les variables d'environnement / le magasin de secrets équivalents) :

```json
{
  "Clients": [
    {
      "Id": "admin-cli",
      "Name": "Admin CLI",
      "ClientSecret": "a-long-random-secret",
      "GrantTypes": ["client_credentials"],
      "Scopes": ["authagonal-admin"]
    }
  ]
}
```

(`ClientSecret` est haché au démarrage ; fournissez plutôt `SecretHashes` si vous préférez ne conserver qu'une valeur pré-hachée dans la configuration. `ClientId`/`ClientName`/`AllowedGrantTypes`/`AllowedScopes` sont acceptés comme alias de `Id`/`Name`/`GrantTypes`/`Scopes`.)

Échangez ensuite les identifiants contre un jeton au point d'accès de jeton standard :

```bash
curl -X POST https://auth.example.com/connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=admin-cli" \
  -d "client_secret=a-long-random-secret" \
  -d "scope=authagonal-admin"
```

```json
{ "access_token": "eyJhbGci...", "token_type": "Bearer", "expires_in": 1800, "scope": "authagonal-admin" }
```

L'octroi `client_credentials` valide le scope demandé par rapport aux `AllowedScopes` du client : comme le client amorcé détient `authagonal-admin`, le jeton est émis. Utilisez-le sous la forme `Authorization: Bearer {access_token}` sur chaque appel d'administration :

```bash
curl https://auth.example.com/api/v1/clients -H "Authorization: Bearer eyJhbGci..."
```

Conservez le secret du client amorcé dans le magasin de secrets de votre déploiement ; sa rotation est un changement de configuration suivi d'un redémarrage.

## Utilisateurs

### Obtenir un utilisateur

```
GET /api/v1/profile/{userId}
```

Renvoie les détails de l'utilisateur, y compris les liens de connexion externe.

### Vérifier l'existence d'un utilisateur

```
GET /api/v1/profile/{userId}/exists
```

Renvoie `204` si l'utilisateur existe, `404` sinon (une sonde d'existence peu coûteuse, sans corps de réponse).

### Enregistrer un utilisateur

```
POST /api/v1/profile/
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Crée un utilisateur et envoie un email de vérification. Renvoie `409 user_exists` si l'email est déjà pris.

Champs optionnels réservés à l'administration : `userId` (identifiant fourni par l'appelant, `409 user_id_in_use` en cas de collision), `emailConfirmed` (crée l'utilisateur déjà vérifié, en sautant l'email de vérification), `companyName`, `organizationId`, `phone`, `locale`, et `customAttributes` (une table de chaînes conservée sur l'utilisateur et transmise aux cibles de provisionnement).

### Mettre à jour un utilisateur

```
PUT /api/v1/profile/
Content-Type: application/json

{
  "userId": "user-id",
  "firstName": "Jane",
  "lastName": "Smith",
  "organizationId": "new-org-id"
}
```

`userId` est requis ; tous les autres champs sont optionnels, seuls les champs fournis sont mis à jour. Le changement de `organizationId` déclenche :
- La rotation du SecurityStamp (invalide toutes les sessions par cookie dans les 30 minutes)
- La révocation de tous les jetons de rafraîchissement

### Supprimer un utilisateur

```
DELETE /api/v1/profile/{userId}
```

Supprime l'utilisateur, révoque tous les octrois et déprovisionne de toutes les applications en aval (au mieux).

### Confirmer l'email

```
POST /api/v1/profile/confirm-email?token={token}
```

### Envoyer un email de vérification

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Lier une identité externe

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### Délier une identité externe

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

## Gestion MFA

### Obtenir le statut MFA

```
GET /api/v1/profile/{userId}/mfa
```

Renvoie le statut MFA et les méthodes inscrites pour un utilisateur.

### Réinitialiser tout le MFA

```
DELETE /api/v1/profile/{userId}/mfa
```

Supprime tous les identifiants MFA et définit `MfaEnabled=false`. L'utilisateur devra se réinscrire si requis.

### Supprimer un identifiant MFA spécifique

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Supprime un identifiant MFA spécifique (par exemple, un authentificateur perdu). Si la dernière méthode principale est supprimée, le MFA est désactivé.

## Fournisseurs SSO

### Fournisseurs SAML

```
POST   /api/v1/saml/connections                    # Créer
GET    /api/v1/saml/connections/{connectionId}     # Obtenir un
PUT    /api/v1/saml/connections/{connectionId}     # Mettre à jour (partiel : seuls les champs fournis changent)
DELETE /api/v1/saml/connections/{connectionId}     # Supprimer
```

La création exige `connectionName`, `entityId`, et **exactement l'un des deux** : `metadataLocation` (une URL de métadonnées) ou `metadataXml` (métadonnées d'IdP collées, pour les IdP sans URL de métadonnées ; elles sont validées à l'analyse et condensées lors de l'enregistrement). Optionnel : `nameIdFormat` (à omettre pour la valeur par défaut emailAddress, `"none"` pour omettre NameIDPolicy, recommandé pour ADFS, ou une URN de format NameID), `signAuthnRequests`, `iconUrl`, `allowedDomains`, `disableJitProvisioning`. Chaque connexion reçoit une paire de clés SP générée par le serveur ; elle n'est jamais renvoyée par l'API. Voir [SAML](saml) pour les détails.

### Fournisseurs OIDC

```
POST   /api/v1/oidc/connections                    # Créer
GET    /api/v1/oidc/connections/{connectionId}     # Obtenir un
DELETE /api/v1/oidc/connections/{connectionId}     # Supprimer
```

La création exige `connectionName`, `metadataLocation`, `clientId`, `clientSecret`, `redirectUrl`. Optionnel : `iconUrl`, `allowedDomains`, `passthroughParams`. Le secret du client est protégé au repos et jamais renvoyé. Voir [Fédération OIDC](oidc-federation).

### Domaines SSO

```
GET    /api/v1/sso/domains                 # Lister tous
```

## Clients

Gérez les clients OAuth à l'exécution. Toutes les routes nécessitent la politique `IdentityAdmin` (le scope d'administration).

```
GET    /api/v1/clients              # Lister tous les clients
GET    /api/v1/clients/{clientId}   # Obtenir un client
POST   /api/v1/clients              # Créer un client
PUT    /api/v1/clients/{clientId}   # Mettre à jour un client
DELETE /api/v1/clients/{clientId}   # Supprimer un client
```

### Créer / Mettre à jour un client

```
POST /api/v1/clients
Content-Type: application/json

{
  "clientId": "my-app",
  "clientName": "My Application",
  "allowedGrantTypes": ["authorization_code"],
  "redirectUris": ["https://app.example.com/callback"],
  "allowedScopes": ["openid", "profile", "email"]
}
```

`POST` renvoie `409` si le client existe déjà. `PUT` met à jour un client existant (`404` s'il est introuvable) ; lors de la mise à jour, seuls les scopes nouvellement ajoutés font l'objet d'une vérification d'escalade.

Remarques :

- **Les empreintes de secret ne sont jamais renvoyées.** `clientSecretHashes` est retiré de chaque réponse (liste, obtention, création, mise à jour). Lors de la mise à jour, omettre `clientSecretHashes` conserve le secret stocké ; fournir de nouvelles empreintes le fait tourner.
- **Le scope d'administration ne peut pas être accordé à un client.** Demander `AdminApi:Scope` (par défaut `authagonal-admin`) dans `allowedScopes` renvoie `403 forbidden_scope` : aucun client ne peut détenir le scope d'administration, sinon un client `client_credentials` pourrait émettre des jetons d'administration indéfiniment.
- Ajouter des scopes que l'appelant n'est pas autorisé à accorder renvoie `403`.

## Scopes

Gérez les scopes OAuth personnalisés à l'exécution. Voir [Scopes OAuth](scopes) pour le modèle de scope complet.

```
GET    /api/v1/scopes           # Lister tous les scopes
GET    /api/v1/scopes/{name}    # Obtenir un scope
POST   /api/v1/scopes           # Créer un scope
PUT    /api/v1/scopes/{name}    # Mettre à jour un scope (seuls les champs fournis changent)
DELETE /api/v1/scopes/{name}    # Supprimer un scope
```

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing — read-only",
  "description": "View invoices and payment history",
  "userClaims": ["billing_plan"]
}
```

Renvoie `201` à la création (`409` si le scope existe déjà), le JSON du scope à l'obtention/mise à jour, et `204` à la suppression.

## Applications de provisionnement

Gérez les cibles de provisionnement en aval à l'exécution. Toutes les routes nécessitent la politique `IdentityAdmin`.

```
GET    /api/v1/provisioning/apps               # Lister les applications (renvoie aussi la limite configurée)
POST   /api/v1/provisioning/apps               # Créer une application
PUT    /api/v1/provisioning/apps/{appId}       # Mettre à jour une application
DELETE /api/v1/provisioning/apps/{appId}       # Supprimer une application
POST   /api/v1/provisioning/apps/{appId}/test  # Envoyer un appel /try de test vers le callback de l'application
```

### Créer / Mettre à jour une application de provisionnement

```
POST /api/v1/provisioning/apps
Content-Type: application/json

{
  "name": "Backend",
  "callbackUrl": "https://api.example.com/provisioning",
  "apiKey": "secret-api-key",
  "tryTimeoutSeconds": 30
}
```

- `name` et `callbackUrl` sont requis ; `callbackUrl` doit être une URL `http(s)` absolue.
- `tryTimeoutSeconds` est borné à la plage 5–300.
- **La clé API n'est jamais renvoyée.** Les réponses exposent `hasApiKey` (un booléen) au lieu de la clé elle-même. Lors de la mise à jour, omettre `apiKey` la laisse inchangée, une chaîne vide l'efface, et une valeur la remplace.
- La création est soumise à un quota configurable par déploiement (`IProvisioningAppQuota`) ; le dépassement renvoie `400 provisioning_app_limit`. La réponse de liste inclut la `limit` actuelle.

### Tester une application de provisionnement

```
POST /api/v1/provisioning/apps/{appId}/test
```

Envoie un `POST {callbackUrl}/try` synthétique avec une charge utile d'exemple (et la clé API de l'application comme jeton bearer si elle est définie) et renvoie `{ success, statusCode, body }` afin que vous puissiez vérifier la connectivité depuis l'interface d'administration.

## Rôles

### Lister les rôles

```
GET /api/v1/roles
```

### Obtenir un rôle

```
GET /api/v1/roles/{roleId}
```

### Créer un rôle

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### Mettre à jour un rôle

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### Supprimer un rôle

```
DELETE /api/v1/roles/{roleId}
```

### Assigner un rôle à un utilisateur

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

L'assignation se fait par **nom de rôle**, non par identifiant de rôle. Renvoie la liste mise à jour des rôles de l'utilisateur.

### Retirer un rôle d'un utilisateur

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

### Obtenir les rôles d'un utilisateur

```
GET /api/v1/roles/user/{userId}
```

## Jetons SCIM

### Générer un jeton

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id",
  "description": "Entra provisioning",
  "expiresInDays": 365
}
```

`description` et `expiresInDays` sont optionnels (omettez `expiresInDays` pour un jeton sans expiration). Renvoie le jeton brut une seule fois. Stockez-le en sécurité : il ne peut pas être récupéré à nouveau.

### Lister les jetons

```
GET /api/v1/scim/tokens?clientId=client-id
```

Renvoie les métadonnées des jetons (identifiant, date de création) sans la valeur brute du jeton.

### Révoquer un jeton

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## Jetons

### Usurper l'identité d'un utilisateur

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

Émet des jetons (accès, rafraîchissement, et, lorsque `openid` est demandé, jeton d'identité) au nom d'un utilisateur sans nécessiter ses identifiants. Utile pour les tests et le support. Les paramètres sont passés en tant que chaînes de requête.

| Paramètre de requête | Requis | Description |
|---|---|---|
| `clientId` | Oui | Le client pour lequel les jetons sont émis. Les durées de vie des jetons proviennent de la configuration de ce client. |
| `userId` | Oui | L'utilisateur à usurper. |
| `scopes` | Non | Liste de scopes **séparés par des espaces** (encodez les espaces en URL). Par défaut, les `AllowedScopes` du client lorsqu'il est omis. |

Restrictions :

- Les scopes sont limités aux `AllowedScopes` du client : demander un scope que le client ne pourrait pas lui-même demander renvoie `400 invalid_scope`.
- Le scope d'administration (`AdminApi:Scope`, par défaut `authagonal-admin`) **ne peut pas** être émis via ce point d'accès ; le demander renvoie `403 forbidden_scope`. Cela empêche un jeton d'administration (éventuellement à durée limitée) d'émettre un jeton d'accès/rafraîchissement d'administration de longue durée.

La réponse est une réponse de jeton standard avec `access_token`, `refresh_token`, `id_token` optionnel, `expires_in`, et le `scope` accordé (séparé par des espaces).
