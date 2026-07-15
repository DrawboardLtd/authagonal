---
layout: default
title: Accueil
locale: fr
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

Serveur d'authentification OAuth 2.0 / OpenID Connect / SAML 2.0 pour .NET, adosse a un stockage cloud interchangeable -- Azure Table Storage ou AWS (DynamoDB / S3 / Secrets Manager).

Un deploiement unique et autonome. Le serveur et l'interface de connexion sont livres sous forme d'une seule image Docker -- la SPA est servie depuis la meme origine que l'API, de sorte que l'authentification par cookie, les redirections et la CSP fonctionnent sans complexite inter-origines.

> **Vous preferez un service gere ?** [Authagonal Cloud](https://authagonal.io) s'occupe de tout pour vous -- multi-tenant, toutes les fonctionnalites dans chaque forfait, sans frais SSO par connexion. → [authagonal.io](https://authagonal.io)

## Fonctionnalites cles

- **Fournisseur OIDC** -- authorization_code + PKCE, client_credentials, refresh_token avec rotation a usage unique
- **SAML 2.0 SP** -- implementation maison avec prise en charge complete d'Azure AD (reponse signee, assertion, ou les deux), une paire de cles SP par connexion pour les AuthnRequests signees + le dechiffrement `EncryptedAssertion`, et la deconnexion unique (Single Logout, initiee par le SP comme par l'IdP)
- **Federation OIDC dynamique** -- connexion a Google, Apple, Azure AD ou tout IdP compatible OIDC
- **Authentification multi-facteurs** -- TOTP, WebAuthn/cles d'acces, codes de recuperation; politique par client (`Disabled` / `Enabled` / `Required`) avec surcharge par utilisateur via `IAuthHook`, appliquee egalement aux connexions federees
- **Provisionnement SCIM 2.0** -- provisionnement entrant d'utilisateurs/groupes depuis Entra ID, Okta, OneLogin; listage pagine par curseur et filtres `eq` adosses a un index aveugle
- **Provisionnement TCC** -- provisionnement Try-Confirm-Cancel dans les applications en aval au moment de l'autorisation
- **Libre-service RGPD** -- export des donnees et suppression de compte planifiee depuis la page de compte hebergee
- **Interface de connexion personnalisable** -- configurable a l'execution via un fichier JSON -- logo, couleurs, CSS personnalise -- aucune recompilation necessaire; localise en 10 langues
- **Hooks d'authentification** -- extensibilite `IAuthHook` pour la journalisation d'audit, la validation personnalisee, les webhooks
- **Points d'extension de chiffrement des PII** -- points d'extension `IFieldCipher` / `IIndexTokenizer` pour le chiffrement au niveau des champs au repos avec recherche par index aveugle a cle (HMAC); codes de recuperation chiffres via `ISecretProvider`
- **Bibliotheque composable** -- `AddAuthagonal()` / `UseAuthagonal()` pour heberger dans votre propre projet avec des substitutions de services personnalisees
- **Stockage cloud interchangeable** -- Azure Table Storage ou AWS (DynamoDB / S3 / Secrets Manager); backends a faible cout, compatibles serverless
- **Sauvegarde et restauration** -- sauvegardes incrementales (pilotees par journal des modifications avec un filet de securite par analyse complete), verification d'integrite, suivi des suppressions par tombstones
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
