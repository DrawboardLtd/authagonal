---
layout: default
title: Authentification multi-facteurs
locale: fr
---

# Authentification multi-facteurs (MFA)

Authagonal prend en charge l'authentification multi-facteurs. Trois méthodes sont disponibles : TOTP (applications d'authentification), WebAuthn/clés d'accès (clés matérielles et données biométriques) et codes de récupération à usage unique. Les clés d'accès peuvent aussi servir à la [connexion sans mot de passe](#passwordless-passkey-login).

Les connexions fédérées (SAML/OIDC) sont également couvertes : une assertion SAML ou OIDC prouve le premier facteur, pas le second. Un utilisateur fédéré ayant inscrit la MFA passe par le même défi MFA local qu'une connexion par mot de passe, et une politique `Required` force l'inscription avant l'émission de toute session. Ce n'est que lorsque la MFA n'est ni inscrite ni requise que la fédération se suffit à elle-même.

## Méthodes prises en charge

| Méthode | Description |
|---|---|
| **TOTP** | Mots de passe à usage unique basés sur le temps (RFC 6238) : 6 chiffres, pas de 30 secondes, SHA-1, vérifiés avec une fenêtre de dérive d'horloge d'un pas. Fonctionne avec n'importe quelle application d'authentification (Google Authenticator, Authy, 1Password, etc.). Un code déjà accepté ne peut pas être rejoué dans sa fenêtre de validité. |
| **WebAuthn / Clés d'accès** | Clés de sécurité matérielles FIDO2, données biométriques de la plateforme (Touch ID, Windows Hello) et clés d'accès synchronisées. Les utilisateurs peuvent enregistrer plusieurs clés d'accès, et les clés d'accès permettent la connexion sans mot de passe. |
| **Codes de récupération** | 10 codes de sauvegarde à usage unique (format `XXXX-XXXX`) pour la récupération de compte lorsque les autres méthodes ne sont pas disponibles. Stockés hachés et chiffrés au repos. |

## Politique MFA

L'application de la MFA est configurée **par client** via la propriété `MfaPolicy` dans `appsettings.json` :

| Valeur | Comportement |
|---|---|
| `Disabled` (par défaut) | Ne pas forcer l'inscription ; l'interface de configuration en libre-service masque la MFA lorsque chaque client est `Disabled` |
| `Enabled` | Proposer l'inscription à la MFA ; ne pas la forcer |
| `Required` | Forcer l'inscription pour les utilisateurs sans MFA |

Un utilisateur qui a inscrit la MFA est **toujours soumis au défi à la connexion, quelle que soit la politique du client**. La MFA est une propriété de l'utilisateur et de sa session, pas du client demandeur : une requête acheminée via un client `Disabled` ne peut donc pas servir à contourner le second facteur d'un utilisateur inscrit.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "MfaPolicy": "Enabled"
    },
    {
      "ClientId": "admin-portal",
      "MfaPolicy": "Required"
    }
  ]
}
```

La valeur par défaut est `Disabled`, donc les clients existants ne sont pas affectés jusqu'à ce que vous optiez pour cette fonctionnalité.

### Remplacement par utilisateur

Implémentez `IAuthHook.ResolveMfaPolicyAsync` pour remplacer la politique client pour des utilisateurs spécifiques :

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    // Exempt service accounts
    if (email.EndsWith("@service.internal"))
        return Task.FromResult(MfaPolicy.Disabled);

    return Task.FromResult(clientPolicy);
}
```

La politique résolue gouverne l'inscription (proposée ou forcée). Elle n'exempte pas du défi un utilisateur déjà inscrit ; les utilisateurs inscrits sont toujours soumis au défi.

Consultez [Extensibilité](extensibility) pour la documentation complète des hooks.

## Flux de connexion

Le flux de connexion avec MFA fonctionne comme suit :

1. L'utilisateur soumet son e-mail et son mot de passe à `POST /api/auth/login`
2. Le serveur vérifie le mot de passe, puis résout la politique MFA effective
3. En fonction de la politique et du statut d'inscription de l'utilisateur :

| Politique | L'utilisateur a la MFA ? | Résultat |
|---|---|---|
| Toute politique | Oui | Retourne `mfaRequired` : l'utilisateur doit vérifier |
| `Disabled` / `Enabled` | Non | Cookie défini, connexion terminée |
| `Required` | Non | Retourne `mfaSetupRequired` : l'utilisateur doit s'inscrire |

