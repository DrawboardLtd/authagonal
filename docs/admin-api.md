---
layout: default
title: Admin API
---

# Admin API

Admin endpoints require a JWT access token with the `authagonal-admin` scope (configurable via `AdminApi:Scope`).

All endpoints are under `/api/v1/`.

## Users

### Get User

```
GET /api/v1/profile/{userId}
```

Returns user details including external login links.

### Register User

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

Creates a user and sends a verification email. Returns `409` if the email is already taken.

### Update User

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

All fields are optional — only provided fields are updated. Changing `organizationId` triggers:
- SecurityStamp rotation (invalidates all cookie sessions within 30 minutes)
- All refresh tokens revoked

### Delete User

```
DELETE /api/v1/profile/{userId}
```

Deletes the user, revokes all grants, and deprovisions from all downstream apps (best-effort).

### Confirm Email

```
POST /api/v1/profile/confirm-email?token={token}
```

### Send Verification Email

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Link External Identity

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### Unlink External Identity

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

## SSO Providers

### SAML Providers

```
GET    /api/v1/sso/saml                    # List all
GET    /api/v1/sso/saml/{connectionId}     # Get one
POST   /api/v1/sso/saml                    # Create
PUT    /api/v1/sso/saml/{connectionId}     # Update
DELETE /api/v1/sso/saml/{connectionId}     # Delete
```

### OIDC Providers

```
GET    /api/v1/sso/oidc                    # List all
GET    /api/v1/sso/oidc/{connectionId}     # Get one
POST   /api/v1/sso/oidc                    # Create
PUT    /api/v1/sso/oidc/{connectionId}     # Update
DELETE /api/v1/sso/oidc/{connectionId}     # Delete
```

### SSO Domains

```
GET    /api/v1/sso/domains                 # List all
GET    /api/v1/sso/domains/{domain}        # Get one
POST   /api/v1/sso/domains                 # Create
DELETE /api/v1/sso/domains/{domain}        # Delete
```

## Tokens

### Impersonate User

```
POST /api/v1/tokens/impersonate
Content-Type: application/json

{
  "userId": "user-id",
  "clientId": "client-id",
  "scopes": ["openid", "profile"]
}
```

Issues tokens on behalf of a user without requiring their credentials. Useful for testing and support.
