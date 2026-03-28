---
layout: default
title: API d'administration
locale: fr
---

# API d'administration

Les points d'acces d'administration necessitent un jeton d'acces JWT avec le scope `authagonal-admin`.

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

## Fournisseurs SSO

### Fournisseurs SAML

```
GET    /api/v1/sso/saml                    # Lister tous
GET    /api/v1/sso/saml/{connectionId}     # Obtenir un
POST   /api/v1/sso/saml                    # Creer
PUT    /api/v1/sso/saml/{connectionId}     # Mettre a jour
DELETE /api/v1/sso/saml/{connectionId}     # Supprimer
```

### Fournisseurs OIDC

```
GET    /api/v1/sso/oidc                    # Lister tous
GET    /api/v1/sso/oidc/{connectionId}     # Obtenir un
POST   /api/v1/sso/oidc                    # Creer
PUT    /api/v1/sso/oidc/{connectionId}     # Mettre a jour
DELETE /api/v1/sso/oidc/{connectionId}     # Supprimer
```

### Domaines SSO

```
GET    /api/v1/sso/domains                 # Lister tous
GET    /api/v1/sso/domains/{domain}        # Obtenir un
POST   /api/v1/sso/domains                 # Creer
DELETE /api/v1/sso/domains/{domain}        # Supprimer
```

## Jetons

### Usurper l'identite d'un utilisateur

```
POST /api/v1/tokens/impersonate
Content-Type: application/json

{
  "userId": "user-id",
  "clientId": "client-id",
  "scopes": ["openid", "profile"]
}
```

Emet des jetons au nom d'un utilisateur sans necessiter ses identifiants. Utile pour les tests et le support.
