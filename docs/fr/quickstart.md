---
layout: default
title: Demarrage rapide
locale: fr
---

# Demarrage rapide

Lancez Authagonal localement en 5 minutes.

## 1. Demarrer le serveur

```bash
docker compose up
```

Cela demarre Authagonal sur `http://localhost:8080` avec Azurite pour le stockage.

## 2. Verifier le fonctionnement

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

Les clients sont injectes au demarrage -- sans risque a chaque deploiement.

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

L'utilisateur voit la page de connexion, s'authentifie et est redirige avec un code d'autorisation.

## 5. Echanger le code

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

Reponse :

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800
}
```

## Demo fonctionnelle

Le repertoire `demos/sample-app/` contient une SPA React complete + API qui implemente le flux OIDC complet ci-dessus. Consultez le [README des demos](https://github.com/authagonal/authagonal/tree/master/demos) pour les instructions.

## Prochaines etapes

- [Configuration](configuration) -- reference complete de tous les parametres
- [Extensibilite](extensibility) -- heberger en tant que bibliotheque, ajouter des hooks personnalises
- [Personnalisation visuelle](branding) -- personnaliser l'interface de connexion
- [SAML](saml) -- ajouter des fournisseurs SSO SAML
- [Provisionnement](provisioning) -- provisionner les utilisateurs dans les applications en aval
