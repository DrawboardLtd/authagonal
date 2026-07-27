---
layout: default
title: Admin API
---

# Admin API

Admin endpoints require a JWT access token with the `authagonal-admin` scope (configurable via `AdminApi:Scope`).

All endpoints are under `/api/v1/`.

## Bootstrapping the first admin token

Every `/api/v1/*` endpoint requires a bearer token carrying the admin scope, but the admin API itself (and [dynamic client registration](client-registration)) **refuses to create or update any client holding that scope** (`403 forbidden_scope`), so a runtime-created client can never escalate to admin. The only way to mint an admin token is a **config-seeded client**: entries in the `Clients:` configuration section are upserted at startup by `ClientSeedService`, and config is trusted, the forbidden-scope guard applies only to the runtime APIs.

Seed a `client_credentials` client with the admin scope in `appsettings.json` (or the equivalent environment variables / secret store):

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

(`ClientSecret` is hashed at startup; supply `SecretHashes` instead if you prefer to keep only a pre-hashed value in config. `ClientId`/`ClientName`/`AllowedGrantTypes`/`AllowedScopes` are accepted as aliases for `Id`/`Name`/`GrantTypes`/`Scopes`.)

Then exchange the credentials for a token at the standard token endpoint:

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

The `client_credentials` grant validates the requested scope against the client's `AllowedScopes`, since the seeded client holds `authagonal-admin`, the token is issued. Use it as `Authorization: Bearer {access_token}` on every admin call:

```bash
curl https://auth.example.com/api/v1/clients -H "Authorization: Bearer eyJhbGci..."
```

Keep the seeded client's secret in your deployment's secret store; rotating it is a config change + restart.

## Users

### Get User

```
GET /api/v1/profile/{userId}
```

Returns user details including external login links.

### User Exists

```
GET /api/v1/profile/{userId}/exists
```

Returns `204` if the user exists, `404` otherwise (a cheap existence probe, no body).

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

Creates a user and sends a verification email. Returns `409 user_exists` if the email is already taken.

Optional admin-only fields: `userId` (caller-supplied id, `409 user_id_in_use` on collision), `emailConfirmed` (create the user already verified, skipping the verification email), `companyName`, `organizationId`, `phone`, `locale`, and `customAttributes` (a string map persisted on the user and forwarded to provisioning targets).

`skipProvisioning: true` creates the identity without running provisioning. It is for a first-party
app that is ITSELF a provisioning target and is already part-way through setting this user up: it is
calling here to mint the identity, not to be called back about a user it is in the middle of
creating. Without it that app receives its own Try for a half-built user, carrying only the
attributes that survived the round trip — and, if it recovers, ends up provisioning the user twice.

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

`userId` is required; every other field is optional, only provided fields are updated. Changing `organizationId` triggers:
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
PUT    /api/v1/saml/connections/{connectionId}     # Update (partial — only supplied fields change)
DELETE /api/v1/saml/connections/{connectionId}     # Delete
```

Create requires `connectionName`, `entityId`, and **exactly one of** `metadataLocation` (a metadata URL) or `metadataXml` (pasted IdP metadata, for IdPs without a metadata URL, it is parse-validated and condensed at save). Optional: `nameIdFormat` (omit for the emailAddress default, `"none"` to omit NameIDPolicy, recommended for ADFS, or a NameID format URN), `signAuthnRequests`, `iconUrl`, `allowedDomains`, `disableJitProvisioning`. Every connection gets a server-generated SP keypair; it is never returned by the API. See [SAML](saml) for details.

### OIDC Providers

```
POST   /api/v1/oidc/connections                    # Create
GET    /api/v1/oidc/connections/{connectionId}     # Get one
DELETE /api/v1/oidc/connections/{connectionId}     # Delete
```

Create requires `connectionName`, `metadataLocation`, `clientId`, `clientSecret`, `redirectUrl`. Optional: `iconUrl`, `allowedDomains`, `passthroughParams`. The client secret is protected at rest and never returned. See [OIDC Federation](oidc-federation).

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
- **The admin scope cannot be granted to a client.** Requesting `AdminApi:Scope` (default `authagonal-admin`) in `allowedScopes` returns `403 forbidden_scope`, no client may hold the admin scope, otherwise a `client_credentials` client could mint admin tokens indefinitely.
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
  "roleName": "admin"
}
```

Assignment is by **role name**, not role id. Returns the user's updated role list.

### Unassign Role from User

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

### Get User's Roles

```
GET /api/v1/roles/user/{userId}
```

### Users in a Role

```
GET /api/v1/roles/{roleName}/users?maxResults=200
```

The reverse of the above — who holds this role — answered from a role membership index rather than
by reading every user. Returns `{ "roleName": "...", "members": [ { "userId", "email", "firstName",
"lastName", "roles" } ] }`; each member carries their full role set, because a console listing one
role almost always wants to show what else its members have.

`404 role_not_found` for a role that does not exist, rather than an empty list — "nobody holds this"
and "you have misspelled the role" are different problems. `501 not_supported` if the configured
store does not index role membership, for the same reason: an empty membership list would read as
"nobody administers this".

Accounts written before the index existed are invisible to it until reindexed
(`IUserStore.ReindexUserAsync`, which upserts a user's memberships without removing any).

## SCIM Tokens

### Generate Token

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id",
  "description": "Entra provisioning",
  "expiresInDays": 365
}
```

`description` and `expiresInDays` are optional (omit `expiresInDays` for a non-expiring token). Returns the raw token once. Store it securely, it cannot be retrieved again.

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

Issues tokens (access, refresh, and, when `openid` is requested, id token) on behalf of a user without requiring their credentials. Useful for testing and support. Parameters are passed as query strings.

| Query parameter | Required | Description |
|---|---|---|
| `clientId` | Yes | The client the tokens are issued for. Token lifetimes come from this client's configuration. |
| `userId` | Yes | The user to impersonate. |
| `scopes` | No | **Space-separated** list of scopes (URL-encode the spaces). Defaults to the client's `AllowedScopes` when omitted. |

Restrictions:

- Scopes are constrained to the client's `AllowedScopes`, requesting any scope the client could not itself request returns `400 invalid_scope`.
- The admin scope (`AdminApi:Scope`, default `authagonal-admin`) **cannot** be issued through this endpoint; requesting it returns `403 forbidden_scope`. This prevents a (possibly time-limited) admin token from minting a long-lived admin access/refresh token.

The response is a standard token response with `access_token`, `refresh_token`, optional `id_token`, `expires_in`, and the granted `scope` (space-separated).
