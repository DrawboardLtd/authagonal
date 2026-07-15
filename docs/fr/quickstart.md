---
layout: default
title: Démarrage rapide
locale: fr
---

# Démarrage rapide

Lancez Authagonal localement en 5 minutes.

## 1. Démarrer le serveur

```bash
docker compose up
```

Cela démarre Authagonal sur `http://localhost:8080` avec Azurite pour le stockage.

## 2. Vérifier le fonctionnement

```bash
# Health check
curl http://localhost:8080/health

# OIDC discovery
curl http://localhost:8080/.well-known/openid-configuration

# Login page (returns the SPA)
curl http://localhost:8080/login
```

## 3. Enregistrer un client

Ajoutez un client dans votre `appsettings.json` (ou passez-le via des variables d'environnement) :

```json
{
  "Clients": [
    {
      "ClientId": "my-web-app",
      "ClientName": "My Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["http://localhost:3000/callback"],
      "PostLogoutRedirectUris": ["http://localhost:3000"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["http://localhost:3000"],
      "RequirePkce": true,
      "RequireClientSecret": false
    }
  ]
}
```

Les clients sont injectés au démarrage, sans risque à chaque déploiement.

## 4. Initier une connexion

Redirigez vos utilisateurs vers :

```
http://localhost:8080/connect/authorize
  ?client_id=my-web-app
  &redirect_uri=http://localhost:3000/callback
  &response_type=code
  &scope=openid profile email
  &state=random-state
  &code_challenge=...
  &code_challenge_method=S256
```

L'utilisateur voit la page de connexion, s'authentifie et est redirigé avec un code d'autorisation.

> **Premier utilisateur :** enregistrez-en un sur `http://localhost:8080/login/register`, ou créez-en un via l'[API d'administration](admin-api). L'auto-enregistrement envoie un e-mail de vérification, et sans expéditeur d'e-mail configuré (le comportement local par défaut) ce message est ignoré. Pour les tests locaux, définissez donc `Auth__AutoConfirmEmailDomains__0=example.dev` (n'importe quel domaine avec lequel vous vous enregistrez) pour ignorer la vérification, ou configurez `Email:ResendApiKey` + `Email:SenderEmail`. Voir [Configuration → Email](configuration#email).

## 5. Échanger le code

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

Réponse :

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800
}
```

## Démo fonctionnelle

Le répertoire `demos/sample-app/` contient une SPA React complète + API qui implémente le flux OIDC complet ci-dessus. Consultez le [README des démos](https://github.com/authagonal/authagonal/tree/master/demos) pour les instructions.

## Prochaines étapes

- [Configuration](configuration) : référence complète de tous les paramètres
- [Extensibilité](extensibility) : héberger en tant que bibliothèque, ajouter des hooks personnalisés
- [Personnalisation visuelle](branding) : personnaliser l'interface de connexion
- [SAML](saml) : ajouter des fournisseurs SSO SAML
- [Provisionnement](provisioning) : provisionner les utilisateurs dans les applications en aval
