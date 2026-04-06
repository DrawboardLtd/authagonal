---
layout: default
title: Authentification multi-facteurs
locale: fr
---

# Authentification multi-facteurs (MFA)

Authagonal prend en charge l'authentification multi-facteurs pour les connexions par mot de passe. Trois méthodes sont disponibles : TOTP (applications d'authentification), WebAuthn/clés d'accès (clés matérielles et données biométriques) et codes de récupération à usage unique.

Les connexions fédérées (SAML/OIDC) ignorent la MFA — le fournisseur d'identité externe gère l'authentification à second facteur.

## Méthodes prises en charge

| Méthode | Description |
|---|---|
| **TOTP** | Mots de passe à usage unique basés sur le temps (RFC 6238). Fonctionne avec n'importe quelle application d'authentification — Google Authenticator, Authy, 1Password, etc. |
| **WebAuthn / Clés d'accès** | Clés de sécurité matérielles FIDO2, données biométriques de la plateforme (Touch ID, Windows Hello) et clés d'accès synchronisées. |
| **Codes de récupération** | 10 codes de sauvegarde à usage unique (format `XXXX-XXXX`) pour la récupération de compte lorsque les autres méthodes ne sont pas disponibles. |

## Politique MFA

L'application de la MFA est configurée **par client** via la propriété `MfaPolicy` dans `appsettings.json` :

| Valeur | Comportement |
|---|---|
| `Disabled` (par défaut) | Aucune vérification MFA, même si l'utilisateur a inscrit la MFA |
| `Enabled` | Vérifier les utilisateurs qui ont inscrit la MFA ; ne pas forcer l'inscription |
| `Required` | Vérifier les utilisateurs inscrits ; forcer l'inscription pour les utilisateurs sans MFA |

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

Consultez [Extensibilité](extensibility) pour la documentation complète des hooks.

## Flux de connexion

Le flux de connexion avec MFA fonctionne comme suit :

1. L'utilisateur soumet son e-mail et son mot de passe à `POST /api/auth/login`
2. Le serveur vérifie le mot de passe, puis résout la politique MFA effective
3. En fonction de la politique et du statut d'inscription de l'utilisateur :

| Politique | L'utilisateur a la MFA ? | Résultat |
|---|---|---|
| `Disabled` | — | Cookie défini, connexion terminée |
| `Enabled` | Non | Cookie défini, connexion terminée |
| `Enabled` | Oui | Retourne `mfaRequired` — l'utilisateur doit vérifier |
| `Required` | Non | Retourne `mfaSetupRequired` — l'utilisateur doit s'inscrire |
| `Required` | Oui | Retourne `mfaRequired` — l'utilisateur doit vérifier |

### Défi MFA

Lorsque `mfaRequired` est retourné, la réponse de connexion inclut un `challengeId` et les méthodes disponibles de l'utilisateur. Le client redirige vers une page de défi MFA où l'utilisateur vérifie avec l'une de ses méthodes inscrites via `POST /api/auth/mfa/verify`.

Les défis expirent après 5 minutes et sont à usage unique.

### Inscription forcée

Lorsque `mfaSetupRequired` est retourné, la réponse inclut un `setupToken`. Ce jeton authentifie l'utilisateur auprès des points de terminaison de configuration MFA (via l'en-tête `X-MFA-Setup-Token`) afin qu'il puisse inscrire une méthode avant d'obtenir une session cookie.

## Inscription à la MFA

Les utilisateurs s'inscrivent à la MFA via les points de terminaison de configuration en libre-service. Ceux-ci nécessitent soit une session cookie authentifiée, soit un jeton de configuration.

### Configuration TOTP

1. Appeler `POST /api/auth/mfa/totp/setup` — retourne un code QR (`data:image/png;base64,...`), une `manualKey` (Base32 pour la saisie manuelle) et un jeton de configuration
2. L'utilisateur scanne le code QR avec son application d'authentification
3. L'utilisateur saisit le code à 6 chiffres pour confirmer : `POST /api/auth/mfa/totp/confirm`

### Configuration WebAuthn / Clé d'accès

1. Appeler `POST /api/auth/mfa/webauthn/setup` — retourne `PublicKeyCredentialCreationOptions`
2. Le client appelle `navigator.credentials.create()` avec les options
3. Envoyer la réponse d'attestation à `POST /api/auth/mfa/webauthn/confirm`

### Codes de récupération

Appeler `POST /api/auth/mfa/recovery/generate` pour générer 10 codes à usage unique. Au moins une méthode principale (TOTP ou WebAuthn) doit être inscrite au préalable.

La régénération des codes remplace tous les codes de récupération existants. Chaque code ne peut être utilisé qu'une seule fois.

## Gestion de la MFA

### Libre-service utilisateur

- `GET /api/auth/mfa/status` — afficher les méthodes inscrites
- `DELETE /api/auth/mfa/credentials/{id}` — supprimer un identifiant spécifique

Si la dernière méthode principale est supprimée, la MFA est désactivée pour l'utilisateur.

### API d'administration

Les administrateurs peuvent gérer la MFA pour n'importe quel utilisateur via l'[API d'administration](admin-api) :

- `GET /api/v1/profile/{userId}/mfa` — afficher le statut MFA d'un utilisateur
- `DELETE /api/v1/profile/{userId}/mfa` — réinitialiser toute la MFA (pour les utilisateurs verrouillés)
- `DELETE /api/v1/profile/{userId}/mfa/{id}` — supprimer un identifiant spécifique

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

## Interface de connexion personnalisée

Si vous créez une interface de connexion personnalisée, gérez ces réponses de `POST /api/auth/login` :

1. **Connexion normale** — `{ userId, email, name }` avec cookie défini. Redirection vers `returnUrl`.
2. **MFA requise** — `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Afficher le formulaire de défi MFA.
3. **Configuration MFA requise** — `{ mfaSetupRequired: true, setupToken }`. Afficher le flux d'inscription MFA.

Consultez l'[API Auth](auth-api) pour la référence complète des points de terminaison.
