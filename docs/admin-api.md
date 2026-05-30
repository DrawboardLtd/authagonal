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

## Clients

Manage OAuth clients at runtime. All routes require the `IdentityAdmin` policy (the admin scope).

```
GET    /api/v1/clients              # List all clients
GET    /api/v1/clients/{clientId}   # Get one client
POST   /api/v1/clients              # Create a client
PUT    /api/v1/clients/{clientId}   # Update a client
DELETE /api/v1/clients/{clientId}   # Delete a client
```

### Create / Update Client

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

`POST` returns `409` if the client already exists. `PUT` updates an existing client (`404` if not found); on update, only newly-added scopes are escalation-checked.

Notes:

- **Secret hashes are never returned.** `clientSecretHashes` is stripped from every response (list, get, create, update). On update, omitting `clientSecretHashes` preserves the stored secret; supplying new hashes rotates it.
- **The admin scope cannot be granted to a client.** Requesting `AdminApi:Scope` (default `authagonal-admin`) in `allowedScopes` returns `403 forbidden_scope` — no client may hold the admin scope, otherwise a `client_credentials` client could mint admin tokens indefinitely.
- Adding scopes the caller is not permitted to grant returns `403`.

## Scopes

Manage custom OAuth scopes at runtime. See [OAuth Scopes](scopes) for the full scope model.

```
GET    /api/v1/scopes           # List all scopes
GET    /api/v1/scopes/{name}    # Get one scope
POST   /api/v1/scopes           # Create a scope
PUT    /api/v1/scopes/{name}    # Update a scope (only supplied fields change)
DELETE /api/v1/scopes/{name}    # Delete a scope
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

Returns `201` on create (`409` if the scope already exists), the scope JSON on get/update, and `204` on delete.

## Provisioning Apps

Manage downstream provisioning targets at runtime. All routes require the `IdentityAdmin` policy.

```
GET    /api/v1/provisioning/apps               # List apps (also returns the configured limit)
POST   /api/v1/provisioning/apps               # Create an app
PUT    /api/v1/provisioning/apps/{appId}       # Update an app
DELETE /api/v1/provisioning/apps/{appId}       # Delete an app
POST   /api/v1/provisioning/apps/{appId}/test  # Send a test /try call to the app's callback
```

### Create / Update Provisioning App

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

- `name` and `callbackUrl` are required; `callbackUrl` must be an absolute `http(s)` URL.
- `tryTimeoutSeconds` is clamped to the range 5–300.
- **The API key is never returned.** Responses expose `hasApiKey` (a boolean) instead of the key itself. On update, omitting `apiKey` leaves it unchanged, an empty string clears it, and a value replaces it.
- Creation is subject to a configurable per-deployment quota (`IProvisioningAppQuota`); exceeding it returns `400 provisioning_app_limit`. The list response includes the current `limit`.

### Test a Provisioning App

```
POST /api/v1/provisioning/apps/{appId}/test
```

Sends a synthetic `POST {callbackUrl}/try` with a sample payload (and the app's API key as a bearer token if set) and returns `{ success, statusCode, body }` so you can verify connectivity from the admin UI.

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
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

Issues tokens (access, refresh, and — when `openid` is requested — id token) on behalf of a user without requiring their credentials. Useful for testing and support. Parameters are passed as query strings.

| Query parameter | Required | Description |
|---|---|---|
| `clientId` | Yes | The client the tokens are issued for. Token lifetimes come from this client's configuration. |
| `userId` | Yes | The user to impersonate. |
| `scopes` | No | **Space-separated** list of scopes (URL-encode the spaces). Defaults to the client's `AllowedScopes` when omitted. |

Restrictions:

- Scopes are constrained to the client's `AllowedScopes` — requesting any scope the client could not itself request returns `400 invalid_scope`.
- The admin scope (`AdminApi:Scope`, default `authagonal-admin`) **cannot** be issued through this endpoint; requesting it returns `403 forbidden_scope`. This prevents a (possibly time-limited) admin token from minting a long-lived admin access/refresh token.

The response is a standard token response with `access_token`, `refresh_token`, optional `id_token`, `expires_in`, and the granted `scope` (space-separated).
