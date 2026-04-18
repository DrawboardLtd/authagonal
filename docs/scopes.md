---
layout: default
title: OAuth Scopes
---

# OAuth Scopes

Authagonal supports both **built-in** OAuth/OIDC scopes and **custom** scopes managed at runtime. Custom scopes are persisted, advertised via the discovery document, and surfaced on the consent screen alongside built-ins.

## Built-in Scopes

These scopes are always available and do not need to be registered:

| Scope | Purpose |
|---|---|
| `openid` | Required to initiate an OIDC flow. Issues an ID token. |
| `profile` | Standard profile claims (name, family_name, given_name, etc.) |
| `email` | Email address and `email_verified` claims |
| `offline_access` | Issues a refresh token alongside the access token |

## Custom Scopes

Custom scopes are managed through the admin API at `/api/v1/scopes`. They require a JWT access token with the `authagonal-admin` scope (configurable via `AdminApi:Scope`).

### Scope Model

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| Field | Description |
|---|---|
| `Name` | The scope identifier sent in token requests (e.g., `billing.read`) |
| `DisplayName` | Human-readable name shown on the consent screen |
| `Description` | Longer description shown on the consent screen |
| `Emphasize` | If `true`, the consent screen highlights this scope as sensitive |
| `Required` | If `true`, the user cannot deselect this scope when consenting |
| `ShowInDiscoveryDocument` | If `true`, the scope appears in `/.well-known/openid-configuration` under `scopes_supported` |
| `UserClaims` | Claims added to the access token when this scope is granted |

## Admin Endpoints

### List Scopes

```
GET /api/v1/scopes
```

Returns `{ "scopes": [ ... ] }`.

### Get Scope

```
GET /api/v1/scopes/{name}
```

Returns the scope or `404` if not found.

### Create Scope

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing — read-only",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

Returns `201 Created` with the scope. Returns `409` if a scope with the same name already exists.

### Update Scope

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing — read",
  "description": "View invoices",
  "emphasize": true
}
```

Only supplied fields are updated; omitted fields retain their current values.

### Delete Scope

```
DELETE /api/v1/scopes/{name}
```

Returns `204 No Content`. Tokens already issued that include this scope remain valid until they expire — revoke them explicitly if needed (see [Admin API](admin-api) token revocation).

## Discovery Document

Scopes with `ShowInDiscoveryDocument = true` appear under `scopes_supported` in `/.well-known/openid-configuration`. Built-in scopes are always advertised.

```json
{
  "scopes_supported": ["openid", "profile", "email", "offline_access", "billing.read"]
}
```

## Consent Screen

When a client requests a scope that is not in its consent-skip list, the consent page lists each requested scope by `DisplayName` (falling back to `Name`) with the `Description` underneath. Scopes with `Emphasize = true` receive a distinct visual treatment. `Required` scopes cannot be deselected.

See [OAuth Consent Screen](index#features) for the user-facing flow.

## Dynamic Client Registration

Clients registered via [Dynamic Client Registration](client-registration) may only request scopes that are either built-in or previously created via the admin API. Unknown scopes are rejected with `invalid_scope`.
