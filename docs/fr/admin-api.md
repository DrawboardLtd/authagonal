---
layout: default
title: API d'administration
locale: fr
---

# API d'administration

Les points d'acces d'administration necessitent un jeton d'acces JWT avec le scope `authagonal-admin` (configurable via `AdminApi:Scope`).

Tous les points d'acces sont sous `/api/v1/`.

## Utilisateurs

### Obtenir un utilisateur

```
GET /api/v1/profile/{userId}
```

Renvoie les details de l'utilisateur, y compris les liens de connexion externe.

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

Cree un utilisateur et envoie un email de verification. Renvoie `409` si l'email est deja pris.

### Mettre a jour un utilisateur

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

Tous les champs sont optionnels -- seuls les champs fournis sont mis a jour. Le changement de `organizationId` declenche :
- La rotation du SecurityStamp (invalide toutes les sessions par cookie dans les 30 minutes)
- La revocation de tous les jetons de rafraichissement

### Supprimer un utilisateur

```
DELETE /api/v1/profile/{userId}
```

Supprime l'utilisateur, revoque tous les octrois et deprovisionne de toutes les applications en aval (au mieux).

### Confirmer l'email

```
POST /api/v1/profile/confirm-email?token={token}
```

### Envoyer un email de verification

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Lier une identite externe

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### Delier une identite externe

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

## Gestion MFA

### Obtenir le statut MFA

```
GET /api/v1/profile/{userId}/mfa
```

Renvoie le statut MFA et les methodes inscrites pour un utilisateur.

### Reinitialiser tout le MFA

```
DELETE /api/v1/profile/{userId}/mfa
```

Supprime tous les identifiants MFA et definit `MfaEnabled=false`. L'utilisateur devra se reinscrire si requis.

### Supprimer un identifiant MFA specifique

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Supprime un identifiant MFA specifique (par exemple, un authentificateur perdu). Si la derniere methode principale est supprimee, le MFA est desactive.

## Fournisseurs SSO

### Fournisseurs SAML

```
POST   /api/v1/saml/connections                    # Creer
GET    /api/v1/saml/connections/{connectionId}     # Obtenir un
PUT    /api/v1/saml/connections/{connectionId}     # Mettre a jour
DELETE /api/v1/saml/connections/{connectionId}     # Supprimer
```

### Fournisseurs OIDC

```
POST   /api/v1/oidc/connections                    # Creer
GET    /api/v1/oidc/connections/{connectionId}     # Obtenir un
DELETE /api/v1/oidc/connections/{connectionId}     # Supprimer
```

### Domaines SSO

```
GET    /api/v1/sso/domains                 # Lister tous
```

## Clients

Gerez les clients OAuth a l'execution. Toutes les routes necessitent la politique `IdentityAdmin` (le scope d'administration).

```
GET    /api/v1/clients              # Lister tous les clients
GET    /api/v1/clients/{clientId}   # Obtenir un client
POST   /api/v1/clients              # Creer un client
PUT    /api/v1/clients/{clientId}   # Mettre a jour un client
DELETE /api/v1/clients/{clientId}   # Supprimer un client
```

### Creer / Mettre a jour un client

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

