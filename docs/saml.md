---
layout: default
title: SAML
---

# SAML 2.0 SP

Authagonal includes a homebrew SAML 2.0 Service Provider implementation. No third-party SAML library — built on `System.Security.Cryptography.Xml.SignedXml` (part of .NET).

## Scope

- **SP-initiated SSO** (user starts at Authagonal, redirected to IdP)
- **HTTP-Redirect binding** for AuthnRequest
- **HTTP-POST binding** for Response (ACS)
- Azure AD is the primary target, but any compliant IdP works

### Not Supported

- IdP-initiated SSO
- SAML logout (use session timeout)
- Assertion encryption (don't publish an encryption cert)
- Artifact binding

## Azure AD Setup

### 1. Create a SAML Provider

**Option A — Configuration (recommended for static setups):**

Add to `appsettings.json`:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "acme-azure",
      "ConnectionName": "Acme Corp Azure AD",
      "EntityId": "https://auth.example.com/saml/acme-azure",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
      "AllowedDomains": ["acme.com"]
    }
  ]
}
```

Providers are seeded on startup. SSO domain mappings are registered automatically from `AllowedDomains`.

**Option B — Admin API (for runtime management):**

```bash
curl -X POST https://auth.example.com/api/v1/saml/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Acme Corp Azure AD",
    "entityId": "https://auth.example.com/saml/acme-azure",
    "metadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
    "allowedDomains": ["acme.com"]
  }'
```

### 2. Configure Azure AD

1. In Azure AD → Enterprise Applications → New Application → Create your own
2. Set up Single Sign-On → SAML
3. **Identifier (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **Reply URL (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **Sign on URL:** `https://auth.example.com/saml/acme-azure/login`

### 3. SSO Domain Routing

When `AllowedDomains` is specified (in config or via the create API), SSO domain mappings are registered automatically. When a user enters `user@acme.com` on the login page, the SPA detects SSO is required and shows "Continue with SSO".

You can also manage domains at runtime via the Admin API — see [Admin API](admin-api).

## Endpoints

| Endpoint | Description |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...` | Initiates SP-initiated SSO. Builds an AuthnRequest and redirects to the IdP. |
| `POST /saml/{connectionId}/acs` | Assertion Consumer Service. Receives the SAML Response, validates it, creates/signs in the user. |
| `GET /saml/{connectionId}/metadata` | SP metadata XML for configuring the IdP. |

## Azure AD Compatibility

| Azure AD Behavior | Handling |
|---|---|
| Signs assertion only (default) | Validates signature on Assertion element |
| Signs response only | Validates signature on Response element |
| Signs both | Validates both signatures |
| SHA-256 (default) | Supports SHA-256 and SHA-1 |
| NameID: emailAddress | Direct email extraction |
| NameID: persistent (opaque) | Falls back to email claim from attributes |
| NameID: transient, unspecified | Falls back to email claim from attributes |

## Claim Mapping

Azure AD claims (full URI format) are mapped to simple names:

| Azure AD Claim URI | Mapped To |
|---|---|
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress` | `email` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname` | `firstName` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname` | `lastName` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name` | `name` (UPN) |
| `http://schemas.microsoft.com/identity/claims/objectidentifier` | `oid` |
| `http://schemas.microsoft.com/identity/claims/tenantid` | `tenantId` |
| `http://schemas.microsoft.com/identity/claims/displayname` | `displayName` |

## Security

- **Replay prevention:** InResponseTo is validated against a stored request ID. Each ID is single-use.
- **Clock skew:** 5-minute tolerance on NotBefore/NotOnOrAfter
- **Wrapping attack prevention:** Signature validation uses the correct reference resolution
- **Open redirect prevention:** RelayState (returnUrl) must be a root-relative path (starting with `/`, no scheme or host)
