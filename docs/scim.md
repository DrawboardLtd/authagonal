---
layout: default
title: SCIM 2.0 Provisioning
nav_order: 13
---

# SCIM 2.0 Provisioning

Authagonal supports SCIM 2.0 (System for Cross-domain Identity Management) for automated user provisioning from enterprise identity providers such as Microsoft Entra ID, Okta, and OneLogin.

## Overview

SCIM is an inbound provisioning protocol: your identity provider pushes user and group changes to Authagonal. This is complementary to the existing TCC (Try-Confirm-Cancel) outbound provisioning that pushes users to downstream applications.

**Supported operations:**
- User CRUD (create, read, update, delete via soft deactivation)
- Group CRUD with member management
- Filtering (`eq` and `co` operators on `userName`, `externalId`, `displayName`)
- Pagination via `startIndex` and `count`
- PATCH for partial updates (including `active=false` deactivation)

**Not supported:** bulk operations, sorting, ETags, password management via SCIM.

## Generating a SCIM Token

SCIM endpoints are authenticated with static Bearer tokens. Generate tokens via the Admin API:

```http
POST /api/v1/scim/tokens
Authorization: Bearer {admin-token}
Content-Type: application/json

{
  "clientId": "your-client-id",
  "description": "Entra ID SCIM token",
  "expiresInDays": 365
}
```

The response includes the raw token **once** — store it securely:

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z"
}
```

### Listing tokens

```http
GET /api/v1/scim/tokens?clientId=your-client-id
Authorization: Bearer {admin-token}
```

### Revoking a token

```http
DELETE /api/v1/scim/tokens/{tokenId}?clientId=your-client-id
Authorization: Bearer {admin-token}
```

## Configuring Your Identity Provider

### Tenant URL

```
https://your-authagonal-instance/scim/v2
```

### Authentication

Use **OAuth Bearer Token** with the token generated above.

### Microsoft Entra ID

1. In Azure portal, go to **Enterprise Applications** > your app > **Provisioning**
2. Set Provisioning Mode to **Automatic**
3. Enter Tenant URL: `https://your-instance/scim/v2`
4. Enter Secret Token: the raw token from the generation step
5. Click **Test Connection** to verify
6. Configure attribute mappings (see below)

### Okta

1. In Okta admin console, go to **Applications** > your app > **Provisioning**
2. Enable **SCIM connector**
3. Set Base URL: `https://your-instance/scim/v2`
4. Set Authentication Mode: **HTTP Header**
5. Enter the Bearer token

### OneLogin

1. In OneLogin admin, go to **Applications** > your app > **Provisioning**
2. Enable provisioning
3. Set SCIM Base URL: `https://your-instance/scim/v2`
4. Set SCIM Bearer Token

## SCIM Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/scim/v2/Users` | List/filter users |
| GET | `/scim/v2/Users/{id}` | Get a user |
| POST | `/scim/v2/Users` | Create a user |
| PUT | `/scim/v2/Users/{id}` | Replace a user |
| PATCH | `/scim/v2/Users/{id}` | Partial update |
| DELETE | `/scim/v2/Users/{id}` | Soft deactivate |
| GET | `/scim/v2/Groups` | List/filter groups |
| GET | `/scim/v2/Groups/{id}` | Get a group |
| POST | `/scim/v2/Groups` | Create a group |
| PUT | `/scim/v2/Groups/{id}` | Replace a group |
| PATCH | `/scim/v2/Groups/{id}` | Add/remove members |
| DELETE | `/scim/v2/Groups/{id}` | Delete a group |
| GET | `/scim/v2/ServiceProviderConfig` | Capabilities |
| GET | `/scim/v2/Schemas` | Schema definitions |
| GET | `/scim/v2/ResourceTypes` | Resource types |

## Attribute Mapping

### User attributes

| SCIM Attribute | Authagonal Field |
|---------------|------------------|
| `userName` | `Email` |
| `name.givenName` | `FirstName` |
| `name.familyName` | `LastName` |
| `displayName` | `FirstName LastName` |
| `emails[type eq "work"].value` | `Email` |
| `active` | `IsActive` |
| `externalId` | `ExternalId` |

### Group attributes

| SCIM Attribute | Authagonal Field |
|---------------|------------------|
| `displayName` | `DisplayName` |
| `externalId` | `ExternalId` |
| `members` | `MemberUserIds` |

## Behavior Details

### User creation
- SCIM-provisioned users are created with `EmailConfirmed = true` (SSO-only, no password).
- The `ScimProvisionedByClientId` field tracks which SCIM client created the user.
- If the client has `ProvisioningApps` configured, TCC provisioning is triggered automatically.

### User deactivation
- `DELETE /scim/v2/Users/{id}` performs a **soft delete** by setting `IsActive = false`.
- `PATCH` with `active = false` also deactivates the user.
- Deactivated users cannot log in via password, SAML, or OIDC.
- All refresh tokens are revoked upon deactivation.
- Deprovisioning is triggered for downstream apps.

### Filtering
Supported filter expressions:
- `userName eq "user@example.com"`
- `externalId eq "12345"`
- `displayName co "John"`

Only single-attribute filters are supported. Complex boolean expressions (`and`, `or`) are not supported.

## Known Limitations

- **No bulk operations** — users and groups must be provisioned individually.
- **No sorting** — results are ordered by creation date.
- **Filter subset** — only `eq` and `co` operators on `userName`, `externalId`, and `displayName`.
- **No password management** — SCIM-provisioned users authenticate via SSO only.
- **Soft delete only** — `DELETE` deactivates rather than permanently removes users.
