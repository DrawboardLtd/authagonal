---
layout: default
title: SAML
---

# SAML 2.0 SP

Authagonal includes a homebrew SAML 2.0 Service Provider implementation. No third-party SAML library: built on `System.Security.Cryptography.Xml.SignedXml` (part of .NET).

## Scope

- **SP-initiated SSO** (user starts at Authagonal, redirected to IdP)
- **HTTP-Redirect binding** for AuthnRequest (optionally signed, see below)
- **HTTP-POST binding** for Response (ACS)
- **Encrypted assertions** (`EncryptedAssertion`) decrypted with a per-connection SP keypair
- **Single Logout** (SP-initiated and IdP-initiated, Redirect and POST bindings)
- Azure AD / Entra ID is the primary target, but any compliant IdP works (Okta, OneLogin, Ping, Google Workspace, ADFS, Shibboleth attribute names are handled)

### Not Supported

- Artifact binding
- AES-GCM assertion encryption (.NET `EncryptedXml` limitation; configure AES-CBC at the IdP, see below)

IdP-initiated SSO is supported **per connection, and off by default**: set `allowUnsolicitedResponses: true` on the connection to accept it. Without it the ACS refuses a Response with no `InResponseTo` and redirects with `error=saml_unsolicited`. Off by default because accepting unsolicited responses lets anyone with an account at the IdP sign a session in from any user-agent, and because requiring the request cookie on the SP-initiated path is worth nothing while the same assertion can be replayed with `InResponseTo` removed. When it is on, the request-ID check is skipped for unsolicited responses but assertion-ID single-use is still enforced (see Security).

## Azure AD Setup

### 1. Create a SAML Provider

**Option A: Configuration (recommended for static setups)**

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

Providers are seeded on startup. SSO domain mappings are registered automatically from `AllowedDomains`. Config-seeded providers require a `MetadataLocation` URL and do not get an SP keypair (so no signed AuthnRequests, encrypted assertions, or signed logout messages); use the Admin API for those features.

`EntityId` is **your SP entity ID** (the identifier you register at the IdP), not the IdP's entity ID.

> **An IdP on your own private network.** `MetadataLocation` must be https and, by default, must resolve to a publicly routable address — the metadata document carries the certificates every assertion is validated against, and Authagonal refuses internal targets on every URL it fetches. To federate with an on-premises IdP, name it in [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard). If the IdP publishes no https metadata endpoint at all, paste the document into `MetadataXml` via the Admin API instead.

**Option B: Admin API (for runtime management)**

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

The API generates the `connectionId` (a GUID) and returns it in the `Location` header and response body. Additional optional fields: `metadataXml` (pasted metadata, see below), `nameIdFormat` (see below), `signAuthnRequests` (force signed AuthnRequests), `iconUrl` (login-button icon), `disableJitProvisioning` (reject unknown users instead of auto-creating them), `allowUnsolicitedResponses` (accept IdP-initiated sign-in — off by default, see above). API-created connections also get an auto-generated SP keypair (see SP Keypair below).

Connections are managed via `POST` / `GET` / `PUT` / `DELETE` on `/api/v1/saml/connections[/{connectionId}]`. `PUT` is a partial update: only fields supplied on the wire are modified.

### 2. Configure Azure AD

