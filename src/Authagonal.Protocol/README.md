# Authagonal.Protocol

Embeddable OIDC / OAuth 2.0 protocol surface extracted from Authagonal.Server.

Provides `/connect/authorize`, `/connect/token`, `/connect/userinfo`, `/.well-known/openid-configuration`, and JWKS endpoints plus the token minting pipeline — nothing else. No user store, no SAML, no admin UI, no login pages.

Plug in your own identity via `IOidcSubjectResolver` and storage via `IClientStore` / `IGrantStore` / `IScopeStore` / `ISigningKeyStore`. Use this when you need to expose OIDC from an app that already has its own identity (e.g. share-link grants, service-to-service auth).
