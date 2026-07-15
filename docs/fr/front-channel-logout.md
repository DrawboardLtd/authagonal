---
layout: default
title: Front-Channel Logout
locale: fr
---

# Front-Channel Logout

Authagonal implémente **OpenID Connect Front-Channel Logout 1.0**, un mécanisme de déconnexion piloté par le navigateur qui complète la [déconnexion back-channel](index#features). Là où la déconnexion back-channel est un POST de serveur à serveur, la déconnexion front-channel affiche l'URL de déconnexion de chaque partie de confiance dans un iframe masqué afin que la session de navigateur de chaque application (cookies, stockage local) soit nettoyée depuis l'intérieur du navigateur de l'utilisateur.

## Quand utiliser laquelle

| Aspect | Back-Channel | Front-Channel |
|---|---|---|
| Sessions côté serveur | ✅ | ❌ |
| Cookies du navigateur / stockage local | ❌ | ✅ |
| Fonctionne quand le navigateur de l'utilisateur est hors ligne | ✅ | ❌ |
| Résiste aux erreurs réseau (nouvelle tentative) | ✅ | ❌ (une seule tentative au mieux) |

La plupart des applications ont intérêt à configurer **les deux**. Le back-channel garantit que le serveur est prévenu ; le front-channel nettoie le navigateur.

## Configuration du client

Ajoutez une URI de déconnexion front-channel à l'enregistrement `OAuthClient` :

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| Champ | Description |
|---|---|
| `FrontChannelLogoutUri` | Le endpoint de déconnexion du client, visible dans le navigateur |
| `FrontChannelLogoutSessionRequired` | Si `true` (par défaut), l'URL est appelée avec les paramètres de requête `iss` et `sid` pour que le client puisse corréler la déconnexion avec la session spécifique |

## Fonctionnement

Lorsque le navigateur visite `/connect/endsession` :

1. Le serveur trouve tous les clients avec lesquels l'utilisateur a actuellement des grants.
2. Pour chaque client ayant une `FrontChannelLogoutUri`, le serveur construit une URL, en y ajoutant `iss=<issuer>` (et `sid=<session_id>`, lorsque la session en possède un) si `FrontChannelLogoutSessionRequired` vaut `true`.
3. Le serveur déconnecte l'utilisateur du cookie du serveur d'autorisation, déclenche les notifications de déconnexion back-channel en arrière-plan, et renvoie une page HTML contenant un `<iframe>` masqué pour chaque URL de déconnexion de client :
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
4. Après un délai de grâce de 2 secondes, le navigateur est redirigé vers `post_logout_redirect_uri`, honoré uniquement lorsque la requête porte aussi un `id_token_hint` identifiant le client et que l'URI figure dans les `PostLogoutRedirectUris` enregistrées de ce client (un paramètre `state`, s'il est fourni, est ajouté à la redirection). Sinon, une confirmation de « déconnexion effectuée » est affichée.

## Gestionnaire de déconnexion côté client

Chaque partie de confiance doit implémenter l'URL référencée par `FrontChannelLogoutUri`. Un gestionnaire minimal :

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. Vérifiez que `iss` correspond au serveur d'autorisation attendu.
2. Si `sid` est fourni, confirmez qu'il correspond à l'identifiant de session du cookie de session.
3. Effacez la session locale (cookies, session côté serveur, stockage de la SPA).
4. Répondez avec `200 OK` et un corps vide (ou une petite page) : la réponse n'est jamais visible par l'utilisateur.

```csharp
app.MapGet("/oidc/frontchannel", (HttpContext ctx) =>
{
    var iss = ctx.Request.Query["iss"].ToString();
    var sid = ctx.Request.Query["sid"].ToString();
    // Valider iss/sid, puis effacer la session locale
    ctx.SignOutAsync();
    return Results.Ok();
});
```

## Document de découverte

La déconnexion front-channel est annoncée dans `/.well-known/openid-configuration` :

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## Dynamic Client Registration

Les clients enregistrés via l'[enregistrement dynamique de client](client-registration) peuvent inclure :

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

## Limitations

- **Au mieux** : les iframes sont chargées une seule fois. Si une erreur réseau ou une extension de navigateur les bloque, il n'y a pas de nouvelle tentative. Associez-la à la déconnexion back-channel pour la fiabilité.
- **Cookies tiers** : certains navigateurs bloquent par défaut les cookies dans les iframes intersites. Si votre partie de confiance repose sur des cookies first-party, vérifiez que le gestionnaire de déconnexion ne dépend pas de l'envoi des cookies.
- **Délai d'expiration** : la page attend environ 2 secondes avant de rediriger/confirmer. Des gestionnaires de déconnexion lourds côté partie de confiance peuvent ne pas se terminer à temps.

## Voir aussi

- [Enregistrement dynamique de client](client-registration) : les paramètres front-channel dans la requête d'enregistrement
- [Scopes OAuth](scopes) : le consentement tenant compte des scopes complète le flux de déconnexion
