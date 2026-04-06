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
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid,profile
```

Emet des jetons au nom d'un utilisateur sans necessiter ses identifiants. Utile pour les tests et le support. Les parametres sont passes en tant que chaines de requete. Le parametre optionnel `refreshTokenLifetime` controle la validite du jeton de rafraichissement.
