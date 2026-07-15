---
layout: default
title: Accueil
locale: fr
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

Serveur d'authentification OAuth 2.0 / OpenID Connect / SAML 2.0 pour .NET, adossé à un stockage cloud interchangeable : Azure Table Storage ou AWS (DynamoDB / S3 / Secrets Manager).

Un déploiement unique et autonome. Le serveur et l'interface de connexion sont livrés sous forme d'une seule image Docker : la SPA est servie depuis la même origine que l'API, de sorte que l'authentification par cookie, les redirections et la CSP fonctionnent sans complexité inter-origines.

> **Vous préférez un service géré ?** [Authagonal Cloud](https://authagonal.io) s'occupe de tout pour vous : multi-tenant, toutes les fonctionnalités dans chaque forfait, sans frais SSO par connexion. → [authagonal.io](https://authagonal.io)

## Fonctionnalités clés

- **Fournisseur OIDC** : authorization_code + PKCE, client_credentials, refresh_token, device_code avec rotation à usage unique
- **SAML 2.0 SP** : implémentation maison avec prise en charge complète d'Azure AD (réponse signée, assertion, ou les deux), une paire de clés SP par connexion pour les AuthnRequests signées + le déchiffrement `EncryptedAssertion`, et la déconnexion unique (Single Logout, initiée par le SP comme par l'IdP)
- **Fédération OIDC dynamique** : connexion à Google, Apple, Azure AD ou tout IdP compatible OIDC
- **Authentification multi-facteurs** : TOTP, WebAuthn/clés d'accès, codes de récupération ; politique par client (`Disabled` / `Enabled` / `Required`) avec surcharge par utilisateur via `IAuthHook`, appliquée également aux connexions fédérées
- **Provisionnement SCIM 2.0** : provisionnement entrant d'utilisateurs/groupes depuis Entra ID, Okta, OneLogin ; listage paginé par curseur et filtres `eq` adossés à un index aveugle
- **Écran de consentement OAuth** : consentement par client avec nouvelle demande selon les scopes et gestion des grants
- **Device Authorization Grant** : flux RFC 8628 pour les appareils à saisie limitée (téléviseurs connectés, CLI, IoT)
- **Introspection de Token** : RFC 7662 pour que les serveurs de ressources vérifient la validité d'un token
- **Déconnexion Back-Channel** : notifications OIDC Back-Channel Logout 1.0 aux parties de confiance
- **Libre-service RGPD** : export des données et suppression de compte planifiée depuis la page de compte hébergée
- **Provisionnement TCC** : provisionnement Try-Confirm-Cancel dans les applications en aval au moment de l'autorisation
- **Interface de connexion personnalisable** : configurable à l'exécution via un fichier JSON (logo, couleurs, CSS personnalisé), aucune recompilation nécessaire ; localisée en 10 langues
- **Hooks d'authentification** : extensibilité `IAuthHook` pour la journalisation d'audit, la validation personnalisée, les webhooks
- **Points d'extension de chiffrement des PII** : points d'extension `IFieldCipher` / `IIndexTokenizer` pour le chiffrement au niveau des champs au repos avec recherche par index aveugle à clé (HMAC) ; codes de récupération chiffrés via `ISecretProvider`
- **HashiCorp Vault Transit** : signature JWT distante sans accès local à la clé privée
- **Bibliothèque composable** : `AddAuthagonal()` / `UseAuthagonal()` pour héberger dans votre propre projet avec des substitutions de services personnalisées
- **Prêt pour Native AOT** : trimming IL et sérialisation JSON générée à la source pour un démarrage rapide
- **Stockage cloud interchangeable** : Azure Table Storage ou AWS (DynamoDB / S3 / Secrets Manager) ; backends à faible coût, compatibles serverless
- **Sauvegarde et restauration** : sauvegardes incrémentales (pilotées par journal des modifications avec un filet de sécurité par analyse complète), vérification d'intégrité, suivi des suppressions par tombstones
- **API d'administration** : CRUD utilisateurs, gestion des fournisseurs SAML/OIDC, routage de domaines SSO, usurpation de jetons

## Architecture

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    │                             │                                    │
    ├─ GET /connect/authorize ──► │                                    │
    │                             ├─ 302 → /login (SPA)                │
    │                             │   ├─ SSO check                     │
    │                             │   └─ SAML/OIDC redirect ─────────► │
    │                             │                                    │
    │                             │ ◄── SAML Response / OIDC callback ─┤
    │                             │   └─ Create user + cookie          │
    │                             │                                    │
    │                             ├─ TCC provisioning (try/confirm)    │
    │                             ├─ Issue authorization code          │
    │ ◄─ 302 ?code=...&state=... ┤                                    │
    │                             │                                    │
    ├─ POST /connect/token ─────► │                                    │
    │ ◄─ { access_token, ... } ──┤                                    │
```

Commencez avec le guide d'[Installation](installation) ou passez directement au [Démarrage rapide](quickstart). Pour héberger Authagonal dans votre propre projet, consultez [Extensibilité](extensibility). Pour la gestion des données, consultez [Sauvegarde et restauration](backup-restore). Pour l'historique complet des modifications, consultez le [Journal des modifications](https://github.com/authagonal/authagonal/blob/master/CHANGELOG.md).
