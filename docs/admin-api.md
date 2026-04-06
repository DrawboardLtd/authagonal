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

## MFA Management

### Get MFA Status

```
GET /api/v1/profile/{userId}/mfa
```

Returns MFA status and enrolled methods for a user.

### Reset All MFA

```
DELETE /api/v1/profile/{userId}/mfa
```

Removes all MFA credentials and sets `MfaEnabled=false`. The user will need to re-enroll if required.

### Remove Specific MFA Credential

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Removes a specific MFA credential (e.g., a lost authenticator). If the last primary method is removed, MFA is disabled.

## SSO Providers

### SAML Providers

```
POST   /api/v1/saml/connections                    # Create
GET    /api/v1/saml/connections/{connectionId}     # Get one
PUT    /api/v1/saml/connections/{connectionId}     # Update
DELETE /api/v1/saml/connections/{connectionId}     # Delete
```

### OIDC Providers

```
POST   /api/v1/oidc/connections                    # Create
GET    /api/v1/oidc/connections/{connectionId}     # Get one
DELETE /api/v1/oidc/connections/{connectionId}     # Delete
```

### SSO Domains

```
GET    /api/v1/sso/domains                 # List all
```

## Roles

### List Roles

```
GET /api/v1/roles
```

### Get Role

```
GET /api/v1/roles/{roleId}
```

### Create Role

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### Update Role

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### Delete Role

```
DELETE /api/v1/roles/{roleId}
```

### Assign Role to User

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleId": "role-id"
}
```

### Unassign Role from User

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleId": "role-id"
}
```

### Get User's Roles

```
GET /api/v1/roles/user/{userId}
```

## SCIM Tokens

### Generate Token

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id"
}
```

Returns the raw token once. Store it securely — it cannot be retrieved again.

### List Tokens

```
GET /api/v1/scim/tokens?clientId=client-id
```

Returns token metadata (ID, created date) without the raw token value.

### Revoke Token

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## Tokens

### Impersonate User

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid,profile
```

Issues tokens on behalf of a user without requiring their credentials. Useful for testing and support. Parameters are passed as query strings. Optional `refreshTokenLifetime` parameter controls refresh token validity.
