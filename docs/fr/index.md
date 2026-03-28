---
layout: default
title: Accueil
locale: fr
---

# Authagonal

Serveur d'authentification OAuth 2.0 / OpenID Connect / SAML 2.0 adosse a Azure Table Storage.

Authagonal remplace Duende IdentityServer + Sustainsys.Saml2 par un deploiement unique et autonome. Le serveur et l'interface de connexion sont livres sous forme d'une seule image Docker -- la SPA est servie depuis la meme origine que l'API, de sorte que l'authentification par cookie, les redirections et la CSP fonctionnent sans complexite inter-origines.

## Fonctionnalites cles

- **Fournisseur OIDC** -- authorization_code + PKCE, client_credentials, refresh_token avec rotation a usage unique
- **SAML 2.0 SP** -- implementation maison avec prise en charge complete d'Azure AD (reponse signee, assertion, ou les deux)
- **Federation OIDC dynamique** -- connexion a Google, Apple, Azure AD ou tout IdP compatible OIDC
- **Provisionnement TCC** -- provisionnement Try-Confirm-Cancel dans les applications en aval au moment de l'autorisation
- **Interface de connexion personnalisable** -- configurable a l'execution via un fichier JSON -- logo, couleurs, CSS personnalise -- aucune recompilation necessaire
- **Hooks d'authentification** -- extensibilite `IAuthHook` pour la journalisation d'audit, la validation personnalisee, les webhooks
- **Bibliotheque composable** -- `AddAuthagonal()` / `UseAuthagonal()` pour heberger dans votre propre projet avec des substitutions de services personnalisees
- **Azure Table Storage** -- stockage backend a faible cout, compatible serverless
- **API d'administration** -- CRUD utilisateurs, gestion des fournisseurs SAML/OIDC, routage de domaines SSO, usurpation de jetons

## Architecture

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    |                             |                                    |
    +- GET /connect/authorize --> |                                    |
    |                             +- 302 -> /login (SPA)               |
    |                             |   +- SSO check                     |
    |                             |   +- SAML/OIDC redirect ---------->|
    |                             |                                    |
    |                             | <-- SAML Response / OIDC callback -|
    |                             |   +- Create user + cookie          |
    |                             |                                    |
    |                             +- TCC provisioning (try/confirm)    |
    |                             +- Issue authorization code          |
    | <-- 302 ?code=...&state=...|                                    |
    |                             |                                    |
    +- POST /connect/token -----> |                                    |
    | <-- { access_token, ... } --|                                    |
```

Commencez avec le guide d'[Installation](installation) ou passez directement au [Demarrage rapide](quickstart). Pour heberger Authagonal dans votre propre projet, consultez [Extensibilite](extensibility).