1. In Azure AD → Enterprise Applications → New Application → Create your own
2. Set up Single Sign-On → SAML
3. **Identifier (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **Reply URL (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **Sign on URL:** `https://auth.example.com/saml/acme-azure/login`

### 3. SSO Domain Routing

When `AllowedDomains` is specified (in config or via the create API), SSO domain mappings are registered automatically. When a user enters `user@acme.com` on the login page, the SPA detects SSO is required and shows "Continue with SSO". A domain can only be mapped to one connection; the API rejects a domain already claimed by a different connection.

You can also manage domains at runtime via the Admin API; see [Admin API](admin-api).

## Pasted Metadata XML

Some IdPs publish no metadata URL (Google Workspace), or their metadata endpoint is unreachable from the SP (private-network ADFS). For those, paste the metadata document instead: supply `metadataXml` on create/update. Exactly one of `metadataLocation` or `metadataXml` must be provided; supplying one on update clears the other.

Pasted metadata is validated at save time and **condensed** (`SamlMetadataParser.Condense`) to a canonical minimal `EntityDescriptor` holding exactly what the SP consumes: entityID, signing certificates, the SSO endpoint, the SLO endpoint if present, and the `WantAuthnRequestsSigned` flag. Vendor documents can exceed 100KB (ADFS `FederationMetadata.xml`), past the 64KB Azure Table property cap, while the parts the SP uses are a few KB. Unparseable pastes are rejected with a 400; the document must contain an `IDPSSODescriptor` with a signing certificate and a `SingleSignOnService`.

## NameID Format

The `nameIdFormat` field controls the `NameIDPolicy` Format requested in the AuthnRequest:

| Value | Behavior |
|---|---|
| omitted / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress` (the historic default) |
| `"none"` | Omit the `NameIDPolicy` element entirely. The ADFS-safe setting: ADFS fails the whole login (MSIS7070) when its claim rules don't emit the requested format. |
| any other value | Sent verbatim as the Format URN (must start with `urn:`) |

On update, `""` resets to the emailAddress default. The SP metadata advertises the connection's requested format (and omits `NameIDFormat` when set to `"none"`).

## Endpoints

| Endpoint | Description |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | Initiates SP-initiated SSO. Builds an AuthnRequest (signed when applicable) and redirects to the IdP. `loginHint` is passed as `login_hint` for IdPs that honor it (Entra, Google). |
| `POST /saml/{connectionId}/acs` | Assertion Consumer Service. Receives the SAML Response, validates it, creates/signs in the user. |
| `GET /saml/{connectionId}/metadata` | SP metadata XML for configuring the IdP. |
| `GET /saml/{connectionId}/logout?returnUrl=...` | SP-initiated Single Logout. Ends the local session, then sends a LogoutRequest to the IdP when it supports SLO. |
| `GET/POST /saml/{connectionId}/slo` | Single Logout endpoint. Receives IdP-initiated LogoutRequests (Redirect or POST binding) and the LogoutResponse leg of SP-initiated SLO. |

The post-login return URL is carried server-side on the stored AuthnRequest (keyed by request ID), not in RelayState: the SAML spec caps RelayState at 80 bytes and some IdPs truncate it. RelayState is only consulted for IdP-initiated flows.

## SP Keypair & Encrypted Assertions

Every API-created connection gets an auto-generated SP keypair: a self-signed 2048-bit RSA certificate (10-year validity), stored as PKCS#12 and protected at rest by the host's secret provider. It is server-only and never returned by the API. The keypair enables:

- **Signed AuthnRequests** (redirect-binding `SigAlg`/`Signature` query signing). Signing turns on automatically when the IdP's metadata declares `WantAuthnRequestsSigned`, or always when the connection sets `signAuthnRequests: true`.
- **Encrypted assertion decryption.** When the SP metadata advertises an encryption certificate, ADFS starts encrypting assertions by default; the ACS decrypts them with the SP private key and runs the decrypted assertion through the same signature/conditions pipeline as a plaintext one. Supported: RSA-OAEP (SHA-1/SHA-256) key transport; AES-128/192/256-CBC and 3DES data encryption. **RSA-1.5 key transport is refused** — PKCS#1 v1.5 unwrapping is a Bleichenbacher/ROBOT oracle — and **AES-GCM is not supported** (.NET `EncryptedXml` limitation). Configure the IdP for RSA-OAEP and AES-CBC. Both failures return the same constant message ("Could not decrypt the assertion."), deliberately: naming the algorithm or the stage that failed is what builds the oracle, so diagnose from the IdP's configuration rather than from the error.
- **Signed logout messages** (LogoutRequest/LogoutResponse on the redirect binding).

The SP metadata publishes the certificate as both a `signing` and an `encryption` `KeyDescriptor`, and sets `AuthnRequestsSigned="true"` when the connection forces signing.

## Single Logout

The ACS records the SAML session on the auth cookie (`saml_connection`, `saml_name_id`, `saml_name_id_format`, `saml_session_index` claims) so logout can be tied back to the IdP session.

- **SP-initiated:** `GET /saml/{connectionId}/logout` always ends the local cookie session first (the user asked to log out; IdP SLO is best-effort). If the browser's session came from this connection and the IdP metadata advertises a `SingleLogoutService`, a LogoutRequest (NameID + SessionIndex, signed when the SP has a key) is sent via the redirect binding; the IdP's LogoutResponse comes back to `/slo`, which lands the user on the stored `returnUrl`. IdPs with no SLO endpoint (Google) just get the local sign-out.
- **IdP-initiated:** the IdP sends a LogoutRequest to `/saml/{connectionId}/slo` (Redirect GET or POST binding). Signed requests are validated against the IdP's metadata certificates. **An unsigned or unverifiable LogoutRequest is refused with a 400** before any session is consulted. There is no session-scoped fallback: a third-party page that navigates the *victim's* browser here supplies the victim's session, not the attacker's, so scoping the fallback to the current session would not have limited who could be logged out. Profiles §4.4.3.1 requires the IdP to sign a LogoutRequest on the Redirect or POST binding anyway, and the connection's metadata already supplies the certificates, so refusing an unsigned one costs no conformant IdP anything. A signed LogoutResponse is returned when the IdP has an SLO endpoint. Front-channel only: the message arrives in the user's browser, so ending the cookie session logs out exactly that browser.

## Metadata Caching & Cert Rollover

- IdP metadata fetched from `MetadataLocation` is cached in memory for 60 minutes (configurable via `Cache:SamlMetadataCacheMinutes`), keyed by the metadata URL (not the connection ID, so no cross-tenant cache confusion is possible).
- Pasted metadata is cached content-addressed (hash of the XML) and never refetched.
- **Signature-failure refetch:** a signature validation failure right after an IdP cert rollover means the cached metadata is stale. On that exact failure the cache entry is evicted and the metadata refetched once, then validation is retried, with a 5-minute cooldown per metadata location so a garbage assertion can't be used to hammer the IdP's metadata endpoint. Without this, a cert rollover would fail logins until the cache TTL lapsed. (URL-fetched metadata only; pasted metadata has nothing to refetch.)

## Azure AD Compatibility

| Azure AD Behavior | Handling |
|---|---|
| Signs assertion only (default) | Validates signature on Assertion element |
| Signs response only | Validates signature on Response element |
| Signs both | Validates both signatures |
| SHA-256 (default) | Supports SHA-256 and SHA-1 |
| NameID: emailAddress | Direct email extraction |
| NameID: persistent (opaque) | Falls back to email claim from attributes |
| NameID: unspecified | Falls back to email claim from attributes |
| NameID: transient | Rotates every login, so it is never used as the federated key. The IdP's stable object-id attribute is used instead; if none is asserted, the login is rejected with an actionable error (configure a persistent or emailAddress NameID, or assert an object-id attribute). |

## Attribute Mapping

Attributes are indexed case-insensitively under both their `Name` and `FriendlyName` (Okta and Shibboleth emit OID Names with human FriendlyNames; matching either is what makes vendor mapping work). Each field tries an alias list in order; the first alias is the Microsoft claim URI, so Entra/ADFS behavior is unchanged, and the rest cover the friendly and OID names Okta, OneLogin, Ping, Google and Shibboleth emit by default:

| Field | Accepted attribute names |
|---|---|
| email | `.../claims/emailaddress`, `email`, `mail`, `emailaddress`, `urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`, `givenName`, `given_name`, `firstName`, `first_name`, `urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`, `sn`, `surname`, `lastName`, `last_name`, `familyName`, `family_name`, `urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`, `displayName`, `urn:oid:2.16.840.1.113730.3.1.241`, `cn`, `urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`, `objectGUID`, `user.objectid` |
| groups | `.../claims/groups`, `groups`, `memberOf`, `.../claims/role`, `urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

(`.../claims/...` abbreviates the full `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` or `http://schemas.microsoft.com/ws/2008/06/identity/claims/...` URI.)

Email resolution priority: explicit email attribute (any alias) → NameID when its format is emailAddress → the `name` claim if it contains `@` → reject (an email is required).

**Groups are multi-valued:** every `AttributeValue` element is captured (one per group membership), not just the first.

## JIT Provisioning

Unknown users are auto-created on first login (email, first/last name from the assertion, email marked confirmed) and linked to the connection by their stable federated identity (`saml:{connectionId}` + NameID, or the object-id for transient NameIDs). Set `disableJitProvisioning: true` to reject unknown users instead. Returning users are matched by the federated link first, never by email alone; an existing local account is attached by email only when the connection's `AllowedDomains` covers that email's domain (the admin's explicit statement that this IdP owns the domain), preventing account takeover via a rogue IdP.

## Security

- **Replay prevention:** for SP-initiated flows, `InResponseTo` is validated against a stored request ID (single-use). Independently, every accepted assertion's ID is stored and enforced single-use, which also covers IdP-initiated responses and responses whose `InResponseTo` was stripped (the assertion ID lives inside the signed assertion, so it cannot be altered without breaking the signature).
- **Clock skew:** 5-minute tolerance on NotBefore/NotOnOrAfter
- **Wrapping attack prevention:** the signature's Reference URI must match the signed element's ID
- **Open redirect prevention:** the post-login return URL must be a root-relative path (starting with `/`, no `//`, no backslashes, since browsers treat `\` as `/`)
- **Domain vouching:** when `AllowedDomains` is configured, assertions for emails outside those domains are rejected, so one connection can't assert another's domain or a local user's email
- **MFA:** federation proves the first factor only. If the user's effective policy requires MFA, the login routes through the local MFA challenge/setup instead of issuing a fully-authenticated session.