### Défi MFA

Lorsque `mfaRequired` est retourné, la réponse de connexion inclut un `challengeId`, les `methods` disponibles de l'utilisateur et (lorsque l'utilisateur possède des clés d'accès) des options d'assertion `webAuthn`. Le client redirige vers une page de défi MFA où l'utilisateur vérifie avec l'une de ses méthodes inscrites via `POST /api/auth/mfa/verify` :

```json
{
  "challengeId": "...",
  "method": "totp",
  "code": "123456"
}
```

`method` vaut `totp`, `recovery` ou `webauthn` (WebAuthn envoie une `assertion` au lieu d'un `code`).

Les défis expirent après 5 minutes (configurable via `Auth:MfaChallengeExpiryMinutes`) et sont consommés lors d'une vérification réussie.

#### Budget de nouvelles tentatives

Un code erroné ne brûle pas le défi. Le point de terminaison de vérification valide d'abord le code et ne consomme le défi qu'en cas de succès : un chiffre TOTP mal saisi peut donc simplement être retenté avec le même `challengeId`. Les tentatives échouées retournent `invalid_code` (ou `assertion_failed` pour WebAuthn) avec un 401 et incrémentent un compteur borné sur le défi ; la cinquième tentative erronée consomme le défi et retourne `too_many_attempts`, forçant une nouvelle connexion. Cela s'applique aux trois méthodes et borne la force brute TOTP à 5 essais par défi.

Un défi manquant, expiré ou déjà consommé retourne `invalid_challenge`.

### Connexions fédérées

Après une assertion SAML ou OIDC réussie, le serveur résout la même politique MFA effective. Un utilisateur ayant inscrit la MFA est redirigé vers la page de défi MFA hébergée (avec un `challengeId`) au lieu de recevoir une session ; un utilisateur sans MFA sous une politique `Required` est redirigé vers la page de configuration MFA (avec un `setupToken`). La session n'est marquée comme authentifiée par MFA qu'une fois la vérification terminée.

### Inscription forcée

Lorsque `mfaSetupRequired` est retourné, la réponse inclut un `setupToken`. Ce jeton authentifie l'utilisateur auprès des points de terminaison de configuration MFA (via l'en-tête `X-MFA-Setup-Token`) afin qu'il puisse inscrire une méthode avant d'obtenir une session cookie. Les jetons de configuration expirent après 15 minutes (configurable via `Auth:MfaSetupTokenExpiryMinutes`).

## Inscription à la MFA

Les utilisateurs s'inscrivent à la MFA via les points de terminaison de configuration en libre-service. Ceux-ci nécessitent soit une session cookie authentifiée, soit un jeton de configuration.

### Configuration TOTP

1. Appeler `POST /api/auth/mfa/totp/setup` : retourne un code QR (`data:image/png;base64,...`), une `manualKey` (Base32 pour la saisie manuelle) et un jeton de configuration
2. L'utilisateur scanne le code QR avec son application d'authentification
3. L'utilisateur saisit le code à 6 chiffres pour confirmer : `POST /api/auth/mfa/totp/confirm`

### Configuration WebAuthn / Clé d'accès

1. Appeler `POST /api/auth/mfa/webauthn/setup` : retourne un `setupToken` et `PublicKeyCredentialCreationOptions`
2. Le client appelle `navigator.credentials.create()` avec les options
3. Envoyer la réponse d'attestation à `POST /api/auth/mfa/webauthn/confirm`

L'inscription d'une clé d'accès exige d'abord un identifiant TOTP confirmé (`totp_required_first`). Les clés d'accès sont une commodité par appareil superposée à un facteur de base portable : chaque compte conserve ainsi un facteur indépendant de l'appareil, et une politique `Required` ne peut pas être satisfaite par une clé d'accès seule.

Les utilisateurs peuvent enregistrer plusieurs clés d'accès (une par appareil). Un identifiant de credential déjà enregistré pour un autre utilisateur est rejeté (`credential_already_registered`), et les utilisateurs dont le domaine d'email est acheminé vers un IdP externe via SSO forcé ne peuvent pas inscrire de clé d'accès locale (`sso_managed`), car elle contournerait l'IdP et son déprovisionnement.

### Codes de récupération

Appeler `POST /api/auth/mfa/recovery/generate` pour générer 10 codes à usage unique. Au moins une méthode principale (TOTP ou WebAuthn) doit être inscrite au préalable.

