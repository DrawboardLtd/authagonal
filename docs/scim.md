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
- Pagination: cursor-based for user listings (`cursor`/`nextCursor`), `startIndex` and `count` for groups
- PATCH for partial updates (including `active=false` deactivation)
- Group-to-role mapping resolved at token issuance

**Not supported:** bulk operations, sorting, ETags, password management via SCIM.

All resources are scoped to the SCIM client that provisioned them: a user or group created by one SCIM token's client is invisible (404) to every other SCIM client.

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

The response includes the raw token **once**. It is stored as a SHA-256 hash and cannot be recovered later, so store it securely:

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "description": "Entra ID SCIM token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z"
}
```

Omit `expiresInDays` (or pass `0`) for a non-expiring token.

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

Every endpoint is also mapped without the `/v2` segment (e.g. `/scim/Users`) for identity providers that append their own path. The discovery endpoints (`ServiceProviderConfig`, `Schemas`, `ResourceTypes`, and the bare `/scim/` and `/scim/v2/` base URLs, which return the ServiceProviderConfig) are anonymous; everything else requires a SCIM Bearer token.

User endpoints are rate-limited to 200 requests per minute per SCIM client; excess requests receive a SCIM error with status `429`.

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
| `preferredLanguage` (falling back to `locale`) | `Locale` |

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
- If the client has `ProvisioningApps` configured, TCC provisioning is triggered automatically. If provisioning rejects the user, the SCIM create is rolled back with a `422` response.
- Creating a user whose `userName` or `externalId` already exists returns a SCIM `409` conflict. Email changes via PUT or PATCH are conflict-checked the same way.

### User deactivation
- `DELETE /scim/v2/Users/{id}` performs a **soft delete** by setting `IsActive = false`. The user record is kept: a subsequent `GET /scim/v2/Users/{id}` still returns it (with `active: false`) rather than a 404.
- `PATCH` with `active = false` also deactivates the user.
- Deactivated users cannot log in via password, SAML, or OIDC.
- All grants (refresh tokens, sessions) are revoked upon deactivation.
- Deprovisioning of downstream apps is triggered by `DELETE` only; a `PATCH` deactivation revokes grants but leaves downstream apps untouched.

### Filtering
The full RFC 7644 §3.4.2.2 filter grammar is supported.

**Operators:** `eq`, `ne`, `co`, `sw`, `ew`, `gt`, `ge`, `lt`, `le`, and `pr` (presence).
**Logical:** `and`, `or`, `not (...)`, with parenthesised grouping — `and` binds tighter than `or`.
**Paths:** sub-attributes (`name.givenName`), multi-valued attributes (`emails.value`), value paths (`emails[type eq "work"].value`) and URN-prefixed names (`urn:ietf:params:scim:schemas:core:2.0:User:userName`).

```
userName eq "user@example.com"
userName sw "sales-" and active eq true
emails[type eq "work"].value co "@acme.com"
not (title pr)
meta.lastModified gt "2026-01-01T00:00:00Z"
```

Semantics follow the RFC: string comparison is case-insensitive, a multi-valued attribute matches when any element matches, and an absent attribute makes every comparison false except `ne`. Input that is not a valid SCIM filter is rejected with `400` and `scimType: invalidFilter`, naming the problem.

**Performance.** `userName eq` and `externalId eq` — the lookups Entra and Okta issue before every create or update — are resolved via indexed point lookups rather than a listing scan, so they stay fast at any user count. Every other filter is evaluated while paging through the client's users, bounded: user PII is encrypted at rest and searchable only through blind indexes, so richer predicates cannot be pushed down to storage. Under cursor pagination `totalResults` reflects what was returned, and is exact once `nextCursor` is absent.

### Pagination
User listings use **cursor pagination**. Each page of `GET /scim/v2/Users` returns a `nextCursor` property in the list response; pass it back as `?cursor=` to fetch the next page. When `nextCursor` is absent, the listing is complete. Page size is controlled by `count` (default 100, maximum 200).

Requesting `startIndex` greater than 1 on the Users endpoint returns a `400` error directing you to cursor pagination; offset paging past the first page is not offered. `totalResults` reports the number of resources returned in the response (it is the true total only when `nextCursor` is absent).

Group listings still use `startIndex`/`count` offset pagination.

### Group membership via PATCH
`PATCH /scim/v2/Groups/{id}` accepts the membership shapes the major identity providers actually send:

- **Add members:** `op: "add"` with `path: "members"` and a value array of `{ "value": "user-id" }` objects. Duplicates are ignored.
- **Replace members:** `op: "replace"` with `path: "members"` replaces the entire membership with the supplied array.
- **Remove a specific member (value array):** `op: "remove"` with `path: "members"` and a value array of the member ids to remove (the shape Entra ID sends).
- **Remove a specific member (path filter):** `op: "remove"` with `path: 'members[value eq "user-id"]'`, the id carried in the path filter with no value (the shape Okta sends for deprovisioning).
- **Remove all members:** `op: "remove"` with `path: "members"` and no value clears the group.

### Group-to-role mapping
Membership in a SCIM group can grant application roles. Mappings are one row per (group, role) pair, and a group may grant several roles. They are resolved at **token issuance**: a user's effective roles are their directly assigned roles plus the roles of every mapped group they belong to, so adding or removing a group member takes effect on the next token without touching the user record. An empty mapping store is a no-op.

Mappings are persisted via the `IScimGroupRoleMappingStore` (implemented by the Azure and AWS storage providers; an in-memory default is registered otherwise) and are managed by the hosting application's admin surface, not via the SCIM API itself.

Optionally, a client with `IncludeGroupsInTokens` enabled also receives the user's SCIM group display names as a `groups` claim in issued tokens.

## Known Limitations

- **No bulk operations:** users and groups must be provisioned individually.
- **No sorting:** user listings return storage order under cursor pagination; group listings are ordered by creation date.
- **Filter subset:** only `eq` and `co` operators on `userName`, `externalId`, and `displayName` (groups: `displayName` and `externalId`).
- **No password management:** SCIM-provisioned users authenticate via SSO only.
- **Soft delete only:** `DELETE` deactivates rather than permanently removes users.
