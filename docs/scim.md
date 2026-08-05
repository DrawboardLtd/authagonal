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
- Pagination: cursor-based (`cursor`/`nextCursor`) on both users and groups; `startIndex` is still accepted on groups for existing clients but is not advertised
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
  "expiresInDays": 365,
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
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
  "expiresAt": "2025-01-01T00:00:00Z",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

Omit `expiresInDays` (or pass `0`) for a non-expiring token.

### Bounding which identities a connector may create

`allowedEmailDomains` is the only control over **which** users a SCIM credential can provision. Set it.

Omitting it produces an unrestricted token, and unrestricted is wider than it sounds. A SCIM-created user is
written with `EmailConfirmed = true` — the address is treated as proven from that moment on — so an
unrestricted connector can create `ceo@some-other-company.example` as a pre-verified account. When the real
owner later signs in through federation, a record with no existing external logins is adopted rather than
refused, so their sign-in binds to that account; and because `ScimProvisionedByClientId` still names the
connector that created it, that connector keeps full ownership of the object — it can read the profile, rename
the `userName`, deactivate it (which revokes every grant), or delete it, which purges the user's passkeys and
group memberships and tombstones the row so the legitimate connector for that domain gets 404 on every
operation.

A token that omits the field logs a warning at mint time naming the token id.

Supply bare domains — `acme.example`, not `@acme.example` or an address. A value that could never match is
refused rather than stored, because a bound that permits nothing looks identical to a misconfigured connector.

Operators can also set a bound in configuration:

```json
{
  "Scim": {
    "Clients": {
      "your-client-id": { "AllowedEmailDomains": ["acme.example"] }
    }
  }
}
```

The two are **intersected**, and an empty list from either source means "no bound from this source". So both
empty is unrestricted; either one alone applies on its own; and when both are set, only domains in both are
permitted — minting a token can narrow an operator's configured bound but never widen it.

Enforced on create, `PUT` and `PATCH` alike, so a rename cannot move an account into a domain the credential
is not allowed to provision.

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
| DELETE | `/scim/v2/Users/{id}` | Tombstone (deactivates; a later GET is 404) |
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

User and group endpoints are rate-limited to 200 requests per minute per SCIM client; excess requests receive a SCIM error with status `429`.

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
- If the client has `ProvisioningApps` configured, TCC provisioning is triggered automatically. If provisioning rejects the user, the SCIM create is rolled back and the response is a SCIM `400` with `scimType: invalidValue` and a fixed message (the downstream app's own text is deliberately not echoed to the SCIM client).
- Creating a user whose `userName` or `externalId` already exists returns a SCIM `409` conflict. Email changes via PUT or PATCH are conflict-checked the same way.

### User deactivation
- `DELETE /scim/v2/Users/{id}` **tombstones** the resource: it deactivates the user, keeps the local record, and stamps `ScimDeletedAt`. A subsequent `GET /scim/v2/Users/{id}` returns **404**, as RFC 7644 §3.6 requires ("the service provider MUST return a 404 for all operations associated with the previously deleted resource"). Do not confirm a deprovision by reading the resource back and expecting `active: false` — the read is a 404, and that is success.
- The record is retained rather than erased so a re-hire can be re-created: the tombstone releases the `userName`/`externalId` a new resource needs, while the local account, its audit history and its group memberships survive.
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

**Performance.** `userName eq` and `externalId eq` — the lookups Entra and Okta issue before every create or update — are resolved via indexed point lookups rather than a listing scan, so they stay fast at any user count. Every other filter is evaluated while paging through the client's users, bounded: user PII is encrypted at rest and searchable only through blind indexes, so richer predicates cannot be pushed down to storage. Under cursor pagination `totalResults` is **omitted** while `nextCursor` is present, and is the exact total once `nextCursor` is absent — see Pagination.

### Pagination
User listings use **cursor pagination**. Each page of `GET /scim/v2/Users` returns a `nextCursor` property in the list response; pass it back as `?cursor=` to fetch the next page. When `nextCursor` is absent, the listing is complete. Page size is controlled by `count` (default 100, maximum 200).

Requesting `startIndex` greater than 1 on the Users endpoint returns a `400` error directing you to cursor pagination; offset paging past the first page is not offered. `totalResults` is **omitted entirely** while `nextCursor` is present, and carries the exact total only on the final page. It deliberately does not report the returned page's size: a syncing client that read `totalResults`, saw it equal the number of resources it had just received, and concluded it held the whole directory silently under-read the tenant. Drive the loop off `nextCursor`, never off `totalResults` — and treat an absent `totalResults` as "not yet known", not as zero.

**Group listings are cursor-paginated too.** `GET /scim/v2/Groups` returns a `nextCursor` on both its filtered
and unfiltered forms; follow it the same way. `startIndex` is still accepted on Groups for clients already using
it, but it is **not advertised** in `ServiceProviderConfig` and should not be relied on: `pagination.index` is a
claim about the provider, not about one collection, and `/Users` does not support it — so the only value that is
true everywhere is `false`. Use cursors, which work on both.

A filtered group listing scans in bounded windows rather than materialising the whole tenant, so it can return
an empty page while matches still exist further on. When that happens it returns a `nextCursor` and **omits**
`totalResults` — an empty page with a cursor means "keep going", and an empty page with no cursor means the
filtered set really is empty. Do not treat the first empty page as the end of the collection.

`count=0` returns `totalResults` with no resources (RFC 7644 §3.4.2.4) on both collections, and a negative
`count` is refused with a `400` rather than clamped.

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
- **No password management:** SCIM-provisioned users authenticate via SSO only.
- **Tombstone, not erasure:** `DELETE` deactivates and tombstones the resource (a later `GET` is a 404, per RFC 7644 §3.6) rather than permanently removing the local user record. For erasure, use the admin API.