La régénération des codes remplace tous les codes de récupération existants. Chaque code ne peut être utilisé qu'une seule fois : un code utilisé est marqué comme consommé et n'est plus accepté.

Les codes ne sont jamais stockés en texte brut : chaque code est haché, et le haché est en plus chiffré au repos avec le fournisseur de secrets du tenant, de sorte qu'un vidage du stockage produit du texte chiffré plutôt qu'un haché attaquable par force brute hors ligne.

## Connexion sans mot de passe par clé d'accès {#passwordless-passkey-login}

Les clés d'accès ne sont pas seulement un second facteur : un utilisateur ayant inscrit une clé d'accès peut se connecter sans mot de passe.

1. `POST /api/auth/mfa/passwordless/begin` retourne un `challengeId` et des `options` d'assertion pour les credentials découvrables, afin que l'authentificateur propose n'importe quelle clé d'accès résidente pour le site
2. Le client appelle `navigator.credentials.get()` avec les options
3. `POST /api/auth/mfa/passwordless/complete` avec `{ challengeId, assertion }` : le serveur résout l'utilisateur à partir de la clé d'accès elle-même et le connecte

La page de connexion hébergée branche cela sur le champ e-mail via la médiation conditionnelle (saisie automatique de clé d'accès) : lorsque le navigateur la prend en charge, une clé d'accès disponible est proposée comme suggestion de saisie automatique sans aucune interface supplémentaire.

Une clé d'accès est une authentification forte résistante au hameçonnage, donc la session résultante porte le marqueur MFA et n'est pas resoumise au défi. Si le domaine d'email de l'utilisateur est acheminé vers un IdP externe via SSO forcé, la connexion sans mot de passe est refusée avec une réponse 409 `sso_required` qui inclut l'URL de redirection SSO, de sorte qu'une clé d'accès locale ne peut pas contourner l'IdP.

## Gestion de la MFA

### Libre-service utilisateur

- `GET /api/auth/mfa/status` : afficher les méthodes inscrites (indique aussi si la MFA est proposée par un client quelconque)
- `DELETE /api/auth/mfa/credentials/{id}` : supprimer un identifiant spécifique

La suppression d'un identifiant nécessite une véritable session authentifiée ; un jeton de configuration n'autorise que l'ajout d'un premier facteur et obtient ici `session_required`, de sorte qu'un jeton de configuration divulgué ne peut pas rétrograder la MFA d'un utilisateur.

Si la dernière méthode principale est supprimée, la MFA est désactivée pour l'utilisateur.

### API d'administration

Les administrateurs peuvent gérer la MFA pour n'importe quel utilisateur via l'[API d'administration](admin-api) :

- `GET /api/v1/profile/{userId}/mfa` : afficher le statut MFA d'un utilisateur
- `DELETE /api/v1/profile/{userId}/mfa` : réinitialiser toute la MFA (pour les utilisateurs verrouillés)
- `DELETE /api/v1/profile/{userId}/mfa/{id}` : supprimer un identifiant spécifique

### Hook d'audit

Implémentez `IAuthHook.OnMfaVerifiedAsync` pour journaliser les événements MFA :

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

L'ensemble du cycle de vie MFA est raccordable : `OnMfaVerifyFailedAsync` (une tentative de vérification échouée), `OnMfaEnrolledAsync` (une méthode confirmée), `OnMfaCredentialRemovedAsync` (un identifiant supprimé, avec un indicateur signalant si cela a désactivé la MFA), et `OnRecoveryCodesRegeneratedAsync`.

## Interface de connexion personnalisée

Si vous créez une interface de connexion personnalisée, gérez ces réponses de `POST /api/auth/login` :

1. **Connexion normale** : `{ userId, email, name }` avec cookie défini. Redirection vers `returnUrl`.
2. **MFA requise** : `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Afficher le formulaire de défi MFA.
3. **Configuration MFA requise** : `{ mfaSetupRequired: true, setupToken }`. Afficher le flux d'inscription MFA.

Lors de la gestion des erreurs de `POST /api/auth/mfa/verify` : `invalid_code` et `assertion_failed` sont réessayables avec le même `challengeId` (dans la limite du budget de tentatives) ; `too_many_attempts` et `invalid_challenge` sont terminaux, renvoyez donc l'utilisateur vers le formulaire de connexion.

Consultez l'[API Auth](auth-api) pour la référence complète des points de terminaison.
