---
layout: default
title: Pushed Authorization Requests
locale: fr
---

# Pushed Authorization Requests (PAR)

[RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) permet à un client de POSTer les paramètres de sa requête d'autorisation directement au serveur avec l'authentification client standard et de recevoir un `request_uri` opaque de courte durée à transmettre au navigateur. Le navigateur visite ensuite `/connect/authorize?request_uri=...&client_id=...` au lieu de porter chaque paramètre dans l'URL.

Pourquoi l'utiliser :

- Les paramètres d'autorisation n'apparaissent jamais dans l'historique du navigateur, les journaux du serveur ou les en-têtes `Referer`.
- Le serveur authentifie le client au moment de l'envoi (push), donc les paramètres sont contrôlés en intégrité avant toute redirection.
- Les jeux de paramètres longs (grandes requêtes `claims`, flux multi-ressources) ne dépassent pas les limites de longueur d'URL.

## Endpoint

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

L'authentification est la même que pour `/connect/token` : HTTP Basic avec `client_id`/`client_secret`, ou identifiants encodés dans le formulaire. Les clients confidentiels doivent s'authentifier ; les clients publics envoient sans secret. Les échecs d'authentification client renvoient `401` (conformément à la RFC 9126, contrairement au endpoint de token où seul `invalid_client` est un 401).

Le corps du formulaire porte les mêmes paramètres qui iraient normalement sur `/connect/authorize` (`response_type`, `redirect_uri`, `scope`, `state`, `code_challenge`, `code_challenge_method`, `nonce`, `resource`, etc.). `request_uri` lui-même est rejeté : chaîner un PAR est interdit par le §2.1 de la spécification. Si le corps porte un `client_id`, il doit correspondre au client authentifié.

### Réponse

```
HTTP/1.1 201 Created
```
```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

Le `request_uri` est à usage unique. Il est retiré du magasin une fois que la requête `/connect/authorize` correspondante le consomme (ou lorsque la fenêtre de 90 secondes expire, selon ce qui survient en premier).

### Étape d'autorisation

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

Lorsque `request_uri` est présent, tous les autres paramètres sont extraits de la charge utile envoyée ; tout le reste dans l'URL est ignoré. Le `client_id` de cette requête doit correspondre au client qui a envoyé la charge utile.

## Exiger PAR par client

Définissez `RequirePushedAuthorizationRequests = true` sur un client pour refuser les requêtes `/connect/authorize` simples en provenance de celui-ci. Toute tentative d'autorisation non-PAR renvoie `invalid_request` avec la description "This client requires requests to be pushed via /connect/par".

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

C'est la posture recommandée pour les clients qui manipulent des scopes sensibles : combinée à PKCE, elle supprime la barre d'URL en tant que surface d'attaque.

## Durée de vie et stockage

La durée de vie du `request_uri` est fixée par le serveur à 90 secondes, ce qui correspond à la valeur typique d'un IdP de référence. Les charges utiles envoyées sont stockées via le même `IGrantStore` que les codes d'autorisation et les refresh tokens, elles héritent donc automatiquement de la stratégie de persistance et de réplication de l'hôte.

## Découverte

Le endpoint PAR s'annonce dans `.well-known/openid-configuration` sous la forme :

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```