`POST` renvoie `409` si le client existe deja. `PUT` met a jour un client existant (`404` s'il est introuvable) ; lors de la mise a jour, seuls les scopes nouvellement ajoutes font l'objet d'une verification d'escalade.

Remarques :

- **Les empreintes de secret ne sont jamais renvoyees.** `clientSecretHashes` est retire de chaque reponse (liste, obtention, creation, mise a jour). Lors de la mise a jour, omettre `clientSecretHashes` conserve le secret stocke ; fournir de nouvelles empreintes le fait tourner.
- **Le scope d'administration ne peut pas etre accorde a un client.** Demander `AdminApi:Scope` (par defaut `authagonal-admin`) dans `allowedScopes` renvoie `403 forbidden_scope` — aucun client ne peut detenir le scope d'administration, sinon un client `client_credentials` pourrait emettre des jetons d'administration indefiniment.
- Ajouter des scopes que l'appelant n'est pas autorise a accorder renvoie `403`.

## Scopes

Gerez les scopes OAuth personnalises a l'execution. Voir [Scopes OAuth](scopes) pour le modele de scope complet.

```
GET    /api/v1/scopes           # Lister tous les scopes
GET    /api/v1/scopes/{name}    # Obtenir un scope
POST   /api/v1/scopes           # Creer un scope
PUT    /api/v1/scopes/{name}    # Mettre a jour un scope (seuls les champs fournis changent)
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

Renvoie `201` a la creation (`409` si le scope existe deja), le JSON du scope a l'obtention/mise a jour, et `204` a la suppression.

## Applications de provisionnement

Gerez les cibles de provisionnement en aval a l'execution. Toutes les routes necessitent la politique `IdentityAdmin`.

```
GET    /api/v1/provisioning/apps               # Lister les applications (renvoie aussi la limite configuree)
POST   /api/v1/provisioning/apps               # Creer une application
PUT    /api/v1/provisioning/apps/{appId}       # Mettre a jour une application
DELETE /api/v1/provisioning/apps/{appId}       # Supprimer une application
POST   /api/v1/provisioning/apps/{appId}/test  # Envoyer un appel /try de test vers le callback de l'application
```

### Creer / Mettre a jour une application de provisionnement

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

- `name` et `callbackUrl` sont requis ; `callbackUrl` doit etre une URL `http(s)` absolue.
- `tryTimeoutSeconds` est borne a la plage 5–300.
- **La cle API n'est jamais renvoyee.** Les reponses exposent `hasApiKey` (un booleen) au lieu de la cle elle-meme. Lors de la mise a jour, omettre `apiKey` la laisse inchangee, une chaine vide l'efface, et une valeur la remplace.
- La creation est soumise a un quota configurable par deploiement (`IProvisioningAppQuota`) ; le depassement renvoie `400 provisioning_app_limit`. La reponse de liste inclut la `limit` actuelle.

### Tester une application de provisionnement

```
POST /api/v1/provisioning/apps/{appId}/test
```

Envoie un `POST {callbackUrl}/try` synthetique avec une charge utile d'exemple (et la cle API de l'application comme jeton bearer si elle est definie) et renvoie `{ success, statusCode, body }` afin que vous puissiez verifier la connectivite depuis l'interface d'administration.

## Roles

### Lister les roles

```
GET /api/v1/roles
```

### Obtenir un role

```
GET /api/v1/roles/{roleId}
```

### Creer un role

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### Mettre a jour un role

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### Supprimer un role

```
DELETE /api/v1/roles/{roleId}
```

### Assigner un role a un utilisateur

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleId": "role-id"
}
```

### Retirer un role d'un utilisateur

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleId": "role-id"
}
```

### Obtenir les roles d'un utilisateur

```
GET /api/v1/roles/user/{userId}
```

## Jetons SCIM

### Generer un jeton

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id"
}
```

Renvoie le jeton brut une seule fois. Stockez-le en securite -- il ne peut pas etre recupere a nouveau.

### Lister les jetons

```
GET /api/v1/scim/tokens?clientId=client-id
```

Renvoie les metadonnees des jetons (identifiant, date de creation) sans la valeur brute du jeton.

### Revoquer un jeton

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## Jetons

### Usurper l'identite d'un utilisateur

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

Emet des jetons (acces, rafraichissement, et — lorsque `openid` est demande — jeton d'identite) au nom d'un utilisateur sans necessiter ses identifiants. Utile pour les tests et le support. Les parametres sont passes en tant que chaines de requete.

| Parametre de requete | Requis | Description |
|---|---|---|
| `clientId` | Oui | Le client pour lequel les jetons sont emis. Les durees de vie des jetons proviennent de la configuration de ce client. |
| `userId` | Oui | L'utilisateur a usurper. |
| `scopes` | Non | Liste de scopes **separes par des espaces** (encodez les espaces en URL). Par defaut, les `AllowedScopes` du client lorsqu'il est omis. |

Restrictions :

- Les scopes sont limites aux `AllowedScopes` du client — demander un scope que le client ne pourrait pas lui-meme demander renvoie `400 invalid_scope`.
- Le scope d'administration (`AdminApi:Scope`, par defaut `authagonal-admin`) **ne peut pas** etre emis via ce point d'acces ; le demander renvoie `403 forbidden_scope`. Cela empeche un jeton d'administration (eventuellement a duree limitee) d'emettre un jeton d'acces/rafraichissement d'administration de longue duree.

La reponse est une reponse de jeton standard avec `access_token`, `refresh_token`, `id_token` optionnel, `expires_in`, et le `scope` accorde (separe par des espaces).
