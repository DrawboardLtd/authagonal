---
layout: default
title: Accueil
locale: fr
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

Serveur d'authentification OAuth 2.0 / OpenID Connect / SAML 2.0 pour .NET, adossé à un stockage interchangeable : votre propre PostgreSQL ou SQLite, Azure Table Storage, ou AWS (DynamoDB / S3 / Secrets Manager).

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
- **Signature des tokens** : ES256 uniquement. Les access tokens portent le `typ: at+jwt` de la RFC
  9068 afin qu'un serveur de ressources puisse les distinguer des id_tokens et des logout tokens,
  mais **la conformité à la RFC 9068 n'est pas revendiquée** : la §2.1 exige RS256 parmi les
  algorithmes pris en charge, et ce serveur ne l'émet ni ne l'accepte. Un algorithme unique est une
  posture délibérée : chaque algorithme accepté de plus est un moyen supplémentaire d'amener un
  vérificateur à utiliser le mauvais.
- **Déconnexion Back-Channel** : notifications OIDC Back-Channel Logout 1.0 aux parties de confiance
- **Libre-service RGPD** : export des données et suppression de compte planifiée depuis la page de compte hébergée
- **Provisionnement TCC** : provisionnement Try-Confirm-Cancel dans les applications en aval au moment de l'autorisation
- **Interface de connexion personnalisable** : configurable à l'exécution via un fichier JSON (logo, couleurs, CSS personnalisé), aucune recompilation nécessaire ; localisée en 10 langues
- **Hooks d'authentification** : extensibilité `IAuthHook` pour la journalisation d'audit, la validation personnalisée, les webhooks
- **Points d'extension de chiffrement des PII** : points d'extension `IFieldCipher` / `IIndexTokenizer` pour le chiffrement au niveau des champs au repos avec recherche par index aveugle à clé (HMAC) ; codes de récupération chiffrés via `ISecretProvider`
- **HashiCorp Vault Transit** : signature JWT distante sans accès local à la clé privée
- **Bibliothèque composable** : `AddAuthagonal()` / `UseAuthagonal()` pour héberger dans votre propre projet avec des substitutions de services personnalisées
- **Prêt pour Native AOT** : trimming IL et sérialisation JSON générée à la source pour un démarrage rapide
- **Stockage interchangeable** : PostgreSQL ou SQLite auto-hébergés (sans compte cloud), ou Azure Table Storage / AWS (DynamoDB / S3 / Secrets Manager) comme backends à faible coût, compatibles serverless
- **Sauvegarde et restauration** : sauvegardes incrémentales (pilotées par journal des modifications avec un filet de sécurité par analyse complète), vérification d'intégrité, suivi des suppressions par tombstones
- **API d'administration** : CRUD utilisateurs, gestion des fournisseurs SAML/OIDC, routage de domaines SSO, usurpation de jetons

## Intégrations courantes

Guides orientés tâches pour les flux que les équipes construisent le plus souvent. Ces pages ne sont
pour l'instant disponibles qu'en anglais :

- **[Faire évoluer un utilisateur](../user-upgrade)** : transformer un compte invité / SSO / sur invitation en un compte avec identifiants via la revendication de compte sans mot de passe, et exécuter la promotion invité → membre standard à la confirmation.
- **[SSO en libre-service](../self-service-sso)** : provisionnement JIT pour les connexions d'entreprise : intégration sur invitation seule ou en libre-service, comment éviter que les IdP externes ne deviennent des pièges, et interstitiels avant fédération.
- **[Sessions fédérées](../federated-sessions)** : révoquer la session locale quand l'IdP en amont le fait (`RevalidateOnRefresh`).
- **[Authentification WebSocket](../websocket-auth)** : authentifier les WebSockets du navigateur via le BFF sans exposer de token.
- **[Authentification agentique](../agentic-auth)** : déléguer l'autorité d'un utilisateur à des agents IA : agents enregistrés, autorité fine RFC 9396, tokens de délégation composites (`act` de la RFC 8693), consentement permanent, approbations à la demande, tickets de capacité.

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
